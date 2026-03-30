using System;
using System.Linq;
using System.Threading.Tasks;
using KeepBill.Data;
using KeepBill.Models;
using KeepBill.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KeepBill.Controllers
{
    [Authorize]
    public class ReportsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ReportsController(ApplicationDbContext context)
        {
            _context = context;
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
            var utcStart = DateTime.SpecifyKind(startDate, DateTimeKind.Utc);
            var utcEndExclusive = DateTime.SpecifyKind(endDate.AddDays(1), DateTimeKind.Utc);

            var invoicesInPeriod = _context.Invoices
                .AsNoTracking()
                .Where(i => i.IssueDate >= startDate && i.IssueDate <= endDate);

            var paymentsInPeriod = _context.Payments
                .AsNoTracking()
                .Where(p => p.PaidAt >= startDate && p.PaidAt <= endDate);

            var vm = new ReportsViewModel
            {
                From = startDate,
                To = endDate,
                InvoicedTotal = await invoicesInPeriod.SumAsync(i => i.GrandTotal),
                ReceivedTotal = await paymentsInPeriod.SumAsync(p => p.Amount),
                IssuedInvoices = await invoicesInPeriod.CountAsync(),
                NewCustomers = await _context.Customers.AsNoTracking()
                    .CountAsync(c => c.CreatedAt >= utcStart && c.CreatedAt < utcEndExclusive)
            };

            vm.OutstandingTotal = await _context.Invoices
                .AsNoTracking()
                .Where(i => i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled)
                .Select(i => i.GrandTotal - ((decimal?)i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m))
                .SumAsync();

            vm.OverdueTotal = await _context.Invoices
                .AsNoTracking()
                .Where(i =>
                    i.DueDate < today &&
                    i.Status != InvoiceStatus.Paid &&
                    i.Status != InvoiceStatus.Cancelled)
                .Select(i => i.GrandTotal - ((decimal?)i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m))
                .SumAsync();

            var monthlyInvoiced = await _context.Invoices
                .AsNoTracking()
                .Where(i => i.IssueDate >= startDate && i.IssueDate <= endDate)
                .GroupBy(i => new { i.IssueDate.Year, i.IssueDate.Month })
                .Select(g => new MonthlyReportItem
                {
                    Year = g.Key.Year,
                    Month = g.Key.Month,
                    Invoiced = g.Sum(i => i.GrandTotal)
                })
                .ToListAsync();

            var monthlyReceived = await _context.Payments
                .AsNoTracking()
                .Where(p => p.PaidAt >= startDate && p.PaidAt <= endDate)
                .GroupBy(p => new { p.PaidAt.Year, p.PaidAt.Month })
                .Select(g => new
                {
                    g.Key.Year,
                    g.Key.Month,
                    Amount = g.Sum(p => p.Amount)
                })
                .ToListAsync();

            vm.MonthlyBreakdown = monthlyInvoiced
                .Select(item =>
                {
                    var received = monthlyReceived
                        .FirstOrDefault(x => x.Year == item.Year && x.Month == item.Month)?.Amount ?? 0m;
                    item.Received = received;
                    return item;
                })
                .OrderByDescending(x => x.Year)
                .ThenByDescending(x => x.Month)
                .Take(12)
                .ToList();

            vm.TopCustomers = await _context.Invoices
                .AsNoTracking()
                .Include(i => i.Customer)
                .Where(i => i.IssueDate >= startDate && i.IssueDate <= endDate && i.Customer != null)
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
                .ToListAsync();

            return View(vm);
        }
    }
}
