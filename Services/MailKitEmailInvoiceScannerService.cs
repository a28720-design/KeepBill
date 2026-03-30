using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
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

namespace KeepBill.Services
{
    public class MailKitEmailInvoiceScannerService : IEmailInvoiceScannerService
    {
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
                    ConfigurationHint = "Configura a secao EmailInbox no appsettings/user-secrets com Host, Username e Password."
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
                    var item = BuildItem(message, options);
                    items.Add(item);
                }

                result.Items = items.OrderByDescending(i => i.ReceivedAtUtc).ToList();
                result.TotalEmailsScanned = result.Items.Count;
                result.TotalInvoicesDetected = result.Items.Count(i => i.IsInvoice);
                await ImportDetectedInvoicesAsync(result.Items.Where(i => i.IsInvoice).ToList(), cancellationToken, result, options, ownerCustomerId);

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

        private EmailInvoiceItemViewModel BuildItem(MimeMessage message, EmailInboxOptions options)
        {
            var mailbox = message.From.Mailboxes.FirstOrDefault();
            var attachments = message.Attachments
                .OfType<MimePart>()
                .Select(a => a.FileName ?? "anexo")
                .ToList();

            var subject = message.Subject ?? string.Empty;
            var textBody = GetMessageText(message);
            var detection = DetectInvoice(subject, textBody, attachments, options);

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
                AttachmentCount = attachments.Count,
                AttachmentNames = string.Join(", ", attachments.Take(8)),
                ParsedTotalAmount = ParseTotalAmount(subject, textBody)
            };
        }

        private (bool isInvoice, string reason) DetectInvoice(string subject, string textBody, List<string> attachmentNames, EmailInboxOptions options)
        {
            var keywords = options.InvoiceKeywords.Select(k => k.ToLowerInvariant()).ToArray();
            var subjectLower = subject.ToLowerInvariant();
            var bodyLower = textBody.ToLowerInvariant();
            var attachmentLower = string.Join(" ", attachmentNames).ToLowerInvariant();

            var keywordInSubject = keywords.Any(k => subjectLower.Contains(k));
            var keywordInBody = keywords.Any(k => bodyLower.Contains(k));
            var keywordInAttachment = keywords.Any(k => attachmentLower.Contains(k));
            var hasTypicalAttachment = attachmentNames.Any(a =>
                a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                a.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

            if (keywordInSubject && hasTypicalAttachment)
                return (true, "Palavra-chave no assunto + anexo PDF/XML");
            if (keywordInAttachment)
                return (true, "Palavra-chave no nome do anexo");
            if (keywordInSubject || keywordInBody)
                return (true, "Palavra-chave encontrada no email");
            if (hasTypicalAttachment)
                return (true, "Anexo com formato tipico de fatura");

            return (false, "Sem padrao claro de fatura");
        }

        private static string GetMessageText(MimeMessage message)
        {
            if (!string.IsNullOrWhiteSpace(message.TextBody))
                return message.TextBody;

            if (string.IsNullOrWhiteSpace(message.HtmlBody))
                return string.Empty;

            return Regex.Replace(message.HtmlBody, "<.*?>", " ");
        }

        private bool IsConfigured(EmailInboxOptions options)
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
                    Description = string.IsNullOrWhiteSpace(item.Subject) ? "Fatura importada do email" : item.Subject.Trim(),
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

            var displayName = string.Empty;
            if (string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(normalizedEmail))
            {
                displayName = normalizedEmail.Split('@')[0];
            }

            var customer = new Customer
            {
                Name = string.IsNullOrWhiteSpace(displayName) ? "Cliente email" : displayName,
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
                $"Detecao: {item.DetectionReason}"
            };

            if (!string.IsNullOrWhiteSpace(item.MessageId))
            {
                lines.Add($"MessageId: {item.MessageId}");
            }

            if (!string.IsNullOrWhiteSpace(item.From))
            {
                lines.Add($"Remetente: {item.From}");
            }

            if (!string.IsNullOrWhiteSpace(item.AttachmentNames))
            {
                lines.Add($"Anexos: {item.AttachmentNames}");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static decimal? ParseTotalAmount(string subject, string textBody)
        {
            var source = $"{subject} {textBody}";
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

            if (parsed.Count == 0)
            {
                return null;
            }

            return parsed.Max();
        }
    }
}
