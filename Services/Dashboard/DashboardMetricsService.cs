using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Expenses;
using HazelInvoice.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace HazelInvoice.Services.Dashboard;

public class DashboardMetricsService : IDashboardMetricsService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<DashboardMetricsService> _logger;
    private readonly IMemoryCache _cache;
    private readonly IAppCacheInvalidator _cacheInvalidator;
    private readonly IExpenseCategoryCatalogService _expenseCategoryCatalog;

    public DashboardMetricsService(
        ApplicationDbContext db,
        ILogger<DashboardMetricsService> logger,
        IMemoryCache cache,
        IAppCacheInvalidator cacheInvalidator,
        IExpenseCategoryCatalogService expenseCategoryCatalog)
    {
        _db = db;
        _logger = logger;
        _cache = cache;
        _cacheInvalidator = cacheInvalidator;
        _expenseCategoryCatalog = expenseCategoryCatalog;
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
            var periods = DashboardPeriods.Create(today.Date);
            var expenseGroupMap = await _expenseCategoryCatalog.GetGroupMapAsync(ct);

            // Keep one query in flight at a time because EF Core DbContext does not support
            // concurrent operations on the same instance.
            var receiptMetrics = await GetReceiptMetricsAsync(periods, ct);
            var expenseMetrics = await GetExpenseMetricsAsync(periods, expenseGroupMap, ct);
            var paymentMetrics = await GetPaymentMetricsAsync(periods, ct);
            var profitMetrics = await GetProfitMetricsAsync(periods, ct);
            var itemMetrics = await GetItemMetricsAsync(periods, ct);
            var dailyCostMetrics = await GetDailyPurchaseCostMetricsAsync(periods, ct);

            var vm = BuildViewModel(
                periods,
                receiptMetrics,
                expenseMetrics,
                paymentMetrics,
                profitMetrics,
                itemMetrics,
                dailyCostMetrics);

            vm.DailySales = await GetDailySalesAsync(periods, ct);
            vm.TopItems = await GetTopItemsAsync(ct);
            vm.RecentUnpaidOrders = await GetRecentOrdersAsync(PaymentStatus.Unpaid, ct);
            vm.RecentPaidOrders = await GetRecentOrdersAsync(PaymentStatus.Paid, ct);
            vm.TopOutlets = await GetTopOutletsAsync(ct);

            _cache.Set(cacheKey, vm, TimeSpan.FromSeconds(30));
            return vm;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DashboardMetricsService.BuildAsync failed.");
            return new DashboardViewModel();
        }
    }

    private DashboardViewModel BuildViewModel(
        DashboardPeriods periods,
        ReceiptMetrics receipts,
        ExpenseMetrics expenses,
        PaymentMetrics payments,
        ProfitMetrics profits,
        ItemMetrics items,
        DailyPurchaseCostMetrics dailyCosts)
    {
        static decimal TrendPercent(decimal current, decimal previous)
        {
            if (previous == 0m) return current == 0m ? 0m : 100m;
            return ((current - previous) / Math.Abs(previous)) * 100m;
        }

        var grossProfitMonth = profits.RevenueMonth - profits.CostMonth;
        var grossProfitPrevMonth = profits.RevenuePrevMonth - profits.CostPrevMonth;
        var grossProfitAllTime = profits.RevenueAllTime - profits.CostAllTime;

        var cashBalance = ComputeCashBalance(payments.PaymentsAllTime, expenses.ExpenseAllTime);
        var previousCashBalance = ComputeCashBalance(payments.PaymentsBeforeToday, expenses.ExpenseBeforeToday);

        var receivables = ComputeReceivableMetrics(receipts, payments);

        return new DashboardViewModel
        {
            SalesToday = receipts.SalesToday,
            SalesWeekly = receipts.SalesWeekly,
            SalesMonthly = receipts.SalesMonthly,
            SalesYearly = receipts.SalesYearly,
            TotalSalesAllTime = receipts.SalesAllTime,

            CollectedRevenueMonthly = payments.PaymentsMonthly,
            CollectedRevenueAllTime = payments.PaymentsAllTime,
            OutstandingReceivablesWeekly = receivables.Week,
            OutstandingReceivablesMonthly = receivables.Month,
            OutstandingReceivablesAllTime = receivables.AllTime,

            UnpaidAmount = receipts.UnpaidAmountAllTime,
            UnpaidInvoiceCount = receipts.UnpaidInvoiceCount,

            ExpenseToday = expenses.ExpenseToday,
            ExpenseMonthly = expenses.ExpenseMonthly,
            DailyExpenseTotal = expenses.DailyExpenseTotal,
            WeeklyExpenseTotal = expenses.WeeklyExpenseTotal,
            MonthlyExpenseTotal = expenses.MonthlyExpenseBucketTotal,
            OtherExpenseTotal = expenses.OtherExpenseTotal,
            TotalExpenseAllTime = expenses.ExpenseAllTime,

            GrossProfit = grossProfitAllTime,
            GrossProfitMonth = grossProfitMonth,
            NetProfit = grossProfitAllTime - expenses.ExpenseAllTime,
            NetProfitMonth = grossProfitMonth - expenses.ExpenseMonthly,

            CashBalance = cashBalance,

            ItemsSoldToday = items.ItemsToday,
            ItemsSoldTodayByUnit = items.ItemsTodayByUnit,
            CostOfGoodsToday = profits.CostToday,
            DailyPurchaseCostsUpdatedToday = dailyCosts.UpdatedToday,
            DailyPurchaseCostsUsingPrevious = dailyCosts.UsingPrevious,

            SalesTodayTrendPercent = TrendPercent(receipts.SalesToday, receipts.SalesYesterday),
            SalesMonthlyTrendPercent = TrendPercent(receipts.SalesMonthly, receipts.SalesPrevMonth),
            GrossProfitTrendPercent = TrendPercent(grossProfitMonth, grossProfitPrevMonth),
            UnpaidTrendPercent = TrendPercent(receivables.Today, receivables.Yesterday),
            ExpenseTodayTrendPercent = TrendPercent(expenses.ExpenseToday, expenses.ExpenseYesterday),
            ExpenseMonthlyTrendPercent = TrendPercent(expenses.ExpenseMonthly, expenses.ExpensePrevMonth),
            CashBalanceTrendPercent = TrendPercent(cashBalance, previousCashBalance),
            ItemsSoldTodayTrendPercent = TrendPercent(items.ItemsToday, items.ItemsYesterday)
        };
    }

    private static decimal ComputeCashBalance(decimal totalInflows, decimal totalOutflows)
    {
        // Keep this intentionally narrow for now:
        // only recorded cash inflows (Payments) minus recorded outflows (Expenses).
        // Future flows like withdrawals/transfers can extend this method cleanly.
        return totalInflows - totalOutflows;
    }

    private static ReceivableMetrics ComputeReceivableMetrics(ReceiptMetrics receipts, PaymentMetrics payments)
        => new()
        {
            Today = receipts.SalesToday - payments.PaymentsToday,
            Yesterday = receipts.SalesYesterday - payments.PaymentsYesterday,
            Week = receipts.SalesWeekly - payments.PaymentsWeekly,
            Month = receipts.SalesMonthly - payments.PaymentsMonthly,
            AllTime = receipts.SalesAllTime - payments.PaymentsAllTime
        };

    private async Task<ReceiptMetrics> GetReceiptMetricsAsync(DashboardPeriods periods, CancellationToken ct)
    {
        var metrics = await _db.Receipts
            .AsNoTracking()
            .Where(r => r.Status != PaymentStatus.Void)
            .GroupBy(_ => 1)
            .Select(g => new ReceiptMetrics
            {
                SalesToday = g.Where(r => r.Date >= periods.DayStart && r.Date < periods.DayEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesYesterday = g.Where(r => r.Date >= periods.YesterdayStart && r.Date < periods.YesterdayEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesWeekly = g.Where(r => r.Date >= periods.WeekStart && r.Date < periods.WeekEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesMonthly = g.Where(r => r.Date >= periods.MonthStart && r.Date < periods.MonthEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesPrevMonth = g.Where(r => r.Date >= periods.PrevMonthStart && r.Date < periods.PrevMonthEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesYearly = g.Where(r => r.Date >= periods.YearStart && r.Date < periods.YearEnd).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                SalesAllTime = g.Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                UnpaidAmountAllTime = g.Sum(r => (decimal?)(r.TotalAmount - r.PaidAmount)) ?? 0m,
                UnpaidInvoiceCount = g.Count(r => r.TotalAmount > r.PaidAmount)
            })
            .SingleOrDefaultAsync(ct);

        return metrics ?? new ReceiptMetrics();
    }

    private async Task<ExpenseMetrics> GetExpenseMetricsAsync(
        DashboardPeriods periods,
        IReadOnlyDictionary<string, ExpenseCategoryGroup> expenseGroupMap,
        CancellationToken ct)
    {
        var dailyNames = expenseGroupMap.Where(x => x.Value == ExpenseCategoryGroup.Daily).Select(x => x.Key).ToArray();
        var weeklyNames = expenseGroupMap.Where(x => x.Value == ExpenseCategoryGroup.Weekly).Select(x => x.Key).ToArray();
        var monthlyNames = expenseGroupMap.Where(x => x.Value == ExpenseCategoryGroup.Monthly).Select(x => x.Key).ToArray();
        var categorizedNames = expenseGroupMap.Keys.ToArray();

        var metrics = await _db.Expenses
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new ExpenseMetrics
            {
                ExpenseToday = g.Where(e => e.Date >= periods.DayStart && e.Date < periods.DayEnd).Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpenseYesterday = g.Where(e => e.Date >= periods.YesterdayStart && e.Date < periods.YesterdayEnd).Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpenseMonthly = g.Where(e => e.Date >= periods.MonthStart && e.Date < periods.MonthEnd).Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpensePrevMonth = g.Where(e => e.Date >= periods.PrevMonthStart && e.Date < periods.PrevMonthEnd).Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpenseAllTime = g.Sum(e => (decimal?)e.Amount) ?? 0m,
                ExpenseBeforeToday = g.Where(e => e.Date < periods.DayStart).Sum(e => (decimal?)e.Amount) ?? 0m,
                DailyExpenseTotal = g.Where(e => dailyNames.Contains(e.Category)).Sum(e => (decimal?)e.Amount) ?? 0m,
                WeeklyExpenseTotal = g.Where(e => weeklyNames.Contains(e.Category)).Sum(e => (decimal?)e.Amount) ?? 0m,
                MonthlyExpenseBucketTotal = g.Where(e => monthlyNames.Contains(e.Category)).Sum(e => (decimal?)e.Amount) ?? 0m,
                OtherExpenseTotal = g.Where(e => !categorizedNames.Contains(e.Category)).Sum(e => (decimal?)e.Amount) ?? 0m
            })
            .SingleOrDefaultAsync(ct);

        return metrics ?? new ExpenseMetrics();
    }

    private async Task<PaymentMetrics> GetPaymentMetricsAsync(DashboardPeriods periods, CancellationToken ct)
    {
        var metrics = await _db.Payments
            .AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new PaymentMetrics
            {
                PaymentsToday = g.Where(p => p.Date >= periods.DayStart && p.Date < periods.DayEnd).Sum(p => (decimal?)p.Amount) ?? 0m,
                PaymentsYesterday = g.Where(p => p.Date >= periods.YesterdayStart && p.Date < periods.YesterdayEnd).Sum(p => (decimal?)p.Amount) ?? 0m,
                PaymentsWeekly = g.Where(p => p.Date >= periods.WeekStart && p.Date < periods.WeekEnd).Sum(p => (decimal?)p.Amount) ?? 0m,
                PaymentsMonthly = g.Where(p => p.Date >= periods.MonthStart && p.Date < periods.MonthEnd).Sum(p => (decimal?)p.Amount) ?? 0m,
                PaymentsPrevMonth = g.Where(p => p.Date >= periods.PrevMonthStart && p.Date < periods.PrevMonthEnd).Sum(p => (decimal?)p.Amount) ?? 0m,
                PaymentsAllTime = g.Sum(p => (decimal?)p.Amount) ?? 0m,
                PaymentsBeforeToday = g.Where(p => p.Date < periods.DayStart).Sum(p => (decimal?)p.Amount) ?? 0m
            })
            .SingleOrDefaultAsync(ct);

        return metrics ?? new PaymentMetrics();
    }

    private async Task<ProfitMetrics> GetProfitMetricsAsync(DashboardPeriods periods, CancellationToken ct)
    {
        var metrics = await _db.ReceiptLines
            .AsNoTracking()
            .Where(l => l.Receipt != null && l.Receipt.Status != PaymentStatus.Void)
            .GroupBy(_ => 1)
            .Select(g => new ProfitMetrics
            {
                RevenueAllTime = g.Sum(l => (decimal?)l.Amount) ?? 0m,
                CostAllTime = g.Sum(l => (decimal?)(l.CostPriceSnapshot * l.Quantity)) ?? 0m,
                CostToday = g.Where(l => l.Receipt!.Date >= periods.DayStart && l.Receipt.Date < periods.DayEnd).Sum(l => (decimal?)(l.CostPriceSnapshot * l.Quantity)) ?? 0m,
                RevenueMonth = g.Where(l => l.Receipt!.Date >= periods.MonthStart && l.Receipt.Date < periods.MonthEnd).Sum(l => (decimal?)l.Amount) ?? 0m,
                CostMonth = g.Where(l => l.Receipt!.Date >= periods.MonthStart && l.Receipt.Date < periods.MonthEnd).Sum(l => (decimal?)(l.CostPriceSnapshot * l.Quantity)) ?? 0m,
                RevenuePrevMonth = g.Where(l => l.Receipt!.Date >= periods.PrevMonthStart && l.Receipt.Date < periods.PrevMonthEnd).Sum(l => (decimal?)l.Amount) ?? 0m,
                CostPrevMonth = g.Where(l => l.Receipt!.Date >= periods.PrevMonthStart && l.Receipt.Date < periods.PrevMonthEnd).Sum(l => (decimal?)(l.CostPriceSnapshot * l.Quantity)) ?? 0m
            })
            .SingleOrDefaultAsync(ct);

        return metrics ?? new ProfitMetrics();
    }

    private async Task<ItemMetrics> GetItemMetricsAsync(DashboardPeriods periods, CancellationToken ct)
    {
        var linesBase = _db.ReceiptLines
            .AsNoTracking()
            .Where(l => l.Receipt != null && l.Receipt.Status != PaymentStatus.Void);

        var itemVolume = await linesBase
            .Where(l => l.Receipt!.Date >= periods.YesterdayStart && l.Receipt.Date < periods.DayEnd)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                ItemsToday = g.Where(l => l.Receipt!.Date >= periods.DayStart && l.Receipt.Date < periods.DayEnd).Sum(l => (decimal?)l.Quantity) ?? 0m,
                ItemsYesterday = g.Where(l => l.Receipt!.Date >= periods.YesterdayStart && l.Receipt.Date < periods.YesterdayEnd).Sum(l => (decimal?)l.Quantity) ?? 0m
            })
            .SingleOrDefaultAsync(ct);

        var byUnit = await linesBase
            .Where(l => l.Receipt!.Date >= periods.DayStart && l.Receipt.Date < periods.DayEnd)
            .GroupBy(l => string.IsNullOrWhiteSpace(l.Unit) ? "unit" : l.Unit)
            .Select(g => new CategoryValuePoint
            {
                Category = g.Key,
                Value = g.Sum(l => (decimal)l.Quantity)
            })
            .OrderBy(g => g.Category)
            .ToListAsync(ct);

        return new ItemMetrics
        {
            ItemsToday = itemVolume?.ItemsToday ?? 0m,
            ItemsYesterday = itemVolume?.ItemsYesterday ?? 0m,
            ItemsTodayByUnit = byUnit
        };
    }

    private async Task<DailyPurchaseCostMetrics> GetDailyPurchaseCostMetricsAsync(DashboardPeriods periods, CancellationToken ct)
    {
        var activeProductIds = await _db.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var updatedToday = await _db.DailyPurchaseCosts
            .AsNoTracking()
            .Where(c => activeProductIds.Contains(c.ProductId) &&
                        c.CostDate >= periods.DayStart &&
                        c.CostDate < periods.DayEnd)
            .Select(c => c.ProductId)
            .Distinct()
            .CountAsync(ct);

        return new DailyPurchaseCostMetrics
        {
            UpdatedToday = updatedToday,
            UsingPrevious = Math.Max(0, activeProductIds.Count - updatedToday)
        };
    }

    private async Task<List<DateValuePoint>> GetDailySalesAsync(DashboardPeriods periods, CancellationToken ct)
    {
        var last7DaysRaw = await _db.Receipts
            .AsNoTracking()
            .Where(r => r.Date >= periods.DayStart.AddDays(-6) && r.Status != PaymentStatus.Void)
            .GroupBy(r => r.Date.Date)
            .Select(g => new DateValuePoint { Date = g.Key, Value = g.Sum(r => r.TotalAmount) })
            .ToListAsync(ct);

        var salesByDate = last7DaysRaw.ToDictionary(s => s.Date.Date, s => s.Value);
        return Enumerable.Range(0, 7)
            .Select(offset =>
            {
                var date = periods.DayStart.AddDays(-6 + offset).Date;
                return new DateValuePoint
                {
                    Date = date,
                    Value = salesByDate.TryGetValue(date, out var value) ? value : 0m
                };
            })
            .ToList();
    }

    private async Task<List<CategoryValuePoint>> GetTopItemsAsync(CancellationToken ct)
    {
        return await _db.ReceiptLines
            .AsNoTracking()
            .Where(l => l.Receipt != null && l.Receipt.Status != PaymentStatus.Void)
            .GroupBy(l => l.ItemName)
            .Select(g => new CategoryValuePoint
            {
                Category = g.Key,
                Value = g.Sum(l => l.Amount)
            })
            .OrderByDescending(g => g.Value)
            .Take(10)
            .ToListAsync(ct);
    }

    private async Task<List<Receipt>> GetRecentOrdersAsync(PaymentStatus status, CancellationToken ct)
    {
        return await _db.Receipts
            .AsNoTracking()
            .Where(r => r.Status == status)
            .OrderByDescending(r => r.Date)
            .Take(8)
            .ToListAsync(ct);
    }

    private async Task<List<CategoryValuePoint>> GetTopOutletsAsync(CancellationToken ct)
    {
        return await _db.Receipts
            .AsNoTracking()
            .Where(r => r.Status != PaymentStatus.Void)
            .GroupBy(r => r.CustomerName)
            .Select(g => new CategoryValuePoint
            {
                Category = g.Key,
                Value = g.Sum(r => r.TotalAmount)
            })
            .OrderByDescending(g => g.Value)
            .Take(10)
            .ToListAsync(ct);
    }

    private sealed record DashboardPeriods
    {
        public required DateTime DayStart { get; init; }
        public required DateTime DayEnd { get; init; }
        public required DateTime YesterdayStart { get; init; }
        public required DateTime YesterdayEnd { get; init; }
        public required DateTime WeekStart { get; init; }
        public required DateTime WeekEnd { get; init; }
        public required DateTime MonthStart { get; init; }
        public required DateTime MonthEnd { get; init; }
        public required DateTime PrevMonthStart { get; init; }
        public required DateTime PrevMonthEnd { get; init; }
        public required DateTime YearStart { get; init; }
        public required DateTime YearEnd { get; init; }

        public static DashboardPeriods Create(DateTime dayStart)
        {
            var normalizedDay = dayStart.Date;
            var diff = (7 + (normalizedDay.DayOfWeek - DayOfWeek.Monday)) % 7;
            var weekStart = normalizedDay.AddDays(-diff).Date;
            var monthStart = new DateTime(normalizedDay.Year, normalizedDay.Month, 1);
            var prevMonthStart = monthStart.AddMonths(-1);
            var yearStart = new DateTime(normalizedDay.Year, 1, 1);

            return new DashboardPeriods
            {
                DayStart = normalizedDay,
                DayEnd = normalizedDay.AddDays(1),
                YesterdayStart = normalizedDay.AddDays(-1),
                YesterdayEnd = normalizedDay,
                WeekStart = weekStart,
                WeekEnd = weekStart.AddDays(7),
                MonthStart = monthStart,
                MonthEnd = monthStart.AddMonths(1),
                PrevMonthStart = prevMonthStart,
                PrevMonthEnd = monthStart,
                YearStart = yearStart,
                YearEnd = yearStart.AddYears(1)
            };
        }
    }

    private sealed record ReceiptMetrics
    {
        public decimal SalesToday { get; init; }
        public decimal SalesYesterday { get; init; }
        public decimal SalesWeekly { get; init; }
        public decimal SalesMonthly { get; init; }
        public decimal SalesPrevMonth { get; init; }
        public decimal SalesYearly { get; init; }
        public decimal SalesAllTime { get; init; }
        public decimal UnpaidAmountAllTime { get; init; }
        public int UnpaidInvoiceCount { get; init; }
    }

    private sealed record ExpenseMetrics
    {
        public decimal ExpenseToday { get; init; }
        public decimal ExpenseYesterday { get; init; }
        public decimal ExpenseMonthly { get; init; }
        public decimal ExpensePrevMonth { get; init; }
        public decimal ExpenseAllTime { get; init; }
        public decimal ExpenseBeforeToday { get; init; }
        public decimal DailyExpenseTotal { get; init; }
        public decimal WeeklyExpenseTotal { get; init; }
        public decimal MonthlyExpenseBucketTotal { get; init; }
        public decimal OtherExpenseTotal { get; init; }
    }

    private sealed record PaymentMetrics
    {
        public decimal PaymentsToday { get; init; }
        public decimal PaymentsYesterday { get; init; }
        public decimal PaymentsWeekly { get; init; }
        public decimal PaymentsMonthly { get; init; }
        public decimal PaymentsPrevMonth { get; init; }
        public decimal PaymentsAllTime { get; init; }
        public decimal PaymentsBeforeToday { get; init; }
    }

    private sealed record ProfitMetrics
    {
        public decimal RevenueAllTime { get; init; }
        public decimal CostAllTime { get; init; }
        public decimal CostToday { get; init; }
        public decimal RevenueMonth { get; init; }
        public decimal CostMonth { get; init; }
        public decimal RevenuePrevMonth { get; init; }
        public decimal CostPrevMonth { get; init; }
    }

    private sealed record ItemMetrics
    {
        public decimal ItemsToday { get; init; }
        public decimal ItemsYesterday { get; init; }
        public List<CategoryValuePoint> ItemsTodayByUnit { get; init; } = [];
    }

    private sealed record DailyPurchaseCostMetrics
    {
        public int UpdatedToday { get; init; }
        public int UsingPrevious { get; init; }
    }

    private sealed record ReceivableMetrics
    {
        public decimal Today { get; init; }
        public decimal Yesterday { get; init; }
        public decimal Week { get; init; }
        public decimal Month { get; init; }
        public decimal AllTime { get; init; }
    }
}
