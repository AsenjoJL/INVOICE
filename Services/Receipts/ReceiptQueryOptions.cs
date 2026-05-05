namespace HazelInvoice.Services.Receipts;

public sealed class ReceiptQueryOptions
{
    public string? Query { get; set; }
    public DateTime? Date { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public bool UnpaidOnly { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
