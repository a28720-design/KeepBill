using System;
using System.Linq;
using System.Threading.Tasks;
using KeepBill.Data;
using KeepBill.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace KeepBill.Controllers
{
    [Authorize]
    public class PaymentsController : Controller
    {
        private readonly ApplicationDbContext _context;

        public PaymentsController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Create(Guid? invoiceId)
        {
            if (invoiceId == null) return NotFound();

            var invoice = await _context.Invoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId);
            if (invoice == null) return NotFound();

            ViewData["Invoice"] = invoice;
            ViewBag.Methods = new SelectList(Enum.GetValues(typeof(PaymentMethod)));
            return View(new Payment { InvoiceId = invoice.Id, Amount = invoice.GrandTotal - await GetPaidAmountAsync(invoice.Id) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Payment payment)
        {
            var invoice = await _context.Invoices.FirstOrDefaultAsync(i => i.Id == payment.InvoiceId);
            if (invoice == null) return NotFound();

            if (!ModelState.IsValid)
            {
                ViewData["Invoice"] = invoice;
                ViewBag.Methods = new SelectList(Enum.GetValues(typeof(PaymentMethod)));
                return View(payment);
            }

            _context.Payments.Add(payment);
            await _context.SaveChangesAsync();
            TempData["Toast"] = "Pagamento registado.";
            return RedirectToAction("Details", "Invoices", new { id = payment.InvoiceId });
        }

        public async Task<IActionResult> Delete(Guid? id)
        {
            if (id == null) return NotFound();

            var payment = await _context.Payments
                .Include(p => p.Invoice)
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == id);
            if (payment == null) return NotFound();

            return View(payment);
        }

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(Guid id)
        {
            var payment = await _context.Payments.FindAsync(id);
            if (payment == null) return NotFound();

            var invoiceId = payment.InvoiceId;
            _context.Payments.Remove(payment);
            await _context.SaveChangesAsync();
            TempData["Toast"] = "Pagamento removido.";
            return RedirectToAction("Details", "Invoices", new { id = invoiceId });
        }

        private async Task<decimal> GetPaidAmountAsync(Guid invoiceId)
        {
            return await _context.Payments
                .Where(p => p.InvoiceId == invoiceId)
                .SumAsync(p => p.Amount);
        }
    }
}
