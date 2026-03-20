using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

using HazelInvoice.ViewModels;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services;
using HazelInvoice.Services.Orders;

namespace HazelInvoice.Controllers;

[Authorize]
public class WeeklyPricesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILookupCacheService _lookupCache;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public WeeklyPricesController(ApplicationDbContext context, ILookupCacheService lookupCache, IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _lookupCache = lookupCache;
        _cacheInvalidator = cacheInvalidator;
    }

    // GET: WeeklyPrices/PriceVersus
    public async Task<IActionResult> PriceVersus(DateTime? date, string? q = null, int page = 1, int pageSize = 40)
    {
        var targetDate = date ?? DateTime.Today;
        var vm = await BuildPriceVersusModelAsync(targetDate, q, page, pageSize);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PriceVersus(PriceVersusViewModel model, int? singleProductId = null)
    {
        if (model.Items == null || model.Items.Count == 0)
        {
            return RedirectToPriceVersus(model.TargetDate, model.SearchTerm, model.CurrentPage, model.PageSize);
        }

        var itemsToSave = GetItemsToSave(model, singleProductId);

        if (itemsToSave.Count == 0)
        {
            TempData["ErrorMessage"] = "No price row was selected to save.";
            return RedirectToPriceVersus(model.TargetDate, model.SearchTerm, model.CurrentPage, model.PageSize);
        }

        var saveResult = await SavePriceItemsAsync(model.TargetDate, model.ApplyToMasterCost, itemsToSave);
        TempData["SuccessMessage"] = saveResult.SavedChanges > 0
            ? singleProductId.HasValue
                ? "Saved the selected product price."
                : $"Saved changes for {saveResult.SavedChanges} price record(s)."
            : "No price changes were detected.";

        return RedirectToPriceVersus(model.TargetDate, model.SearchTerm, model.CurrentPage, model.PageSize);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportTemplate(IFormFile? importFile, DateTime targetDate, string? importMode = null, string? searchTerm = null, int page = 1, int pageSize = 40, string? returnUrl = null)
    {
        var normalizedImportMode = NormalizeImportMode(importMode);
        if (importFile == null || importFile.Length == 0)
        {
            TempData["ErrorMessage"] = "Please choose an Excel (.xlsx) price template.";
            return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
        }

        if (!string.Equals(Path.GetExtension(importFile.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "Invalid file type. Please upload an .xlsx file.";
            return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
        }

        await using var stream = new MemoryStream();
        await importFile.CopyToAsync(stream);

        SimpleXlsxSheet sheet;
        try
        {
            sheet = SimpleXlsxReader.ReadFirstSheet(stream);
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Unable to read Excel file: {ex.Message}";
            return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
        }

        var headerRow = FindPriceTemplateHeaderRow(sheet);
        if (headerRow <= 0)
        {
            TempData["ErrorMessage"] = "Could not find the price template header row.";
            return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
        }

        var itemCol = FindColumnByHeader(sheet, headerRow, "items");
        var sellingPriceCol = FindColumnByHeader(sheet, headerRow, "price");
        var unitCol = FindColumnByHeader(sheet, headerRow, "uom");
        var purchasePriceCol = FindNextPriceColumn(sheet, headerRow, sellingPriceCol);

        var requiresDeliveryPrice = normalizedImportMode is "both" or "delivery-only";
        var requiresPurchasePrice = normalizedImportMode is "both" or "original-only";

        if (itemCol <= 0 || unitCol <= 0 || (requiresDeliveryPrice && sellingPriceCol <= 0) || (requiresPurchasePrice && purchasePriceCol <= 0))
        {
            TempData["ErrorMessage"] = "The template must contain item, price, unit, and purchase-price columns.";
            return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
        }

        var products = await _context.Products
            .Where(p => p.IsActive)
            .ToListAsync();

        var productMap = new Dictionary<string, Product>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in products)
        {
            var nameKey = OrderImportHelpers.NormalizeKey(product.Name);
            if (!string.IsNullOrWhiteSpace(nameKey))
                productMap[nameKey] = product;

            var skuKey = OrderImportHelpers.NormalizeKey(product.SKU);
            if (!string.IsNullOrWhiteSpace(skuKey) && !productMap.ContainsKey(skuKey))
                productMap[skuKey] = product;
        }

        var productIds = products.Select(p => p.Id).ToList();
        var existingWeeklyPrices = await _context.WeeklyPrices
            .Where(w => productIds.Contains(w.ProductId) && w.EffectiveFrom <= targetDate.Date && w.EffectiveTo >= targetDate.Date)
            .ToListAsync();

        var weeklyPriceMap = existingWeeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.EffectiveFrom)
                      .ThenByDescending(w => w.Id)
                      .First());

        var importedItems = new List<PriceVersusItem>();
        var matchedProducts = 0;
        var updatedUnits = 0;
        var unmatchedProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var row = headerRow + 1; row <= sheet.MaxRow; row++)
        {
            var productNameRaw = sheet.GetCell(row, itemCol);
            if (string.IsNullOrWhiteSpace(productNameRaw))
                continue;

            var normalizedName = OrderImportHelpers.NormalizeKey(productNameRaw);
            if (string.IsNullOrWhiteSpace(normalizedName) || normalizedName == "items")
                continue;

            if (!OrderImportHelpers.TryResolveProductByName(productNameRaw, productMap, out var product))
            {
                unmatchedProducts.Add(productNameRaw.Trim());
                continue;
            }

            decimal? importedDeliveryPrice = null;
            if (sellingPriceCol > 0 &&
                SimpleXlsxReader.TryParseDecimal(sheet.GetCell(row, sellingPriceCol), out var parsedDeliveryPrice) &&
                parsedDeliveryPrice >= 0)
            {
                importedDeliveryPrice = parsedDeliveryPrice;
            }

            decimal? importedCost = null;
            if (purchasePriceCol > 0 &&
                SimpleXlsxReader.TryParseDecimal(sheet.GetCell(row, purchasePriceCol), out var parsedCost) &&
                parsedCost >= 0)
            {
                importedCost = parsedCost;
            }

            if (requiresDeliveryPrice && !importedDeliveryPrice.HasValue)
                continue;
            if (requiresPurchasePrice && !importedCost.HasValue)
                continue;

            var unit = sheet.GetCell(row, unitCol).Trim();
            if (!string.IsNullOrWhiteSpace(unit) && !string.Equals(product.Unit, unit, StringComparison.OrdinalIgnoreCase))
            {
                product.Unit = unit;
                updatedUnits++;
            }

            var currentWeekly = weeklyPriceMap.TryGetValue(product.Id, out var wp) ? wp : null;
            var currentCost = currentWeekly?.CostOverride ?? product.UnitCost;
            var currentDeliveryPrice = currentWeekly?.DeliveryPrice ?? (product.UnitCost + product.Markup + product.DeliveryFee);

            var cost = normalizedImportMode switch
            {
                "original-only" => importedCost!.Value,
                "delivery-only" => currentCost,
                _ => importedCost ?? currentCost
            };

            var deliveryPrice = normalizedImportMode switch
            {
                "original-only" => Math.Max(currentDeliveryPrice, cost),
                "delivery-only" => importedDeliveryPrice!.Value,
                _ => importedDeliveryPrice ?? Math.Max(currentDeliveryPrice, cost)
            };

            var markup = Math.Max(deliveryPrice - cost, 0m);
            importedItems.Add(new PriceVersusItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Unit = string.IsNullOrWhiteSpace(unit) ? product.Unit : unit,
                Cost = cost,
                Markup = markup,
                BasePrice = deliveryPrice,
                DeliveryPrice = deliveryPrice,
                DeliveryFee = 0m,
                MasterCost = product.UnitCost,
                MasterMarkup = product.Markup,
                MasterDeliveryFee = product.DeliveryFee
            });
            matchedProducts++;
        }

        if (importedItems.Count == 0)
        {
            TempData["ErrorMessage"] = unmatchedProducts.Count > 0
                ? $"No matching products were imported. Unmatched: {string.Join(", ", unmatchedProducts.Take(8))}{(unmatchedProducts.Count > 8 ? "..." : "")}."
                : "No valid price rows were found in the template.";
            return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
        }

        var saveResult = await SavePriceItemsAsync(targetDate, applyToMasterCost: false, importedItems);

        var unmatchedNote = unmatchedProducts.Count > 0
            ? $" Unmatched products: {string.Join(", ", unmatchedProducts.Take(8))}{(unmatchedProducts.Count > 8 ? "..." : "")}."
            : string.Empty;

        var successMessage = $"Imported {GetImportModeLabel(normalizedImportMode)} for {saveResult.Items.Count} product(s); updated {updatedUnits} unit value(s).{unmatchedNote}";
        TempData["SuccessMessage"] = successMessage;
        TempData["Message"] = successMessage;
        return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePriceRow(PriceVersusViewModel model, int singleProductId)
    {
        if (model.Items == null || model.Items.Count == 0)
        {
            return BadRequest(new { success = false, message = "No price row was submitted." });
        }

        var itemsToSave = GetItemsToSave(model, singleProductId);
        if (itemsToSave.Count == 0)
        {
            return BadRequest(new { success = false, message = "No matching product row was found." });
        }

        var saveResult = await SavePriceItemsAsync(model.TargetDate, model.ApplyToMasterCost, itemsToSave);
        var savedItem = saveResult.Items.First();

        return Json(new
        {
            success = true,
            changed = saveResult.SavedChanges > 0,
            message = saveResult.SavedChanges > 0 ? "Saved the selected product price." : "No price changes were detected.",
            item = new
            {
                savedItem.ProductId,
                cost = savedItem.Cost.ToString("0.00"),
                markup = savedItem.Markup.ToString("0.00"),
                basePrice = savedItem.BasePrice.ToString("0.00"),
                deliveryPrice = savedItem.DeliveryPrice.ToString("0.00"),
                deliveryFee = (savedItem.DeliveryFee ?? 0m).ToString("0.00"),
                hasWeeklyRecord = savedItem.HasWeeklyRecord,
                masterCost = savedItem.MasterCost.ToString("0.00"),
                masterMarkup = savedItem.MasterMarkup.ToString("0.00"),
                masterDeliveryFee = savedItem.MasterDeliveryFee.ToString("0.00")
            }
        });
    }

    // GET: WeeklyPrices
    public async Task<IActionResult> Index()
    {
        var prices = await _context.WeeklyPrices
            .AsNoTracking()
            .Include(w => w.Product)
            .OrderByDescending(w => w.EffectiveFrom)
            .ToListAsync();
        return View(prices);
    }

    // GET: WeeklyPrices/Create
    public async Task<IActionResult> Create()
    {
        ViewData["ProductId"] = new SelectList(await _lookupCache.GetActiveProductsAsync(HttpContext.RequestAborted), "Id", "Name");
        // Default to this week
        var now = DateTime.Now;
        var startOfWeek = now.AddDays(-(int)now.DayOfWeek + 1); // Monday
        var endOfWeek = startOfWeek.AddDays(6); // Sunday

        return View(new WeeklyPrice { EffectiveFrom = startOfWeek, EffectiveTo = endOfWeek });
    }

    // POST: WeeklyPrices/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,ProductId,EffectiveFrom,EffectiveTo,BasePrice,DeliveryPrice")] WeeklyPrice weeklyPrice)
    {
        var product = await _context.Products.FindAsync(weeklyPrice.ProductId);
        ValidateWeeklyPriceInput(weeklyPrice, product);

        if (ModelState.IsValid && product != null)
        {
            ApplyWeeklyPriceValues(weeklyPrice, product);

            _context.Add(weeklyPrice);
            await _context.SaveChangesAsync();
            _cacheInvalidator.InvalidateWeeklyPrices();
            return RedirectToAction(nameof(Index));
        }

        ViewData["ProductId"] = new SelectList(await _lookupCache.GetActiveProductsAsync(HttpContext.RequestAborted), "Id", "Name", weeklyPrice.ProductId);
        return View(weeklyPrice);
    }

    // GET: WeeklyPrices/Edit/5
    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var weeklyPrice = await _context.WeeklyPrices.FindAsync(id);
        if (weeklyPrice == null) return NotFound();
        ViewData["ProductId"] = new SelectList(await _lookupCache.GetActiveProductsAsync(HttpContext.RequestAborted), "Id", "Name", weeklyPrice.ProductId);
        return View(weeklyPrice);
    }

    // POST: WeeklyPrices/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,ProductId,EffectiveFrom,EffectiveTo,BasePrice,DeliveryPrice")] WeeklyPrice weeklyPrice)
    {
        if (id != weeklyPrice.Id) return NotFound();

        var existing = await _context.WeeklyPrices.FindAsync(id);
        if (existing == null) return NotFound();

        var product = await _context.Products.FindAsync(weeklyPrice.ProductId);
        ValidateWeeklyPriceInput(weeklyPrice, product);

        if (ModelState.IsValid && product != null)
        {
            existing.ProductId = weeklyPrice.ProductId;
            existing.EffectiveFrom = weeklyPrice.EffectiveFrom;
            existing.EffectiveTo = weeklyPrice.EffectiveTo;
            existing.BasePrice = weeklyPrice.BasePrice;
            existing.DeliveryPrice = weeklyPrice.DeliveryPrice;
            ApplyWeeklyPriceValues(existing, product);

            try
            {
                _context.Update(existing);
                await _context.SaveChangesAsync();
                _cacheInvalidator.InvalidateWeeklyPrices();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!WeeklyPriceExists(weeklyPrice.Id)) return NotFound();
                else throw;
            }
            return RedirectToAction(nameof(Index));
        }

        ViewData["ProductId"] = new SelectList(await _lookupCache.GetActiveProductsAsync(HttpContext.RequestAborted), "Id", "Name", weeklyPrice.ProductId);
        return View(weeklyPrice);
    }

    // Clone last week's prices to this week
    public async Task<IActionResult> CloneLastWeek()
    {
        var lastWeekStart = DateTime.Now.AddDays(-(int)DateTime.Now.DayOfWeek + 1 - 7).Date;
        var thisWeekStart = DateTime.Now.AddDays(-(int)DateTime.Now.DayOfWeek + 1).Date;
        var thisWeekEnd = thisWeekStart.AddDays(6);
        var lastWeekEnd = lastWeekStart.AddDays(1);
        var thisWeekEndExclusive = thisWeekStart.AddDays(1);

        var lastWeekPrices = await _context.WeeklyPrices
            .Where(w => w.EffectiveFrom >= lastWeekStart && w.EffectiveFrom < lastWeekEnd)
            .ToListAsync();

        foreach (var price in lastWeekPrices)
        {
            // Check if exists for this week
            var exists = await _context.WeeklyPrices.AnyAsync(w => w.ProductId == price.ProductId && w.EffectiveFrom >= thisWeekStart && w.EffectiveFrom < thisWeekEndExclusive);
            if (!exists)
            {
                var newPrice = new WeeklyPrice
                {
                    ProductId = price.ProductId,
                    EffectiveFrom = thisWeekStart,
                    EffectiveTo = thisWeekEnd,
                    CostOverride = price.CostOverride,
                    DeliveryFee = price.DeliveryFee,
                    BasePrice = price.BasePrice,
                    DeliveryPrice = price.DeliveryPrice,
                    Markup = price.Markup
                };
                _context.Add(newPrice);
            }
        }
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateWeeklyPrices();
        return RedirectToAction(nameof(Index));
    }

    private async Task<PriceVersusViewModel> BuildPriceVersusModelAsync(
        DateTime targetDate,
        string? searchTerm = null,
        int page = 1,
        int pageSize = 40,
        IEnumerable<PriceVersusItem>? postedItems = null)
    {
        var day = targetDate.Date;
        var (weekStart, weekEnd) = GetWeekRange(targetDate);
        var normalizedSearch = searchTerm?.Trim() ?? string.Empty;
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 20, 200);

        var productsQuery = _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            productsQuery = productsQuery.Where(p =>
                p.Name.Contains(normalizedSearch) ||
                p.SKU.Contains(normalizedSearch) ||
                p.Unit.Contains(normalizedSearch) ||
                (p.Category != null && p.Category.Contains(normalizedSearch)));
        }

        var totalItems = await productsQuery.CountAsync();
        var products = await productsQuery
            .OrderBy(p => p.Name)
            .ThenBy(p => p.SKU)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var productIds = products.Select(p => p.Id).ToList();
        var weeklyPrices = await _context.WeeklyPrices
            .AsNoTracking()
            .Where(w => productIds.Contains(w.ProductId) && w.EffectiveFrom <= day && w.EffectiveTo >= day)
            .ToListAsync();

        var weeklyMap = weeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.EffectiveFrom)
                      .ThenByDescending(w => w.Id)
                      .First());

        var postedMap = postedItems?
            .Where(i => i.ProductId > 0)
            .GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => g.Last())
            ?? new Dictionary<int, PriceVersusItem>();

        var items = new List<PriceVersusItem>(products.Count);
        foreach (var p in products)
        {
            decimal masterCost = p.UnitCost;
            decimal masterMarkup = p.Markup;
            decimal masterDeliveryFee = p.DeliveryFee;
            decimal cost = masterCost;
            decimal markup = masterMarkup;
            decimal deliveryFee = masterDeliveryFee;
            bool hasRec = false;

            if (weeklyMap.TryGetValue(p.Id, out var wp))
            {
                hasRec = true;
                if (wp.CostOverride.HasValue)
                    cost = wp.CostOverride.Value;

                if (wp.DeliveryFee.HasValue)
                    deliveryFee = wp.DeliveryFee.Value;

                if (wp.Markup != 0)
                    markup = wp.Markup;
                else if (wp.BasePrice > 0)
                    markup = wp.BasePrice - cost;
            }

            if (postedMap.TryGetValue(p.Id, out var posted))
            {
                cost = posted.Cost;
                markup = posted.Markup;
                if (posted.DeliveryPrice > 0)
                    deliveryFee = posted.DeliveryPrice - (posted.Cost + posted.Markup);
            }

            var basePrice = cost + markup;
            items.Add(new PriceVersusItem
            {
                ProductId = p.Id,
                ProductName = p.Name,
                Unit = p.Unit,
                Cost = cost,
                Markup = markup,
                BasePrice = basePrice,
                DeliveryPrice = basePrice + deliveryFee,
                DeliveryFee = deliveryFee,
                MasterCost = masterCost,
                MasterMarkup = masterMarkup,
                MasterDeliveryFee = masterDeliveryFee,
                HasWeeklyRecord = hasRec
            });
        }

        return new PriceVersusViewModel
        {
            TargetDate = targetDate,
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            SearchTerm = normalizedSearch,
            CurrentPage = page,
            PageSize = pageSize,
            TotalItems = totalItems,
            Items = items
        };
    }


    private bool WeeklyPriceExists(int id)
    {
        return _context.WeeklyPrices.Any(e => e.Id == id);
    }

    private static List<PriceVersusItem> GetItemsToSave(PriceVersusViewModel model, int? singleProductId)
        => singleProductId.HasValue
            ? model.Items.Where(i => i.ProductId == singleProductId.Value).ToList()
            : model.Items.ToList();

    private IActionResult RedirectToPriceVersus(DateTime targetDate, string? searchTerm, int page, int pageSize)
        => RedirectToAction(nameof(PriceVersus), new
        {
            date = targetDate.ToString("yyyy-MM-dd"),
            q = searchTerm,
            page = Math.Max(1, page),
            pageSize = Math.Clamp(pageSize, 20, 200)
        });

    private IActionResult RedirectAfterImport(DateTime targetDate, string? searchTerm, int page, int pageSize, string? returnUrl)
        => !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToPriceVersus(targetDate, searchTerm, page, pageSize);

    private static (DateTime WeekStart, DateTime WeekEnd) GetWeekRange(DateTime targetDate)
    {
        int diff = (7 + (targetDate.DayOfWeek - DayOfWeek.Monday)) % 7;
        var weekStart = targetDate.AddDays(-1 * diff).Date;
        return (weekStart, weekStart.AddDays(6).Date);
    }

    private static int FindPriceTemplateHeaderRow(SimpleXlsxSheet sheet)
    {
        for (var row = 1; row <= Math.Min(sheet.MaxRow, 10); row++)
        {
            var hasItems = false;
            var hasPrice = false;
            var hasUnit = false;
            for (var col = 1; col <= sheet.MaxCol; col++)
            {
                var key = OrderImportHelpers.NormalizeKey(sheet.GetCell(row, col));
                if (key == "items") hasItems = true;
                if (key == "price") hasPrice = true;
                if (key == "uom") hasUnit = true;
            }

            if (hasItems && hasPrice && hasUnit)
                return row;
        }

        return 0;
    }

    private static int FindColumnByHeader(SimpleXlsxSheet sheet, int headerRow, string expectedKey)
    {
        for (var col = 1; col <= sheet.MaxCol; col++)
        {
            if (OrderImportHelpers.NormalizeKey(sheet.GetCell(headerRow, col)) == expectedKey)
                return col;
        }

        return 0;
    }

    private static int FindNextPriceColumn(SimpleXlsxSheet sheet, int headerRow, int firstPriceColumn)
    {
        if (firstPriceColumn <= 0) return 0;
        for (var col = firstPriceColumn + 1; col <= sheet.MaxCol; col++)
        {
            if (OrderImportHelpers.NormalizeKey(sheet.GetCell(headerRow, col)) == "price")
                return col;
        }

        return 0;
    }

    private static string NormalizeImportMode(string? importMode)
        => importMode?.Trim().ToLowerInvariant() switch
        {
            "delivery-only" => "delivery-only",
            "original-only" => "original-only",
            _ => "both"
        };

    private static string GetImportModeLabel(string importMode)
        => importMode switch
        {
            "delivery-only" => "delivery prices only",
            "original-only" => "original prices only",
            _ => "weekly prices"
        };

    private void ValidateWeeklyPriceInput(WeeklyPrice weeklyPrice, Product? product)
    {
        if (product == null)
        {
            ModelState.AddModelError("ProductId", "Product not found.");
            return;
        }

        if (weeklyPrice.BasePrice < 0)
            ModelState.AddModelError("BasePrice", "Base price cannot be negative.");
        if (weeklyPrice.DeliveryPrice < 0)
            ModelState.AddModelError("DeliveryPrice", "Delivery price cannot be negative.");

        if (!ModelState.IsValid)
            return;

        var markup = weeklyPrice.BasePrice - product.UnitCost;
        var deliveryFee = weeklyPrice.DeliveryPrice - weeklyPrice.BasePrice;

        if (markup < 0)
            ModelState.AddModelError("BasePrice", "Base price cannot be lower than cost.");
        if (deliveryFee < 0)
            ModelState.AddModelError("DeliveryPrice", "Delivery price cannot be lower than base price.");
    }

    private static void ApplyWeeklyPriceValues(WeeklyPrice weeklyPrice, Product product)
    {
        var markup = weeklyPrice.BasePrice - product.UnitCost;
        var deliveryFee = weeklyPrice.DeliveryPrice - weeklyPrice.BasePrice;

        weeklyPrice.Markup = markup;
        weeklyPrice.DeliveryFee = deliveryFee != product.DeliveryFee ? deliveryFee : null;

        var basePrice = product.UnitCost + markup;
        var effectiveDeliveryFee = weeklyPrice.DeliveryFee ?? product.DeliveryFee;
        weeklyPrice.BasePrice = basePrice;
        weeklyPrice.DeliveryPrice = basePrice + effectiveDeliveryFee;
    }

    private static (decimal Cost, decimal Markup, decimal BasePrice, decimal DeliveryPrice, decimal DeliveryFee) NormalizePostedPricing(
        PriceVersusItem item,
        Product product)
    {
        var cost = Math.Max(item.Cost, 0m);
        var basePriceFromPost = Math.Max(item.BasePrice, cost);
        var markup = Math.Max(item.Markup, basePriceFromPost - cost);
        var basePrice = cost + markup;
        var deliveryPrice = Math.Max(item.DeliveryPrice, basePrice);
        var deliveryFee = deliveryPrice - basePrice;

        if (deliveryPrice == 0m)
        {
            deliveryFee = product.DeliveryFee;
            deliveryPrice = basePrice + deliveryFee;
        }

        return (cost, markup, basePrice, deliveryPrice, deliveryFee);
    }

    private async Task<(int SavedChanges, List<PriceVersusItem> Items)> SavePriceItemsAsync(
        DateTime targetDate,
        bool applyToMasterCost,
        List<PriceVersusItem> itemsToSave)
    {
        var (weekStart, weekEnd) = GetWeekRange(targetDate);

        var pids = itemsToSave.Select(i => i.ProductId).Distinct().ToList();

        var productMap = await _context.Products
            .Where(p => pids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p);

        var existing = await _context.WeeklyPrices
            .Where(w => pids.Contains(w.ProductId) &&
                        w.EffectiveFrom <= targetDate && w.EffectiveTo >= targetDate)
            .ToListAsync();

        var existingGroups = existing
            .GroupBy(w => w.ProductId)
            .ToList();

        var existingMap = existingGroups.ToDictionary(
            g => g.Key,
            g => g.OrderByDescending(w => w.EffectiveFrom)
                  .ThenByDescending(w => w.Id)
                  .First());

        var duplicateWeeklyPrices = existingGroups
            .SelectMany(g => g.OrderByDescending(w => w.EffectiveFrom).ThenByDescending(w => w.Id).Skip(1))
            .ToList();

        if (duplicateWeeklyPrices.Count > 0)
        {
            _context.WeeklyPrices.RemoveRange(duplicateWeeklyPrices);
        }

        var savedChanges = 0;
        var savedItems = new List<PriceVersusItem>(itemsToSave.Count);

        foreach (var item in itemsToSave)
        {
            if (!productMap.TryGetValue(item.ProductId, out var prod))
            {
                continue;
            }

            var normalized = NormalizePostedPricing(item, prod);
            var masterCost = prod.UnitCost;
            var masterMarkup = prod.Markup;
            var masterDeliveryFee = prod.DeliveryFee;
            var cost = normalized.Cost;
            var markup = normalized.Markup;
            var deliveryFee = normalized.DeliveryFee;

            if (applyToMasterCost && masterCost != cost)
            {
                prod.UnitCost = cost;
                masterCost = cost;
                savedChanges++;
            }

            decimal? costOverride = null;
            if (!applyToMasterCost && cost != masterCost)
            {
                costOverride = cost;
            }

            decimal? deliveryFeeOverride = null;
            if (deliveryFee != masterDeliveryFee)
            {
                deliveryFeeOverride = deliveryFee;
            }

            var effectiveCost = costOverride ?? masterCost;
            var effectiveDeliveryFee = deliveryFeeOverride ?? masterDeliveryFee;
            var basePrice = effectiveCost + markup;
            var deliveryPrice = basePrice + effectiveDeliveryFee;
            var shouldHaveWeekly = costOverride.HasValue || markup != masterMarkup || deliveryFeeOverride.HasValue;

            if (existingMap.TryGetValue(item.ProductId, out var wp))
            {
                if (!shouldHaveWeekly)
                {
                    _context.WeeklyPrices.Remove(wp);
                    savedChanges++;
                }
                else
                {
                    var changed = false;
                    if (wp.CostOverride != costOverride)
                    {
                        wp.CostOverride = costOverride;
                        changed = true;
                    }
                    if (wp.DeliveryFee != deliveryFeeOverride)
                    {
                        wp.DeliveryFee = deliveryFeeOverride;
                        changed = true;
                    }
                    if (wp.Markup != markup)
                    {
                        wp.Markup = markup;
                        changed = true;
                    }
                    if (wp.BasePrice != basePrice || wp.DeliveryPrice != deliveryPrice)
                    {
                        wp.BasePrice = basePrice;
                        wp.DeliveryPrice = deliveryPrice;
                        changed = true;
                    }

                    if (changed)
                    {
                        _context.Update(wp);
                        savedChanges++;
                    }
                }
            }
            else if (shouldHaveWeekly)
            {
                var newWp = new WeeklyPrice
                {
                    ProductId = item.ProductId,
                    EffectiveFrom = weekStart,
                    EffectiveTo = weekEnd,
                    CostOverride = costOverride,
                    DeliveryFee = deliveryFeeOverride,
                    BasePrice = basePrice,
                    DeliveryPrice = deliveryPrice,
                    Markup = markup
                };
                _context.Add(newWp);
                savedChanges++;
            }

            savedItems.Add(new PriceVersusItem
            {
                ProductId = item.ProductId,
                ProductName = item.ProductName,
                Unit = item.Unit,
                Cost = effectiveCost,
                Markup = markup,
                BasePrice = basePrice,
                DeliveryPrice = deliveryPrice,
                DeliveryFee = effectiveDeliveryFee,
                MasterCost = masterCost,
                MasterMarkup = masterMarkup,
                MasterDeliveryFee = masterDeliveryFee,
                HasWeeklyRecord = shouldHaveWeekly
            });
        }

        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateWeeklyPrices();
        _cacheInvalidator.InvalidateProducts();
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();

        return (savedChanges, savedItems);
    }
}
