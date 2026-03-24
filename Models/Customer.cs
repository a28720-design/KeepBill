using System;
using System.ComponentModel.DataAnnotations;

namespace KeepBill.Models
{
    public class Customer
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        [Required, StringLength(120)]
        public string Name { get; set; } = string.Empty;

        [StringLength(20)]
        public string? TaxId { get; set; }

        [EmailAddress, StringLength(160)]
        public string? Email { get; set; }

        [Phone, StringLength(40)]
        public string? Phone { get; set; }

        [StringLength(180)]
        public string? BillingAddress { get; set; }

        [StringLength(90)]
        public string? City { get; set; }

        [StringLength(90)]
        public string? Country { get; set; }

        [StringLength(500)]
        public string? Notes { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }
    }
}
