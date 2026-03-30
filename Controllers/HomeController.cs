using KeepBill.Data;
using KeepBill.Models;
using KeepBill.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text;

namespace KeepBill.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        public HomeController(ILogger<HomeController> logger, ApplicationDbContext context, IConfiguration configuration)
        {
            _logger = logger;
            _context = context;
            _configuration = configuration;
        }

        public IActionResult Index()
        {
            if (User.Identity?.IsAuthenticated == true)
            {
                return RedirectToAction(nameof(Dashboard));
            }

            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult RequestDemo(string name, string email, string company, string phone, string message)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
            {
                TempData["LandingError"] = "Preenche pelo menos nome e email para pedir a demonstracao.";
                return RedirectToAction(nameof(Index));
            }

            var contactEmail = GetContactEmail();
            var subject = $"Pedido de demonstracao KeepBill - {company}".TrimEnd(' ', '-');
            var body = new StringBuilder()
                .AppendLine("Novo pedido de demonstracao")
                .AppendLine()
                .AppendLine($"Nome: {name}")
                .AppendLine($"Email: {email}")
                .AppendLine($"Empresa: {company}")
                .AppendLine($"Telefone: {phone}")
                .AppendLine("Mensagem:")
                .AppendLine(string.IsNullOrWhiteSpace(message) ? "-" : message)
                .ToString();

            var mailto = $"mailto:{contactEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";

            TempData["LandingSuccess"] = $"A abrir o teu cliente de email para enviar o pedido para {contactEmail}.";
            return Redirect(mailto);
        }

        [Authorize]
        public async Task<IActionResult> Dashboard()
        {
            var today = DateTime.UtcNow.Date;
            var startMonth = new DateTime(today.Year, today.Month, 1);
            var isAdmin = IsCurrentUserAdmin();
            var customerId = await GetSessionCustomerIdAsync(createIfMissing: false);

            if (!isAdmin && !customerId.HasValue)
            {
                return View(new DashboardViewModel());
            }

            IQueryable<Customer> customersQuery = _context.Customers.AsNoTracking();
            IQueryable<Invoice> invoicesQuery = _context.Invoices.AsNoTracking();
            IQueryable<Payment> paymentsQuery = _context.Payments.AsNoTracking();

            if (!isAdmin && customerId.HasValue)
            {
                customersQuery = customersQuery.Where(c => c.Id == customerId.Value);
                invoicesQuery = invoicesQuery.Where(i => i.CustomerId == customerId.Value);
                paymentsQuery = paymentsQuery.Where(p => _context.Invoices.Any(i => i.Id == p.InvoiceId && i.CustomerId == customerId.Value));
            }

            var vm = new DashboardViewModel
            {
                TotalCustomers = await customersQuery.CountAsync(),
                ActiveProducts = await _context.Products.CountAsync(p => p.IsActive),
                InvoicesIssued = await invoicesQuery.CountAsync(),
                OutstandingAmount = await invoicesQuery
                    .Where(i => i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled)
                    .Select(i => i.GrandTotal - ((decimal?)i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m))
                    .SumAsync(),
                PaidThisMonth = await paymentsQuery
                    .Where(p => p.PaidAt >= startMonth)
                    .SumAsync(p => p.Amount)
            };

            vm.OverdueInvoices = await invoicesQuery
                .CountAsync(i =>
                    i.DueDate < today &&
                    i.Status != InvoiceStatus.Paid &&
                    i.Status != InvoiceStatus.Cancelled);

            vm.OverdueAmount = await invoicesQuery
                .Where(i =>
                    i.DueDate < today &&
                    i.Status != InvoiceStatus.Paid &&
                    i.Status != InvoiceStatus.Cancelled)
                .Select(i => i.GrandTotal - ((decimal?)i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m))
                .SumAsync();

            vm.UpcomingInvoices = await invoicesQuery
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

            vm.OverdueInvoiceCards = await invoicesQuery
                .Include(i => i.Customer)
                .Where(i => i.Status != InvoiceStatus.Paid && i.Status != InvoiceStatus.Cancelled && i.DueDate < today)
                .OrderBy(i => i.DueDate)
                .Take(5)
                .Select(i => new InvoiceCard
                {
                    Id = i.Id,
                    Number = i.Number,
                    CustomerName = i.Customer != null ? i.Customer.Name : "-",
                    DueDate = i.DueDate,
                    Balance = i.GrandTotal - ((decimal?)i.Payments.Sum(p => (decimal?)p.Amount) ?? 0m),
                    Status = i.Status
                })
                .ToListAsync();

            vm.MonthlySummary = await invoicesQuery
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

            var displayName = User?.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = email.Split('@')[0];
            }

            var customer = new Customer
            {
                Name = displayName!,
                Email = email,
                CreatedAt = DateTime.UtcNow
            };
            _context.Customers.Add(customer);
            await _context.SaveChangesAsync();
            return customer.Id;
        }

        private string GetContactEmail()
        {
            var configured = _configuration["Landing:ContactEmail"];
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            var adminEmails = _configuration.GetSection("Administration:AdminEmails").Get<string[]>() ?? Array.Empty<string>();
            return adminEmails.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ?? "admin@keepbill.local";
        }
    }
}
