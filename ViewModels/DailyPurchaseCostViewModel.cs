using System.ComponentModel.DataAnnotations;

namespace HazelInvoice.ViewModels;

public class DailyPurchaseCostIndexViewModel
{
    [DataType(DataType.Date)]
    public DateTime TargetDate { get; set; }

    public string? SearchTerm { get; set; }

    public string? SelectedGroupName { get; set; }

    public List<string> AvailableGroupNames { get; set; } = new();

    public int TotalProducts { get; set; }

    public int UpdatedForDateCount { get; set; }

    public int UsingPreviousCostCount => Math.Max(0, TotalProducts - UpdatedForDateCount);

    public List<DailyPurchaseCostItemViewModel> Items { get; set; } = new();
}

public class DailyPurchaseCostItemViewModel
{
    public int ProductId { get; set; }

    public string SKU { get; set; } = string.Empty;

    public string ProductName { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public decimal DefaultUnitCost { get; set; }

    public decimal EffectiveUnitCost { get; set; }

    public DateTime? SourceDate { get; set; }

    public bool IsUpdatedForDate { get; set; }

    [Range(0.01, 999999999, ErrorMessage = "Purchase cost must be greater than zero.")]
    public decimal? PurchaseCostPerUnit { get; set; }
}
