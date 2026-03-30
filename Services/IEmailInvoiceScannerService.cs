using System.Threading;
using System.Threading.Tasks;
using KeepBill.Models;
using KeepBill.Models.ViewModels;

namespace KeepBill.Services
{
    public interface IEmailInvoiceScannerService
    {
        EmailInvoicesViewModel GetLastResult();
        Task<EmailInvoicesViewModel> ScanAsync(
            EmailInboxOptions? optionsOverride = null,
            Guid? ownerCustomerId = null,
            CancellationToken cancellationToken = default);
    }
}
