using System.Threading.Tasks;
using KeepBill.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace KeepBill.Controllers
{
    [Authorize]
    public class EmailInvoicesController : Controller
    {
        private readonly IEmailInvoiceScannerService _scannerService;

        public EmailInvoicesController(IEmailInvoiceScannerService scannerService)
        {
            _scannerService = scannerService;
        }

        public IActionResult Index()
        {
            return RedirectToAction("Index", "Invoices");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Sync()
        {
            await _scannerService.ScanAsync();
            TempData["Toast"] = "Sincronizacao de email concluida.";
            return RedirectToAction("Index", "Invoices");
        }
    }
}
