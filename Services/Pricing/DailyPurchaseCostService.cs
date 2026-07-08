using HazelInvoice.Data;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Services.Pricing;

public sealed class DailyPurchaseCostService : IDailyPurchaseCostService
{
    private readonly ApplicationDbContext _context;

    public DailyPurchaseCostService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyDictionary<int, DailyPurchaseCostSnapshot>> GetEffectiveCostsAsync(
        IEnumerable<int> productIds,
        DateTime costDate,
        CancellationToken cancellationToken = default)
    {
        var ids = NormalizeProductIds(productIds);
        if (ids.Count == 0)
            return new Dictionary<int, DailyPurchaseCostSnapshot>();

        var targetDate = costDate.Date;
        var costs = await _context.DailyPurchaseCosts
            .AsNoTracking()
            .Where(c => ids.Contains(c.ProductId) && c.CostDate <= targetDate)
            .Select(c => new DailyPurchaseCostSnapshot(c.ProductId, c.CostDate, c.UnitCost))
            .ToListAsync(cancellationToken);

        return costs
            .GroupBy(c => c.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(c => c.CostDate).First());
    }

    public async Task<IReadOnlyDictionary<int, DailyPurchaseCostSnapshot>> GetExactCostsAsync(
        IEnumerable<int> productIds,
        DateTime costDate,
        CancellationToken cancellationToken = default)
    {
        var ids = NormalizeProductIds(productIds);
        if (ids.Count == 0)
            return new Dictionary<int, DailyPurchaseCostSnapshot>();

        var targetDate = costDate.Date;
        return await _context.DailyPurchaseCosts
            .AsNoTracking()
            .Where(c => ids.Contains(c.ProductId) && c.CostDate == targetDate)
            .Select(c => new DailyPurchaseCostSnapshot(c.ProductId, c.CostDate, c.UnitCost))
            .ToDictionaryAsync(c => c.ProductId, c => c, cancellationToken);
    }

    private static List<int> NormalizeProductIds(IEnumerable<int> productIds)
    {
        return productIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }
}
