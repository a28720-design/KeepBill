using System;
using System.Collections.Generic;

namespace KeepBill.Models.ViewModels
{
    public class EmailInvoicesViewModel
    {
        public bool IsConfigured { get; set; }
        public string? ConfigurationHint { get; set; }
        public DateTime? LastSyncUtc { get; set; }
        public string? FolderUsed { get; set; }
        public int TotalEmailsScanned { get; set; }
        public int TotalInvoicesDetected { get; set; }
        public int TotalInvoicesImported { get; set; }
        public int TotalInvoicesSkipped { get; set; }
        public string? LastError { get; set; }
        public IReadOnlyList<EmailInvoiceItemViewModel> Items { get; set; } = Array.Empty<EmailInvoiceItemViewModel>();
    }

    public class EmailInvoiceItemViewModel
    {
        public string MessageId { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;
        public string FromEmail { get; set; } = string.Empty;
        public string FromDisplayName { get; set; } = string.Empty;
        public DateTime ReceivedAtUtc { get; set; }
        public bool IsInvoice { get; set; }
        public string DetectionReason { get; set; } = string.Empty;
        public int AttachmentCount { get; set; }
        public string AttachmentNames { get; set; } = string.Empty;
        public decimal? ParsedTotalAmount { get; set; }
        public string ParsedInvoiceNumber { get; set; } = string.Empty;
        public string DetectedCategory { get; set; } = "Outros";
        public string SourceLiteral { get; set; } = string.Empty;
    }
}
