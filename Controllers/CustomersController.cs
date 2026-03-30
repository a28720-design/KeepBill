using System;
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
    public class CustomersController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;

        public CustomersController(ApplicationDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        public async Task<IActionResult> Index(string? search)
        {
            var query = _context.Customers.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(c =>
                    EF.Functions.ILike(c.Name, $"%{term}%") ||
                    (c.TaxId != null && EF.Functions.ILike(c.TaxId, $"%{term}%")) ||
                    (c.Email != null && EF.Functions.ILike(c.Email, $"%{term}%")));
            }

            var customers = await query.OrderBy(c => c.Name).ToListAsync();
            ViewData["Search"] = search;
            ViewData["IsAdmin"] = IsCurrentUserAdmin();
            return View(customers);
        }

        public async Task<IActionResult> Details(Guid? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var customer = await _context.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id);
            if (customer == null)
            {
                return NotFound();
            }

            var recentInvoices = await _context.Invoices
                .AsNoTracking()
                .Include(i => i.Payments)
                .Where(i => i.CustomerId == customer.Id)
                .OrderByDescending(i => i.IssueDate)
                .Take(8)
                .Select(i => new CustomerInvoiceSummary
                {
                    Id = i.Id,
                    Number = i.Number,
                    IssueDate = i.IssueDate,
                    DueDate = i.DueDate,
                    Status = i.Status,
                    Total = i.GrandTotal,
                    Paid = i.Payments.Sum(p => p.Amount),
                    Balance = i.GrandTotal - i.Payments.Sum(p => p.Amount)
                })
                .ToListAsync();

            var totals = await _context.Invoices
                .AsNoTracking()
                .Include(i => i.Payments)
                .Where(i => i.CustomerId == customer.Id)
                .Select(i => new
                {
                    i.GrandTotal,
                    Paid = i.Payments.Sum(p => p.Amount)
                })
                .ToListAsync();

            var vm = new CustomerDetailsViewModel
            {
                Customer = customer,
                TotalInvoices = totals.Count,
                TotalInvoiced = totals.Sum(x => x.GrandTotal),
                TotalPaid = totals.Sum(x => x.Paid),
                OutstandingBalance = totals.Sum(x => x.GrandTotal - x.Paid),
                RecentInvoices = recentInvoices
            };

            return View(vm);
        }

        public IActionResult Create()
        {
            if (!IsCurrentUserAdmin()) return Forbid();
            return View(new Customer());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Customer customer)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            if (!ModelState.IsValid)
            {
                return View(customer);
            }

            customer.CreatedAt = DateTime.UtcNow;
            _context.Add(customer);
            await _context.SaveChangesAsync();
            TempData["Toast"] = "Cliente criado com sucesso.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(Guid? id)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            if (id == null)
            {
                return NotFound();
            }

            var customer = await _context.Customers.FindAsync(id);
            if (customer == null)
            {
                return NotFound();
            }
            return View(customer);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Guid id, Customer formModel)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            if (id != formModel.Id)
            {
                return NotFound();
            }

            if (!ModelState.IsValid)
            {
                return View(formModel);
            }

            var customer = await _context.Customers.FindAsync(id);
            if (customer == null)
            {
                return NotFound();
            }

            customer.Name = formModel.Name;
            customer.TaxId = formModel.TaxId;
            customer.Email = formModel.Email;
            customer.Phone = formModel.Phone;
            customer.BillingAddress = formModel.BillingAddress;
            customer.City = formModel.City;
            customer.Country = formModel.Country;
            customer.Notes = formModel.Notes;
            customer.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            TempData["Toast"] = "Cliente atualizado.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Delete(Guid? id)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            if (id == null)
            {
                return NotFound();
            }

            var customer = await _context.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id);
            if (customer == null)
            {
                return NotFound();
            }

            return View(customer);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(Guid id)
        {
            if (!IsCurrentUserAdmin()) return Forbid();

            var customer = await _context.Customers.FindAsync(id);
            if (customer == null)
            {
                return NotFound();
            }

            _context.Customers.Remove(customer);
            await _context.SaveChangesAsync();
            TempData["Toast"] = "Cliente eliminado.";
            return RedirectToAction(nameof(Index));
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
    }
}
