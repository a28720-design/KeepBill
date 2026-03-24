using System;
using System.ComponentModel.DataAnnotations;

namespace KeepBill.Models.ViewModels
{
    public class InvoiceLineInput
    {
        public Guid? Id { get; set; }
        public Guid? ProductId { get; set; }

        [StringLength(200)]
        public string? Description { get; set; }

        [Range(0, 999999)]
        public decimal Quantity { get; set; } = 1;

        [Range(0, 999999)]
        public decimal UnitPrice { get; set; }

        [Range(0, 100)]
        public decimal VatRate { get; set; } = 23m;
    }
}
