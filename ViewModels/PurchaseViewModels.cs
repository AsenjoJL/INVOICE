using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class PurchaseListViewModel
{
    public DateTime Date { get; set; } = DateTime.Today;
    public string StatusFilter { get; set; } = "All";
    public List<Purchase> Purchases { get; set; } = new();
    public decimal GrandTotal { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal TotalBalance { get; set; }
}

public class PayablesViewModel
{
    public DateTime? Date { get; set; }
    public List<PayableSupplierSummary> Suppliers { get; set; } = new();
    public decimal TotalBalance { get; set; }
}

public class PayableSupplierSummary
{
    public string SupplierName { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal Balance { get; set; }
    public List<Purchase> Purchases { get; set; } = new();
}
