using System;
using System.Collections.Generic;

namespace KeepBill.Models.ViewModels
{
    public class ReportsViewModel
    {
        public DateTime From { get; set; }
        public DateTime To { get; set; }
        public decimal InvoicedTotal { get; set; }
        public decimal ReceivedTotal { get; set; }
        public decimal OutstandingTotal { get; set; }
        public decimal OverdueTotal { get; set; }
        public int IssuedInvoices { get; set; }
        public int NewCustomers { get; set; }
        public IReadOnlyList<MonthlyReportItem> MonthlyBreakdown { get; set; } = Array.Empty<MonthlyReportItem>();
        public IReadOnlyList<TopCustomerItem> TopCustomers { get; set; } = Array.Empty<TopCustomerItem>();
    }

    public class MonthlyReportItem
    {
        public int Year { get; set; }
        public int Month { get; set; }
        public decimal Invoiced { get; set; }
        public decimal Received { get; set; }
    }

    public class TopCustomerItem
    {
        public Guid CustomerId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        public int InvoiceCount { get; set; }
        public decimal InvoicedTotal { get; set; }
    }
}
