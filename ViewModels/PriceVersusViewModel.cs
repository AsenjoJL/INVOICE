using System;
using System.Collections.Generic;

namespace HazelInvoice.ViewModels;

public class PriceVersusViewModel
{
    public DateTime TargetDate { get; set; } = DateTime.Today;
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd { get; set; }
    public bool ApplyToMasterCost { get; set; } = false;
    public string SearchTerm { get; set; } = string.Empty;
    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 40;
    public int TotalItems { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalItems / (double)Math.Max(PageSize, 1)));
    
    public List<PriceVersusItem> Items { get; set; } = new();
}

public class PriceVersusItem
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    
    public decimal Cost { get; set; } // Original / UnitCost
    public decimal Markup { get; set; }
    public decimal BasePrice { get; set; }
    public decimal DeliveryPrice { get; set; }
    public decimal? DeliveryFee { get; set; }

    public decimal MasterCost { get; set; }
    public decimal MasterMarkup { get; set; }
    public decimal MasterDeliveryFee { get; set; }
    
    public bool HasWeeklyRecord { get; set; }
}
