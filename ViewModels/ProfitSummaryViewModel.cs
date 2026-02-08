using System;
using System.Collections.Generic;
using HazelInvoice.Models;

namespace HazelInvoice.ViewModels;

public class ProfitSummaryViewModel
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public bool IncludeUnpaid { get; set; }
    public decimal PercentFee { get; set; } = 1.0m;

    // A & B: Daily Data
    public List<DailyProfitStat> DailyStats { get; set; } = new();
    
    // Totals for A & B
    public decimal TotalGrossSales { get; set; }
    public decimal TotalFees { get; set; }
    public decimal TotalGrossProfit { get; set; }

    // C: Deductions
    public List<Deduction> Deductions { get; set; } = new();
    public decimal TotalDeductions { get; set; }
    public decimal NetProfit { get; set; } // GrossProfit - Deductions

    // D: Profit Sharing
    // Fixed names for now based on prompt examples "Hazel" / "Troy" but adaptable
    public string Partner1Name { get; set; } = "Troy";
    public string Partner2Name { get; set; } = "Hazel";

    public decimal Partner1SharePercent { get; set; } = 40m;
    public decimal Partner2SharePercent { get; set; } = 60m;
    
    public decimal Partner1ShareAmount { get; set; }
    public decimal Partner2ShareAmount { get; set; }
    
    // E: Partner Purchases
    public List<PartnerPurchase> PartnerPurchases { get; set; } = new();
    public decimal TotalPartner1Purchases { get; set; }
    public decimal TotalPartner2Purchases { get; set; }
    
    // D Extension: Capital Funds
    public List<PartnerCapital> CapitalFunds { get; set; } = new();
    public decimal TotalCapitalFund { get; set; }

    // F: Ledger / Config
    public decimal Partner1OpeningBalance { get; set; }
    public decimal Partner2OpeningBalance { get; set; }
    public List<LedgerRow> Ledger { get; set; } = new();

    // G: Final Calculation
    public decimal Partner1Final { get; set; }
    public decimal Partner2Final { get; set; }

    // -- Integrated Summary All Data --
    public decimal TotalPaidReceipts { get; set; }
    public decimal TotalUnpaidReceipts { get; set; }
    public int TotalReceiptCount { get; set; }
    public decimal TotalItemsSold { get; set; }
    
    public List<TopItemDto> TopItems { get; set; } = new();
    public List<OutletSummaryDto> OutletSummaries { get; set; } = new();
}

public class LedgerRow 
{
    public DateTime Date { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public decimal Balance { get; set; }
}

public class DailyProfitStat
{
    public DateTime Date { get; set; }
    public decimal SalesAmount { get; set; }
    public decimal FeeAmount { get; set; }
    public decimal GrossProfit { get; set; }
}


