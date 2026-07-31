using HazelInvoice.Data;
using HazelInvoice.Helpers;
using HazelInvoice.Models;
using HazelInvoice.Services;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Orders;
using HazelInvoice.Services.Pricing;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class DailyPurchaseCostsController : Controller
{
    private const int MaxImportFileBytes = 20 * 1024 * 1024;

    private readonly ApplicationDbContext _context;
    private readonly IDailyPurchaseCostService _dailyPurchaseCosts;
    private readonly IAppCacheInvalidator _cacheInvalidator;
    private readonly HazelInvoice.Services.Clients.IClientGroupService _clientGroups;

    public DailyPurchaseCostsController(
        ApplicationDbContext context,
        IDailyPurchaseCostService dailyPurchaseCosts,
        IAppCacheInvalidator cacheInvalidator,
        HazelInvoice.Services.Clients.IClientGroupService clientGroups)
    {
        _context = context;
        _dailyPurchaseCosts = dailyPurchaseCosts;
        _cacheInvalidator = cacheInvalidator;
        _clientGroups = clientGroups;
    }

    [HttpGet]
    public async Task<IActionResult> Index(DateTime? date, string? q, string? groupName, CancellationToken cancellationToken)
    {
        var targetDate = (date ?? BusinessDate.Today()).Date;
        var model = await BuildIndexModelAsync(targetDate, q, groupName, cancellationToken);
        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadTemplate(DateTime? date, string? q, string? groupName, CancellationToken cancellationToken)
    {
        var targetDate = (date ?? BusinessDate.Today()).Date;
        // Templates are always full active-product exports so newly added items are included.
        var model = await BuildIndexModelAsync(targetDate, null, groupName, cancellationToken);

        var rows = new List<IReadOnlyList<string>>
        {
            new[] { "DAILY PURCHASE COST TEMPLATE" },
            new[] { "Cost Date", targetDate.ToString("yyyy-MM-dd") },
            new[] { "Fill PRICE only for items with new purchase cost. Blank prices will be skipped on import." },
            new[] { string.Empty },
            new[] { "LIST OF ITEMS", "PRICE", "UOM", "SKU" }
        };

        rows.AddRange(model.Items.Select(item => new[]
        {
            item.ProductName,
            item.PurchaseCostPerUnit?.ToString("0.##") ?? string.Empty,
            item.Unit,
            item.SKU
        }));

        var bytes = SimpleXlsxWriter.WriteSingleSheet(
            "Daily Purchase Cost",
            rows,
            new SimpleXlsxSheetOptions
            {
                AutoFitColumns = true
            });
        var safeGroup = string.IsNullOrWhiteSpace(groupName) ? "All" : System.Text.RegularExpressions.Regex.Replace(groupName, "[^A-Za-z0-9]+", "_").Trim('_');
        var fileName = $"DailyPurchaseCostTemplate_{safeGroup}_{targetDate:yyyy-MM-dd}.xlsx";
        return File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportTemplate(
        IFormFile? importFile,
        DateTime targetDate,
        string? searchTerm,
        string? groupName,
        CancellationToken cancellationToken)
    {
        targetDate = targetDate.Date;

        if (importFile == null || importFile.Length == 0)
        {
            TempData["ErrorMessage"] = "Please choose an Excel (.xlsx) daily purchase cost template.";
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = searchTerm, groupName });
        }

        if (!string.Equals(Path.GetExtension(importFile.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "Invalid file type. Please upload an .xlsx file.";
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = searchTerm, groupName });
        }

        if (importFile.Length > MaxImportFileBytes)
        {
            TempData["ErrorMessage"] = "File is too large. Maximum size is 20 MB.";
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = searchTerm, groupName });
        }

        await using var stream = new MemoryStream();
        await importFile.CopyToAsync(stream, cancellationToken);

        SimpleXlsxSheet sheet;
        try
        {
            sheet = SimpleXlsxReader.ReadFirstSheet(stream);
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Unable to read Excel file: {ex.Message}";
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = searchTerm });
        }

        var headerRow = FindTemplateHeaderRow(sheet);
        if (headerRow <= 0)
        {
            TempData["ErrorMessage"] = "Could not find the daily purchase cost header row.";
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = searchTerm });
        }

        var itemCol = FindColumnByHeader(sheet, headerRow, "listofitems", "item", "items", "product", "productname", "particulars");
        var priceCol = FindColumnByHeader(sheet, headerRow, "price", "purchasecost", "dailycost", "dailypurchasecost", "unitcost", "cost");
        var skuCol = FindColumnByHeader(sheet, headerRow, "sku", "code");

        if (itemCol <= 0 || priceCol <= 0)
        {
            TempData["ErrorMessage"] = "The template must contain LIST OF ITEMS and PRICE columns.";
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = searchTerm });
        }

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync(cancellationToken);
        var productMap = OrderImportHelpers.BuildProductLookup(products);
        var importedItems = new List<DailyPurchaseCostItemViewModel>();
        var unmatched = new List<string>();
        var invalidPrices = new List<string>();

        for (var row = headerRow + 1; row <= sheet.MaxRow; row++)
        {
            var productName = sheet.GetCell(row, itemCol).Trim();
            var sku = skuCol > 0 ? sheet.GetCell(row, skuCol).Trim() : string.Empty;
            var rawPrice = sheet.GetCell(row, priceCol).Trim();

            if (string.IsNullOrWhiteSpace(productName) && string.IsNullOrWhiteSpace(sku) && string.IsNullOrWhiteSpace(rawPrice))
                continue;

            if (string.IsNullOrWhiteSpace(rawPrice))
                continue;

            if (!SimpleXlsxReader.TryParseDecimal(rawPrice, out var purchaseCost) || purchaseCost <= 0m)
            {
                invalidPrices.Add(string.IsNullOrWhiteSpace(productName) ? $"row {row}" : productName);
                continue;
            }

            Product? product = null;
            if (!string.IsNullOrWhiteSpace(sku) &&
                productMap.TryGetValue(OrderImportHelpers.NormalizeKey(sku), out var skuProduct))
            {
                product = skuProduct;
            }
            else if (!string.IsNullOrWhiteSpace(productName) &&
                     OrderImportHelpers.TryResolveProductByName(productName, productMap, out var nameProduct))
            {
                product = nameProduct;
            }

            if (product == null)
            {
                unmatched.Add(string.IsNullOrWhiteSpace(productName) ? $"row {row}" : productName);
                continue;
            }

            importedItems.Add(new DailyPurchaseCostItemViewModel
            {
                ProductId = product.Id,
                PurchaseCostPerUnit = purchaseCost
            });
        }

        if (invalidPrices.Count > 0)
        {
            TempData["ErrorMessage"] = $"Invalid purchase cost for: {string.Join(", ", invalidPrices.Take(8))}{(invalidPrices.Count > 8 ? "..." : "")}. Costs must be greater than zero.";
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = searchTerm });
        }

        if (importedItems.Count == 0)
        {
            TempData["ErrorMessage"] = unmatched.Count > 0
                ? $"No purchase costs were imported. Unmatched items: {string.Join(", ", unmatched.Take(8))}{(unmatched.Count > 8 ? "..." : "")}."
                : "No purchase costs were found in the template.";
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = searchTerm });
        }

        var saved = await SavePurchaseCostsAsync(
            targetDate,
            importedItems
                .GroupBy(i => i.ProductId)
                .Select(g => g.Last())
                .ToList(),
            cancellationToken);

        var unmatchedNote = unmatched.Count > 0
            ? $" Unmatched items skipped: {string.Join(", ", unmatched.Take(8))}{(unmatched.Count > 8 ? "..." : "")}."
            : string.Empty;
        TempData["Message"] = saved == 0
            ? $"Imported template, but daily purchase costs were already up to date.{unmatchedNote}"
            : $"Imported and saved {saved} daily purchase cost update{(saved == 1 ? "" : "s")}.{unmatchedNote}";

        return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd") });
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
            var invalidModel = await BuildIndexModelAsync(targetDate, model.SearchTerm, model.SelectedGroupName, cancellationToken);
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
            return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = model.SearchTerm, groupName = model.SelectedGroupName });
        }

        var saved = await SavePurchaseCostsAsync(targetDate, postedItems, cancellationToken);

        TempData["Message"] = saved == 0
            ? "Daily purchase costs were already up to date."
            : $"Saved {saved} daily purchase cost update{(saved == 1 ? "" : "s")}.";

        return RedirectToAction(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = model.SearchTerm, groupName = model.SelectedGroupName });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveItem(
        DateTime targetDate,
        string? searchTerm,
        string? groupName,
        int productId,
        decimal? purchaseCostPerUnit,
        CancellationToken cancellationToken)
    {
        targetDate = targetDate.Date;
        var anchor = $"cost-row-{productId}";
        var returnUrl = BuildIndexReturnUrl(targetDate, searchTerm, groupName, anchor);

        if (productId <= 0)
        {
            TempData["ErrorMessage"] = "Could not identify the product to update.";
            return LocalRedirect(returnUrl);
        }

        if (!purchaseCostPerUnit.HasValue || purchaseCostPerUnit <= 0m)
        {
            TempData["ErrorMessage"] = "Purchase cost must be greater than zero.";
            return LocalRedirect(returnUrl);
        }

        var item = new DailyPurchaseCostItemViewModel
        {
            ProductId = productId,
            PurchaseCostPerUnit = purchaseCostPerUnit
        };

        var saved = await SavePurchaseCostsAsync(targetDate, new List<DailyPurchaseCostItemViewModel> { item }, cancellationToken);
        TempData["Message"] = saved == 0
            ? "This purchase cost was already up to date."
            : "Saved purchase cost for this item.";

        return LocalRedirect(returnUrl);
    }

    private async Task<int> SavePurchaseCostsAsync(
        DateTime targetDate,
        List<DailyPurchaseCostItemViewModel> postedItems,
        CancellationToken cancellationToken)
    {
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

        return saved;
    }

    private string BuildIndexReturnUrl(DateTime targetDate, string? searchTerm, string? groupName, string anchor)
    {
        var url = Url.Action(nameof(Index), new { date = targetDate.ToString("yyyy-MM-dd"), q = searchTerm, groupName }) ?? "/DailyPurchaseCosts";
        return $"{url}#{anchor}";
    }

    private static int FindTemplateHeaderRow(SimpleXlsxSheet sheet)
    {
        for (var row = 1; row <= Math.Min(sheet.MaxRow, 12); row++)
        {
            var hasItem = false;
            var hasPrice = false;
            for (var col = 1; col <= sheet.MaxCol; col++)
            {
                var key = OrderImportHelpers.NormalizeKey(sheet.GetCell(row, col));
                if (key is "listofitems" or "item" or "items" or "product" or "productname" or "particulars")
                    hasItem = true;
                if (key is "price" or "purchasecost" or "dailycost" or "dailypurchasecost" or "unitcost" or "cost")
                    hasPrice = true;
            }

            if (hasItem && hasPrice)
                return row;
        }

        return -1;
    }

    private static int FindColumnByHeader(SimpleXlsxSheet sheet, int headerRow, params string[] expectedKeys)
    {
        var expected = expectedKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var col = 1; col <= sheet.MaxCol; col++)
        {
            var key = OrderImportHelpers.NormalizeKey(sheet.GetCell(headerRow, col));
            if (expected.Contains(key))
                return col;
        }

        return -1;
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
        string? groupName,
        CancellationToken cancellationToken)
    {
        var normalizedSearch = searchTerm?.Trim() ?? string.Empty;
        var selectedGroup = await _clientGroups.ResolveClientGroupOrDefaultAsync(groupName, cancellationToken);
        var availableGroups = await _clientGroups.GetClientGroupNamesAsync(cancellationToken);
        
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
        else if (selectedGroup != "All")
        {
            var includedGroups = await _clientGroups.ResolveOutletGroupsForClientAsync(selectedGroup, cancellationToken);
            var dayStart = targetDate.Date;
            var dayEnd = dayStart.AddDays(1);

            var outletsAll = await _context.Customers
                .AsNoTracking()
                .Where(c => c.IsActive && includedGroups.Contains(c.GroupName))
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(cancellationToken);
                
            var allOutletIds = outletsAll.Select(c => c.Id).ToList();
            var allOutletNames = outletsAll.Select(c => c.Name).ToList();

            var productIdsInDay = await _context.ReceiptLines
                .AsNoTracking()
                .Where(l => l.ProductId != null)
                .Join(_context.Receipts.AsNoTracking(),
                    l => l.ReceiptId,
                    r => r.Id,
                    (l, r) => new { l, r })
                .Where(x => x.r.Date >= dayStart && x.r.Date < dayEnd &&
                            x.r.Status != PaymentStatus.Void &&
                            ((x.r.CustomerId.HasValue && allOutletIds.Contains(x.r.CustomerId.Value)) ||
                             (!x.r.CustomerId.HasValue && allOutletNames.Contains(x.r.CustomerName))))
                .Select(x => x.l.ProductId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            // Filter to products ordered today OR products that already have a daily cost for today
            var costsToday = await _context.DailyPurchaseCosts
                .AsNoTracking()
                .Where(c => c.CostDate == dayStart)
                .Select(c => c.ProductId)
                .ToListAsync(cancellationToken);

            var combinedIds = productIdsInDay.Concat(costsToday).Distinct().ToList();
            productsQuery = productsQuery.Where(p => combinedIds.Contains(p.Id));
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
            SelectedGroupName = selectedGroup,
            AvailableGroupNames = availableGroups.ToList(),
            TotalProducts = products.Count,
            UpdatedForDateCount = exactCosts.Count,
            Items = items
        };
    }
}
