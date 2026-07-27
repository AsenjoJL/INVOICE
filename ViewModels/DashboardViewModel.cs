namespace HazelInvoice.ViewModels;

public class DashboardViewModel
{
    public string SelectedGroupName { get; set; } = "All";
    public List<string> AvailableGroupNames { get; set; } = new();
    public bool IsGroupFiltered => !string.Equals(SelectedGroupName, "All", StringComparison.OrdinalIgnoreCase);

    public decimal SalesToday { get; set; }
    public decimal SalesWeekly { get; set; }
    public decimal SalesMonthly { get; set; }
    public decimal SalesYearly { get; set; }
    public decimal CollectedRevenueMonthly { get; set; }
    public decimal CollectedRevenueAllTime { get; set; }
    public decimal OutstandingReceivablesWeekly { get; set; }
    public decimal OutstandingReceivablesMonthly { get; set; }
    public decimal OutstandingReceivablesAllTime { get; set; }
    public decimal ExpenseMonthly { get; set; }
    public decimal DailyExpenseTotal { get; set; }
    public decimal WeeklyExpenseTotal { get; set; }
    public decimal MonthlyExpenseTotal { get; set; }
    public decimal OtherExpenseTotal { get; set; }
    public decimal ItemsSoldToday { get; set; }
    public List<CategoryValuePoint> ItemsSoldTodayByUnit { get; set; } = new();
    public decimal CostOfGoodsToday { get; set; }
    public decimal CostOfGoodsMonth { get; set; }
    public int DailyPurchaseCostsUpdatedToday { get; set; }
    public int DailyPurchaseCostsUsingPrevious { get; set; }
    public decimal ExpenseToday { get; set; }
    public decimal TotalExpenseAllTime { get; set; }
    public decimal TotalSalesAllTime { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal NetProfit { get; set; }
    public decimal GrossProfitToday { get; set; }
    public decimal NetProfitToday { get; set; }
    public decimal GrossProfitMonth { get; set; }
    public decimal NetProfitMonth { get; set; }
    public decimal ProfitFeePercent { get; set; } = 1.0m;
    public decimal ProfitFeeToday { get; set; }
    public decimal ProfitFeeMonth { get; set; }
    public decimal CashBalance { get; set; }
    public decimal UnpaidAmount { get; set; }
    public int UnpaidInvoiceCount { get; set; }

    // KPI trend percentages (current vs previous period)
    public decimal SalesTodayTrendPercent { get; set; }
    public decimal SalesMonthlyTrendPercent { get; set; }
    public decimal GrossProfitTodayTrendPercent { get; set; }
    public decimal GrossProfitTrendPercent { get; set; }
    public decimal NetProfitTodayTrendPercent { get; set; }
    public decimal NetProfitMonthTrendPercent { get; set; }
    public decimal UnpaidTrendPercent { get; set; }
    public decimal ExpenseTodayTrendPercent { get; set; }
    public decimal ExpenseMonthlyTrendPercent { get; set; }
    public decimal CashBalanceTrendPercent { get; set; }
    public decimal ItemsSoldTodayTrendPercent { get; set; }
    
    // Charts Data
    public List<DateValuePoint> DailySales { get; set; } = new();
    public List<CategoryValuePoint> TopItems { get; set; } = new();
    
    // UI Lists
    public List<HazelInvoice.Models.Receipt> RecentUnpaidOrders { get; set; } = new();
    public List<HazelInvoice.Models.Receipt> RecentPaidOrders { get; set; } = new();
    public List<CategoryValuePoint> TopOutlets { get; set; } = new();
}

public class DateValuePoint
{
    public DateTime Date { get; set; }
    public decimal Value { get; set; }
}

public class CategoryValuePoint
{
    public string Category { get; set; } = string.Empty;
    public decimal Value { get; set; }
}
