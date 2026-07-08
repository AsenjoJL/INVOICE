namespace HazelInvoice.Services.Pricing;

public interface IDailyPurchaseCostService
{
    Task<IReadOnlyDictionary<int, DailyPurchaseCostSnapshot>> GetEffectiveCostsAsync(
        IEnumerable<int> productIds,
        DateTime costDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, DailyPurchaseCostSnapshot>> GetExactCostsAsync(
        IEnumerable<int> productIds,
        DateTime costDate,
        CancellationToken cancellationToken = default);
}

public sealed record DailyPurchaseCostSnapshot(
    int ProductId,
    DateTime CostDate,
    decimal UnitCost);
