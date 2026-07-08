using HazelInvoice.Data;
using HazelInvoice.Helpers;
using HazelInvoice.Models;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Services.Pricing;

public sealed class ProductPricingService : IProductPricingService
{
    private readonly ApplicationDbContext _context;
    private readonly IDailyPurchaseCostService _dailyPurchaseCosts;

    public ProductPricingService(
        ApplicationDbContext context,
        IDailyPurchaseCostService dailyPurchaseCosts)
    {
        _context = context;
        _dailyPurchaseCosts = dailyPurchaseCosts;
    }

    public async Task<IReadOnlyDictionary<int, EffectiveProductPrice>> GetEffectivePricesAsync(
        IEnumerable<int> productIds,
        DateTime priceDate,
        CancellationToken cancellationToken = default)
    {
        var ids = productIds
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<int, EffectiveProductPrice>();

        // The calendar owns reset timing. Callers pass the business/order date;
        // this service decides whether weekly prices or zero reset prices apply.
        var isResetDay = WeeklyPriceCalendar.IsResetDay(priceDate);
        var applicableDate = WeeklyPriceCalendar.GetApplicablePriceDate(priceDate);

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new ProductPriceSnapshot(
                p.Id,
                p.UnitCost,
                p.Markup,
                p.DeliveryFee))
            .ToListAsync(cancellationToken);

        var weeklyPrices = applicableDate.HasValue
            ? await _context.WeeklyPrices
                .AsNoTracking()
                .Where(w => ids.Contains(w.ProductId) &&
                            w.EffectiveFrom <= applicableDate.Value &&
                            w.EffectiveTo >= applicableDate.Value)
                .ToListAsync(cancellationToken)
            : new List<WeeklyPrice>();

        var weeklyPriceMap = weeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.EffectiveFrom)
                      .ThenByDescending(w => w.Id)
                      .First());

        var dailyCostMap = await _dailyPurchaseCosts.GetEffectiveCostsAsync(ids, priceDate, cancellationToken);

        return products.ToDictionary(
            p => p.Id,
            p => BuildEffectivePrice(
                p,
                weeklyPriceMap.GetValueOrDefault(p.Id),
                dailyCostMap.GetValueOrDefault(p.Id),
                isResetDay));
    }

    private static EffectiveProductPrice BuildEffectivePrice(
        ProductPriceSnapshot product,
        WeeklyPrice? weeklyPrice,
        DailyPurchaseCostSnapshot? dailyCost,
        bool isResetDay)
    {
        if (isResetDay)
        {
            return new EffectiveProductPrice(
                product.Id,
                Cost: 0m,
                Markup: 0m,
                BasePrice: 0m,
                DeliveryFee: 0m,
                DeliveryPrice: 0m,
                HasWeeklyPrice: false,
                IsResetDay: true);
        }

        var cost = dailyCost?.UnitCost ?? weeklyPrice?.CostOverride ?? product.UnitCost;
        if (weeklyPrice is { DeliveryPrice: 0m, BasePrice: 0m })
        {
            return new EffectiveProductPrice(
                product.Id,
                cost,
                Markup: -cost,
                BasePrice: 0m,
                DeliveryFee: 0m,
                DeliveryPrice: 0m,
                HasWeeklyPrice: true,
                IsResetDay: false);
        }

        var markup = product.Markup;
        var deliveryFee = weeklyPrice?.DeliveryFee ?? product.DeliveryFee;

        if (weeklyPrice != null)
        {
            if (weeklyPrice.Markup != 0m)
            {
                markup = weeklyPrice.Markup;
            }
            else if (weeklyPrice.BasePrice > 0m)
            {
                markup = weeklyPrice.BasePrice - cost;
            }
        }

        var basePrice = cost + markup;
        var deliveryPrice = weeklyPrice != null
            ? Math.Max(weeklyPrice.DeliveryPrice, 0m)
            : basePrice + deliveryFee;

        if (weeklyPrice != null)
        {
            deliveryFee = deliveryPrice - basePrice;
        }

        return new EffectiveProductPrice(
            product.Id,
            cost,
            markup,
            basePrice,
            deliveryFee,
            deliveryPrice,
            HasWeeklyPrice: weeklyPrice != null,
            IsResetDay: false);
    }

    private sealed record ProductPriceSnapshot(
        int Id,
        decimal UnitCost,
        decimal Markup,
        decimal DeliveryFee);
}
