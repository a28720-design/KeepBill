using System.Threading;
using System.Threading.Tasks;
using KeepBill.Models.ViewModels;

namespace KeepBill.Services
{
    public interface IEmailInvoiceScannerService
    {
        EmailInvoicesViewModel GetLastResult();
        Task<EmailInvoicesViewModel> ScanAsync(CancellationToken cancellationToken = default);
    }
}
