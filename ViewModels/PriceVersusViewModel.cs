using System;
using System.Collections.Generic;

namespace HazelInvoice.ViewModels;

public class PriceVersusViewModel
{
    public DateTime TargetDate { get; set; } = DateTime.Today;
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd { get; set; }
    public bool ApplyToMasterCost { get; set; } = false;
    
    public List<PriceVersusItem> Items { get; set; } = new();
}

public class PriceVersusItem
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    
    public decimal Cost { get; set; } // Original / UnitCost
    public decimal Markup { get; set; }
    public decimal DeliveryFee { get; set; }

    public decimal MasterCost { get; set; }
    public decimal MasterMarkup { get; set; }
    public decimal MasterDeliveryFee { get; set; }
    
    public bool HasWeeklyRecord { get; set; }
}
