using Microsoft.AspNetCore.Identity.UI.Services;

namespace KeepBill.Services
{
    public class KeepBillEmailSender : IEmailSender
    {
        public Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            return Task.CompletedTask;
        }
    }
}
