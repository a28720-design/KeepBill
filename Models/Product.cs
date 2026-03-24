using System;
using System.ComponentModel.DataAnnotations;

namespace KeepBill.Models
{
    public class Product
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required, StringLength(120)]
        public string Name { get; set; } = string.Empty;

        [StringLength(250)]
        public string? Description { get; set; }

        [StringLength(20)]
        public string? Unit { get; set; }

        [Range(0, 999999)]
        [DataType(DataType.Currency)]
        public decimal UnitPrice { get; set; }

        [Range(0, 100)]
        public decimal VatRate { get; set; } = 23m;

        public bool IsActive { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }
    }
}
