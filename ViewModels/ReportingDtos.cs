namespace HazelInvoice.ViewModels;

public class TopItemDto
{
    public string ItemName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal TotalAmount { get; set; }
}

public class OutletSummaryDto
{
    public string OutletName { get; set; } = string.Empty;
    public decimal PaidAmount { get; set; }
    public decimal UnpaidAmount { get; set; }
    public decimal TotalAmount { get; set; }
}

public class DailyTrendDto
{
    public DateTime Date { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal UnpaidAmount { get; set; }
    public decimal TotalAmount { get; set; }
}
