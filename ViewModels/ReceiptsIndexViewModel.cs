namespace HazelInvoice.ViewModels;

public class ReceiptsIndexViewModel
{
    public int TotalCount { get; set; }
    public List<ReceiptListItemViewModel> Receipts { get; set; } = new();

    public string Query { get; set; } = string.Empty;
    public string SelectedClientGroup { get; set; } = "All";
    public List<string> AvailableClientGroups { get; set; } = new();
    public DateTime? Date { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
    public bool UnpaidOnly { get; set; }
}
