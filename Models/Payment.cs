using System;
using System.ComponentModel.DataAnnotations;

namespace KeepBill.Models
{
    public class Payment
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid InvoiceId { get; set; }
        public Invoice? Invoice { get; set; }

        [Range(0, 999999)]
        public decimal Amount { get; set; }

        public PaymentMethod Method { get; set; } = PaymentMethod.BankTransfer;

        [StringLength(120)]
        public string? Reference { get; set; }

        [DataType(DataType.Date)]
        public DateTime PaidAt { get; set; } = DateTime.UtcNow.Date;

        [StringLength(250)]
        public string? Notes { get; set; }
    }
}
