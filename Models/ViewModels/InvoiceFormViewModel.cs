using System.Collections.Generic;

namespace KeepBill.Models.ViewModels
{
    public class InvoiceFormViewModel
    {
        public Invoice Invoice { get; set; } = new Invoice();
        public List<InvoiceLineInput> Lines { get; set; } = new List<InvoiceLineInput>();
    }
}
