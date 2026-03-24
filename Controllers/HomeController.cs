using KeepBill.Data;
using KeepBill.Models;
using KeepBill.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

namespace KeepBill.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly ApplicationDbContext _context;

        public HomeController(ILogger<HomeController> logger, ApplicationDbContext context)
        {
            _logger = logger;
            _context = context;
        }

        public async Task<IActionResult> Index()
        {
            var today = DateTime.UtcNow.Date;
            var startMonth = new DateTime(today.Year, today.Month, 1);

            var vm = new DashboardViewModel
            {
                TotalCustomers = await _context.Customers.CountAsync(),
                ActiveProducts = await _context.Products.CountAsync(p => p.IsActive),
                InvoicesIssued = await _context.Invoices.CountAsync(),
                OutstandingAmount = await _context.Invoices
                    .Where(i => i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled)
                    .Select(i => i.GrandTotal - ((decimal?)i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m))
                    .SumAsync(),
                PaidThisMonth = await _context.Payments
                    .Where(p => p.PaidAt >= startMonth)
                    .SumAsync(p => p.Amount)
            };

            vm.UpcomingInvoices = await _context.Invoices
                .AsNoTracking()
                .Include(i => i.Customer)
                .Where(i => i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled && i.DueDate >= today)
                .OrderBy(i => i.DueDate)
                .Take(5)
                .Select(i => new InvoiceCard
                {
                    Id = i.Id,
                    Number = i.Number,
                    CustomerName = i.Customer != null ? i.Customer.Name : "—",
                    DueDate = i.DueDate,
                    Balance = i.GrandTotal - ((decimal?)i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m),
                    Status = i.Status
                })
                .ToListAsync();

            vm.MonthlySummary = await _context.Invoices
                .AsNoTracking()
                .Where(i => i.IssueDate >= startMonth && i.IssueDate < startMonth.AddMonths(1))
                .GroupBy(i => i.Status)
                .Select(g => new StatusSummary
                {
                    Status = g.Key,
                    Count = g.Count(),
                    Total = g.Sum(i => i.GrandTotal)
                })
                .ToListAsync();

            return View(vm);
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }
    }
}
