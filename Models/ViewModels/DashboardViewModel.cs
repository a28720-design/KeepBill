using System;
using System.Collections.Generic;

namespace KeepBill.Models.ViewModels
{
    public class DashboardViewModel
    {
        public int TotalCustomers { get; set; }
        public int ActiveProducts { get; set; }
        public int InvoicesIssued { get; set; }
        public decimal OutstandingAmount { get; set; }
        public int OverdueInvoices { get; set; }
        public decimal OverdueAmount { get; set; }
        public decimal PaidThisMonth { get; set; }
        public IReadOnlyList<InvoiceCard> UpcomingInvoices { get; set; } = Array.Empty<InvoiceCard>();
        public IReadOnlyList<InvoiceCard> OverdueInvoiceCards { get; set; } = Array.Empty<InvoiceCard>();
        public IReadOnlyList<StatusSummary> MonthlySummary { get; set; } = Array.Empty<StatusSummary>();
    }

    public class InvoiceCard
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
        public DateTime DueDate { get; set; }
        public decimal Balance { get; set; }
        public InvoiceStatus Status { get; set; }
    }

    public class StatusSummary
    {
        public InvoiceStatus Status { get; set; }
        public int Count { get; set; }
        public decimal Total { get; set; }
    }
}
