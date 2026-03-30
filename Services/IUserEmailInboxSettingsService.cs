using KeepBill.Models;

namespace KeepBill.Services
{
    public interface IUserEmailInboxSettingsService
    {
        Task<EmailInboxOptions> GetAsync(System.Security.Claims.ClaimsPrincipal principal, CancellationToken cancellationToken = default);
        Task SaveAsync(System.Security.Claims.ClaimsPrincipal principal, EmailInboxOptions options, CancellationToken cancellationToken = default);
    }
}
