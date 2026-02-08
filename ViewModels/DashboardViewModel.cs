namespace HazelInvoice.ViewModels;

public class DashboardViewModel
{
    public decimal SalesToday { get; set; }
    public decimal SalesWeekly { get; set; }
    public decimal SalesMonthly { get; set; }
    public decimal SalesYearly { get; set; }
    public decimal ExpenseMonthly { get; set; }
    public int ItemsSoldToday { get; set; }
    public List<CategoryValuePoint> ItemsSoldTodayByUnit { get; set; } = new();
    public decimal ExpenseToday { get; set; }
    public decimal TotalExpenseAllTime { get; set; }
    public decimal TotalSalesAllTime { get; set; }
    public decimal GrossProfit { get; set; }
    public decimal NetProfit { get; set; }
    public decimal CashBalance { get; set; }
    public decimal UnpaidAmount { get; set; }
    
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
