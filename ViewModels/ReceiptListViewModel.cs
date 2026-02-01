using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class ReceiptListViewModel
{
    public DateTime Date { get; set; }
    public PaymentStatus Status { get; set; }
    public List<Receipt> Receipts { get; set; } = new();
    public decimal GrandTotal { get; set; }
}
