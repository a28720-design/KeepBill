using System;
using System.Linq;
using System.Threading.Tasks;
using KeepBill.Data;
using KeepBill.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Text;

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

        public async Task<IActionResult> Index(string? search, string? method, DateTime? from, DateTime? to)
        {
            IQueryable<Payment> query = _context.Payments
                .AsNoTracking()
                .Include(p => p.Invoice!)
                .ThenInclude(i => i.Customer);

            query = ApplyPaymentFilters(query, search, method, from, to);

            var payments = await query
                .OrderByDescending(p => p.PaidAt)
                .ToListAsync();

            ViewData["Search"] = search;
            ViewData["SelectedMethod"] = method;
            ViewData["From"] = from?.ToString("yyyy-MM-dd");
            ViewData["To"] = to?.ToString("yyyy-MM-dd");
            ViewBag.Methods = new SelectList(Enum.GetNames(typeof(PaymentMethod)));
            ViewData["PaymentCount"] = payments.Count;
            ViewData["PaymentTotal"] = payments.Sum(p => p.Amount);
            return View(payments);
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv(string? search, string? method, DateTime? from, DateTime? to)
        {
            IQueryable<Payment> query = _context.Payments
                .AsNoTracking()
                .Include(p => p.Invoice!)
                .ThenInclude(i => i.Customer);

            query = ApplyPaymentFilters(query, search, method, from, to);
            var payments = await query
                .OrderByDescending(p => p.PaidAt)
                .ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Data,Fatura,Cliente,Metodo,Referencia,Valor");
            foreach (var payment in payments)
            {
                sb.AppendLine(string.Join(',',
                    payment.PaidAt.ToString("yyyy-MM-dd"),
                    Escape(payment.Invoice?.Number ?? string.Empty),
                    Escape(payment.Invoice?.Customer?.Name ?? string.Empty),
                    payment.Method,
                    Escape(payment.Reference ?? string.Empty),
                    payment.Amount.ToString("0.00")));
            }

            var fileName = $"pagamentos_{DateTime.UtcNow:yyyyMMddHHmm}.csv";
            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", fileName);
        }

        public async Task<IActionResult> Create(Guid? invoiceId)
        {
            if (invoiceId == null) return NotFound();

            var invoice = await _context.Invoices
                .AsNoTracking()
                .Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.Id == invoiceId);
            if (invoice == null) return NotFound();

            var paidAmount = await GetPaidAmountAsync(invoice.Id);
            var remaining = invoice.GrandTotal - paidAmount;
            if (invoice.Status == InvoiceStatus.Cancelled)
            {
                TempData["Toast"] = "Não é possível registar pagamentos em faturas canceladas.";
                return RedirectToAction("Details", "Invoices", new { id = invoice.Id });
            }

            ViewData["Invoice"] = invoice;
            ViewData["RemainingAmount"] = remaining > 0m ? remaining : 0m;
            ViewBag.Methods = new SelectList(Enum.GetValues(typeof(PaymentMethod)));
            return View(new Payment { InvoiceId = invoice.Id, Amount = remaining > 0m ? remaining : 0m });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(Payment payment)
        {
            var invoice = await _context.Invoices
                .Include(i => i.Customer)
                .FirstOrDefaultAsync(i => i.Id == payment.InvoiceId);
            if (invoice == null) return NotFound();

            var paidAmount = await GetPaidAmountAsync(payment.InvoiceId);
            var remaining = invoice.GrandTotal - paidAmount;
            if (invoice.Status == InvoiceStatus.Cancelled)
            {
                ModelState.AddModelError(string.Empty, "Não é possível registar pagamentos em faturas canceladas.");
            }

            if (payment.Amount <= 0)
            {
                ModelState.AddModelError(nameof(Payment.Amount), "O valor deve ser superior a zero.");
            }

            if (remaining <= 0m)
            {
                ModelState.AddModelError(nameof(Payment.Amount), "Esta fatura já está totalmente paga.");
            }
            else if (payment.Amount > remaining)
            {
                ModelState.AddModelError(nameof(Payment.Amount), $"O valor excede o saldo em falta ({remaining:C}).");
            }

            if (!ModelState.IsValid)
            {
                ViewData["Invoice"] = invoice;
                ViewData["RemainingAmount"] = remaining > 0m ? remaining : 0m;
                ViewBag.Methods = new SelectList(Enum.GetValues(typeof(PaymentMethod)));
                return View(payment);
            }

            _context.Payments.Add(payment);
            await _context.SaveChangesAsync();
            await UpdateInvoiceStatusAsync(invoice.Id);
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
            await UpdateInvoiceStatusAsync(invoiceId);
            TempData["Toast"] = "Pagamento removido.";
            return RedirectToAction("Details", "Invoices", new { id = invoiceId });
        }

        private async Task<decimal> GetPaidAmountAsync(Guid invoiceId)
        {
            return await _context.Payments
                .Where(p => p.InvoiceId == invoiceId)
                .SumAsync(p => p.Amount);
        }

        private static string Escape(string value)
        {
            if (value.Contains(',') || value.Contains('"'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return value;
        }

        private IQueryable<Payment> ApplyPaymentFilters(
            IQueryable<Payment> query,
            string? search,
            string? method,
            DateTime? from,
            DateTime? to)
        {
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(p =>
                    (p.Reference != null && EF.Functions.ILike(p.Reference, $"%{term}%")) ||
                    EF.Functions.ILike(p.Invoice!.Number, $"%{term}%") ||
                    EF.Functions.ILike(p.Invoice!.Customer!.Name, $"%{term}%"));
            }

            if (!string.IsNullOrWhiteSpace(method) && Enum.TryParse<PaymentMethod>(method, out var methodFilter))
            {
                query = query.Where(p => p.Method == methodFilter);
            }

            if (from.HasValue)
            {
                query = query.Where(p => p.PaidAt >= from.Value.Date);
            }

            if (to.HasValue)
            {
                query = query.Where(p => p.PaidAt <= to.Value.Date);
            }

            return query;
        }

        private async Task UpdateInvoiceStatusAsync(Guid invoiceId)
        {
            var invoice = await _context.Invoices
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == invoiceId);

            if (invoice == null || invoice.Status == InvoiceStatus.Cancelled)
            {
                return;
            }

            var paidAmount = invoice.Payments.Sum(p => p.Amount);
            if (paidAmount <= 0)
            {
                if (invoice.Status != InvoiceStatus.Draft)
                {
                    invoice.Status = InvoiceStatus.Issued;
                }
            }
            else if (paidAmount < invoice.GrandTotal)
            {
                invoice.Status = InvoiceStatus.PartiallyPaid;
            }
            else
            {
                invoice.Status = InvoiceStatus.Paid;
            }

            await _context.SaveChangesAsync();
        }
    }
}
