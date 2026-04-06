using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KeepBill.Data;
using KeepBill.Models;
using KeepBill.Models.ViewModels;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using UglyToad.PdfPig;

namespace KeepBill.Services
{
    public class MailKitEmailInvoiceScannerService : IEmailInvoiceScannerService
    {
        private static readonly string[] CommonInvoiceCategories =
        {
            "Utilidades", "Telecomunicacoes", "Software", "Renda", "Transporte", "Seguros", "Impostos", "Compras", "Outros"
        };

        private readonly EmailInboxOptions _options;
        private readonly ApplicationDbContext _context;
        private readonly ILogger<MailKitEmailInvoiceScannerService> _logger;
        private EmailInvoicesViewModel _lastResult = new EmailInvoicesViewModel();

        public MailKitEmailInvoiceScannerService(
            IOptions<EmailInboxOptions> options,
            ApplicationDbContext context,
            ILogger<MailKitEmailInvoiceScannerService> logger)
        {
            _options = options.Value;
            _context = context;
            _logger = logger;
        }

        public EmailInvoicesViewModel GetLastResult() => _lastResult;

        public async Task<EmailInvoicesViewModel> ScanAsync(
            EmailInboxOptions? optionsOverride = null,
            Guid? ownerCustomerId = null,
            CancellationToken cancellationToken = default)
        {
            var options = optionsOverride ?? _options;
            if (!IsConfigured(options))
            {
                _lastResult = new EmailInvoicesViewModel
                {
                    IsConfigured = false,
                    ConfigurationHint = "Configura a secao EmailInbox com Host, Username e Password."
                };
                return _lastResult;
            }

            var result = new EmailInvoicesViewModel
            {
                IsConfigured = true,
                LastSyncUtc = DateTime.UtcNow
            };

            try
            {
                using var client = new ImapClient();
                await client.ConnectAsync(options.Host, options.Port, options.UseSsl, cancellationToken);
                await client.AuthenticateAsync(options.Username, options.Password, cancellationToken);

                var folder = string.IsNullOrWhiteSpace(options.Folder) ? "INBOX" : options.Folder.Trim();
                var inbox = await ResolveFolderAsync(client, folder, cancellationToken);
                await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
                result.FolderUsed = inbox.FullName;

                var sinceDate = DateTime.UtcNow.AddDays(-Math.Max(1, options.DaysBack));
                SearchQuery query = SearchQuery.DeliveredAfter(sinceDate);
                if (options.OnlyUnread)
                {
                    query = query.And(SearchQuery.NotSeen);
                }

                var uids = await inbox.SearchAsync(query, cancellationToken);
                var selectedUids = uids
                    .OrderByDescending(u => u.Id)
                    .Take(Math.Max(1, options.MaxMessages))
                    .ToList();

                var items = new List<EmailInvoiceItemViewModel>();
                foreach (var uid in selectedUids)
                {
                    var message = await inbox.GetMessageAsync(uid, cancellationToken);
                    items.Add(await BuildItemAsync(message, options, cancellationToken));
                }

                result.Items = items.OrderByDescending(i => i.ReceivedAtUtc).ToList();
                result.TotalEmailsScanned = result.Items.Count;
                result.TotalInvoicesDetected = result.Items.Count(i => i.IsInvoice);

                await ImportDetectedInvoicesAsync(
                    result.Items.Where(i => i.IsInvoice).ToList(),
                    cancellationToken,
                    result,
                    options,
                    ownerCustomerId);

                await client.DisconnectAsync(true, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Email invoice sync failed.");
                result.LastError = ex.Message;
            }

            _lastResult = result;
            return result;
        }

        private async Task<EmailInvoiceItemViewModel> BuildItemAsync(
            MimeMessage message,
            EmailInboxOptions options,
            CancellationToken cancellationToken)
        {
            var mailbox = message.From.Mailboxes.FirstOrDefault();
            var subject = message.Subject ?? string.Empty;
            var textBody = GetMessageText(message);
            var attachmentData = await ReadAttachmentsAsync(message, cancellationToken);
            var attachmentNames = attachmentData.Select(a => a.FileName).ToList();
            var attachmentText = string.Join(Environment.NewLine, attachmentData.Select(a => a.ExtractedText).Where(t => !string.IsNullOrWhiteSpace(t)));

            var parseSource = $"{subject}\n{textBody}\n{attachmentText}";
            var parsedAmount = ParseTotalAmount(parseSource);
            var detection = DetectInvoice(message, subject, textBody, attachmentNames, attachmentText, options, parsedAmount);

            return new EmailInvoiceItemViewModel
            {
                MessageId = message.MessageId ?? string.Empty,
                Subject = subject,
                From = message.From.ToString(),
                FromEmail = mailbox?.Address ?? string.Empty,
                FromDisplayName = mailbox?.Name ?? string.Empty,
                ReceivedAtUtc = message.Date.UtcDateTime,
                IsInvoice = detection.isInvoice,
                DetectionReason = detection.reason,
                AttachmentCount = attachmentNames.Count,
                AttachmentNames = string.Join(", ", attachmentNames.Take(10)),
                ParsedTotalAmount = parsedAmount,
                ParsedInvoiceNumber = detection.invoiceNumber,
                DetectedCategory = detection.category,
                SourceLiteral = detection.sourceLiteral
            };
        }

        private (bool isInvoice, string reason, string invoiceNumber, string category, string sourceLiteral) DetectInvoice(
            MimeMessage message,
            string subject,
            string textBody,
            List<string> attachmentNames,
            string attachmentText,
            EmailInboxOptions options,
            decimal? parsedAmount)
        {
            var combined = $"{subject}\n{textBody}\n{string.Join(' ', attachmentNames)}\n{attachmentText}";
            var combinedLower = combined.ToLowerInvariant();
            var keywords = options.InvoiceKeywords.Select(k => k.ToLowerInvariant()).ToArray();

            var keywordInSubject = keywords.Any(k => subject.Contains(k, StringComparison.OrdinalIgnoreCase));
            var keywordInBody = keywords.Any(k => textBody.Contains(k, StringComparison.OrdinalIgnoreCase));
            var keywordInAttachmentName = keywords.Any(k => attachmentNames.Any(a => a.Contains(k, StringComparison.OrdinalIgnoreCase)));
            var keywordInAttachmentText = keywords.Any(k => attachmentText.Contains(k, StringComparison.OrdinalIgnoreCase));

            var hasPdfOrXmlAttachment = attachmentNames.Any(a =>
                a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                a.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

            var hasInvoiceLikeAttachmentName = attachmentNames.Any(a =>
                Regex.IsMatch(a, @"\b(fatura|factura|invoice|recibo|ft|fa)\b", RegexOptions.IgnoreCase));

            var invoiceNumber = ExtractInvoiceNumber(combined);
            var hasInvoiceNumber = !string.IsNullOrWhiteSpace(invoiceNumber);
            var hasAmount = parsedAmount.HasValue && parsedAmount.Value > 0m;
            var hasTaxSignals = ContainsAny(combinedLower, "nif", "vat", "iva", "subtotal", "total", "taxa");

            var hasListUnsubscribe = message.Headers.Any(h => h.Field.Equals("List-Unsubscribe", StringComparison.OrdinalIgnoreCase));
            var hasMarketingSignals = ContainsAny(
                combinedLower,
                "newsletter", "unsubscribe", "campanha", "promocao", "promo", "desconto", "black friday", "oferta", "cupao", "cupom", "deal");
            var hasNoReplySender = (message.From.ToString() ?? string.Empty).Contains("noreply", StringComparison.OrdinalIgnoreCase)
                                   || (message.From.ToString() ?? string.Empty).Contains("no-reply", StringComparison.OrdinalIgnoreCase);

            var spamPenalty = 0;
            if (hasListUnsubscribe) spamPenalty += 3;
            if (hasMarketingSignals) spamPenalty += 3;
            if (hasNoReplySender) spamPenalty += 1;

            var score = 0;
            if (keywordInSubject) score += 3;
            if (keywordInBody) score += 2;
            if (keywordInAttachmentName) score += 2;
            if (keywordInAttachmentText) score += 3;
            if (hasPdfOrXmlAttachment) score += 2;
            if (hasInvoiceLikeAttachmentName) score += 3;
            if (hasInvoiceNumber) score += 3;
            if (hasAmount) score += 2;
            if (hasTaxSignals) score += 2;
            score -= spamPenalty;

            var minimumEvidence = hasInvoiceNumber || (hasAmount && hasTaxSignals) || (hasInvoiceLikeAttachmentName && hasPdfOrXmlAttachment);
            var isInvoice = score >= 7 && minimumEvidence;

            var reasonParts = new List<string>();
            if (keywordInSubject) reasonParts.Add("keyword assunto");
            if (keywordInBody) reasonParts.Add("keyword corpo");
            if (keywordInAttachmentName) reasonParts.Add("keyword nome anexo");
            if (keywordInAttachmentText) reasonParts.Add("keyword texto anexo");
            if (hasPdfOrXmlAttachment) reasonParts.Add("anexo PDF/XML");
            if (hasInvoiceLikeAttachmentName) reasonParts.Add("anexo com nome de fatura");
            if (hasInvoiceNumber) reasonParts.Add($"numero {invoiceNumber}");
            if (hasAmount) reasonParts.Add($"valor {parsedAmount:0.00} EUR");
            if (hasTaxSignals) reasonParts.Add("sinais fiscais");
            if (spamPenalty > 0) reasonParts.Add($"anti-lixo -{spamPenalty}");
            reasonParts.Add($"score {score}");

            if (!isInvoice && spamPenalty >= 4)
            {
                reasonParts.Add("marcado como newsletter/anuncio");
            }

            var category = DetectCategory(combined);
            if (!CommonInvoiceCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                category = "Outros";
            }

            var sourceLiteral = ExtractSourceLiteral(subject, textBody, attachmentText, attachmentNames, invoiceNumber);

            return (
                isInvoice,
                reasonParts.Count == 0 ? "Sem sinais de fatura" : string.Join(" | ", reasonParts),
                invoiceNumber,
                category,
                sourceLiteral);
        }

        private static string GetMessageText(MimeMessage message)
        {
            if (!string.IsNullOrWhiteSpace(message.TextBody))
            {
                return message.TextBody;
            }

            if (string.IsNullOrWhiteSpace(message.HtmlBody))
            {
                return string.Empty;
            }

            var withoutTags = Regex.Replace(message.HtmlBody, "<.*?>", " ");
            return Regex.Replace(withoutTags, @"\s+", " ").Trim();
        }

        private static bool IsConfigured(EmailInboxOptions options)
        {
            return !string.IsNullOrWhiteSpace(options.Host)
                   && !string.IsNullOrWhiteSpace(options.Username)
                   && !string.IsNullOrWhiteSpace(options.Password);
        }

        private static async Task<IMailFolder> ResolveFolderAsync(ImapClient client, string configuredFolder, CancellationToken cancellationToken)
        {
            if (string.Equals(configuredFolder, "INBOX", StringComparison.OrdinalIgnoreCase))
            {
                return client.Inbox;
            }

            try
            {
                return await client.GetFolderAsync(configuredFolder, cancellationToken);
            }
            catch
            {
                return client.Inbox;
            }
        }

        private async Task ImportDetectedInvoicesAsync(
            List<EmailInvoiceItemViewModel> emailInvoiceItems,
            CancellationToken cancellationToken,
            EmailInvoicesViewModel result,
            EmailInboxOptions options,
            Guid? ownerCustomerId)
        {
            if (!emailInvoiceItems.Any())
            {
                return;
            }

            var imported = 0;
            var skipped = 0;
            var utcNow = DateTime.UtcNow;
            var mailboxCustomer = ownerCustomerId.HasValue
                ? await _context.Customers.FirstOrDefaultAsync(c => c.Id == ownerCustomerId.Value, cancellationToken)
                : null;
            mailboxCustomer ??= await GetOrCreateMailboxCustomerAsync(utcNow, cancellationToken, options);

            foreach (var item in emailInvoiceItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var invoiceNumber = BuildImportedInvoiceNumber(item);
                var exists = await _context.Invoices.AnyAsync(i => i.Number == invoiceNumber, cancellationToken);
                if (exists)
                {
                    skipped++;
                    continue;
                }

                var issueDate = item.ReceivedAtUtc == default ? utcNow.Date : item.ReceivedAtUtc.Date;
                var total = item.ParsedTotalAmount.GetValueOrDefault(0m);
                var subtotal = total < 0 ? 0m : total;
                var literal = string.IsNullOrWhiteSpace(item.SourceLiteral) ? item.Subject : item.SourceLiteral;
                if (string.IsNullOrWhiteSpace(literal))
                {
                    literal = "Fatura importada do email";
                }

                var invoice = new Invoice
                {
                    Number = invoiceNumber,
                    CustomerId = mailboxCustomer.Id,
                    IssueDate = issueDate,
                    DueDate = issueDate.AddDays(15),
                    Status = InvoiceStatus.Issued,
                    Currency = "EUR",
                    Subtotal = subtotal,
                    VatTotal = 0m,
                    GrandTotal = subtotal,
                    Notes = BuildImportedInvoiceNotes(item),
                    CreatedAt = utcNow,
                    UpdatedAt = utcNow
                };

                invoice.Lines.Add(new InvoiceLine
                {
                    Description = TrimToMaxLength(literal, 200),
                    Quantity = 1m,
                    UnitPrice = subtotal,
                    VatRate = 0m,
                    LineTotal = subtotal
                });

                _context.Invoices.Add(invoice);
                imported++;
            }

            if (imported > 0)
            {
                await _context.SaveChangesAsync(cancellationToken);
            }

            result.TotalInvoicesImported = imported;
            result.TotalInvoicesSkipped = skipped;
        }

        private async Task<Customer> GetOrCreateMailboxCustomerAsync(
            DateTime utcNow,
            CancellationToken cancellationToken,
            EmailInboxOptions options)
        {
            var normalizedEmail = string.IsNullOrWhiteSpace(options.Username)
                ? null
                : options.Username.Trim().ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(normalizedEmail))
            {
                var existing = await _context.Customers
                    .FirstOrDefaultAsync(c => c.Email != null && c.Email.ToLower() == normalizedEmail, cancellationToken);
                if (existing != null)
                {
                    return existing;
                }
            }

            var displayName = string.IsNullOrWhiteSpace(normalizedEmail)
                ? "Cliente email"
                : normalizedEmail.Split('@')[0];

            var customer = new Customer
            {
                Name = displayName,
                Email = normalizedEmail,
                Notes = "Criado automaticamente pela sincronizacao de email.",
                CreatedAt = utcNow,
                UpdatedAt = utcNow
            };

            _context.Customers.Add(customer);
            return customer;
        }

        private static string BuildImportedInvoiceNumber(EmailInvoiceItemViewModel item)
        {
            var baseKey = string.IsNullOrWhiteSpace(item.MessageId)
                ? $"{item.ReceivedAtUtc:yyyyMMddHHmmss}|{item.FromEmail}|{item.Subject}"
                : item.MessageId.Trim();

            var hash = ComputeShortHash(baseKey);
            var day = (item.ReceivedAtUtc == default ? DateTime.UtcNow : item.ReceivedAtUtc).ToString("yyyyMMdd");
            return $"EML-{day}-{hash}";
        }

        private static string ComputeShortHash(string input)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).Substring(0, 8);
        }

        private static string BuildImportedInvoiceNotes(EmailInvoiceItemViewModel item)
        {
            var lines = new List<string>
            {
                "Importado automaticamente do email.",
                $"Categoria: {item.DetectedCategory}",
                $"Detecao: {item.DetectionReason}",
                $"Origem literal: {item.SourceLiteral}"
            };

            if (!string.IsNullOrWhiteSpace(item.ParsedInvoiceNumber))
            {
                lines.Add($"Numero identificado: {item.ParsedInvoiceNumber}");
            }

            if (!string.IsNullOrWhiteSpace(item.MessageId))
            {
                lines.Add($"MessageId: {item.MessageId}");
            }

            if (!string.IsNullOrWhiteSpace(item.From))
            {
                lines.Add($"Remetente: {item.From}");
            }

            if (!string.IsNullOrWhiteSpace(item.Subject))
            {
                lines.Add($"Assunto: {item.Subject}");
            }

            if (!string.IsNullOrWhiteSpace(item.AttachmentNames))
            {
                lines.Add($"Anexos: {item.AttachmentNames}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static decimal? ParseTotalAmount(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return null;
            }

            var regex = new Regex(
                @"(?:€|eur)\s*([0-9]{1,3}(?:[.,\s][0-9]{3})*(?:[.,][0-9]{2})?|[0-9]+(?:[.,][0-9]{2})?)|([0-9]{1,3}(?:[.,\s][0-9]{3})*(?:[.,][0-9]{2})?|[0-9]+(?:[.,][0-9]{2})?)\s*(?:€|eur)",
                RegexOptions.IgnoreCase);

            var matches = regex.Matches(source);
            if (matches.Count == 0)
            {
                return null;
            }

            var parsed = new List<decimal>();
            foreach (Match match in matches)
            {
                var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var normalized = value.Replace(" ", string.Empty).Replace(".", string.Empty).Replace(",", ".");
                if (decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) && amount > 0m)
                {
                    parsed.Add(decimal.Round(amount, 2));
                }
            }

            return parsed.Count == 0 ? null : parsed.Max();
        }

        private static string ExtractInvoiceNumber(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            var patterns = new[]
            {
                @"\b(?:fatura|factura|invoice|inv|ft|fa|recibo)\s*(?:n[oº.]?|#|num(?:ero)?)?\s*[:\-]?\s*([A-Z0-9\-/]{4,})\b",
                @"\b(?:no|n[oº.]?)\s*[:\-]?\s*([A-Z0-9\-/]{5,})\b"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(source, pattern, RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    return match.Groups[1].Value.Trim();
                }
            }

            return string.Empty;
        }

        private static string ExtractSourceLiteral(
            string subject,
            string body,
            string attachmentText,
            List<string> attachmentNames,
            string invoiceNumber)
        {
            var lines = $"{body}\n{attachmentText}"
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => Regex.Replace(l.Trim(), @"\s+", " "))
                .Where(l => !string.IsNullOrWhiteSpace(l) && l.Length > 8)
                .Take(400)
                .ToList();

            if (!string.IsNullOrWhiteSpace(invoiceNumber))
            {
                var byNumber = lines.FirstOrDefault(l => l.Contains(invoiceNumber, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(byNumber))
                {
                    return byNumber;
                }
            }

            var byInvoiceSignal = lines.FirstOrDefault(l =>
                l.Contains("fatura", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("invoice", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("recibo", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("nif", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("total", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(byInvoiceSignal))
            {
                return byInvoiceSignal;
            }

            if (!string.IsNullOrWhiteSpace(subject))
            {
                return subject.Trim();
            }

            var firstAttachment = attachmentNames.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstAttachment))
            {
                return firstAttachment;
            }

            return "Fatura importada do email";
        }

        private static string DetectCategory(string source)
        {
            var text = source.ToLowerInvariant();

            if (ContainsAny(text, "eletricidade", "energia", "agua", "water", "gas", "luz", "edp", "galp", "endesa")) return "Utilidades";
            if (ContainsAny(text, "internet", "telecom", "vodafone", "meo", "nos", "telefone", "fibra", "movel")) return "Telecomunicacoes";
            if (ContainsAny(text, "software", "licenca", "subscription", "saas", "adobe", "microsoft", "google workspace", "openai", "chatgpt")) return "Software";
            if (ContainsAny(text, "renda", "arrendamento", "condominio", "rent", "imovel", "escritorio")) return "Renda";
            if (ContainsAny(text, "combustivel", "fuel", "gasolina", "diesel", "portagem", "uber", "bolt", "estacionamento")) return "Transporte";
            if (ContainsAny(text, "seguro", "insurance", "multirriscos", "saude", "automovel")) return "Seguros";
            if (ContainsAny(text, "imposto", "at ", "iva", "financas", "tax", "seguranca social")) return "Impostos";
            if (ContainsAny(text, "material", "office", "papelaria", "fornecedor", "amazon", "compra", "mercearia", "supermercado")) return "Compras";

            return "Outros";
        }

        private static bool ContainsAny(string source, params string[] terms)
        {
            return terms.Any(term => source.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static string TrimToMaxLength(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed.Substring(0, maxLength);
        }

        private async Task<List<AttachmentSummary>> ReadAttachmentsAsync(MimeMessage message, CancellationToken cancellationToken)
        {
            var output = new List<AttachmentSummary>();
            foreach (var attachment in message.Attachments.OfType<MimePart>())
            {
                var fileName = attachment.FileName ?? "anexo";
                var extracted = string.Empty;

                try
                {
                    using var ms = new MemoryStream();
                    await attachment.Content.DecodeToAsync(ms, cancellationToken);
                    var bytes = ms.ToArray();
                    extracted = ExtractAttachmentText(fileName, bytes);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha a extrair texto do anexo {FileName}.", fileName);
                }

                output.Add(new AttachmentSummary
                {
                    FileName = fileName,
                    ExtractedText = TrimToMaxLength(extracted, 12000)
                });
            }

            return output;
        }

        private string ExtractAttachmentText(string fileName, byte[] contentBytes)
        {
            if (contentBytes.Length == 0)
            {
                return string.Empty;
            }

            if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractPdfText(contentBytes);
            }

            if (fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeAsText(contentBytes);
            }

            return string.Empty;
        }

        private string ExtractPdfText(byte[] contentBytes)
        {
            try
            {
                using var stream = new MemoryStream(contentBytes);
                using var doc = PdfDocument.Open(stream);
                var text = string.Join(
                    Environment.NewLine,
                    doc.GetPages()
                        .Take(8)
                        .Select(p => p.Text)
                        .Where(t => !string.IsNullOrWhiteSpace(t)));
                return Regex.Replace(text, @"\s+", " ").Trim();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Falha ao ler texto do PDF. Pode ser PDF digitalizado sem texto embutido.");
                return string.Empty;
            }
        }

        private static string DecodeAsText(byte[] bytes)
        {
            var utf8 = Encoding.UTF8.GetString(bytes);
            if (!string.IsNullOrWhiteSpace(utf8))
            {
                return utf8;
            }

            return Encoding.GetEncoding("iso-8859-1").GetString(bytes);
        }

        private sealed class AttachmentSummary
        {
            public string FileName { get; set; } = string.Empty;
            public string ExtractedText { get; set; } = string.Empty;
        }
    }
}
