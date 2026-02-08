using HazelInvoice.Data;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly ApplicationDbContext _context;

    public DashboardController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index()
    {
        var today = DateTime.Today;
        var dayStart = today;
        var dayEnd = today.AddDays(1);
        
        var model = new DashboardViewModel();

        // 1. Sales Today
        model.SalesToday = await _context.Receipts
            .Where(r => r.Date >= dayStart && r.Date < dayEnd && r.Status != Models.PaymentStatus.Void)
            .SumAsync(r => r.TotalAmount);

        // (New) Weekly (Mon-Sun)
        int diff = (7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7;
        var weekStart = today.AddDays(-1 * diff).Date;
        var weekEnd = weekStart.AddDays(7);
        model.SalesWeekly = await _context.Receipts
            .Where(r => r.Date >= weekStart && r.Date < weekEnd && r.Status != Models.PaymentStatus.Void)
            .SumAsync(r => r.TotalAmount);

        // (New) Monthly
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        model.SalesMonthly = await _context.Receipts
            .Where(r => r.Date >= monthStart && r.Date < monthEnd && r.Status != Models.PaymentStatus.Void)
            .SumAsync(r => r.TotalAmount);

        model.ExpenseMonthly = await _context.Expenses
            .Where(e => e.Date >= monthStart && e.Date < monthEnd)
            .SumAsync(e => e.Amount);

        // (New) Annual
        var yearStart = new DateTime(today.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);
        model.SalesYearly = await _context.Receipts
            .Where(r => r.Date >= yearStart && r.Date < yearEnd && r.Status != Models.PaymentStatus.Void)
            .SumAsync(r => r.TotalAmount);

        // 2. Items Sold Today
        model.ItemsSoldToday = await _context.ReceiptLines
            .Where(l => l.Receipt != null &&
                        l.Receipt.Date >= dayStart && l.Receipt.Date < dayEnd &&
                        l.Receipt.Status != Models.PaymentStatus.Void)
            .SumAsync(l => l.Quantity);

        model.ItemsSoldTodayByUnit = await _context.ReceiptLines
            .Where(l => l.Receipt != null &&
                        l.Receipt.Date >= dayStart && l.Receipt.Date < dayEnd &&
                        l.Receipt.Status != Models.PaymentStatus.Void)
            .GroupBy(l => string.IsNullOrWhiteSpace(l.Unit) ? "unit" : l.Unit)
            .Select(g => new CategoryValuePoint
            {
                Category = g.Key,
                Value = g.Sum(l => (decimal)l.Quantity)
            })
            .OrderBy(g => g.Category)
            .ToListAsync();

        // 3. Expense Today
        model.ExpenseToday = await _context.Expenses
            .Where(e => e.Date >= dayStart && e.Date < dayEnd)
            .SumAsync(e => e.Amount);

        // 4. All Time Sales
        model.TotalSalesAllTime = await _context.Receipts
            .Where(r => r.Status != Models.PaymentStatus.Void)
            .SumAsync(r => r.TotalAmount);

        // 5. Unpaid
        model.UnpaidAmount = await _context.Receipts
            .Where(r => r.Status != Models.PaymentStatus.Void)
            .SumAsync(r => r.TotalAmount - r.PaidAmount);

        // 6. Cash Balance (Simplified: Total Payments - Total Expenses)
        var totalPayments = await _context.Payments.SumAsync(p => p.Amount);
        var totalExpenses = await _context.Expenses.SumAsync(e => e.Amount);
        model.TotalExpenseAllTime = totalExpenses;
        model.CashBalance = totalPayments - totalExpenses;

        // 7. Gross Profit (Revenue - Cost of Goods)
        // Harder query, need to join lines with product costs.
        // Approximate for now or iterate if small data.
        // EF Core projection:
        var salesLines = await _context.ReceiptLines
            .Include(l => l.Product)
            .Include(l => l.Service)
            .Where(l => l.Receipt != null && l.Receipt.Status != Models.PaymentStatus.Void)
            .ToListAsync(); // Pull into memory for complex calcs or optimize later

        decimal totalRevenue = salesLines.Sum(l => l.Amount);
        decimal totalCost = salesLines.Sum(l => 
            (l.Product != null ? l.Product.UnitCost * l.Quantity : 0) + 
            (l.Service != null ? (l.Service.Cost ?? 0) * l.Quantity : 0)
        );
        
        model.GrossProfit = totalRevenue - totalCost;
        model.NetProfit = model.GrossProfit - totalExpenses;

        // Charts: Daily Sales (Last 7 days)
        var last7Days = await _context.Receipts
            .Where(r => r.Date >= today.AddDays(-6) && r.Status != Models.PaymentStatus.Void)
            .GroupBy(r => r.Date.Date)
            .Select(g => new DateValuePoint { Date = g.Key, Value = g.Sum(r => r.TotalAmount) })
            .ToListAsync();
        
        model.DailySales = last7Days;

        // Top Items (Top 10)
        var topItems = salesLines
            .GroupBy(l => l.ItemName)
            .Select(g => new CategoryValuePoint { Category = g.Key, Value = g.Sum(l => l.Amount) })
            .OrderByDescending(g => g.Value)
            .Take(10)
            .ToList();
        
        model.TopItems = topItems;

        // -- New Data for Redesign --
        
        // Recent Unpaid (Top 8)
        model.RecentUnpaidOrders = await _context.Receipts
            .Where(r => r.Status == Models.PaymentStatus.Unpaid)
            .OrderByDescending(r => r.Date)
            .Take(8)
            .ToListAsync();

        // Recent Paid (Top 8)
        model.RecentPaidOrders = await _context.Receipts
            .Where(r => r.Status == Models.PaymentStatus.Paid)
            .OrderByDescending(r => r.Date)
            .Take(8)
            .ToListAsync();

        // Top Outlets (Top 10)
        model.TopOutlets = await _context.Receipts
            .Where(r => r.Status != Models.PaymentStatus.Void)
            .GroupBy(r => r.CustomerName)
            .Select(g => new CategoryValuePoint { Category = g.Key, Value = g.Sum(r => r.TotalAmount) })
            .OrderByDescending(g => g.Value)
            .Take(10)
            .ToListAsync();

        return View(model);
    }
}
