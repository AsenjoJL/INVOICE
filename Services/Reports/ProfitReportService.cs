using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using HazelInvoice.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Memory;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Expenses;

namespace HazelInvoice.Services.Reports;

internal sealed record PartnerAttributedRow(string PartnerName, string ItemName, decimal Amount);

/// <summary>
/// Builds the Profit & Sales Summary view model using DB-side aggregation.
/// Keeps the controller thin and avoids loading all receipts/lines into memory.
/// </summary>
public sealed class ProfitReportService : IProfitReportService
{
    private readonly ApplicationDbContext _context;
    private readonly ILogger<ProfitReportService> _logger;
    private readonly bool _partnersEnabled;
    private readonly IMemoryCache _cache;
    private readonly IAppCacheInvalidator _cacheInvalidator;
    private readonly IExpenseCategoryCatalogService _expenseCategoryCatalog;

    public ProfitReportService(
        ApplicationDbContext context,
        ILogger<ProfitReportService> logger,
        IOptions<FeaturesOptions> features,
        IMemoryCache cache,
        IAppCacheInvalidator cacheInvalidator,
        IExpenseCategoryCatalogService expenseCategoryCatalog)
    {
        _context = context;
        _logger = logger;
        _partnersEnabled = features?.Value?.PartnersEnabled ?? false;
        _cache = cache;
        _cacheInvalidator = cacheInvalidator;
        _expenseCategoryCatalog = expenseCategoryCatalog;
    }

    public async Task<ProfitSummaryViewModel> BuildAsync(ProfitReportQueryOptions options, CancellationToken ct = default)
    {
        var cacheKey = AppCacheKeys.ProfitReport(
            options.StartDate.Date,
            options.EndDate.Date,
            options.IncludeUnpaid,
            options.PercentFee,
            options.Partner1SharePercent);

        if (_cacheInvalidator is AppCacheInvalidator invalidator)
            invalidator.TrackProfitReportKey(cacheKey);

        if (_cache.TryGetValue(cacheKey, out ProfitSummaryViewModel? cached) && cached != null)
            return cached;

        var startDateOnly = options.StartDate.Date;
        var endDateOnly = options.EndDate.Date;
        var endExclusive = endDateOnly.AddDays(1);

        var vm = new ProfitSummaryViewModel
        {
            StartDate = options.StartDate,
            EndDate = options.EndDate,
            IncludeUnpaid = options.IncludeUnpaid,
            PercentFee = options.PercentFee,
            // When partners are disabled, default to a single-owner view:
            // Partner1 = 100%, Partner2 = 0%.
            Partner1SharePercent = _partnersEnabled ? options.Partner1SharePercent : 100m,
            Partner2SharePercent = _partnersEnabled ? (100m - options.Partner1SharePercent) : 0m,
        };

        // Base receipts filter (not void, date range).
        // This is used for "all receipts in range" stats, regardless of the IncludeUnpaid toggle.
        IQueryable<Receipt> allReceiptsQuery = _context.Receipts
            .AsNoTracking()
            .Where(r => r.Date >= startDateOnly && r.Date < endExclusive && r.Status != PaymentStatus.Void);

        // Apply optional paid-only filter for the report's primary numbers.
        IQueryable<Receipt> receiptsQuery = allReceiptsQuery;
        if (!options.IncludeUnpaid)
        {
            receiptsQuery = receiptsQuery.Where(r => r.Status == PaymentStatus.Paid);
        }

        // Always compute range totals ignoring the IncludeUnpaid toggle (for clear UX messages).
        var allReceiptAgg = await allReceiptsQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                MinDate = g.Min(r => (DateTime?)r.Date),
                MaxDate = g.Max(r => (DateTime?)r.Date),
                PaidCount = g.Count(r => r.Status == PaymentStatus.Paid),
                UnpaidCount = g.Count(r => r.Status == PaymentStatus.Unpaid),
                PaidTotal = g.Where(r => r.Status == PaymentStatus.Paid).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                UnpaidTotal = g.Where(r => r.Status == PaymentStatus.Unpaid).Sum(r => (decimal?)r.TotalAmount) ?? 0m
            })
            .FirstOrDefaultAsync(ct);

        vm.AllReceiptCount = allReceiptAgg?.Count ?? 0;
        vm.AllPaidReceiptCount = allReceiptAgg?.PaidCount ?? 0;
        vm.AllUnpaidReceiptCount = allReceiptAgg?.UnpaidCount ?? 0;
        vm.AllPaidTotal = allReceiptAgg?.PaidTotal ?? 0m;
        vm.AllUnpaidTotal = allReceiptAgg?.UnpaidTotal ?? 0m;

        // Single aggregate row for counts/sums/min/max.
        var receiptAgg = await receiptsQuery
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Count = g.Count(),
                MinDate = g.Min(r => (DateTime?)r.Date),
                MaxDate = g.Max(r => (DateTime?)r.Date),
                PaidTotal = g.Where(r => r.Status == PaymentStatus.Paid).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                UnpaidTotal = g.Where(r => r.Status == PaymentStatus.Unpaid).Sum(r => (decimal?)r.TotalAmount) ?? 0m
            })
            .FirstOrDefaultAsync(ct);

        // Collection range should reflect all receipts in the selected dates
        // even if the report is filtered to paid-only.
        if (allReceiptAgg?.MinDate != null && allReceiptAgg.MaxDate != null)
        {
            vm.CollectionStartDate = allReceiptAgg.MinDate.Value.Date;
            vm.CollectionEndDate = allReceiptAgg.MaxDate.Value.Date;
        }
        else
        {
            vm.CollectionStartDate = startDateOnly;
            vm.CollectionEndDate = endDateOnly;
        }

        vm.TotalReceiptCount = receiptAgg?.Count ?? 0;
        // These totals should reflect the whole range (not only the filtered report scope)
        // so users can immediately see if there are unpaid receipts in the selected dates.
        vm.AutoReceivedAmount = vm.AllPaidTotal;
        vm.TotalPaidReceipts = vm.AllPaidTotal;
        vm.TotalUnpaidReceipts = vm.AllUnpaidTotal;

        // Manual received override (same rule as existing implementation)
        var receivedOverride = await _context.CollectionReceivedOverrides
            .AsNoTracking()
            .Where(o => o.StartDate == startDateOnly && o.EndDate == endDateOnly)
            .OrderByDescending(o => o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        vm.ReceivedAmountIsManual = receivedOverride != null;
        vm.ReceivedAmount = receivedOverride?.Amount ?? vm.AutoReceivedAmount;

        // Daily sales (DB group-by).
        var dailySales = await receiptsQuery
            .GroupBy(r => r.Date.Date)
            .Select(g => new { Date = g.Key, SalesAmount = g.Sum(r => (decimal?)r.TotalAmount) ?? 0m })
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        // Daily gross profit (ReceiptLines joined to receipts filter; uses snapshot cost, else Product.UnitCost, else 0).
        var dailyProfit = await (
            from l in _context.ReceiptLines.AsNoTracking()
            join r in receiptsQuery on l.ReceiptId equals r.Id
            join p in _context.Products.AsNoTracking() on l.ProductId equals p.Id into pJoin
            from p in pJoin.DefaultIfEmpty()
            group new { l, p } by r.Date.Date
            into g
            select new
            {
                Date = g.Key,
                GrossProfit = g.Sum(x =>
                    (decimal?)x.l.Amount -
                    ((decimal?)x.l.Quantity *
                     (x.l.CostPriceSnapshot != 0m
                         ? x.l.CostPriceSnapshot
                         : (x.p != null ? x.p.UnitCost : 0m))))
                    ?? 0m
            }
        ).ToListAsync(ct);

        var profitByDate = dailyProfit.ToDictionary(x => x.Date, x => x.GrossProfit);
        foreach (var day in dailySales)
        {
            vm.DailyStats.Add(new DailyProfitStat
            {
                Date = day.Date,
                SalesAmount = day.SalesAmount,
                FeeAmount = day.SalesAmount * (options.PercentFee / 100m),
                GrossProfit = profitByDate.TryGetValue(day.Date, out var gp) ? gp : 0m
            });
        }

        vm.TotalGrossSales = vm.DailyStats.Sum(s => s.SalesAmount);
        vm.TotalFees = vm.DailyStats.Sum(s => s.FeeAmount);
        vm.TotalGrossProfit = vm.DailyStats.Sum(s => s.GrossProfit);

        // Side tables (usually small; keep simple and consistent).
        var deductions = await _context.Deductions
            .AsNoTracking()
            .Where(d => d.Date >= startDateOnly && d.Date < endExclusive)
            .ToListAsync(ct);

        var purchases = _partnersEnabled
            ? await _context.PartnerPurchases
                .AsNoTracking()
                .Where(p => p.Date >= startDateOnly && p.Date < endExclusive)
                .ToListAsync(ct)
            : new List<PartnerPurchase>();

        var capitals = await _context.PartnerCapitals
            .AsNoTracking()
            .Where(c => c.Date >= startDateOnly && c.Date < endExclusive)
            .ToListAsync(ct);

        var expenses = await _context.Expenses
            .AsNoTracking()
            .Where(e => e.Date >= startDateOnly && e.Date < endExclusive)
            .ToListAsync(ct);

        // Partner balances & names (optional)
        var balances = _partnersEnabled
            ? await _context.PartnerBalanceConfigs
                .AsNoTracking()
                .OrderBy(b => b.PartnerName)
                .ToListAsync(ct)
            : new List<PartnerBalanceConfig>();

        if (_partnersEnabled)
        {
            if (balances.Count >= 1) vm.Partner1Name = balances[0].PartnerName;
            if (balances.Count >= 2) vm.Partner2Name = balances[1].PartnerName;
        }
        else
        {
            vm.Partner1Name = "OWNER";
            vm.Partner2Name = string.Empty;
        }

        vm.Partner1OpeningBalance = _partnersEnabled
            ? (balances.FirstOrDefault(b => b.PartnerName == vm.Partner1Name)?.OpeningBalance ?? 0)
            : 0m;
        vm.Partner2OpeningBalance = _partnersEnabled
            ? (balances.FirstOrDefault(b => b.PartnerName == vm.Partner2Name)?.OpeningBalance ?? 0)
            : 0m;

        var noteDeductions = deductions
            .Where(d => string.Equals(d.Category, "Note", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var mainDeductions = deductions.Except(noteDeductions).ToList();

        vm.Deductions = mainDeductions;
        vm.TotalDeductions = mainDeductions.Sum(d => d.Amount);

        vm.CapitalFunds = capitals;
        vm.TotalCapitalFund = capitals.Sum(c => c.Amount);

        vm.Expenses = expenses;
        var expenseGroupMap = await _expenseCategoryCatalog.GetGroupMapAsync(ct);
        vm.ExpenseGroups = BuildExpenseGroups(expenses, expenseGroupMap);
        vm.TotalExpenses = expenses.Sum(e => e.Amount);

        // Net Profit = Gross - Deductions - Expenses - Capital Funds (Retained)
        vm.NetProfit = vm.TotalGrossProfit - vm.TotalDeductions - vm.TotalExpenses - vm.TotalCapitalFund;

        // Profit Sharing
        vm.Partner1ShareAmount = vm.NetProfit * (vm.Partner1SharePercent / 100m);
        vm.Partner2ShareAmount = vm.NetProfit * (vm.Partner2SharePercent / 100m);

        // Purchases
        vm.PartnerPurchases = purchases;
        vm.TotalPartner1Purchases = _partnersEnabled
            ? purchases.Where(p => p.PartnerName == vm.Partner1Name).Sum(p => p.Amount)
            : 0m;
        vm.TotalPartner2Purchases = _partnersEnabled
            ? purchases.Where(p => p.PartnerName == vm.Partner2Name).Sum(p => p.Amount)
            : 0m;

        // Partner-attributed sales (from receipt lines tagging).
        // Group by PartnerName + ItemName so the UI can show a clean per-partner list like the Excel format.
        var partnerAttributed = _partnersEnabled
            ? await (
                from l in _context.ReceiptLines.AsNoTracking()
                join r in receiptsQuery on l.ReceiptId equals r.Id
                where !string.IsNullOrWhiteSpace(l.PartnerName) && l.Amount > 0m
                group l by new { Partner = l.PartnerName!, Item = l.ItemName }
                into g
                select new PartnerAttributedRow(
                    g.Key.Partner,
                    g.Key.Item ?? string.Empty,
                    g.Sum(x => (decimal?)x.Amount) ?? 0m)
            ).ToListAsync(ct)
            : new List<PartnerAttributedRow>();

        vm.PartnerSalesAttributions = _partnersEnabled
            ? partnerAttributed
                .GroupBy(x => x.PartnerName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => new PartnerSalesAttributionGroup
                {
                    PartnerName = g.Key,
                    Items = g
                        .Where(x => x.Amount > 0m)
                        .OrderByDescending(x => x.Amount)
                        .ThenBy(x => x.ItemName)
                        .Select(x => new PartnerSalesAttributionItem
                        {
                            ItemName = x.ItemName,
                            Amount = x.Amount
                        })
                        .ToList()
                })
                .OrderBy(x => x.PartnerName)
                .ToList()
            : new List<PartnerSalesAttributionGroup>();

        vm.TotalPartnerSalesAttributed = vm.PartnerSalesAttributions.Sum(g => g.TotalAmount);

        // Final Calculation (share - purchases)
        vm.Partner1Final = vm.Partner1ShareAmount - vm.TotalPartner1Purchases;
        vm.Partner2Final = vm.Partner2ShareAmount - vm.TotalPartner2Purchases;

        vm.NoteDeductionAmount = noteDeductions.Sum(d => d.Amount);
        vm.NoteRemainingBalance = vm.Partner2Final - vm.NoteDeductionAmount;

        // Ledger Calculation (Right Side)
        decimal runningBalance = vm.Partner1OpeningBalance;
        var ledgerItems = new List<LedgerRow>();

        ledgerItems.AddRange(mainDeductions.Select(d => new LedgerRow
        {
            Date = d.Date,
            Description = d.Description,
            Amount = -d.Amount
        }));

        ledgerItems.AddRange(expenses.Select(e => new LedgerRow
        {
            Date = e.Date,
            Description = $"Expense: {e.Category} {e.Description}".Trim(),
            Amount = -e.Amount
        }));

        if (_partnersEnabled)
        {
            ledgerItems.AddRange(purchases.Select(p => new LedgerRow
            {
                Date = p.Date,
                Description = $"{p.PartnerName}: {p.Notes}",
                Amount = -p.Amount
            }));
        }

        ledgerItems = ledgerItems.OrderBy(x => x.Date).ToList();
        foreach (var row in ledgerItems)
        {
            runningBalance += row.Amount;
            row.Balance = runningBalance;
        }
        vm.Ledger = ledgerItems;

        // Integrated Summary: items, top items, outlets.
        try
        {
            vm.TotalItemsSold = await (
                from l in _context.ReceiptLines.AsNoTracking()
                join r in receiptsQuery on l.ReceiptId equals r.Id
                select (decimal?)l.Quantity
            ).SumAsync(ct) ?? 0m;

            vm.TopItems = await (
                from l in _context.ReceiptLines.AsNoTracking()
                join r in receiptsQuery on l.ReceiptId equals r.Id
                group l by l.ItemName
                into g
                select new TopItemDto
                {
                    ItemName = g.Key ?? string.Empty,
                    Quantity = g.Sum(x => x.Quantity),
                    TotalAmount = g.Sum(x => x.Amount)
                }
            ).OrderByDescending(x => x.Quantity).Take(20).ToListAsync(ct);

            vm.OutletSummaries = await receiptsQuery
                .GroupBy(r => r.CustomerName)
                .Select(g => new OutletSummaryDto
                {
                    OutletName = g.Key ?? string.Empty,
                    PaidAmount = g.Where(r => r.Status == PaymentStatus.Paid).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                    UnpaidAmount = g.Where(r => r.Status == PaymentStatus.Unpaid).Sum(r => (decimal?)r.TotalAmount) ?? 0m,
                    TotalAmount = g.Sum(r => (decimal?)r.TotalAmount) ?? 0m
                })
                .OrderByDescending(x => x.TotalAmount)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // If anything above fails (translation/provider edge cases), don't break the entire report.
            _logger.LogWarning(ex, "Profit report summary queries failed; returning report without TopItems/OutletSummaries.");
        }

        _cache.Set(cacheKey, vm, TimeSpan.FromSeconds(30));
        return vm;
    }

    private static List<ExpenseGroupViewModel> BuildExpenseGroups(
        List<Expense> expenses,
        IReadOnlyDictionary<string, ExpenseCategoryGroup> expenseGroupMap)
    {
        var grouped = ExpenseCategoryCatalog.OrderedGroups.ToDictionary(
            group => ExpenseCategoryCatalog.GetLabel(group),
            _ => new List<Expense>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var expense in expenses.OrderBy(e => e.Date).ThenBy(e => e.Category))
        {
            var category = ExpenseCategoryCatalog.NormalizeName(expense.Category);
            var groupName = expenseGroupMap.TryGetValue(category, out var mapped)
                ? ExpenseCategoryCatalog.GetLabel(mapped)
                : ExpenseCategoryCatalog.GetLabel(ExpenseCategoryGroup.Other);

            grouped[groupName].Add(expense);
        }

        return grouped
            .Where(g => g.Value.Count > 0)
            .Select(g => new ExpenseGroupViewModel
            {
                Label = g.Key,
                Items = g.Value
            })
            .ToList();
    }
}
