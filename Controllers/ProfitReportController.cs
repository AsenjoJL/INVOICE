using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;

namespace HazelInvoice.Controllers;

[Authorize]
public class ProfitReportController : Controller
{
    private readonly ApplicationDbContext _context;

    public ProfitReportController(ApplicationDbContext context)
    {
        _context = context;
    }

    // GET: ProfitReport
    public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate, bool includeUnpaid = true, decimal percentFee = 1.0m, decimal split1 = 40m)
    {
        var start = startDate ?? DateTime.Today.AddDays(-14);
        var end = endDate ?? DateTime.Today;
        var startDateOnly = start.Date;
        var endExclusive = end.Date.AddDays(1);

        var vm = new ProfitSummaryViewModel
        {
            StartDate = start,
            EndDate = end,
            IncludeUnpaid = includeUnpaid,
            PercentFee = percentFee,
            Partner1SharePercent = split1,
            Partner2SharePercent = 100m - split1
        };

        // 1. Fetch Receipts & Lines
        // Note: Using efficient query with AsNoTracking
        var query = _context.Receipts
            .AsNoTracking()
            .Include(r => r.Lines)
            .ThenInclude(l => l.Product)
            .Where(r => r.Date >= startDateOnly && r.Date < endExclusive && r.Status != PaymentStatus.Void);

        if (!includeUnpaid)
        {
            query = query.Where(r => r.Status == PaymentStatus.Paid);
        }

        var receipts = await query.ToListAsync();

        // 2. Fetch Deductions & Purchases & Capitals
        var deductions = await _context.Deductions
            .AsNoTracking()
            .Where(d => d.Date >= startDateOnly && d.Date < endExclusive)
            .ToListAsync();
            
        var purchases = await _context.PartnerPurchases
            .AsNoTracking()
            .Where(p => p.Date >= startDateOnly && p.Date < endExclusive)
            .ToListAsync();

        var capitals = await _context.PartnerCapitals
            .AsNoTracking()
            .Where(c => c.Date >= startDateOnly && c.Date < endExclusive)
            .ToListAsync();

        // 3. Opening Balances & Partner Names
        var balances = await _context.PartnerBalanceConfigs
            .AsNoTracking()
            .OrderBy(b => b.PartnerName)
            .ToListAsync();
            
        if (balances.Count >= 1) vm.Partner1Name = balances[0].PartnerName;
        if (balances.Count >= 2) vm.Partner2Name = balances[1].PartnerName;
        
        vm.Partner1OpeningBalance = balances.FirstOrDefault(b => b.PartnerName == vm.Partner1Name)?.OpeningBalance ?? 0;
        vm.Partner2OpeningBalance = balances.FirstOrDefault(b => b.PartnerName == vm.Partner2Name)?.OpeningBalance ?? 0;


        // -- CALCULATIONS --

        // Group by Day
        var dailyGroups = receipts.GroupBy(r => r.Date.Date).OrderBy(g => g.Key);

        foreach (var dayGroup in dailyGroups)
        {
            var stat = new DailyProfitStat { Date = dayGroup.Key };
            
            // Sales Amount
            stat.SalesAmount = dayGroup.Sum(r => r.TotalAmount);
            stat.FeeAmount = stat.SalesAmount * (percentFee / 100m);
            
            // Gross Profit
            decimal dayProfit = 0;
            foreach(var r in dayGroup)
            {
                foreach(var line in r.Lines)
                {
                    // Cost Logic: Snapshot > Product.UnitCost > 0
                    decimal cost = line.CostPriceSnapshot;
                    if (cost == 0 && line.Product != null) cost = line.Product.UnitCost; 
                    
                    // Profit = (SellingPrice - Cost) * Quantity
                    decimal lineProfit = (line.Price - cost) * line.Quantity;
                    dayProfit += lineProfit;
                }
            }
            stat.GrossProfit = dayProfit;
            
            vm.DailyStats.Add(stat);
        }

        vm.TotalGrossSales = vm.DailyStats.Sum(s => s.SalesAmount);
        vm.TotalFees = vm.DailyStats.Sum(s => s.FeeAmount);
        vm.TotalGrossProfit = vm.DailyStats.Sum(s => s.GrossProfit);

        vm.Deductions = deductions;
        vm.TotalDeductions = deductions.Sum(d => d.Amount);
        
        vm.CapitalFunds = capitals;
        vm.TotalCapitalFund = capitals.Sum(c => c.Amount);

        // Net Profit = Gross - Deductions - Capital Funds (Retained)
        vm.NetProfit = vm.TotalGrossProfit - vm.TotalDeductions - vm.TotalCapitalFund;

        // Profit Sharing
        vm.Partner1ShareAmount = vm.NetProfit * (vm.Partner1SharePercent / 100m);
        vm.Partner2ShareAmount = vm.NetProfit * (vm.Partner2SharePercent / 100m);

        // Purchases
        vm.PartnerPurchases = purchases;
        vm.TotalPartner1Purchases = purchases.Where(p => p.PartnerName == vm.Partner1Name).Sum(p => p.Amount);
        vm.TotalPartner2Purchases = purchases.Where(p => p.PartnerName == vm.Partner2Name).Sum(p => p.Amount);

        // Final Calculation
        // Share - Purchases
        vm.Partner1Final = vm.Partner1ShareAmount - vm.TotalPartner1Purchases;
        vm.Partner2Final = vm.Partner2ShareAmount - vm.TotalPartner2Purchases;
        
        // Ledger Calculation (Right Side)
        // Assume Ledger tracks General Fund from a configured Opening Balance
        decimal runningBalance = vm.Partner1OpeningBalance; // Use P1 Opening as General for now
        
        var ledgerItems = new List<LedgerRow>();
        
        // Add Deductions (Outflow)
        ledgerItems.AddRange(deductions.Select(d => new LedgerRow { 
            Date = d.Date, 
            Description = d.Description, 
            Amount = -d.Amount 
        }));
        
        // Add Purchases? (If paid from fund). Usually yes.
         ledgerItems.AddRange(purchases.Select(p => new LedgerRow { 
            Date = p.Date, 
            Description = $"{p.PartnerName}: {p.Notes}", 
            Amount = -p.Amount 
        }));

        // Sort by Date
        ledgerItems = ledgerItems.OrderBy(x => x.Date).ToList();
        
        foreach(var row in ledgerItems)
        {
             runningBalance += row.Amount;
             row.Balance = runningBalance;
        }
        vm.Ledger = ledgerItems;

        // -- INTEGRATED SUMMARY ALL DATA --
        vm.TotalPaidReceipts = receipts.Where(r => r.Status == PaymentStatus.Paid).Sum(r => r.TotalAmount);
        vm.TotalUnpaidReceipts = receipts.Where(r => r.Status == PaymentStatus.Unpaid).Sum(r => r.TotalAmount);
        vm.TotalReceiptCount = receipts.Count;
        vm.TotalItemsSold = receipts.SelectMany(r => r.Lines).Sum(l => l.Quantity);

        vm.TopItems = receipts.SelectMany(r => r.Lines)
            .GroupBy(l => l.ItemName)
            .Select(g => new TopItemDto {
                ItemName = g.Key,
                Quantity = g.Sum(l => l.Quantity),
                TotalAmount = g.Sum(l => l.Amount)
            })
            .OrderByDescending(x => x.Quantity)
            .Take(20)
            .ToList();

        vm.OutletSummaries = receipts
            .GroupBy(r => r.CustomerName)
            .Select(g => new OutletSummaryDto {
                OutletName = g.Key,
                PaidAmount = g.Where(r => r.Status == PaymentStatus.Paid).Sum(r => r.TotalAmount),
                UnpaidAmount = g.Where(r => r.Status == PaymentStatus.Unpaid).Sum(r => r.TotalAmount),
                TotalAmount = g.Sum(r => r.TotalAmount)
            })
            .OrderByDescending(x => x.TotalAmount)
            .ToList();

        return View(vm);
    }
    
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDeduction(Deduction model, string returnUrl)
    {
        if(ModelState.IsValid)
        {
            // Default applied to General if unset
            if(string.IsNullOrEmpty(model.AppliedTo)) model.AppliedTo = "General";
            
            _context.Deductions.Add(model);
            await _context.SaveChangesAsync();
        }
        return Redirect(returnUrl);
    }
    
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCapital(PartnerCapital model, string returnUrl)
    {
         if(ModelState.IsValid)
        {
            _context.PartnerCapitals.Add(model);
            await _context.SaveChangesAsync();
        }
        return Redirect(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPurchase(PartnerPurchase model, string returnUrl)
    {
         if(ModelState.IsValid)
        {
            _context.PartnerPurchases.Add(model);
            await _context.SaveChangesAsync();
        }
        return Redirect(returnUrl);
    }
}
