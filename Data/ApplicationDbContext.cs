using KeepBill.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace KeepBill.Data
{
    public class ApplicationDbContext : IdentityDbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Customer> Customers => Set<Customer>();
        public DbSet<Product> Products => Set<Product>();
        public DbSet<Invoice> Invoices => Set<Invoice>();
        public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
        public DbSet<Payment> Payments => Set<Payment>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            builder.Entity<Customer>(entity =>
            {
                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(120);

                entity.Property(e => e.TaxId)
                    .HasMaxLength(20);

                entity.Property(e => e.Email)
                    .HasMaxLength(160);

                entity.Property(e => e.Phone)
                    .HasMaxLength(40);

                entity.Property(e => e.BillingAddress)
                    .HasMaxLength(180);

                entity.Property(e => e.City)
                    .HasMaxLength(90);

                entity.Property(e => e.Country)
                    .HasMaxLength(90);

                entity.Property(e => e.Notes)
                    .HasMaxLength(500);

                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("timezone('utc', now())");

                entity.HasIndex(e => e.Name);
                entity.HasIndex(e => e.TaxId);
            });

            builder.Entity<Product>(entity =>
            {
                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(120);

                entity.Property(e => e.Description)
                    .HasMaxLength(250);

                entity.Property(e => e.Unit)
                    .HasMaxLength(20);

                entity.Property(e => e.UnitPrice)
                    .HasColumnType("numeric(12,2)");

                entity.Property(e => e.VatRate)
                    .HasColumnType("numeric(5,2)");

                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("timezone('utc', now())");

                entity.HasIndex(e => new { e.Name, e.IsActive });
            });

            builder.Entity<Invoice>(entity =>
            {
                entity.Property(e => e.Number)
                    .IsRequired()
                    .HasMaxLength(30);

                entity.Property(e => e.IssueDate)
                    .HasColumnType("date");

                entity.Property(e => e.DueDate)
                    .HasColumnType("date");

                entity.Property(e => e.Currency)
                    .HasMaxLength(3)
                    .HasDefaultValue("EUR");

                entity.Property(e => e.Status)
                    .HasConversion<string>()
                    .HasMaxLength(30);

                entity.Property(e => e.Subtotal)
                    .HasColumnType("numeric(14,2)");

                entity.Property(e => e.VatTotal)
                    .HasColumnType("numeric(14,2)");

                entity.Property(e => e.GrandTotal)
                    .HasColumnType("numeric(14,2)");

                entity.Property(e => e.CreatedAt)
                    .HasDefaultValueSql("timezone('utc', now())");

                entity.HasIndex(e => e.Number)
                    .IsUnique();

                entity.HasOne(e => e.Customer)
                    .WithMany()
                    .HasForeignKey(e => e.CustomerId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            builder.Entity<InvoiceLine>(entity =>
            {
                entity.Property(e => e.Description)
                    .IsRequired()
                    .HasMaxLength(200);

                entity.Property(e => e.Quantity)
                    .HasColumnType("numeric(12,2)");

                entity.Property(e => e.UnitPrice)
                    .HasColumnType("numeric(12,2)");

                entity.Property(e => e.VatRate)
                    .HasColumnType("numeric(5,2)");

                entity.Property(e => e.LineTotal)
                    .HasColumnType("numeric(14,2)");

                entity.HasOne(e => e.Invoice)
                    .WithMany(i => i.Lines)
                    .HasForeignKey(e => e.InvoiceId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.Product)
                    .WithMany()
                    .HasForeignKey(e => e.ProductId)
                    .OnDelete(DeleteBehavior.SetNull);
            });

            builder.Entity<Payment>(entity =>
            {
                entity.Property(e => e.Amount)
                    .HasColumnType("numeric(14,2)");

                entity.Property(e => e.Method)
                    .HasConversion<string>()
                    .HasMaxLength(30);

                entity.Property(e => e.Reference)
                    .HasMaxLength(120);

                entity.Property(e => e.Notes)
                    .HasMaxLength(250);

                entity.Property(e => e.PaidAt)
                    .HasColumnType("date");

                entity.HasOne(e => e.Invoice)
                    .WithMany(i => i.Payments)
                    .HasForeignKey(e => e.InvoiceId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}
