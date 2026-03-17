using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class ReceiptListItemViewModel
{
    public int Id { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public ReceiptType Type { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public PaymentStatus Status { get; set; }
}

