using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KeepBill.Data;
using KeepBill.Models;
using KeepBill.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace KeepBill.Controllers
{
    [Authorize]
    public class ReportsController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        public ReportsController(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        public async Task<IActionResult> Index(DateTime? from, DateTime? to)
        {
            var today = DateTime.UtcNow.Date;
            var startDate = (from ?? new DateTime(today.Year, today.Month, 1)).Date;
            var endDate = (to ?? today).Date;
            if (endDate < startDate)
            {
                (startDate, endDate) = (endDate, startDate);
            }

            var periodDays = (int)(endDate - startDate).TotalDays + 1;

            var scopedInvoicesQuery = await ApplyOwnershipScopeAsync(
                _context.Invoices
                    .AsNoTracking()
                    .Include(i => i.Customer)
                    .Include(i => i.Payments));

            var scopedPaymentsQuery = await ApplyOwnershipScopeToPaymentsAsync(
                _context.Payments
                    .AsNoTracking()
                    .Include(p => p.Invoice));

            var invoicesInPeriodQuery = scopedInvoicesQuery
                .Where(i => i.IssueDate >= startDate && i.IssueDate <= endDate);

            var paymentsInPeriodQuery = scopedPaymentsQuery
                .Where(p => p.PaidAt >= startDate && p.PaidAt <= endDate);

            var invoicesInPeriod = await invoicesInPeriodQuery.ToListAsync();
            var paymentsInPeriod = await paymentsInPeriodQuery.ToListAsync();

            var vm = new ReportsViewModel
            {
                From = startDate,
                To = endDate,
                PeriodDays = periodDays,
                InvoicedTotal = invoicesInPeriod.Sum(i => i.GrandTotal),
                ReceivedTotal = paymentsInPeriod.Sum(p => p.Amount),
                IssuedInvoices = invoicesInPeriod.Count,
                NewCustomers = invoicesInPeriod
                    .Where(i => i.Customer != null && i.Customer.CreatedAt.Date >= startDate && i.Customer.CreatedAt.Date <= endDate)
                    .Select(i => i.CustomerId)
                    .Distinct()
                    .Count()
            };

            vm.OutstandingTotal = await scopedInvoicesQuery
                .Where(i => i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled)
                .Select(i => i.GrandTotal - ((decimal?)i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m))
                .SumAsync();

            vm.OverdueTotal = await scopedInvoicesQuery
                .Where(i =>
                    i.DueDate < today &&
                    i.Status != InvoiceStatus.Paid &&
                    i.Status != InvoiceStatus.Cancelled)
                .Select(i => i.GrandTotal - ((decimal?)i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m))
                .SumAsync();

            vm.ReceivedRatePercent = vm.InvoicedTotal <= 0m
                ? 0m
                : decimal.Round((vm.ReceivedTotal / vm.InvoicedTotal) * 100m, 2);

            vm.AverageInvoiceValue = vm.IssuedInvoices <= 0
                ? 0m
                : decimal.Round(vm.InvoicedTotal / vm.IssuedInvoices, 2);

            vm.AverageDaysToPayment = CalculateAverageDaysToPayment(invoicesInPeriod);

            vm.StatusBreakdown = invoicesInPeriod
                .GroupBy(i => i.Status.ToString())
                .Select(g => new LabelValueItem
                {
                    Label = g.Key,
                    Value = g.Count()
                })
                .OrderByDescending(x => x.Value)
                .ToList();

            vm.CategoryBreakdown = invoicesInPeriod
                .GroupBy(i => ExtractMetadataValue(i.Notes, "Categoria:"))
                .Select(g => new LabelValueItem
                {
                    Label = string.IsNullOrWhiteSpace(g.Key) ? "Sem categoria" : g.Key,
                    Value = g.Sum(i => i.GrandTotal)
                })
                .OrderByDescending(x => x.Value)
                .Take(10)
                .ToList();

            vm.PaymentMethodBreakdown = paymentsInPeriod
                .GroupBy(p => p.Method.ToString())
                .Select(g => new LabelValueItem
                {
                    Label = g.Key,
                    Value = g.Sum(p => p.Amount)
                })
                .OrderByDescending(x => x.Value)
                .ToList();

            vm.DailyTrend = BuildDailyTrend(startDate, endDate, invoicesInPeriod, paymentsInPeriod);

            var monthlyInvoiced = invoicesInPeriod
                .GroupBy(i => new { i.IssueDate.Year, i.IssueDate.Month })
                .Select(g => new MonthlyReportItem
                {
                    Year = g.Key.Year,
                    Month = g.Key.Month,
                    Invoiced = g.Sum(i => i.GrandTotal)
                })
                .ToList();

            var monthlyReceived = paymentsInPeriod
                .GroupBy(p => new { p.PaidAt.Year, p.PaidAt.Month })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    Amount = g.Sum(p => p.Amount)
                })
                .ToList();

            vm.MonthlyBreakdown = monthlyInvoiced
                .Select(item =>
                {
                    var received = monthlyReceived
                        .FirstOrDefault(x => x.Year == item.Year && x.Month == item.Month)?.Amount ?? 0m;
                    item.Received = received;
                    return item;
                })
                .OrderBy(x => x.Year)
                .ThenBy(x => x.Month)
                .TakeLast(12)
                .ToList();

            vm.TopCustomers = invoicesInPeriod
                .Where(i => i.Customer != null)
                .GroupBy(i => new { i.CustomerId, i.Customer!.Name })
                .Select(g => new TopCustomerItem
                {
                    CustomerId = g.Key.CustomerId,
                    CustomerName = g.Key.Name,
                    InvoiceCount = g.Count(),
                    InvoicedTotal = g.Sum(i => i.GrandTotal)
                })
                .OrderByDescending(c => c.InvoicedTotal)
                .Take(8)
                .ToList();

            return View(vm);
        }

        private static IReadOnlyList<DailyTrendItem> BuildDailyTrend(
            DateTime startDate,
            DateTime endDate,
            IReadOnlyList<Invoice> invoicesInPeriod,
            IReadOnlyList<Payment> paymentsInPeriod)
        {
            var invoicedByDay = invoicesInPeriod
                .GroupBy(i => i.IssueDate.Date)
                .ToDictionary(g => g.Key, g => g.Sum(i => i.GrandTotal));

            var receivedByDay = paymentsInPeriod
                .GroupBy(p => p.PaidAt.Date)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));

            var rows = new List<DailyTrendItem>();
            for (var day = startDate; day <= endDate; day = day.AddDays(1))
            {
                rows.Add(new DailyTrendItem
                {
                    Day = day,
                    Invoiced = invoicedByDay.TryGetValue(day, out var invoiced) ? invoiced : 0m,
                    Received = receivedByDay.TryGetValue(day, out var received) ? received : 0m
                });
            }

            return rows;
        }

        private static decimal CalculateAverageDaysToPayment(IEnumerable<Invoice> invoicesInPeriod)
        {
            var paidInvoices = invoicesInPeriod
                .Where(i => i.Payments.Any())
                .Select(i => new
                {
                    IssueDate = i.IssueDate.Date,
                    FirstPayment = i.Payments.OrderBy(p => p.PaidAt).First().PaidAt.Date
                })
                .ToList();

            if (paidInvoices.Count == 0)
            {
                return 0m;
            }

            var average = paidInvoices
                .Average(i => (i.FirstPayment - i.IssueDate).TotalDays);

            return decimal.Round((decimal)average, 2);
        }

        private static string ExtractMetadataValue(string? notes, string key)
        {
            if (string.IsNullOrWhiteSpace(notes) || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var lines = notes.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(key.Length).Trim();
                }
            }

            return string.Empty;
        }

        private bool IsCurrentUserAdmin()
        {
            var currentEmail = User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(currentEmail))
            {
                return false;
            }

            var adminEmails = _configuration.GetSection("Administration:AdminEmails").Get<string[]>() ?? Array.Empty<string>();
            return adminEmails.Any(a => string.Equals(a, currentEmail, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<Guid?> GetSessionCustomerIdAsync(bool createIfMissing)
        {
            var email = User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(email))
            {
                return null;
            }

            var existingId = await _context.Customers
                .Where(c => c.Email != null && c.Email.ToLower() == email.ToLower())
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync();

            if (existingId.HasValue || !createIfMissing)
            {
                return existingId;
            }

            var displayName = email.Split('@')[0];
            var customer = new Customer
            {
                Name = displayName,
                Email = email,
                CreatedAt = DateTime.UtcNow
            };
            _context.Customers.Add(customer);
            await _context.SaveChangesAsync();
            return customer.Id;
        }

        private async Task<IQueryable<Invoice>> ApplyOwnershipScopeAsync(IQueryable<Invoice> query)
        {
            if (IsCurrentUserAdmin())
            {
                return query;
            }

            var customerId = await GetSessionCustomerIdAsync(createIfMissing: false);
            if (!customerId.HasValue)
            {
                return query.Where(i => false);
            }

            return query.Where(i => i.CustomerId == customerId.Value);
        }

        private async Task<IQueryable<Payment>> ApplyOwnershipScopeToPaymentsAsync(IQueryable<Payment> query)
        {
            if (IsCurrentUserAdmin())
            {
                return query;
            }

            var customerId = await GetSessionCustomerIdAsync(createIfMissing: false);
            if (!customerId.HasValue)
            {
                return query.Where(p => false);
            }

            return query.Where(p => p.Invoice != null && p.Invoice.CustomerId == customerId.Value);
        }
    }
}
