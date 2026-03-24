namespace KeepBill.Models
{
    /// <summary>
    /// Represents Supabase project credentials for downstream services (storage, auth, etc.).
    /// </summary>
    public class SupabaseOptions
    {
        public string Url { get; set; } = string.Empty;
        public string AnonPublicKey { get; set; } = string.Empty;
        public string ServiceRoleKey { get; set; } = string.Empty;
    }
}
