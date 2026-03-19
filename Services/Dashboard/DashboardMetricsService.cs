using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;
using HazelInvoice.Services.Caching;

namespace HazelInvoice.Services.Dashboard;

public class DashboardMetricsService : IDashboardMetricsService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<DashboardMetricsService> _logger;
    private readonly IMemoryCache _cache;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public DashboardMetricsService(
        ApplicationDbContext db,
        ILogger<DashboardMetricsService> logger,
        IMemoryCache cache,
        IAppCacheInvalidator cacheInvalidator)
    {
        _db = db;
        _logger = logger;
        _cache = cache;
        _cacheInvalidator = cacheInvalidator;
    }

    public async Task<DashboardViewModel> BuildAsync(DateTime today, CancellationToken ct = default)
    {
        var cacheKey = AppCacheKeys.Dashboard(today.Date);
        if (_cacheInvalidator is AppCacheInvalidator invalidator)
            invalidator.TrackDashboardKey(cacheKey);

        if (_cache.TryGetValue(cacheKey, out DashboardViewModel? cached) && cached != null)
            return cached;

        try
        {
            // Keep everything centralized and reusable so future dashboard widgets don't bloat controllers.
            var dayStart = today.Date;
            var dayEnd = dayStart.AddDays(1);
            var yesterdayStart = dayStart.AddDays(-1);
            var yesterdayEnd = dayStart;

        // Weekly (Mon-Sun)
        var diff = (7 + (dayStart.DayOfWeek - DayOfWeek.Monday)) % 7;
        var weekStart = dayStart.AddDays(-1 * diff).Date;
        var weekEnd = weekStart.AddDays(7);

        // Monthly
        var monthStart = new DateTime(dayStart.Year, dayStart.Month, 1);
        var monthEnd = monthStart.AddMonths(1);
        var prevMonthStart = monthStart.AddMonths(-1);
        var prevMonthEnd = monthStart;

        // Annual
        var yearStart = new DateTime(dayStart.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);

        static decimal TrendPercent(decimal current, decimal previous)
        {
            if (previous == 0m) return current == 0m ? 0m : 100m;
            return ((current - previous) / Math.Abs(previous)) * 100m;
        }

        var vm = new DashboardViewModel();

        // ---- Receipts aggregates (single roundtrip, no tracking) ----
        var receiptsAgg = await _db.Receipts
            .AsNoTracking()
            .Where(r => r.Status != PaymentStatus.Void)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                SalesToday = g.Where(r => r.Date >= dayStart && r.Date < dayEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesYesterday = g.Where(r => r.Date >= yesterdayStart && r.Date < yesterdayEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesWeekly = g.Where(r => r.Date >= weekStart && r.Date < weekEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesMonthly = g.Where(r => r.Date >= monthStart && r.Date < monthEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesPrevMonth = g.Where(r => r.Date >= prevMonthStart && r.Date < prevMonthEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesYearly = g.Where(r => r.Date >= yearStart && r.Date < yearEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesAllTime = g.Sum(r => (decimal?)r.TotalAmount) ?? 0m,

                UnpaidAllTime = g.Sum(r => (decimal?)(r.TotalAmount - r.PaidAmount)) ?? 0m,
                UnpaidCount = g.Count(r => r.TotalAmount > r.PaidAmount),
                UnpaidToday = g.Where(r => r.Date >= dayStart && r.Date < dayEnd).Sum(r => (decimal?)(r.TotalAmount - r.PaidAmount)) ?? 0m,
                UnpaidYesterday = g.Where(r => r.Date >= yesterdayStart && r.Date < yesterdayEnd).Sum(r => (decimal?)(r.TotalAmount - r.PaidAmount)) ?? 0m
            })
            .SingleOrDefaultAsync(ct);

        if (receiptsAgg == null)
        {
            // Empty database; keep defaults.
            receiptsAgg = new
            {
                SalesToday = 0m,
                SalesYesterday = 0m,
                SalesWeekly = 0m,
                SalesMonthly = 0m,
                SalesPrevMonth = 0m,
                SalesYearly = 0m,
                SalesAllTime = 0m,
                UnpaidAllTime = 0m,
                UnpaidCount = 0,
                UnpaidToday = 0m,
                UnpaidYesterday = 0m
            };
        }

        vm.SalesToday = receiptsAgg.SalesToday;
        vm.SalesWeekly = receiptsAgg.SalesWeekly;
        vm.SalesMonthly = receiptsAgg.SalesMonthly;
        vm.SalesYearly = receiptsAgg.SalesYearly;
        vm.TotalSalesAllTime = receiptsAgg.SalesAllTime;
        vm.UnpaidAmount = receiptsAgg.UnpaidAllTime;
        vm.UnpaidInvoiceCount = receiptsAgg.UnpaidCount;

        vm.SalesTodayTrendPercent = TrendPercent(receiptsAgg.SalesToday, receiptsAgg.SalesYesterday);
        vm.SalesMonthlyTrendPercent = TrendPercent(receiptsAgg.SalesMonthly, receiptsAgg.SalesPrevMonth);
        vm.UnpaidTrendPercent = TrendPercent(receiptsAgg.UnpaidToday, receiptsAgg.UnpaidYesterday);

        // ---- Expenses + Payments aggregates ----
        var expenseAgg = await _db.Expenses
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ExpenseToday = g.Where(e => e.Date >= dayStart && e.Date < dayEnd).Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpenseYesterday = g.Where(e => e.Date >= yesterdayStart && e.Date < yesterdayEnd).Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpenseMonthly = g.Where(e => e.Date >= monthStart && e.Date < monthEnd).Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpensePrevMonth = g.Where(e => e.Date >= prevMonthStart && e.Date < prevMonthEnd).Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpenseAllTime = g.Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpenseBeforeToday = g.Where(e => e.Date < dayStart).Sum(e => (decimal?)e.Amount) ?? 0m
            })
            .SingleOrDefaultAsync(ct)
            ?? new
            {
                ExpenseToday = 0m,
                ExpenseYesterday = 0m,
                ExpenseMonthly = 0m,
                ExpensePrevMonth = 0m,
                ExpenseAllTime = 0m,
                ExpenseBeforeToday = 0m
            };

        var paymentAgg = await _db.Payments
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                PaymentsAllTime = g.Sum(p => (decimal?)p.Amount) ?? 0m,
                PaymentsBeforeToday = g.Where(p => p.Date < dayStart).Sum(p => (decimal?)p.Amount) ?? 0m
            })
            .SingleOrDefaultAsync(ct)
            ?? new { PaymentsAllTime = 0m, PaymentsBeforeToday = 0m };

        vm.ExpenseToday = expenseAgg.ExpenseToday;
        vm.ExpenseMonthly = expenseAgg.ExpenseMonthly;
        vm.TotalExpenseAllTime = expenseAgg.ExpenseAllTime;
        vm.ExpenseTodayTrendPercent = TrendPercent(expenseAgg.ExpenseToday, expenseAgg.ExpenseYesterday);
        vm.ExpenseMonthlyTrendPercent = TrendPercent(expenseAgg.ExpenseMonthly, expenseAgg.ExpensePrevMonth);

        // Cash Balance: Total Payments - Total Expenses (all time)
        vm.CashBalance = paymentAgg.PaymentsAllTime - expenseAgg.ExpenseAllTime;
        var previousCashBalance = paymentAgg.PaymentsBeforeToday - expenseAgg.ExpenseBeforeToday;
        vm.CashBalanceTrendPercent = TrendPercent(vm.CashBalance, previousCashBalance);

        // ---- ReceiptLines aggregates (no in-memory loads) ----
        var linesBase = _db.ReceiptLines
            .AsNoTracking()
            .Where(l => l.Receipt != null && l.Receipt.Status != PaymentStatus.Void);

        // Items Sold Today/Yesterday
        var itemsAgg = await linesBase
            .Where(l => l.Receipt!.Date >= yesterdayStart && l.Receipt.Date < dayEnd)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ItemsToday = g.Where(l => l.Receipt!.Date >= dayStart && l.Receipt.Date < dayEnd).Sum(l => (decimal?)l.Quantity) ?? 0m,
                ItemsYesterday = g.Where(l => l.Receipt!.Date >= yesterdayStart && l.Receipt.Date < yesterdayEnd).Sum(l => (decimal?)l.Quantity) ?? 0m
            })
            .SingleOrDefaultAsync(ct)
            ?? new { ItemsToday = 0m, ItemsYesterday = 0m };

        vm.ItemsSoldToday = itemsAgg.ItemsToday;
        vm.ItemsSoldTodayTrendPercent = TrendPercent(itemsAgg.ItemsToday, itemsAgg.ItemsYesterday);

        // Unit breakdown (today only)
        vm.ItemsSoldTodayByUnit = await linesBase
            .Where(l => l.Receipt!.Date >= dayStart && l.Receipt.Date < dayEnd)
            .GroupBy(l => string.IsNullOrWhiteSpace(l.Unit) ? "unit" : l.Unit)
            .Select(g => new CategoryValuePoint
            {
                Category = g.Key,
                Value = g.Sum(l => (decimal)l.Quantity)
            })
            .OrderBy(g => g.Category)
            .ToListAsync(ct);

        // Gross profit (all time) using snapshots (stable & fast)
        var profitAgg = await linesBase
            .GroupBy(_ => 1)
            .Select(g => new
            {
                RevenueAllTime = g.Sum(l => (decimal?)l.Amount) ?? 0m,
                CostAllTime = g.Sum(l => (decimal?)(l.CostPriceSnapshot * l.Quantity)) ?? 0m,
                RevenueMonth = g.Where(l => l.Receipt!.Date >= monthStart && l.Receipt.Date < monthEnd).Sum(l => (decimal?)l.Amount) ?? 0m,
                CostMonth = g.Where(l => l.Receipt!.Date >= monthStart && l.Receipt.Date < monthEnd).Sum(l => (decimal?)(l.CostPriceSnapshot * l.Quantity)) ?? 0m,
                RevenuePrevMonth = g.Where(l => l.Receipt!.Date >= prevMonthStart && l.Receipt.Date < prevMonthEnd).Sum(l => (decimal?)l.Amount) ?? 0m,
                CostPrevMonth = g.Where(l => l.Receipt!.Date >= prevMonthStart && l.Receipt.Date < prevMonthEnd).Sum(l => (decimal?)(l.CostPriceSnapshot * l.Quantity)) ?? 0m
            })
            .SingleOrDefaultAsync(ct)
            ?? new
            {
                RevenueAllTime = 0m,
                CostAllTime = 0m,
                RevenueMonth = 0m,
                CostMonth = 0m,
                RevenuePrevMonth = 0m,
                CostPrevMonth = 0m
            };

        vm.GrossProfit = profitAgg.RevenueAllTime - profitAgg.CostAllTime;
        vm.NetProfit = vm.GrossProfit - expenseAgg.ExpenseAllTime;
        var grossMonth = profitAgg.RevenueMonth - profitAgg.CostMonth;
        var grossPrev = profitAgg.RevenuePrevMonth - profitAgg.CostPrevMonth;
        vm.GrossProfitTrendPercent = TrendPercent(grossMonth, grossPrev);

        // ---- Charts / Lists ----
        // Daily Sales (last 7 days)
        var last7DaysRaw = await _db.Receipts
            .AsNoTracking()
            .Where(r => r.Date >= dayStart.AddDays(-6) && r.Status != PaymentStatus.Void)
            .GroupBy(r => r.Date.Date)
            .Select(g => new DateValuePoint { Date = g.Key, Value = g.Sum(r => r.TotalAmount) })
            .ToListAsync(ct);

        var salesByDate = last7DaysRaw.ToDictionary(s => s.Date.Date, s => s.Value);
        vm.DailySales = Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var date = dayStart.AddDays(-6 + offset).Date;
                return new DateValuePoint
                {
                    Date = date,
                    Value = salesByDate.TryGetValue(date, out var value) ? value : 0m
                };
            })
            .ToList();

        // Top Items (Top 10) - query-based, no full list
        vm.TopItems = await linesBase
            .GroupBy(l => l.ItemName)
            .Select(g => new CategoryValuePoint { Category = g.Key, Value = g.Sum(l => l.Amount) })
            .OrderByDescending(g => g.Value)
            .Take(10)
            .ToListAsync(ct);

        // Recent Unpaid/Paid (Top 8)
        vm.RecentUnpaidOrders = await _db.Receipts
            .AsNoTracking()
            .Where(r => r.Status == PaymentStatus.Unpaid)
            .OrderByDescending(r => r.Date)
            .Take(8)
            .ToListAsync(ct);

        vm.RecentPaidOrders = await _db.Receipts
            .AsNoTracking()
            .Where(r => r.Status == PaymentStatus.Paid)
            .OrderByDescending(r => r.Date)
            .Take(8)
            .ToListAsync(ct);

        // Top Outlets (Top 10)
        vm.TopOutlets = await _db.Receipts
            .AsNoTracking()
            .Where(r => r.Status != PaymentStatus.Void)
            .GroupBy(r => r.CustomerName)
            .Select(g => new CategoryValuePoint { Category = g.Key, Value = g.Sum(r => r.TotalAmount) })
            .OrderByDescending(g => g.Value)
            .Take(10)
            .ToListAsync(ct);

            _cache.Set(cacheKey, vm, TimeSpan.FromSeconds(30));
            return vm;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DashboardMetricsService.BuildAsync failed.");
            return new DashboardViewModel();
        }
    }
}
