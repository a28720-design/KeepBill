using System;
using System.Collections.Generic;

namespace KeepBill.Models.ViewModels
{
    public class CustomerDetailsViewModel
    {
        public Customer Customer { get; set; } = new Customer();
        public int TotalInvoices { get; set; }
        public decimal TotalInvoiced { get; set; }
        public decimal TotalPaid { get; set; }
        public decimal OutstandingBalance { get; set; }
        public IReadOnlyList<CustomerInvoiceSummary> RecentInvoices { get; set; } = Array.Empty<CustomerInvoiceSummary>();
    }

    public class CustomerInvoiceSummary
    {
        public Guid Id { get; set; }
        public string Number { get; set; } = string.Empty;
        public DateTime IssueDate { get; set; }
        public DateTime DueDate { get; set; }
        public InvoiceStatus Status { get; set; }
        public decimal Total { get; set; }
        public decimal Paid { get; set; }
        public decimal Balance { get; set; }
    }
}
