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

        public async Task<IActionResult> Index(bool? showInactive)
        {
            var includeInactive = showInactive ?? false;
            var items = await _context.Products
                .AsNoTracking()
                .Where(p => includeInactive || p.IsActive)
                .OrderBy(p => p.Name)
                .ToListAsync();

            ViewData["ShowInactive"] = includeInactive;
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
