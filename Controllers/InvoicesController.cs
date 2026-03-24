using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KeepBill.Data;
using KeepBill.Models;
using KeepBill.Models.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace KeepBill.Controllers
{
    [Authorize]
    public class InvoicesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public InvoicesController(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<IActionResult> Index(string? status)
        {
            var query = _context.Invoices
                .AsNoTracking()
                .Include(i => i.Customer)
                .OrderByDescending(i => i.IssueDate);

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<InvoiceStatus>(status, out var filter))
            {
                query = query.Where(i => i.Status == filter).OrderByDescending(i => i.IssueDate);
            }

            ViewData["SelectedStatus"] = status;
            ViewData["Statuses"] = Enum.GetNames(typeof(InvoiceStatus));

            var list = await query.ToListAsync();
            return View(list);
        }

        public async Task<IActionResult> Details(Guid? id)
        {
            if (id == null) return NotFound();

            var invoice = await _context.Invoices
                .Include(i => i.Customer)
                .Include(i => i.Lines)
                .ThenInclude(l => l.Product)
                .Include(i => i.Payments)
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == id);

            if (invoice == null) return NotFound();

            var paidAmount = invoice.Payments.Sum(p => p.Amount);
            ViewData["PaidAmount"] = paidAmount;
            ViewData["BalanceAmount"] = invoice.GrandTotal - paidAmount;

            return View(invoice);
        }

        public async Task<IActionResult> Create()
        {
            var vm = new InvoiceFormViewModel
            {
                Invoice = new Invoice
                {
                    Number = await GenerateNextNumberAsync(),
                    IssueDate = DateTime.UtcNow.Date,
                    DueDate = DateTime.UtcNow.Date.AddDays(15)
                }
            };
            EnsureMinimumLines(vm);
            await PopulateLookupsAsync();
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(InvoiceFormViewModel vm)
        {
            CleanLines(vm);

            if (!vm.Lines.Any())
            {
                ModelState.AddModelError(string.Empty, "Adicione pelo menos uma linha de faturação.");
            }

            if (!ModelState.IsValid)
            {
                EnsureMinimumLines(vm);
                await PopulateLookupsAsync();
                return View(vm);
            }

            vm.Invoice.Number = string.IsNullOrWhiteSpace(vm.Invoice.Number)
                ? await GenerateNextNumberAsync()
                : vm.Invoice.Number;

            vm.Invoice.CreatedAt = DateTime.UtcNow;
            vm.Invoice.UpdatedAt = DateTime.UtcNow;

            var lines = BuildInvoiceLines(vm.Lines);
            ApplyTotals(vm.Invoice, lines);
            vm.Invoice.Lines = lines;

            _context.Invoices.Add(vm.Invoice);
            await _context.SaveChangesAsync();
            TempData["Toast"] = "Fatura criada com sucesso.";
            return RedirectToAction(nameof(Details), new { id = vm.Invoice.Id });
        }

        public async Task<IActionResult> Edit(Guid? id)
        {
            if (id == null) return NotFound();

            var invoice = await _context.Invoices
                .Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (invoice == null) return NotFound();

            var vm = new InvoiceFormViewModel
            {
                Invoice = invoice,
                Lines = invoice.Lines
                    .OrderBy(l => l.Id)
                    .Select(l => new InvoiceLineInput
                    {
                        Id = l.Id,
                        ProductId = l.ProductId,
                        Description = l.Description,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice,
                        VatRate = l.VatRate
                    })
                    .ToList()
            };
            EnsureMinimumLines(vm);
            await PopulateLookupsAsync();
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Guid id, InvoiceFormViewModel vm)
        {
            if (id != vm.Invoice.Id) return NotFound();

            CleanLines(vm);

            if (!vm.Lines.Any())
            {
                ModelState.AddModelError(string.Empty, "Adicione pelo menos uma linha de faturação.");
            }

            if (!ModelState.IsValid)
            {
                EnsureMinimumLines(vm);
                await PopulateLookupsAsync();
                return View(vm);
            }

            var invoice = await _context.Invoices
                .Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == id);
            if (invoice == null) return NotFound();

            invoice.CustomerId = vm.Invoice.CustomerId;
            invoice.IssueDate = vm.Invoice.IssueDate;
            invoice.DueDate = vm.Invoice.DueDate;
            invoice.Status = vm.Invoice.Status;
            invoice.Notes = vm.Invoice.Notes;
            invoice.Currency = vm.Invoice.Currency;
            invoice.UpdatedAt = DateTime.UtcNow;

            _context.InvoiceLines.RemoveRange(invoice.Lines);

            var lines = BuildInvoiceLines(vm.Lines);
            ApplyTotals(invoice, lines);
            invoice.Lines = lines;

            await _context.SaveChangesAsync();
            TempData["Toast"] = "Fatura atualizada.";
            return RedirectToAction(nameof(Details), new { id = invoice.Id });
        }

        private void EnsureMinimumLines(InvoiceFormViewModel vm, int count = 4)
        {
            while (vm.Lines.Count < count)
            {
                vm.Lines.Add(new InvoiceLineInput());
            }
        }

        private void CleanLines(InvoiceFormViewModel vm)
        {
            vm.Lines = vm.Lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Description))
                .ToList();
        }

        private static List<InvoiceLine> BuildInvoiceLines(IEnumerable<InvoiceLineInput> inputs)
        {
            var result = new List<InvoiceLine>();
            foreach (var line in inputs)
            {
                var amount = line.Quantity * line.UnitPrice;
                var vatAmount = amount * (line.VatRate / 100m);
                result.Add(new InvoiceLine
                {
                    ProductId = line.ProductId,
                    Description = line.Description!.Trim(),
                    Quantity = line.Quantity,
                    UnitPrice = line.UnitPrice,
                    VatRate = line.VatRate,
                    LineTotal = decimal.Round(amount + vatAmount, 2)
                });
            }
            return result;
        }

        private static void ApplyTotals(Invoice invoice, List<InvoiceLine> lines)
        {
            var subtotal = lines.Sum(l => l.Quantity * l.UnitPrice);
            var vat = lines.Sum(l => (l.Quantity * l.UnitPrice) * (l.VatRate / 100m));
            invoice.Subtotal = decimal.Round(subtotal, 2);
            invoice.VatTotal = decimal.Round(vat, 2);
            invoice.GrandTotal = invoice.Subtotal + invoice.VatTotal;
        }

        private async Task PopulateLookupsAsync()
        {
            var customers = await _context.Customers
                .OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync();
            var products = await _context.Products
                .Where(p => p.IsActive)
                .OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name, p.UnitPrice, p.VatRate })
                .ToListAsync();

            ViewBag.Customers = new SelectList(customers, "Id", "Name");
            ViewBag.Products = new SelectList(products, "Id", "Name");
            ViewBag.Statuses = new SelectList(Enum.GetValues(typeof(InvoiceStatus)));
        }

        private async Task<string> GenerateNextNumberAsync()
        {
            var prefix = DateTime.UtcNow.ToString("yyyyMMdd");
            var lastNumber = await _context.Invoices
                .Where(i => i.Number.StartsWith($"KB-{prefix}"))
                .OrderByDescending(i => i.Number)
                .Select(i => i.Number)
                .FirstOrDefaultAsync();

            if (lastNumber == null)
            {
                return $"KB-{prefix}-001";
            }

            var sequencePart = lastNumber.Split('-').Last();
            if (!int.TryParse(sequencePart, out var seq))
            {
                seq = 0;
            }
            return $"KB-{prefix}-{(seq + 1):000}";
        }

        [HttpGet]
        public async Task<IActionResult> ExportCsv(string? status)
        {
            var query = _context.Invoices
                .AsNoTracking()
                .Include(i => i.Customer)
                .Include(i => i.Payments)
                .OrderByDescending(i => i.IssueDate);

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<InvoiceStatus>(status, out var filter))
            {
                query = query.Where(i => i.Status == filter).OrderByDescending(i => i.IssueDate);
            }

            var invoices = await query.ToListAsync();
            var sb = new StringBuilder();
            sb.AppendLine("Numero,Cliente,Emissao,Vencimento,Estado,Total,Pago,Saldo");
            foreach (var invoice in invoices)
            {
                var paid = invoice.Payments.Sum(p => p.Amount);
                var balance = invoice.GrandTotal - paid;
                sb.AppendLine(string.Join(',',
                    Escape(invoice.Number),
                    Escape(invoice.Customer?.Name ?? string.Empty),
                    invoice.IssueDate.ToString("yyyy-MM-dd"),
                    invoice.DueDate.ToString("yyyy-MM-dd"),
                    invoice.Status,
                    invoice.GrandTotal.ToString("0.00"),
                    paid.ToString("0.00"),
                    balance.ToString("0.00")));
            }

            var fileName = $"faturas_{DateTime.UtcNow:yyyyMMddHHmm}.csv";
            return File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", fileName);
        }

        private static string Escape(string value)
        {
            if (value.Contains(',') || value.Contains('"'))
            {
                return $"\"{value.Replace("\"", "\"\"")}\"";
            }
            return value;
        }
    }
}
