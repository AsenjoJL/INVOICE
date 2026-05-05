using HazelInvoice.Data;
using HazelInvoice.Configuration;
using HazelInvoice.Helpers;
using HazelInvoice.Models;
using HazelInvoice.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HazelInvoice.Services.Orders;

public sealed class VegetableMatrixTemplateService : IVegetableMatrixTemplateService
{
    private readonly ApplicationDbContext _context;
    private readonly HashSet<string> _outletGroups;
    private readonly string _productHeader;
    private readonly string _priceHeader;

    public VegetableMatrixTemplateService(ApplicationDbContext context, IOptions<OperationsOptions> operations)
    {
        _context = context;
        _outletGroups = (operations.Value.OutletGroups ?? []).Where(g => !string.IsNullOrWhiteSpace(g))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _productHeader = string.IsNullOrWhiteSpace(operations.Value.VegetableTemplateProductHeader)
            ? "Vegetables"
            : operations.Value.VegetableTemplateProductHeader;
        _priceHeader = string.IsNullOrWhiteSpace(operations.Value.VegetableTemplatePriceHeader)
            ? "Price"
            : operations.Value.VegetableTemplatePriceHeader;
    }

    public async Task<byte[]> BuildTemplateAsync(DateTime date, CancellationToken cancellationToken = default)
    {
        // Keep template compatible with ImportVegetableMatrixExcel:
        // - Header row must have "Vegetables" in column A.
        // - Outlet headers should match Customer.Name values.
        var outlets = await _context.Customers
            .AsNoTracking()
            .Where(c => c.IsActive && c.GroupName != null && _outletGroups.Contains(c.GroupName))
            .OrderBy(c => c.GroupName)
            .ThenBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync(cancellationToken);

        var day = date.Date;
        var applicableDay = WeeklyPriceCalendar.GetApplicablePriceDate(day);

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.UnitCost,
                p.Markup,
                p.DeliveryFee
            })
            .ToListAsync(cancellationToken);

        var productIds = products.Select(p => p.Id).ToList();
        var weeklyPrices = applicableDay.HasValue
            ? await _context.WeeklyPrices
                .AsNoTracking()
                .Where(w => productIds.Contains(w.ProductId) && w.EffectiveFrom <= applicableDay.Value && w.EffectiveTo >= applicableDay.Value)
                .ToListAsync(cancellationToken)
            : new List<WeeklyPrice>();

        var weeklyPriceMap = weeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.EffectiveFrom)
                      .ThenByDescending(w => w.Id)
                      .First());

        // Rows: title row, header row, product rows.
        var rows = new List<IReadOnlyList<string>>(capacity: 2 + products.Count);

        rows.Add(new[]
        {
            "VEGETABLE ORDER MATRIX TEMPLATE",
            $"Delivery Date: {date:yyyy-MM-dd}"
        });

        var header = new List<string>(capacity: 2 + outlets.Count)
        {
            _productHeader,
            _priceHeader
        };
        header.AddRange(outlets);
        rows.Add(header);

        foreach (var product in products)
        {
            var effectivePrice = WeeklyPriceCalendar.IsResetDay(day)
                ? 0m
                : weeklyPriceMap.TryGetValue(product.Id, out var weeklyPrice) && weeklyPrice.DeliveryPrice > 0
                    ? weeklyPrice.DeliveryPrice
                    : product.UnitCost + product.Markup + product.DeliveryFee;

            // Keep columns A/B only; outlet qty cells intentionally blank.
            rows.Add(new[]
            {
                product.Name ?? string.Empty,
                effectivePrice.ToString("0.00")
            });
        }

        return SimpleXlsxWriter.WriteSingleSheet("Template", rows);
    }
}
