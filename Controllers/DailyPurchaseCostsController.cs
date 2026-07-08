using HazelInvoice.Data;
using HazelInvoice.Helpers;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Pricing;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class DailyPurchaseCostsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IDailyPurchaseCostService _dailyPurchaseCosts;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public DailyPurchaseCostsController(
        ApplicationDbContext context,
        IDailyPurchaseCostService dailyPurchaseCosts,
        IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _dailyPurchaseCosts = dailyPurchaseCosts;
        _cacheInvalidator = cacheInvalidator;
    }

    [HttpGet]
    public async Task<IActionResult> Index(DateTime? date, string? q, CancellationToken cancellationToken)
    {
        var targetDate = (date ?? BusinessDate.Today()).Date;
        var model = await BuildIndexModelAsync(targetDate, q, cancellationToken);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(DailyPurchaseCostIndexViewModel model, CancellationToken cancellationToken)
    {
        var targetDate = model.TargetDate.Date;
        var postedItems = model.Items
            .Where(i => i.ProductId > 0 && i.PurchaseCostPerUnit.HasValue)
            .ToList();

        if (postedItems.Any(i => i.PurchaseCostPerUnit <= 0m))
        {
            ModelState.AddModelError(string.Empty, "Purchase cost must be greater than zero.");
        }

        if (!ModelState.IsValid)
        {
            var invalidModel = await BuildIndexModelAsync(targetDate, model.SearchTerm, cancellationToken);
            var postedMap = model.Items
                .Where(i => i.ProductId > 0)
                .GroupBy(i => i.ProductId)
                .ToDictionary(g => g.Key, g => g.Last().PurchaseCostPerUnit);

            foreach (var item in invalidModel.Items)
            {
                if (postedMap.TryGetValue(item.ProductId, out var postedCost))
                    item.PurchaseCostPerUnit = postedCost;
            }

            return View("Index", invalidModel);
        }

        if (postedItems.Count == 0)
        {
            TempData["Message"] = "No daily purchase costs were entered.";
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = model.SearchTerm });
        }

        var productIds = postedItems.Select(i => i.ProductId).Distinct().ToList();
        var activeProductIds = (await _context.Products
            .Where(p => productIds.Contains(p.Id) && p.IsActive)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var existingCosts = await _context.DailyPurchaseCosts
            .Where(c => productIds.Contains(c.ProductId) && c.CostDate == targetDate)
            .ToDictionaryAsync(c => c.ProductId, cancellationToken);

        var now = DateTime.Now;
        var saved = 0;
        var postedCostByProduct = new Dictionary<int, decimal>();
        foreach (var item in postedItems)
        {
            if (!activeProductIds.Contains(item.ProductId) || !item.PurchaseCostPerUnit.HasValue)
                continue;

            var unitCost = Math.Round(item.PurchaseCostPerUnit.Value, 2, MidpointRounding.AwayFromZero);
            postedCostByProduct[item.ProductId] = unitCost;
            if (existingCosts.TryGetValue(item.ProductId, out var existing))
            {
                if (existing.UnitCost != unitCost)
                {
                    existing.UnitCost = unitCost;
                    existing.UpdatedAt = now;
                    existing.RecordedById = User.Identity?.Name;
                    saved++;
                }
            }
            else
            {
                _context.DailyPurchaseCosts.Add(new DailyPurchaseCost
                {
                    ProductId = item.ProductId,
                    CostDate = targetDate,
                    UnitCost = unitCost,
                    CreatedAt = now,
                    UpdatedAt = now,
                    RecordedById = User.Identity?.Name
                });
                saved++;
            }

        }

        await RefreshSameDayReceiptCostsAsync(targetDate, postedCostByProduct, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();
        _cacheInvalidator.InvalidateWeeklyPrices();

        TempData["Message"] = saved == 0
            ? "Daily purchase costs were already up to date."
            : $"Saved {saved} daily purchase cost update{(saved == 1 ? "" : "s")}.";

        return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = model.SearchTerm });
    }

    private async Task RefreshSameDayReceiptCostsAsync(
        DateTime targetDate,
        IReadOnlyDictionary<int, decimal> costByProduct,
        CancellationToken cancellationToken)
    {
        if (costByProduct.Count == 0)
            return;

        var dayStart = targetDate.Date;
        var dayEnd = dayStart.AddDays(1);
        var productIds = costByProduct.Keys.ToList();

        var receiptLines = await _context.ReceiptLines
            .Include(l => l.Receipt)
            .Where(l => l.ProductId.HasValue &&
                        productIds.Contains(l.ProductId.Value) &&
                        l.Receipt != null &&
                        l.Receipt.Status != PaymentStatus.Void &&
                        l.Receipt.Date >= dayStart &&
                        l.Receipt.Date < dayEnd)
            .ToListAsync(cancellationToken);

        foreach (var line in receiptLines)
        {
            if (line.ProductId.HasValue && costByProduct.TryGetValue(line.ProductId.Value, out var unitCost))
            {
                line.CostPriceSnapshot = unitCost;
            }
        }
    }

    private async Task<DailyPurchaseCostIndexViewModel> BuildIndexModelAsync(
        DateTime targetDate,
        string? searchTerm,
        CancellationToken cancellationToken)
    {
        var normalizedSearch = searchTerm?.Trim() ?? string.Empty;
        var productsQuery = _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            productsQuery = productsQuery.Where(p =>
                p.Name.Contains(normalizedSearch) ||
                p.SKU.Contains(normalizedSearch) ||
                p.Unit.Contains(normalizedSearch) ||
                p.Category.Contains(normalizedSearch));
        }

        var products = await productsQuery
            .OrderBy(p => p.Name)
            .ThenBy(p => p.SKU)
            .Select(p => new
            {
                p.Id,
                p.SKU,
                p.Name,
                p.Unit,
                p.UnitCost
            })
            .ToListAsync(cancellationToken);

        var productIds = products.Select(p => p.Id).ToList();
        var effectiveCosts = await _dailyPurchaseCosts.GetEffectiveCostsAsync(productIds, targetDate, cancellationToken);
        var exactCosts = await _dailyPurchaseCosts.GetExactCostsAsync(productIds, targetDate, cancellationToken);

        var items = products.Select(p =>
        {
            effectiveCosts.TryGetValue(p.Id, out var effectiveCost);
            exactCosts.TryGetValue(p.Id, out var exactCost);
            var activeCost = effectiveCost?.UnitCost ?? p.UnitCost;

            return new DailyPurchaseCostItemViewModel
            {
                ProductId = p.Id,
                SKU = p.SKU,
                ProductName = p.Name,
                Unit = p.Unit,
                DefaultUnitCost = p.UnitCost,
                EffectiveUnitCost = activeCost,
                SourceDate = effectiveCost?.CostDate,
                IsUpdatedForDate = exactCost != null,
                PurchaseCostPerUnit = exactCost?.UnitCost
            };
        }).ToList();

        return new DailyPurchaseCostIndexViewModel
        {
            TargetDate = targetDate,
            SearchTerm = normalizedSearch,
            TotalProducts = products.Count,
            UpdatedForDateCount = exactCosts.Count,
            Items = items
        };
    }
}
