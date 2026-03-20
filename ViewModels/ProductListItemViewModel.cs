namespace HazelInvoice.ViewModels;

public sealed class ProductListItemViewModel
{
    public int Id { get; set; }
    public string SKU { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal UnitCost { get; set; }
    public decimal EffectiveDeliveryPrice { get; set; }
    public bool HasWeeklyPrice { get; set; }
    public bool IsActive { get; set; }
}
