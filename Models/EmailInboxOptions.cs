namespace KeepBill.Models
{
    public class EmailInboxOptions
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; } = 993;
        public bool UseSsl { get; set; } = true;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Folder { get; set; } = "INBOX";
        public int DaysBack { get; set; } = 30;
        public int MaxMessages { get; set; } = 100;
        public bool OnlyUnread { get; set; } = false;
        public string[] InvoiceKeywords { get; set; } = new[] { "fatura", "invoice", "recibo", "billing", "nif", "iva" };
    }
}
