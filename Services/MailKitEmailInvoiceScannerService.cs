using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KeepBill.Models;
using KeepBill.Models.ViewModels;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Options;
using MimeKit;

namespace KeepBill.Services
{
    public class MailKitEmailInvoiceScannerService : IEmailInvoiceScannerService
    {
        private readonly EmailInboxOptions _options;
        private EmailInvoicesViewModel _lastResult = new EmailInvoicesViewModel();

        public MailKitEmailInvoiceScannerService(IOptions<EmailInboxOptions> options)
        {
            _options = options.Value;
        }

        public EmailInvoicesViewModel GetLastResult() => _lastResult;

        public async Task<EmailInvoicesViewModel> ScanAsync(CancellationToken cancellationToken = default)
        {
            if (!IsConfigured())
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
                await client.ConnectAsync(_options.Host, _options.Port, _options.UseSsl, cancellationToken);
                await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken);

                var folder = string.IsNullOrWhiteSpace(_options.Folder) ? "INBOX" : _options.Folder;
                var inbox = await client.GetFolderAsync(folder, cancellationToken);
                await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                var sinceDate = DateTime.UtcNow.AddDays(-Math.Max(1, _options.DaysBack));
                SearchQuery query = SearchQuery.DeliveredAfter(sinceDate);
                if (_options.OnlyUnread)
                {
                    query = query.And(SearchQuery.NotSeen);
                }

                var uids = await inbox.SearchAsync(query, cancellationToken);
                var selectedUids = uids
                    .OrderByDescending(u => u.Id)
                    .Take(Math.Max(1, _options.MaxMessages))
                    .ToList();

                var items = new List<EmailInvoiceItemViewModel>();
                foreach (var uid in selectedUids)
                {
                    var message = await inbox.GetMessageAsync(uid, cancellationToken);
                    var item = BuildItem(message);
                    items.Add(item);
                }

                result.Items = items.OrderByDescending(i => i.ReceivedAtUtc).ToList();
                result.TotalEmailsScanned = result.Items.Count;
                result.TotalInvoicesDetected = result.Items.Count(i => i.IsInvoice);

                await client.DisconnectAsync(true, cancellationToken);
            }
            catch (Exception ex)
            {
                result.LastError = ex.Message;
            }

            _lastResult = result;
            return result;
        }

        private EmailInvoiceItemViewModel BuildItem(MimeMessage message)
        {
            var attachments = message.Attachments
                .OfType<MimePart>()
                .Select(a => a.FileName ?? "anexo")
                .ToList();

            var subject = message.Subject ?? string.Empty;
            var textBody = GetMessageText(message);
            var detection = DetectInvoice(subject, textBody, attachments);

            return new EmailInvoiceItemViewModel
            {
                MessageId = message.MessageId ?? string.Empty,
                Subject = subject,
                From = message.From.ToString(),
                ReceivedAtUtc = message.Date.UtcDateTime,
                IsInvoice = detection.isInvoice,
                DetectionReason = detection.reason,
                AttachmentCount = attachments.Count,
                AttachmentNames = string.Join(", ", attachments.Take(8))
            };
        }

        private (bool isInvoice, string reason) DetectInvoice(string subject, string textBody, List<string> attachmentNames)
        {
            var keywords = _options.InvoiceKeywords.Select(k => k.ToLowerInvariant()).ToArray();
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

        private bool IsConfigured()
        {
            return !string.IsNullOrWhiteSpace(_options.Host)
                   && !string.IsNullOrWhiteSpace(_options.Username)
                   && !string.IsNullOrWhiteSpace(_options.Password);
        }
    }
}
