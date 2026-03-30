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
using Microsoft.Extensions.Configuration;
using KeepBill.Services;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace KeepBill.Controllers
{
    [Authorize]
    public class InvoicesController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly IConfiguration _configuration;
        private readonly IEmailInvoiceScannerService _emailInvoiceScannerService;
        private readonly IUserEmailInboxSettingsService _userEmailInboxSettingsService;

        public InvoicesController(
            ApplicationDbContext context,
            IConfiguration configuration,
            IEmailInvoiceScannerService emailInvoiceScannerService,
            IUserEmailInboxSettingsService userEmailInboxSettingsService)
        {
            _context = context;
            _configuration = configuration;
            _emailInvoiceScannerService = emailInvoiceScannerService;
            _userEmailInboxSettingsService = userEmailInboxSettingsService;
        }

        public async Task<IActionResult> Index(string? status, string? source, string? search, DateTime? dueFrom, DateTime? dueTo, int? dueSoonDays, bool overdueOnly = false)
        {
            IQueryable<Invoice> query = _context.Invoices
                .AsNoTracking()
                .Include(i => i.Customer)
                .Include(i => i.Payments);

            query = await ApplyOwnershipScopeAsync(query);
            query = ApplyInvoiceFilters(query, status, source, search, dueFrom, dueTo, dueSoonDays, overdueOnly);

            ViewData["SelectedStatus"] = status;
            ViewData["SelectedSource"] = source;
            ViewData["Search"] = search;
            ViewData["DueFrom"] = dueFrom?.ToString("yyyy-MM-dd");
            ViewData["DueTo"] = dueTo?.ToString("yyyy-MM-dd");
            ViewData["DueSoonDays"] = dueSoonDays;
            ViewData["OverdueOnly"] = overdueOnly;
            ViewData["Statuses"] = Enum.GetNames(typeof(InvoiceStatus));
            var lastSync = _emailInvoiceScannerService.GetLastResult();
            if (TempData["EmailLastSyncUtc"] is string tempLastSync && DateTime.TryParse(tempLastSync, out var parsedSync))
            {
                ViewData["EmailLastSyncUtc"] = parsedSync;
            }
            else
            {
                ViewData["EmailLastSyncUtc"] = lastSync.LastSyncUtc;
            }

            ViewData["EmailLastError"] = TempData["EmailLastError"] as string ?? lastSync.LastError;
            ViewData["EmailImported"] = TempData["EmailImported"] is string tempImported && int.TryParse(tempImported, out var parsedImported)
                ? parsedImported
                : lastSync.TotalInvoicesImported;
            ViewData["EmailSkipped"] = TempData["EmailSkipped"] is string tempSkipped && int.TryParse(tempSkipped, out var parsedSkipped)
                ? parsedSkipped
                : lastSync.TotalInvoicesSkipped;

            var emailOptions = await _userEmailInboxSettingsService.GetAsync(User);
            ViewData["EmailConfigured"] = !string.IsNullOrWhiteSpace(emailOptions.Host)
                                          && !string.IsNullOrWhiteSpace(emailOptions.Username)
                                          && !string.IsNullOrWhiteSpace(emailOptions.Password);
            ViewData["EmailUsername"] = emailOptions.Username;
            ViewData["EmailHost"] = emailOptions.Host;

            var list = await query.ToListAsync();
            return View(list);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveEmailSettings(string username, string password)
        {
            var current = await _userEmailInboxSettingsService.GetAsync(User);
            if (string.IsNullOrWhiteSpace(username))
            {
                TempData["Toast"] = "Indica o email da tua caixa de correio.";
                return RedirectToAction(nameof(Index));
            }

            current.Username = username.Trim();
            if (!string.IsNullOrWhiteSpace(password))
            {
                current.Password = password.Trim();
            }
            ApplyProviderDefaults(current);

            await _userEmailInboxSettingsService.SaveAsync(User, current);
            var options = await _userEmailInboxSettingsService.GetAsync(User);
            var ownerCustomerId = await GetSessionCustomerIdAsync(createIfMissing: true);
            var result = await _emailInvoiceScannerService.ScanAsync(options, ownerCustomerId);
            TempData["EmailLastSyncUtc"] = (result.LastSyncUtc ?? DateTime.UtcNow).ToString("o");
            TempData["EmailImported"] = result.TotalInvoicesImported.ToString();
            TempData["EmailSkipped"] = result.TotalInvoicesSkipped.ToString();
            TempData["EmailLastError"] = result.LastError ?? string.Empty;

            if (!result.IsConfigured)
            {
                TempData["Toast"] = "A configuracao da tua caixa de email ainda nao esta completa.";
                return RedirectToAction(nameof(Index));
            }

            if (!string.IsNullOrWhiteSpace(result.LastError))
            {
                TempData["Toast"] = $"Erro na sincronizacao: {result.LastError}";
                return RedirectToAction(nameof(Index));
            }

            TempData["Toast"] = $"Configuracao guardada e sincronizacao concluida: {result.TotalInvoicesImported} novas faturas, {result.TotalInvoicesSkipped} ignoradas (duplicadas).";
            return RedirectToAction(nameof(Index));
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
            if (!await CanAccessInvoiceAsync(invoice))
            {
                return Forbid();
            }

            var paidAmount = invoice.Payments.Sum(p => p.Amount);
            ViewData["PaidAmount"] = paidAmount;
            ViewData["BalanceAmount"] = invoice.GrandTotal - paidAmount;

            return View(invoice);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateStatus(Guid id, InvoiceStatus status)
        {
            var invoice = await _context.Invoices
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == id);
            if (invoice == null) return NotFound();
            if (!await CanAccessInvoiceAsync(invoice))
            {
                return Forbid();
            }

            var paid = invoice.Payments.Sum(p => p.Amount);
            if (status == InvoiceStatus.Draft && paid > 0m)
            {
                TempData["Toast"] = "Nao pode marcar como Draft uma fatura com pagamentos.";
                return RedirectToAction(nameof(Details), new { id });
            }

            if (status == InvoiceStatus.Paid && paid < invoice.GrandTotal)
            {
                TempData["Toast"] = "Nao pode marcar como paga sem liquidar o valor total.";
                return RedirectToAction(nameof(Details), new { id });
            }

            if (status == InvoiceStatus.Cancelled && paid > 0m)
            {
                TempData["Toast"] = "Nao pode cancelar uma fatura com pagamentos registados.";
                return RedirectToAction(nameof(Details), new { id });
            }

            invoice.Status = status;
            invoice.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            TempData["Toast"] = "Estado da fatura atualizado.";
            return RedirectToAction(nameof(Details), new { id });
        }

        [HttpGet]
        public async Task<IActionResult> ExportPdf(Guid id)
        {
            var invoice = await _context.Invoices
                .AsNoTracking()
                .Include(i => i.Customer)
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (invoice == null) return NotFound();
            if (!await CanAccessInvoiceAsync(invoice))
            {
                return Forbid();
            }

            var paidAmount = invoice.Payments.Sum(p => p.Amount);
            var balance = invoice.GrandTotal - paidAmount;

            var bytes = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(28);
                    page.Size(PageSizes.A4);
                    page.DefaultTextStyle(x => x.FontSize(10));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("KEEPBILL").SemiBold().FontSize(18).FontColor(Colors.Blue.Darken2);
                        col.Item().Text($"Fatura {invoice.Number}").FontSize(12);
                        col.Item().Text($"Emitida: {invoice.IssueDate:dd/MM/yyyy}   Vencimento: {invoice.DueDate:dd/MM/yyyy}");
                    });

                    page.Content().Column(col =>
                    {
                        col.Spacing(12);
                        col.Item().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(customerCol =>
                        {
                            customerCol.Item().Text("Cliente").SemiBold();
                            customerCol.Item().Text(invoice.Customer?.Name ?? "-");
                            customerCol.Item().Text(invoice.Customer?.TaxId ?? "-");
                            customerCol.Item().Text(invoice.Customer?.Email ?? "-");
                        });

                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c =>
                            {
                                c.RelativeColumn(4);
                                c.RelativeColumn(1);
                                c.RelativeColumn(1.4f);
                                c.RelativeColumn(1);
                                c.RelativeColumn(1.4f);
                            });

                            table.Header(h =>
                            {
                                h.Cell().Text("Descricao").SemiBold();
                                h.Cell().AlignRight().Text("Qtd").SemiBold();
                                h.Cell().AlignRight().Text("Preco").SemiBold();
                                h.Cell().AlignRight().Text("IVA").SemiBold();
                                h.Cell().AlignRight().Text("Total").SemiBold();
                            });

                            foreach (var line in invoice.Lines)
                            {
                                table.Cell().Text(line.Description);
                                table.Cell().AlignRight().Text(line.Quantity.ToString("0.##"));
                                table.Cell().AlignRight().Text(line.UnitPrice.ToString("0.00"));
                                table.Cell().AlignRight().Text($"{line.VatRate:0.##}%");
                                table.Cell().AlignRight().Text(line.LineTotal.ToString("0.00"));
                            }
                        });

                        col.Item().AlignRight().Column(totals =>
                        {
                            totals.Item().Text($"Subtotal: {invoice.Subtotal:0.00} {invoice.Currency}");
                            totals.Item().Text($"IVA: {invoice.VatTotal:0.00} {invoice.Currency}");
                            totals.Item().Text($"Total: {invoice.GrandTotal:0.00} {invoice.Currency}").SemiBold();
                            totals.Item().Text($"Pago: {paidAmount:0.00} {invoice.Currency}");
                            totals.Item().Text($"Em falta: {balance:0.00} {invoice.Currency}").SemiBold();
                        });
                    });

                    page.Footer().AlignCenter().Text(x =>
                    {
                        x.Span($"Documento gerado em {DateTime.UtcNow:dd/MM/yyyy HH:mm} UTC");
                    });
                });
            }).GeneratePdf();

            var fileName = $"fatura_{invoice.Number}.pdf";
            return File(bytes, "application/pdf", fileName);
        }

        public async Task<IActionResult> Create()
        {
            var isAdmin = IsCurrentUserAdmin();
            var vm = new InvoiceFormViewModel
            {
                Invoice = new Invoice
                {
                    Number = await GenerateNextNumberAsync(),
                    IssueDate = DateTime.UtcNow.Date,
                    DueDate = DateTime.UtcNow.Date.AddDays(15)
                }
            };

            if (!isAdmin)
            {
                var customer = await GetOrCreateSessionCustomerAsync();
                vm.Invoice.CustomerId = customer.Id;
                ViewData["LockCustomer"] = true;
                ViewData["LockedCustomerName"] = customer.Name;
            }

            EnsureMinimumLines(vm);
            await PopulateLookupsAsync(isAdmin ? null : vm.Invoice.CustomerId);
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(InvoiceFormViewModel vm)
        {
            var isAdmin = IsCurrentUserAdmin();
            if (!isAdmin)
            {
                var customer = await GetOrCreateSessionCustomerAsync();
                vm.Invoice.CustomerId = customer.Id;
                ViewData["LockCustomer"] = true;
                ViewData["LockedCustomerName"] = customer.Name;
            }

            CleanLines(vm);
            ValidateInvoiceDates(vm.Invoice);

            if (!vm.Lines.Any())
            {
                ModelState.AddModelError(string.Empty, "Adicione pelo menos uma linha de faturação.");
            }

            if (!ModelState.IsValid)
            {
                EnsureMinimumLines(vm);
                await PopulateLookupsAsync(isAdmin ? null : vm.Invoice.CustomerId);
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
            var isAdmin = IsCurrentUserAdmin();

            var invoice = await _context.Invoices
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == id);

            if (invoice == null) return NotFound();

            if (!isAdmin)
            {
                var currentCustomerId = await GetSessionCustomerIdAsync(createIfMissing: false);
                if (!currentCustomerId.HasValue || invoice.CustomerId != currentCustomerId.Value)
                {
                    return Forbid();
                }

                ViewData["LockCustomer"] = true;
                ViewData["LockedCustomerName"] = (await _context.Customers.FindAsync(invoice.CustomerId))?.Name ?? "Cliente";
            }

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
            await PopulateLookupsAsync(isAdmin ? null : vm.Invoice.CustomerId);
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(Guid id, InvoiceFormViewModel vm)
        {
            if (id != vm.Invoice.Id) return NotFound();
            var isAdmin = IsCurrentUserAdmin();

            if (!isAdmin)
            {
                var currentCustomerId = await GetSessionCustomerIdAsync(createIfMissing: true);
                if (!currentCustomerId.HasValue)
                {
                    return Forbid();
                }
                vm.Invoice.CustomerId = currentCustomerId.Value;
            }

            CleanLines(vm);
            ValidateInvoiceDates(vm.Invoice);

            if (!vm.Lines.Any())
            {
                ModelState.AddModelError(string.Empty, "Adicione pelo menos uma linha de faturação.");
            }

            if (!ModelState.IsValid)
            {
                if (!isAdmin)
                {
                    ViewData["LockCustomer"] = true;
                    ViewData["LockedCustomerName"] = (await _context.Customers.FindAsync(vm.Invoice.CustomerId))?.Name ?? "Cliente";
                }
                EnsureMinimumLines(vm);
                await PopulateLookupsAsync(isAdmin ? null : vm.Invoice.CustomerId);
                return View(vm);
            }

            var invoice = await _context.Invoices
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == id);
            if (invoice == null) return NotFound();

            if (!isAdmin)
            {
                var currentCustomerId = await GetSessionCustomerIdAsync(createIfMissing: false);
                if (!currentCustomerId.HasValue || invoice.CustomerId != currentCustomerId.Value)
                {
                    return Forbid();
                }
                vm.Invoice.CustomerId = currentCustomerId.Value;
            }

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
            RecalculateStatusFromPayments(invoice);

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

        private void ValidateInvoiceDates(Invoice invoice)
        {
            if (invoice.DueDate.Date < invoice.IssueDate.Date)
            {
                ModelState.AddModelError(nameof(invoice.DueDate), "A data de vencimento deve ser igual ou posterior à data de emissão.");
            }
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

        private static void RecalculateStatusFromPayments(Invoice invoice)
        {
            if (invoice.Status == InvoiceStatus.Cancelled)
            {
                return;
            }

            var paidAmount = invoice.Payments.Sum(p => p.Amount);
            if (paidAmount <= 0m)
            {
                invoice.Status = invoice.Status == InvoiceStatus.Draft ? InvoiceStatus.Draft : InvoiceStatus.Issued;
            }
            else if (paidAmount < invoice.GrandTotal)
            {
                invoice.Status = InvoiceStatus.PartiallyPaid;
            }
            else
            {
                invoice.Status = InvoiceStatus.Paid;
            }
        }

        private async Task PopulateLookupsAsync(Guid? onlyCustomerId = null)
        {
            IQueryable<Customer> customerQuery = _context.Customers;
            if (onlyCustomerId.HasValue)
            {
                customerQuery = customerQuery.Where(c => c.Id == onlyCustomerId.Value);
            }

            var customers = await customerQuery
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
        public async Task<IActionResult> ExportCsv(string? status, string? source, string? search, DateTime? dueFrom, DateTime? dueTo, int? dueSoonDays, bool overdueOnly = false)
        {
            IQueryable<Invoice> query = _context.Invoices
                .AsNoTracking()
                .Include(i => i.Customer)
                .Include(i => i.Payments);

            query = await ApplyOwnershipScopeAsync(query);
            query = ApplyInvoiceFilters(query, status, source, search, dueFrom, dueTo, dueSoonDays, overdueOnly);

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

        private IQueryable<Invoice> ApplyInvoiceFilters(
            IQueryable<Invoice> query,
            string? status,
            string? source,
            string? search,
            DateTime? dueFrom,
            DateTime? dueTo,
            int? dueSoonDays,
            bool overdueOnly)
        {
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<InvoiceStatus>(status, out var statusFilter))
            {
                query = query.Where(i => i.Status == statusFilter);
            }

            if (!string.IsNullOrWhiteSpace(source))
            {
                var normalized = source.Trim().ToLowerInvariant();
                if (normalized == "email")
                {
                    query = query.Where(i => i.Number.StartsWith("EML-"));
                }
                else if (normalized == "manual")
                {
                    query = query.Where(i => !i.Number.StartsWith("EML-"));
                }
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(i =>
                    EF.Functions.ILike(i.Number, $"%{term}%") ||
                    EF.Functions.ILike(i.Customer!.Name, $"%{term}%"));
            }

            if (dueFrom.HasValue)
            {
                query = query.Where(i => i.DueDate >= dueFrom.Value.Date);
            }

            if (dueTo.HasValue)
            {
                query = query.Where(i => i.DueDate <= dueTo.Value.Date);
            }

            if (dueSoonDays.HasValue && dueSoonDays.Value > 0)
            {
                var today = DateTime.UtcNow.Date;
                var end = today.AddDays(dueSoonDays.Value);
                query = query.Where(i =>
                    i.DueDate >= today &&
                    i.DueDate <= end &&
                    i.Status != InvoiceStatus.Paid &&
                    i.Status != InvoiceStatus.Cancelled);
            }

            if (overdueOnly)
            {
                var today = DateTime.UtcNow.Date;
                query = query.Where(i =>
                    i.DueDate < today &&
                    i.Status != InvoiceStatus.Paid &&
                    i.Status != InvoiceStatus.Cancelled);
            }

            return query.OrderByDescending(i => i.IssueDate);
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

            var customer = await GetOrCreateSessionCustomerAsync();
            return customer.Id;
        }

        private async Task<Customer> GetOrCreateSessionCustomerAsync()
        {
            var email = User?.Identity?.Name;
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new InvalidOperationException("Sessão sem email associado.");
            }

            var customer = await _context.Customers
                .FirstOrDefaultAsync(c => c.Email != null && c.Email.ToLower() == email.ToLower());

            if (customer != null)
            {
                return customer;
            }

            var displayName = User?.Claims.FirstOrDefault(c => c.Type == "name")?.Value;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                displayName = email.Split('@')[0];
            }

            customer = new Customer
            {
                Name = displayName!,
                Email = email,
                CreatedAt = DateTime.UtcNow
            };
            _context.Customers.Add(customer);
            await _context.SaveChangesAsync();
            return customer;
        }

        private async Task<IQueryable<Invoice>> ApplyOwnershipScopeAsync(IQueryable<Invoice> query)
        {
            if (IsCurrentUserAdmin())
            {
                return query;
            }

            var customerId = await GetSessionCustomerIdAsync(createIfMissing: false);
            if (!customerId.HasValue)
            {
                return query.Where(i => false);
            }

            return query.Where(i => i.CustomerId == customerId.Value);
        }

        private async Task<bool> CanAccessInvoiceAsync(Invoice invoice)
        {
            if (IsCurrentUserAdmin())
            {
                return true;
            }

            var customerId = await GetSessionCustomerIdAsync(createIfMissing: false);
            return customerId.HasValue && invoice.CustomerId == customerId.Value;
        }

        private static void ApplyProviderDefaults(EmailInboxOptions options)
        {
            var email = options.Username?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!email.Contains('@'))
            {
                return;
            }

            var domain = email.Split('@').Last();
            var hostFromDomain = domain switch
            {
                "gmail.com" => "imap.gmail.com",
                "outlook.com" => "outlook.office365.com",
                "hotmail.com" => "outlook.office365.com",
                "live.com" => "outlook.office365.com",
                "office365.com" => "outlook.office365.com",
                _ => options.Host
            };

            options.Host = string.IsNullOrWhiteSpace(options.Host) ? hostFromDomain : options.Host;
            options.Port = options.Port <= 0 ? 993 : options.Port;
            options.UseSsl = true;
            options.Folder = string.IsNullOrWhiteSpace(options.Folder) ? "INBOX" : options.Folder;
            options.DaysBack = options.DaysBack <= 0 ? 30 : options.DaysBack;
            options.MaxMessages = options.MaxMessages <= 0 ? 100 : options.MaxMessages;
            options.InvoiceKeywords = (options.InvoiceKeywords == null || options.InvoiceKeywords.Length == 0)
                ? new[] { "fatura", "invoice", "recibo", "billing", "nif", "iva" }
                : options.InvoiceKeywords;
        }
    }
}
