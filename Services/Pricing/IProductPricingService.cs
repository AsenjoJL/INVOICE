namespace HazelInvoice.Services.Pricing;

/// <summary>
/// Single source of truth for product prices shown in catalog, orders, templates, and receipt snapshots.
/// Keep weekly/reset/fallback pricing changes here so screens cannot drift apart.
/// </summary>
public interface IProductPricingService
{
    Task<IReadOnlyDictionary<int, EffectiveProductPrice>> GetEffectivePricesAsync(
        IEnumerable<int> productIds,
        DateTime priceDate,
        CancellationToken cancellationToken = default);
}

public sealed record EffectiveProductPrice(
    int ProductId,
    decimal Cost,
    decimal Markup,
    decimal BasePrice,
    decimal DeliveryFee,
    decimal DeliveryPrice,
    bool HasWeeklyPrice,
    bool IsResetDay);
