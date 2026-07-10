using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

using HazelInvoice.ViewModels;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services;
using HazelInvoice.Services.Orders;
using HazelInvoice.Services.Pricing;
using HazelInvoice.Helpers;

namespace HazelInvoice.Controllers;

[Authorize]
public class WeeklyPricesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILookupCacheService _lookupCache;
    private readonly IAppCacheInvalidator _cacheInvalidator;
    private readonly IDailyPurchaseCostService _dailyPurchaseCosts;

    public WeeklyPricesController(
        ApplicationDbContext context,
        ILookupCacheService lookupCache,
        IAppCacheInvalidator cacheInvalidator,
        IDailyPurchaseCostService dailyPurchaseCosts)
    {
        _context = context;
        _lookupCache = lookupCache;
        _cacheInvalidator = cacheInvalidator;
        _dailyPurchaseCosts = dailyPurchaseCosts;
    }

    // GET: WeeklyPrices/PriceVersus
    public async Task<IActionResult> PriceVersus(DateTime? date, string? q = null, int page = 1, int pageSize = 40)
    {
        var targetDate = date ?? BusinessDate.Now();
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

        if (importFile.Length > 20 * 1024 * 1024)
        {
            TempData["ErrorMessage"] = "File is too large. Maximum size is 20 MB.";
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

        var requiresDeliveryPrice = normalizedImportMode is "both" or "delivery-only";
        var requiresPurchasePrice = normalizedImportMode is "both" or "original-only";

        var headerRow = FindPriceTemplateHeaderRow(sheet);
        var firstDataRow = headerRow + 1;
        int itemCol;
        int sellingPriceCol;
        int unitCol;
        int purchasePriceCol;

        if (headerRow > 0)
        {
            itemCol = FindColumnByHeaders(sheet, headerRow, "items", "item", "product", "productname");
            sellingPriceCol = FindColumnByHeaders(sheet, headerRow, "deliveryprice", "sellingprice", "price");
            unitCol = FindColumnByHeaders(sheet, headerRow, "uom", "unit");
            purchasePriceCol = FindColumnByHeaders(sheet, headerRow, "purchasecost", "originalprice", "purchaseprice", "costprice");
        }
        else if (TryFindHeaderlessDeliveryPriceTemplate(sheet, out firstDataRow, out itemCol, out sellingPriceCol, out unitCol))
        {
            normalizedImportMode = "delivery-only";
            requiresDeliveryPrice = true;
            requiresPurchasePrice = false;
            purchasePriceCol = 0;
        }
        else
        {
            TempData["ErrorMessage"] = "Could not find the price template header row.";
            return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
        }

        if (purchasePriceCol <= 0)
        {
            purchasePriceCol = FindNextPriceColumn(sheet, headerRow, sellingPriceCol);
        }

        if (itemCol <= 0 || unitCol <= 0 || (requiresDeliveryPrice && sellingPriceCol <= 0) || (requiresPurchasePrice && purchasePriceCol <= 0))
        {
            TempData["ErrorMessage"] = "The template must contain item, price, unit, and purchase-price columns.";
            return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
        }

        var products = await _context.Products.ToListAsync();
        var productMap = OrderImportHelpers.BuildProductLookup(products);

        var productIds = products.Select(p => p.Id).ToList();
        var applicableTargetDate = WeeklyPriceCalendar.GetApplicablePriceDate(targetDate);
        var existingWeeklyPrices = applicableTargetDate.HasValue
            ? await _context.WeeklyPrices
                .Where(w => productIds.Contains(w.ProductId) && w.EffectiveFrom <= applicableTargetDate.Value && w.EffectiveTo >= applicableTargetDate.Value)
                .ToListAsync()
            : new List<WeeklyPrice>();

        var weeklyPriceMap = existingWeeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.EffectiveFrom)
                      .ThenByDescending(w => w.Id)
                      .First());

        var dailyCostMap = await _dailyPurchaseCosts.GetEffectiveCostsAsync(productIds, targetDate, HttpContext.RequestAborted);

        var importedItems = new List<PriceVersusItem>();
        var matchedProducts = 0;
        var updatedUnits = 0;
        var unmatchedProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var row = firstDataRow; row <= sheet.MaxRow; row++)
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
            var currentCost = dailyCostMap.TryGetValue(product.Id, out var dailyCost)
                ? dailyCost.UnitCost
                : currentWeekly?.CostOverride ?? product.UnitCost;
            var currentDeliveryFee = currentWeekly?.DeliveryFee ?? product.DeliveryFee;
            var currentDeliveryPrice = currentWeekly?.DeliveryPrice ?? (product.UnitCost + product.Markup + product.DeliveryFee);

            var cost = normalizedImportMode switch
            {
                "original-only" => importedCost!.Value,
                "delivery-only" => currentCost,
                _ => importedCost ?? currentCost
            };

            var deliveryPrice = normalizedImportMode switch
            {
                "original-only" => currentDeliveryPrice,
                "delivery-only" => importedDeliveryPrice!.Value,
                _ => importedDeliveryPrice ?? Math.Max(currentDeliveryPrice, cost)
            };

            var (markup, basePrice, deliveryFee) = DeriveImportedPricing(cost, deliveryPrice, currentDeliveryFee);
            importedItems.Add(new PriceVersusItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                Unit = string.IsNullOrWhiteSpace(unit) ? product.Unit : unit,
                Cost = cost,
                Markup = markup,
                BasePrice = basePrice,
                DeliveryPrice = deliveryPrice,
                DeliveryFee = deliveryFee,
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

        var completeDeliveryPriceRefresh = requiresDeliveryPrice;
        var saveResult = await SavePriceItemsAsync(
            targetDate,
            applyToMasterCost: false,
            importedItems,
            forceCreateWeeklyRecords: completeDeliveryPriceRefresh,
            completeWeeklyRefresh: completeDeliveryPriceRefresh);

        var unmatchedNote = unmatchedProducts.Count > 0
            ? $" Unmatched products: {string.Join(", ", unmatchedProducts.Take(8))}{(unmatchedProducts.Count > 8 ? "..." : "")}."
            : string.Empty;

        var zeroedNote = completeDeliveryPriceRefresh
            ? $" Products without a changed new delivery price were set to zero for {targetDate:MMM dd, yyyy}; prices matching last week remain zero."
            : string.Empty;

        var successMessage = $"Imported {GetImportModeLabel(normalizedImportMode)} for {matchedProducts} product(s); updated {updatedUnits} unit value(s).{zeroedNote}{unmatchedNote}";
        TempData["SuccessMessage"] = successMessage;
        TempData["Message"] = successMessage;

        if (completeDeliveryPriceRefresh)
        {
            await UpdateUnpaidReceiptPricesForDateAsync(targetDate);
        }

        return RedirectAfterImport(targetDate, searchTerm, page, pageSize, returnUrl);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadTemplate(DateTime? targetDate, string? importMode = null, string? returnUrl = null)
    {
        var normalizedImportMode = NormalizeImportMode(importMode);
        var targetMoment = targetDate ?? BusinessDate.Now();
        var day = targetMoment.Date;
        var applicableDay = WeeklyPriceCalendar.GetApplicablePriceDate(targetMoment);

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ThenBy(p => p.SKU)
            .ToListAsync();

        var productIds = products.Select(p => p.Id).ToList();
        var weeklyPrices = applicableDay.HasValue
            ? await _context.WeeklyPrices
                .AsNoTracking()
                .Where(w => productIds.Contains(w.ProductId) && w.EffectiveFrom <= applicableDay.Value && w.EffectiveTo >= applicableDay.Value)
                .ToListAsync()
            : new List<WeeklyPrice>();

        var weeklyMap = weeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.EffectiveFrom)
                      .ThenByDescending(w => w.Id)
                      .First());

        var dailyCostMap = await _dailyPurchaseCosts.GetEffectiveCostsAsync(productIds, day, HttpContext.RequestAborted);

        var rows = new List<IReadOnlyList<string>>(capacity: products.Count + 2)
        {
            new[]
            {
                "WEEKLY PRICE TEMPLATE",
                $"Target Date: {day:yyyy-MM-dd}",
                $"Mode: {GetImportModeLabel(normalizedImportMode)}"
            },
            BuildTemplateHeader(normalizedImportMode)
        };

        foreach (var product in products)
        {
            var weekly = weeklyMap.TryGetValue(product.Id, out var wp) ? wp : null;
            var currentCost = applicableDay.HasValue
                ? dailyCostMap.TryGetValue(product.Id, out var dailyCost)
                    ? dailyCost.UnitCost
                    : weekly?.CostOverride ?? product.UnitCost
                : 0m;
            var currentDelivery = applicableDay.HasValue ? weekly?.DeliveryPrice ?? (product.UnitCost + product.Markup + product.DeliveryFee) : 0m;

            rows.Add(BuildTemplateRow(product.Name, product.Unit, currentDelivery, currentCost, normalizedImportMode));
        }

        var fileName = normalizedImportMode switch
        {
            "delivery-only" => $"DeliveryPriceTemplate_{day:yyyy-MM-dd}.xlsx",
            "original-only" => $"PurchaseCostTemplate_{day:yyyy-MM-dd}.xlsx",
            _ => $"WeeklyPriceTemplate_{day:yyyy-MM-dd}.xlsx"
        };

        var bytes = SimpleXlsxWriter.WriteSingleSheet("Price Template", rows);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
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
        var (startOfWeek, endOfWeek) = WeeklyPriceCalendar.GetWeekRange(BusinessDate.Now());

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
        var (thisWeekStart, thisWeekEnd) = WeeklyPriceCalendar.GetWeekRange(BusinessDate.Now());
        var lastWeekStart = thisWeekStart.AddDays(-7).Date;
        var lastWeekEnd = lastWeekStart.AddDays(5).Date;

        var lastWeekPrices = await _context.WeeklyPrices
            .Where(w => w.EffectiveFrom == lastWeekStart && w.EffectiveTo == lastWeekEnd)
            .ToListAsync();

        foreach (var price in lastWeekPrices)
        {
            // Check if exists for this week
            var exists = await _context.WeeklyPrices.AnyAsync(w => w.ProductId == price.ProductId && w.EffectiveFrom == thisWeekStart && w.EffectiveTo == thisWeekEnd);
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
        var applicableDay = WeeklyPriceCalendar.GetApplicablePriceDate(targetDate);
        var isResetDay = WeeklyPriceCalendar.IsResetDay(targetDate);
        var (weekStart, weekEnd) = WeeklyPriceCalendar.GetWeekRange(targetDate);
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
        var weeklyPrices = applicableDay.HasValue
            ? await _context.WeeklyPrices
                .AsNoTracking()
                .Where(w => productIds.Contains(w.ProductId) && w.EffectiveFrom <= applicableDay.Value && w.EffectiveTo >= applicableDay.Value)
                .ToListAsync()
            : new List<WeeklyPrice>();

        var weeklyMap = weeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.EffectiveFrom)
                      .ThenByDescending(w => w.Id)
                      .First());

        var dailyCostMap = await _dailyPurchaseCosts.GetEffectiveCostsAsync(productIds, targetDate, HttpContext.RequestAborted);

        var postedMap = postedItems?
            .Where(i => i.ProductId > 0)
            .GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => g.Last())
            ?? new Dictionary<int, PriceVersusItem>();

        var items = new List<PriceVersusItem>(products.Count);
        foreach (var p in products)
        {
            decimal masterCost = p.UnitCost;
            var dateCost = dailyCostMap.TryGetValue(p.Id, out var dailyCost)
                ? dailyCost.UnitCost
                : masterCost;
            decimal masterMarkup = p.Markup;
            decimal masterDeliveryFee = p.DeliveryFee;
            decimal cost = isResetDay ? 0m : dateCost;
            decimal basePrice = isResetDay ? 0m : masterCost + masterMarkup;
            decimal markup = isResetDay ? 0m : basePrice - cost;
            decimal deliveryFee = isResetDay ? 0m : masterDeliveryFee;
            decimal deliveryPrice = isResetDay ? 0m : basePrice + deliveryFee;
            bool hasRec = false;

            if (!isResetDay && weeklyMap.TryGetValue(p.Id, out var wp))
            {
                hasRec = true;
                var sellingCostBasis = wp.CostOverride ?? masterCost;
                cost = dailyCostMap.ContainsKey(p.Id)
                    ? cost
                    : sellingCostBasis;

                if (wp.DeliveryFee.HasValue)
                    deliveryFee = wp.DeliveryFee.Value;

                if (wp.DeliveryPrice == 0m && wp.BasePrice == 0m)
                {
                    basePrice = 0m;
                    markup = -cost;
                    deliveryFee = 0m;
                    deliveryPrice = 0m;
                }
                else
                {
                    if (wp.BasePrice > 0)
                        basePrice = wp.BasePrice;
                    else if (wp.Markup != 0)
                        basePrice = sellingCostBasis + wp.Markup;
                    else
                        basePrice = sellingCostBasis + masterMarkup;

                    deliveryPrice = wp.DeliveryPrice > 0
                        ? wp.DeliveryPrice
                        : basePrice + deliveryFee;
                    deliveryFee = deliveryPrice - basePrice;
                    markup = basePrice - cost;
                }
            }

            if (postedMap.TryGetValue(p.Id, out var posted))
            {
                cost = posted.Cost;
                markup = posted.Markup;
                if (posted.DeliveryPrice > 0)
                    deliveryFee = posted.DeliveryPrice - (posted.Cost + posted.Markup);

                basePrice = cost + markup;
                deliveryPrice = basePrice + deliveryFee;
            }

            items.Add(new PriceVersusItem
            {
                ProductId = p.Id,
                ProductName = p.Name,
                Unit = p.Unit,
                Cost = cost,
                Markup = markup,
                BasePrice = basePrice,
                DeliveryPrice = deliveryPrice,
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
            ? LocalRedirect(AttachTargetDate(returnUrl, targetDate))
            : RedirectToPriceVersus(targetDate, searchTerm, page, pageSize);

    private static string AttachTargetDate(string returnUrl, DateTime targetDate)
    {
        return QueryHelpers.AddQueryString(returnUrl, "date", targetDate.ToString("yyyy-MM-dd"));
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
                if (key is "items" or "item" or "product" or "productname") hasItems = true;
                if (key is "price" or "deliveryprice" or "sellingprice" or "purchasecost" or "originalprice" or "purchaseprice" or "costprice") hasPrice = true;
                if (key is "uom" or "unit") hasUnit = true;
            }

            if (hasItems && hasPrice && hasUnit)
                return row;
        }

        return 0;
    }

    private static bool TryFindHeaderlessDeliveryPriceTemplate(
        SimpleXlsxSheet sheet,
        out int firstDataRow,
        out int itemCol,
        out int deliveryPriceCol,
        out int unitCol)
    {
        firstDataRow = 0;
        itemCol = 0;
        deliveryPriceCol = 0;
        unitCol = 0;

        var title = OrderImportHelpers.NormalizeKey(sheet.GetCell(1, 1));
        if (title != "weeklypricetemplate")
            return false;

        const int candidateFirstDataRow = 2;
        const int candidateItemCol = 1;
        const int candidateDeliveryPriceCol = 2;
        const int candidateUnitCol = 3;

        var sampleRowsWithPrices = 0;
        for (var row = candidateFirstDataRow; row <= Math.Min(sheet.MaxRow, candidateFirstDataRow + 8); row++)
        {
            var productName = sheet.GetCell(row, candidateItemCol);
            var rawPrice = sheet.GetCell(row, candidateDeliveryPriceCol);
            if (!string.IsNullOrWhiteSpace(productName) &&
                SimpleXlsxReader.TryParseDecimal(rawPrice, out var price) &&
                price >= 0m)
            {
                sampleRowsWithPrices++;
            }
        }

        if (sampleRowsWithPrices < 2)
            return false;

        firstDataRow = candidateFirstDataRow;
        itemCol = candidateItemCol;
        deliveryPriceCol = candidateDeliveryPriceCol;
        unitCol = candidateUnitCol;
        return true;
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

    private static int FindColumnByHeaders(SimpleXlsxSheet sheet, int headerRow, params string[] expectedKeys)
    {
        foreach (var expectedKey in expectedKeys)
        {
            var col = FindColumnByHeader(sheet, headerRow, expectedKey);
            if (col > 0)
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
            "original-only" => "purchase cost only",
            _ => "weekly prices"
        };

    private static string[] BuildTemplateHeader(string importMode)
        => importMode switch
        {
            "delivery-only" => ["ITEMS", "DELIVERY PRICE", "UOM"],
            "original-only" => ["ITEMS", "PURCHASE COST", "UOM"],
            _ => ["ITEMS", "DELIVERY PRICE", "UOM", "PURCHASE COST"]
        };

    private static string[] BuildTemplateRow(string productName, string unit, decimal deliveryPrice, decimal originalPrice, string importMode)
        => importMode switch
        {
            "delivery-only" => [productName, deliveryPrice.ToString("0.00"), unit],
            "original-only" => [productName, originalPrice.ToString("0.00"), unit],
            _ => [productName, deliveryPrice.ToString("0.00"), unit, originalPrice.ToString("0.00")]
        };

    private static (decimal Markup, decimal BasePrice, decimal DeliveryFee) DeriveImportedPricing(
        decimal cost,
        decimal deliveryPrice,
        decimal preferredDeliveryFee)
    {
        var normalizedCost = Math.Max(cost, 0m);
        var normalizedDeliveryPrice = Math.Max(deliveryPrice, 0m);
        var normalizedPreferredFee = Math.Max(preferredDeliveryFee, 0m);

        var basePrice = normalizedDeliveryPrice - normalizedPreferredFee;
        if (basePrice < 0m)
        {
            basePrice = 0m;
        }

        var markup = basePrice - normalizedCost;
        var deliveryFee = normalizedDeliveryPrice - basePrice;
        return (markup, basePrice, deliveryFee);
    }

    private static bool PricesMatch(decimal left, decimal right)
        => Math.Abs(left - right) < 0.005m;

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

        var deliveryFee = weeklyPrice.DeliveryPrice - weeklyPrice.BasePrice;

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
        var basePriceFromPost = Math.Max(item.BasePrice, 0m);
        var markup = item.Markup;
        if (basePriceFromPost > 0m)
        {
            markup = basePriceFromPost - cost;
        }
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

    private static PriceVersusItem BuildZeroPriceItem(Product product)
        => new()
        {
            ProductId = product.Id,
            ProductName = product.Name,
            Unit = product.Unit,
            Cost = product.UnitCost,
            Markup = -product.UnitCost,
            BasePrice = 0m,
            DeliveryPrice = 0m,
            DeliveryFee = 0m,
            MasterCost = product.UnitCost,
            MasterMarkup = product.Markup,
            MasterDeliveryFee = product.DeliveryFee,
            HasWeeklyRecord = true
        };

    private async Task UpdateUnpaidReceiptPricesForDateAsync(DateTime targetDate)
    {
        var day = targetDate.Date;
        var dayEnd = day.AddDays(1);

        var receipts = await _context.Receipts
            .Include(r => r.Lines)
            .Where(r => r.Date >= day &&
                        r.Date < dayEnd &&
                        r.Status == PaymentStatus.Unpaid)
            .ToListAsync();

        var productIds = receipts
            .SelectMany(r => r.Lines)
            .Where(l => l.ProductId.HasValue)
            .Select(l => l.ProductId!.Value)
            .Distinct()
            .ToList();

        if (receipts.Count == 0 || productIds.Count == 0)
            return;

        var applicableDate = WeeklyPriceCalendar.GetApplicablePriceDate(targetDate)
            ?? WeeklyPriceCalendar.GetWeekRange(targetDate).WeekStart;

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.UnitCost })
            .ToDictionaryAsync(p => p.Id, p => p.UnitCost);

        var weeklyPrices = await _context.WeeklyPrices
            .AsNoTracking()
            .Where(w => productIds.Contains(w.ProductId) &&
                        w.EffectiveFrom <= applicableDate &&
                        w.EffectiveTo >= applicableDate)
            .ToListAsync();

        var weeklyPriceMap = weeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.EffectiveFrom)
                      .ThenByDescending(w => w.Id)
                      .First());

        var dailyCostMap = await _dailyPurchaseCosts.GetEffectiveCostsAsync(productIds, targetDate, HttpContext.RequestAborted);

        foreach (var receipt in receipts)
        {
            foreach (var line in receipt.Lines.Where(l => l.ProductId.HasValue))
            {
                var productId = line.ProductId!.Value;
                var weeklyPrice = weeklyPriceMap.GetValueOrDefault(productId);
                var price = weeklyPrice?.DeliveryPrice ?? 0m;
                var cost = dailyCostMap.TryGetValue(productId, out var dailyCost)
                    ? dailyCost.UnitCost
                    : weeklyPrice?.CostOverride
                    ?? (products.TryGetValue(productId, out var productCost) ? productCost : 0m);

                line.Price = price;
                line.Amount = Math.Round(line.Quantity * price, 2, MidpointRounding.AwayFromZero);
                line.CostPriceSnapshot = cost;
            }

            receipt.TotalAmount = receipt.Lines.Sum(l => l.Amount);
            receipt.PaidAmount = 0m;
            receipt.Status = PaymentStatus.Unpaid;
        }

        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();
    }

    private async Task<(int SavedChanges, List<PriceVersusItem> Items)> SavePriceItemsAsync(
        DateTime targetDate,
        bool applyToMasterCost,
        List<PriceVersusItem> itemsToSave,
        bool forceCreateWeeklyRecords = false,
        bool completeWeeklyRefresh = false)
    {
        var (weekStart, weekEnd) = WeeklyPriceCalendar.GetWeekRange(targetDate);

        var postedMap = itemsToSave
            .Where(i => i.ProductId > 0)
            .GroupBy(i => i.ProductId)
            .ToDictionary(g => g.Key, g => g.Last());

        var postedProductIds = postedMap.Keys.ToList();

        var applicableTargetDate = WeeklyPriceCalendar.GetApplicablePriceDate(targetDate) ?? weekStart;

        var productQuery = _context.Products.AsQueryable();
        productQuery = completeWeeklyRefresh
            ? productQuery.Where(p => p.IsActive || postedProductIds.Contains(p.Id))
            : productQuery.Where(p => postedProductIds.Contains(p.Id));

        var productMap = await productQuery.ToDictionaryAsync(p => p.Id, p => p);
        var pids = productMap.Keys.ToList();

        var existing = await _context.WeeklyPrices
            .Where(w => pids.Contains(w.ProductId) &&
                        w.EffectiveFrom <= applicableTargetDate && w.EffectiveTo >= applicableTargetDate)
            .ToListAsync();

        var existingGroups = existing
            .GroupBy(w => w.ProductId)
            .ToList();

        var existingMap = existingGroups.ToDictionary(
            g => g.Key,
            g => g.OrderByDescending(w => w.EffectiveFrom)
                  .ThenByDescending(w => w.Id)
                  .First());

        var previousWeekStart = weekStart.AddDays(-7);
        var previousWeekEnd = weekEnd.AddDays(-7);
        var previousWeekPrices = completeWeeklyRefresh
            ? await _context.WeeklyPrices
                .AsNoTracking()
                .Where(w => pids.Contains(w.ProductId) &&
                            w.EffectiveFrom <= previousWeekEnd &&
                            w.EffectiveTo >= previousWeekStart)
                .ToListAsync()
            : new List<WeeklyPrice>();

        var previousWeekMap = previousWeekPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
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
        var savedItems = new List<PriceVersusItem>(productMap.Count);

        foreach (var prod in productMap.Values.OrderBy(p => p.Name).ThenBy(p => p.Id))
        {
            var hasPostedPrice = postedMap.TryGetValue(prod.Id, out var postedItem);
            var item = hasPostedPrice
                ? postedItem!
                : BuildZeroPriceItem(prod);

            // A repeated price is not considered a fresh update. Keep it zero
            // until the supplier/import file gives a changed delivery price.
            var matchesPreviousWeekPrice = completeWeeklyRefresh &&
                                           hasPostedPrice &&
                                           previousWeekMap.TryGetValue(prod.Id, out var previousWeekPrice) &&
                                           PricesMatch(item.DeliveryPrice, previousWeekPrice.DeliveryPrice);

            var forceZeroPrice = completeWeeklyRefresh && (!hasPostedPrice || item.DeliveryPrice <= 0m || matchesPreviousWeekPrice);

            var normalized = NormalizePostedPricing(item, prod);
            var masterCost = prod.UnitCost;
            var masterMarkup = prod.Markup;
            var masterDeliveryFee = prod.DeliveryFee;
            var cost = forceZeroPrice ? masterCost : normalized.Cost;
            var markup = forceZeroPrice ? -cost : normalized.Markup;
            var deliveryFee = forceZeroPrice ? 0m : normalized.DeliveryFee;

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
            else if (forceZeroPrice)
            {
                // Snapshot the current cost so an explicit zero price stays zero
                // even if the master product cost changes before the week ends.
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
            if (forceZeroPrice)
            {
                basePrice = 0m;
                deliveryPrice = 0m;
            }

            var shouldHaveWeekly = forceCreateWeeklyRecords ||
                                   costOverride.HasValue ||
                                   markup != masterMarkup ||
                                   deliveryFeeOverride.HasValue;

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
