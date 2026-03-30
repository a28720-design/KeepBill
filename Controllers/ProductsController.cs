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
    public class ProductsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ProductsController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index(string? search, string status = "active")
        {
            IQueryable<Product> query = _context.Products.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(p =>
                    EF.Functions.ILike(p.Name, $"%{term}%") ||
                    (p.Description != null && EF.Functions.ILike(p.Description, $"%{term}%")));
            }

            status = (status ?? "active").ToLowerInvariant();
            query = status switch
            {
                "inactive" => query.Where(p => !p.IsActive),
                "all" => query,
                _ => query.Where(p => p.IsActive)
            };

            var items = await query.OrderBy(p => p.Name).ToListAsync();

            ViewData["Search"] = search;
            ViewData["Status"] = status;
            ViewData["TotalProducts"] = await _context.Products.CountAsync();
            ViewData["ActiveProducts"] = await _context.Products.CountAsync(p => p.IsActive);
            ViewData["InactiveProducts"] = await _context.Products.CountAsync(p => !p.IsActive);
            return View(items);
        }

        public async Task<IActionResult> Details(Guid? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var product = await _context.Products
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id);
            if (product == null)
            {
                return NotFound();
            }

            return View(product);
        }

        public IActionResult Create()
        {
            return View(new Product());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Product product)
        {
            if (!ModelState.IsValid)
            {
                return View(product);
            }

            product.CreatedAt = DateTime.UtcNow;
            _context.Add(product);
            await _context.SaveChangesAsync();
            TempData["Toast"] = "Produto criado.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Edit(Guid? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var product = await _context.Products.FindAsync(id);
            if (product == null)
            {
                return NotFound();
            }
            return View(product);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Guid id, Product formModel)
        {
            if (id != formModel.Id)
            {
                return NotFound();
            }

            if (!ModelState.IsValid)
            {
                return View(formModel);
            }

            var product = await _context.Products.FindAsync(id);
            if (product == null)
            {
                return NotFound();
            }

            product.Name = formModel.Name;
            product.Description = formModel.Description;
            product.Unit = formModel.Unit;
            product.UnitPrice = formModel.UnitPrice;
            product.VatRate = formModel.VatRate;
            product.IsActive = formModel.IsActive;
            product.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            TempData["Toast"] = "Produto atualizado.";
            return RedirectToAction(nameof(Index));
        }

        public async Task<IActionResult> Delete(Guid? id)
        {
            if (id == null)
            {
                return NotFound();
            }

            var product = await _context.Products
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == id);
            if (product == null)
            {
                return NotFound();
            }

            return View(product);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(Guid id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null)
            {
                return NotFound();
            }

            _context.Products.Remove(product);
            await _context.SaveChangesAsync();
            TempData["Toast"] = "Produto eliminado.";
            return RedirectToAction(nameof(Index));
        }
    }
}
