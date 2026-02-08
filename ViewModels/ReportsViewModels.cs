using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class IncomeStatementViewModel
{
    public string Period { get; set; } = string.Empty;
    public decimal Revenue { get; set; }
    public decimal COGS { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal Expenses { get; set; }
    public decimal NetProfit { get; set; }
}

public class InventoryReportItem
{
    public Product Product { get; set; } = null!;
    public int CurrentStock { get; set; }
    public string Status { get; set; } = string.Empty;
}
