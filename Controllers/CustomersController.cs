using System;
using System.Linq;
using System.Threading.Tasks;
using KeepBill.Data;
using KeepBill.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KeepBill.Controllers
{
    [Authorize]
    public class CustomersController : Controller
    {
        private readonly ApplicationDbContext _context;

        public CustomersController(ApplicationDbContext context)
        {
            _context = context;
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

            return View(customer);
        }

        public IActionResult Create()
        {
            return View(new Customer());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Customer customer)
        {
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
    }
}
