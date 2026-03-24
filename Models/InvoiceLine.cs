using System;
using System.ComponentModel.DataAnnotations;

namespace KeepBill.Models
{
    public class InvoiceLine
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required]
        public Guid InvoiceId { get; set; }
        public Invoice? Invoice { get; set; }

        public Guid? ProductId { get; set; }
        public Product? Product { get; set; }

        [Required, StringLength(200)]
        public string Description { get; set; } = string.Empty;

        [Range(0, 999999)]
        public decimal Quantity { get; set; } = 1;

        [Range(0, 999999)]
        public decimal UnitPrice { get; set; }

        [Range(0, 100)]
        public decimal VatRate { get; set; }

        [Range(0, 999999)]
        public decimal LineTotal { get; set; }
    }
}
