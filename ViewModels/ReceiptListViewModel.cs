using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class ReceiptListViewModel
{
    public DateTime Date { get; set; }
    public PaymentStatus Status { get; set; }
    public List<Receipt> Receipts { get; set; } = new();
    public List<ReceiptDateGroupViewModel> DateGroups { get; set; } = new();
    public decimal GrandTotal { get; set; }
}

public class ReceiptDateGroupViewModel
{
    public DateTime Date { get; set; }
    public List<Receipt> Receipts { get; set; } = new();
    public decimal TotalAmount { get; set; }
    public decimal TotalItems { get; set; }
}
