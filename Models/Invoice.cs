using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace KeepBill.Models
{
    public class Invoice
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required, StringLength(30)]
        public string Number { get; set; } = string.Empty;

        [Required]
        public Guid CustomerId { get; set; }
        public Customer? Customer { get; set; }

        [DataType(DataType.Date)]
        public DateTime IssueDate { get; set; } = DateTime.UtcNow.Date;

        [DataType(DataType.Date)]
        public DateTime DueDate { get; set; } = DateTime.UtcNow.Date.AddDays(15);

        [Required]
        public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

        [StringLength(3)]
        public string Currency { get; set; } = "EUR";

        [Range(0, double.MaxValue)]
        public decimal Subtotal { get; set; }

        [Range(0, double.MaxValue)]
        public decimal VatTotal { get; set; }

        [Range(0, double.MaxValue)]
        public decimal GrandTotal { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAt { get; set; }

        public ICollection<InvoiceLine> Lines { get; set; } = new List<InvoiceLine>();
        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}
