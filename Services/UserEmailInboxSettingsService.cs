using KeepBill.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace KeepBill.Services
{
    public class UserEmailInboxSettingsService : IUserEmailInboxSettingsService
    {
        private const string Provider = "KeepBill.EmailInbox";
        private readonly UserManager<IdentityUser> _userManager;
        private readonly IDataProtector _protector;
        private readonly EmailInboxOptions _defaults;

        public UserEmailInboxSettingsService(
            UserManager<IdentityUser> userManager,
            IDataProtectionProvider dataProtectionProvider,
            IOptions<EmailInboxOptions> defaults)
        {
            _userManager = userManager;
            _protector = dataProtectionProvider.CreateProtector("KeepBill.EmailInbox.Password.v1");
            _defaults = defaults.Value;
        }

        public async Task<EmailInboxOptions> GetAsync(System.Security.Claims.ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            var user = await _userManager.GetUserAsync(principal);
            if (user == null)
            {
                return CloneDefaults();
            }

            var options = CloneDefaults();
            options.Host = await GetTokenAsync(user, "Host") ?? options.Host;
            options.Port = ParseInt(await GetTokenAsync(user, "Port"), options.Port);
            options.UseSsl = ParseBool(await GetTokenAsync(user, "UseSsl"), options.UseSsl);
            options.Username = await GetTokenAsync(user, "Username") ?? options.Username;
            options.Folder = await GetTokenAsync(user, "Folder") ?? options.Folder;
            options.DaysBack = ParseInt(await GetTokenAsync(user, "DaysBack"), options.DaysBack);
            options.MaxMessages = ParseInt(await GetTokenAsync(user, "MaxMessages"), options.MaxMessages);
            options.OnlyUnread = ParseBool(await GetTokenAsync(user, "OnlyUnread"), options.OnlyUnread);

            var keywords = await GetTokenAsync(user, "InvoiceKeywords");
            if (!string.IsNullOrWhiteSpace(keywords))
            {
                options.InvoiceKeywords = keywords
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            var protectedPassword = await GetTokenAsync(user, "Password");
            if (!string.IsNullOrWhiteSpace(protectedPassword))
            {
                try
                {
                    options.Password = _protector.Unprotect(protectedPassword);
                }
                catch
                {
                    options.Password = string.Empty;
                }
            }

            return options;
        }

        public async Task SaveAsync(System.Security.Claims.ClaimsPrincipal principal, EmailInboxOptions options, CancellationToken cancellationToken = default)
        {
            var user = await _userManager.GetUserAsync(principal);
            if (user == null)
            {
                return;
            }

            await SetTokenAsync(user, "Host", options.Host?.Trim() ?? string.Empty);
            await SetTokenAsync(user, "Port", Math.Max(1, options.Port).ToString());
            await SetTokenAsync(user, "UseSsl", options.UseSsl.ToString());
            await SetTokenAsync(user, "Username", options.Username?.Trim() ?? string.Empty);
            await SetTokenAsync(user, "Folder", string.IsNullOrWhiteSpace(options.Folder) ? "INBOX" : options.Folder.Trim());
            await SetTokenAsync(user, "DaysBack", Math.Clamp(options.DaysBack, 1, 365).ToString());
            await SetTokenAsync(user, "MaxMessages", Math.Clamp(options.MaxMessages, 1, 1000).ToString());
            await SetTokenAsync(user, "OnlyUnread", options.OnlyUnread.ToString());

            var keywords = (options.InvoiceKeywords ?? Array.Empty<string>())
                .Select(k => k?.Trim() ?? string.Empty)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            await SetTokenAsync(user, "InvoiceKeywords", string.Join(',', keywords));

            if (!string.IsNullOrWhiteSpace(options.Password))
            {
                var protectedPassword = _protector.Protect(options.Password);
                await SetTokenAsync(user, "Password", protectedPassword);
            }
        }

        private EmailInboxOptions CloneDefaults()
        {
            return new EmailInboxOptions
            {
                Host = _defaults.Host,
                Port = _defaults.Port,
                UseSsl = _defaults.UseSsl,
                Username = _defaults.Username,
                Password = _defaults.Password,
                Folder = _defaults.Folder,
                DaysBack = _defaults.DaysBack,
                MaxMessages = _defaults.MaxMessages,
                OnlyUnread = _defaults.OnlyUnread,
                InvoiceKeywords = _defaults.InvoiceKeywords?.ToArray() ?? Array.Empty<string>()
            };
        }

        private async Task<string?> GetTokenAsync(IdentityUser user, string name)
        {
            return await _userManager.GetAuthenticationTokenAsync(user, Provider, name);
        }

        private async Task SetTokenAsync(IdentityUser user, string name, string value)
        {
            await _userManager.SetAuthenticationTokenAsync(user, Provider, name, value);
        }

        private static int ParseInt(string? value, int fallback)
        {
            return int.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private static bool ParseBool(string? value, bool fallback)
        {
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }
    }
}
