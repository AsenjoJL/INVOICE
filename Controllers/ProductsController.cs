using HazelInvoice.Data;
using HazelInvoice.Helpers;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using HazelInvoice.ViewModels;
using HazelInvoice.Helpers;
using HazelInvoice.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace HazelInvoice.Controllers;

[Authorize]
public class ProductsController : Controller
{
    private const string ScopeActive = "active";
    private const string ScopeInactive = "inactive";
    private const string ScopeAll = "all";

    private readonly ApplicationDbContext _context;
    private readonly ILookupCacheService _lookupCache;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public ProductsController(ApplicationDbContext context, ILookupCacheService lookupCache, IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _lookupCache = lookupCache;
        _cacheInvalidator = cacheInvalidator;
    }

    // GET: Products
    public async Task<IActionResult> Index(string? q = null, DateTime? date = null, string? scope = null, int? clientGroupId = null)
    {
        var businessMoment = date ?? BusinessDate.Now();
        var businessDate = businessMoment.Date;
        var applicableBusinessDate = WeeklyPriceCalendar.GetApplicablePriceDate(businessMoment);
        var isResetDay = WeeklyPriceCalendar.IsResetDay(businessMoment);
        var normalizedScope = NormalizeScope(scope);
        
        var query = _context.Products
            .AsNoTracking()
            .AsQueryable();

        if (clientGroupId.HasValue)
        {
            query = query.Where(p => p.ClientGroupId == clientGroupId.Value || p.ClientGroupId == null);
        }

        var summary = await query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Active = g.Count(p => p.IsActive)
            })
            .FirstOrDefaultAsync();

        query = normalizedScope switch
        {
            ScopeInactive => query.Where(p => !p.IsActive),
            ScopeAll => query,
            _ => query.Where(p => p.IsActive)
        };

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(p =>
                p.Name.Contains(term) ||
                p.SKU.Contains(term) ||
                (p.Category != null && p.Category.Contains(term)) ||
                p.Unit.Contains(term));
        }

        var products = await query
            .OrderBy(p => p.Name)
            .ThenBy(p => p.SKU)
            .ToListAsync();

        var productIds = products.Select(p => p.Id).ToList();
        var weeklyPrices = applicableBusinessDate.HasValue
            ? await _context.WeeklyPrices
                .AsNoTracking()
                .Where(w => productIds.Contains(w.ProductId) && w.EffectiveFrom <= applicableBusinessDate.Value && w.EffectiveTo >= applicableBusinessDate.Value)
                .ToListAsync()
            : new List<WeeklyPrice>();

        var weeklyMap = weeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(w => w.EffectiveFrom)
                      .ThenByDescending(w => w.Id)
                      .First());

        var viewModel = products.Select(product =>
        {
            var weeklyPrice = !isResetDay && weeklyMap.TryGetValue(product.Id, out var wp) ? wp : null;
            var effectiveCost = isResetDay ? 0m : weeklyPrice?.CostOverride ?? product.UnitCost;
            var effectiveDeliveryFee = isResetDay ? 0m : weeklyPrice?.DeliveryFee ?? product.DeliveryFee;

            decimal effectiveMarkup = isResetDay ? 0m : product.Markup;
            if (!isResetDay && weeklyPrice != null)
            {
                if (weeklyPrice.Markup != 0)
                {
                    effectiveMarkup = weeklyPrice.Markup;
                }
                else if (weeklyPrice.BasePrice > 0)
                {
                    effectiveMarkup = weeklyPrice.BasePrice - effectiveCost;
                }
            }

            var effectiveBasePrice = effectiveCost + effectiveMarkup;
            var effectiveDeliveryPrice = isResetDay ? 0m : weeklyPrice?.DeliveryPrice ?? (effectiveBasePrice + effectiveDeliveryFee);

            return new ProductListItemViewModel
            {
                Id = product.Id,
                SKU = product.SKU,
                Name = product.Name,
                Category = product.Category,
                Unit = product.Unit,
                UnitCost = product.UnitCost,
                EffectiveCost = effectiveCost,
                EffectiveBasePrice = effectiveBasePrice,
                EffectiveDeliveryFee = effectiveDeliveryFee,
                EffectiveDeliveryPrice = effectiveDeliveryPrice,
                HasWeeklyPrice = weeklyPrice != null,
                IsActive = product.IsActive
            };
        }).ToList();

        ViewBag.SearchTerm = q ?? string.Empty;
        ViewBag.TargetDate = businessDate;
        ViewBag.Scope = normalizedScope;
        ViewBag.ClientGroupId = clientGroupId;
        ViewBag.ClientGroupOptions = await _context.ClientGroups.Where(g => g.IsActive).OrderBy(g => g.DisplayOrder).ThenBy(g => g.Name).ToListAsync();
        ViewBag.TotalProducts = summary?.Total ?? 0;
        ViewBag.ActiveProducts = summary?.Active ?? 0;
        ViewBag.InactiveProducts = (summary?.Total ?? 0) - (summary?.Active ?? 0);
        return View(viewModel);
    }

    // GET: Products/Create
    public async Task<IActionResult> Create()
    {
        ViewBag.CategoryOptions = await GetCategoryOptionsAsync();
        ViewBag.SupplierOptions = await GetSupplierOptionsAsync();
        ViewBag.ClientGroupOptions = await _context.ClientGroups.Where(g => g.IsActive).OrderBy(g => g.DisplayOrder).ThenBy(g => g.Name).ToListAsync();
        return View();
    }

    // POST: Products/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,SKU,Name,Category,SupplierId,ClientGroupId,Unit,UnitCost,DeliveryFee,IsActive")] Product product)
    {
        // Auto-generate SKU if empty or default
        if (string.IsNullOrWhiteSpace(product.SKU) || product.SKU == "V-XXX")
        {
            var lastSku = await _context.Products
                .Where(p => p.SKU.StartsWith("V-"))
                .Select(p => p.SKU)
                .OrderByDescending(s => s)
                .FirstOrDefaultAsync();

            int nextNum = 1;
            if (lastSku != null && lastSku.Length > 2)
            {
                if (int.TryParse(lastSku.Substring(2), out int current))
                    nextNum = current + 1;
            }
            product.SKU = $"V-{nextNum:003}";
            
            // Clear validation error for SKU since we just fixed it
            ModelState.Remove("SKU");
        }

        if (ModelState.IsValid)
        {
            _context.Add(product);
            await _context.SaveChangesAsync();
            _cacheInvalidator.InvalidateProducts();
            _cacheInvalidator.InvalidateWeeklyPrices();
            return RedirectToAction(nameof(Index));
        }
        ViewBag.CategoryOptions = await GetCategoryOptionsAsync();
        ViewBag.SupplierOptions = await GetSupplierOptionsAsync();
        ViewBag.ClientGroupOptions = await _context.ClientGroups.Where(g => g.IsActive).OrderBy(g => g.DisplayOrder).ThenBy(g => g.Name).ToListAsync();
        return View(product);
    }

    // GET: Products/Edit/5
    public async Task<IActionResult> Edit(int? id, string? returnUrl = null)
    {
        if (id == null) return NotFound();

        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();
        ViewBag.ReturnUrl = returnUrl;
        ViewBag.CategoryOptions = await GetCategoryOptionsAsync();
        ViewBag.SupplierOptions = await GetSupplierOptionsAsync();
        ViewBag.ClientGroupOptions = await _context.ClientGroups.Where(g => g.IsActive).OrderBy(g => g.DisplayOrder).ThenBy(g => g.Name).ToListAsync();
        return View(product);
    }

    // POST: Products/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,SKU,Name,Category,SupplierId,ClientGroupId,Unit,UnitCost,DeliveryFee,IsActive")] Product product, string? returnUrl = null, int? returnClientPage = null)
    {
        if (id != product.Id) return NotFound();

        if (ModelState.IsValid)
        {
            try
            {
                _context.Update(product);
                await _context.SaveChangesAsync();
                _cacheInvalidator.InvalidateProducts();
                _cacheInvalidator.InvalidateWeeklyPrices();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!ProductExists(product.Id)) return NotFound();
                else throw;
            }

            if (!product.IsActive)
            {
                returnUrl = BuildActiveCatalogReturnUrl(returnUrl);
            }

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                var redirectUrl = returnUrl;
                if (returnClientPage.HasValue && returnClientPage.Value > 1)
                {
                    redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "restorePage", returnClientPage.Value.ToString());
                }

                redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "highlightProductId", product.Id.ToString());

                return LocalRedirect(redirectUrl);
            }

            return RedirectToAction(nameof(Index));
        }
        ViewBag.ReturnUrl = returnUrl;
        ViewBag.CategoryOptions = await GetCategoryOptionsAsync();
        ViewBag.SupplierOptions = await GetSupplierOptionsAsync();
        ViewBag.ClientGroupOptions = await _context.ClientGroups.Where(g => g.IsActive).OrderBy(g => g.DisplayOrder).ThenBy(g => g.Name).ToListAsync();
        return View(product);
    }

    // GET: Products/Delete/5
    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();

        ViewBag.ReturnUrl = Request.Query["returnUrl"].ToString();
        return View(product);
    }

    // POST: Products/Delete/5
    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id, string? returnUrl = null, int? returnClientPage = null, int? returnScrollY = null)
    {
        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();

        product.IsActive = false;
        _context.Update(product);
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateProducts();
        _cacheInvalidator.InvalidateWeeklyPrices();

        TempData["Message"] = $"Deactivated {product.Name}.";
        returnUrl = BuildActiveCatalogReturnUrl(returnUrl);
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(BuildReturnUrl(returnUrl, returnClientPage, returnScrollY));

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePermanent(int id, string? returnUrl = null, int? returnClientPage = null, int? returnScrollY = null)
    {
        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();

        var hasReceiptLines = await _context.ReceiptLines.AnyAsync(l => l.ProductId == id);
        var hasPurchaseLines = await _context.PurchaseLines.AnyAsync(l => l.ProductId == id);
        var hasStockMovements = await _context.ProductStockMovements.AnyAsync(m => m.ProductId == id);

        if (hasReceiptLines || hasPurchaseLines || hasStockMovements)
        {
            TempData["Message"] = $"Cannot delete {product.Name} from the database because it already has receipts, purchases, or stock history. Deactivate it instead.";
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                return LocalRedirect(BuildReturnUrl(returnUrl, returnClientPage, returnScrollY));

            return RedirectToAction(nameof(Index));
        }

        var weeklyPrices = await _context.WeeklyPrices.Where(w => w.ProductId == id).ToListAsync();
        if (weeklyPrices.Count > 0)
            _context.WeeklyPrices.RemoveRange(weeklyPrices);

        _context.Products.Remove(product);
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateProducts();
        _cacheInvalidator.InvalidateWeeklyPrices();

        TempData["Message"] = $"Deleted {product.Name} from the database.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(BuildReturnUrl(returnUrl, returnClientPage, returnScrollY));

        return RedirectToAction(nameof(Index));
    }

    // POST: Products/GenerateSkus
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateSkus()
    {
        var products = await _context.Products.OrderBy(p => p.Name).ToListAsync();
        int i = 1;
        foreach (var p in products)
        {
            p.SKU = $"V-{i:003}"; 
            i++;
        }
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateProducts();
        TempData["Message"] = $"Renumbered {products.Count} products. First: {products.FirstOrDefault()?.Name} ({products.FirstOrDefault()?.SKU})";
        return RedirectToAction(nameof(Index));
    }

    private bool ProductExists(int id)
    {
        return _context.Products.Any(e => e.Id == id);
    }

    private static string BuildReturnUrl(string returnUrl, int? returnClientPage, int? returnScrollY = null)
    {
        var redirectUrl = returnUrl;

        if (returnClientPage.HasValue && returnClientPage.Value > 1)
        {
            redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "restorePage", returnClientPage.Value.ToString());
        }

        if (returnScrollY.HasValue && returnScrollY.Value > 0)
        {
            redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "restoreScrollY", returnScrollY.Value.ToString());
        }

        return redirectUrl;
    }

    private static string? BuildActiveCatalogReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return null;
        }

        var path = returnUrl;
        var queryString = string.Empty;

        var queryIndex = returnUrl.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            path = returnUrl[..queryIndex];
            queryString = returnUrl[(queryIndex + 1)..];
        }

        var query = QueryHelpers.ParseQuery(queryString);
        var normalized = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query)
        {
            if (string.Equals(pair.Key, "scope", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, "restorePage", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, "highlightProductId", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            normalized[pair.Key] = pair.Value;
        }

        normalized["scope"] = ScopeActive;
        return QueryHelpers.AddQueryString(path, normalized);
    }

    private async Task<List<string>> GetCategoryOptionsAsync()
    {
        return await _context.Products
            .AsNoTracking()
            .Where(p => !string.IsNullOrWhiteSpace(p.Category))
            .Select(p => p.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    private async Task<List<Supplier>> GetSupplierOptionsAsync()
    {
        return (await _lookupCache.GetActiveSuppliersAsync(HttpContext.RequestAborted)).ToList();
    }

    private static string NormalizeScope(string? scope)
    {
        var value = scope?.Trim().ToLowerInvariant();
        return value switch
        {
            ScopeInactive => ScopeInactive,
            ScopeAll => ScopeAll,
            _ => ScopeActive
        };
    }

    [HttpGet]
    public IActionResult DownloadTemplate()
    {
        var rows = new List<List<string>>
        {
            new List<string> { "Name", "Category", "Unit", "UnitCost", "Markup", "DeliveryFee" },
            new List<string> { "Sample Carrot", "Vegetables", "kg", "50.00", "20.00", "0" },
            new List<string> { "Sample Pork", "Meat", "kg", "250.00", "30.00", "10.00" }
        };

        var options = new SimpleXlsxSheetOptions
        {
            AutoFitColumns = true,
            MinimumColumnWidth = 15
        };

        var xlsxBytes = SimpleXlsxWriter.WriteSingleSheet("Products Template", rows, options);
        return File(xlsxBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Product_Import_Template.xlsx");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportExcel(IFormFile? importFile, int? clientGroupId)
    {
        if (importFile == null || importFile.Length == 0)
        {
            TempData["ErrorMessage"] = "Please choose an Excel (.xlsx) file.";
            return RedirectToAction(nameof(Index), new { clientGroupId });
        }

        if (!string.Equals(Path.GetExtension(importFile.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "Invalid file type. Please upload an .xlsx file.";
            return RedirectToAction(nameof(Index), new { clientGroupId });
        }

        try
        {
            await using var stream = new MemoryStream();
            await importFile.CopyToAsync(stream);

            var sheet = SimpleXlsxReader.ReadFirstSheet(stream);
            if (sheet.MaxRow < 2)
            {
                TempData["ErrorMessage"] = "The file is empty or missing data rows.";
                return RedirectToAction(nameof(Index), new { clientGroupId });
            }

            // Find headers
            int nameCol = -1, catCol = -1, unitCol = -1, costCol = -1, markupCol = -1, deliveryCol = -1;
            for (int c = 1; c <= sheet.MaxCol; c++)
            {
                var header = sheet.GetCell(1, c).Trim().ToLowerInvariant().Replace(" ", "");
                if (header == "name") nameCol = c;
                else if (header == "category") catCol = c;
                else if (header == "unit") unitCol = c;
                else if (header == "unitcost") costCol = c;
                else if (header == "markup") markupCol = c;
                else if (header == "deliveryfee") deliveryCol = c;
            }

            if (nameCol == -1 || costCol == -1)
            {
                TempData["ErrorMessage"] = "Missing required columns: Name, UnitCost.";
                return RedirectToAction(nameof(Index), new { clientGroupId });
            }

            var newProducts = new List<Product>();
            for (int r = 2; r <= sheet.MaxRow; r++)
            {
                var name = sheet.GetCell(r, nameCol);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var cat = catCol != -1 ? sheet.GetCell(r, catCol) : "";
                var unit = unitCol != -1 ? sheet.GetCell(r, unitCol) : "pc";
                
                if (string.IsNullOrWhiteSpace(unit)) unit = "pc";
                
                var rawCost = costCol != -1 ? sheet.GetCell(r, costCol) : "0";
                var rawMarkup = markupCol != -1 ? sheet.GetCell(r, markupCol) : "0";
                var rawDelivery = deliveryCol != -1 ? sheet.GetCell(r, deliveryCol) : "0";

                SimpleXlsxReader.TryParseDecimal(rawCost, out var cost);
                SimpleXlsxReader.TryParseDecimal(rawMarkup, out var markup);
                SimpleXlsxReader.TryParseDecimal(rawDelivery, out var delivery);

                var p = new Product
                {
                    Name = name.Length > 100 ? name[..100] : name,
                    Category = cat.Length > 50 ? cat[..50] : cat,
                    Unit = unit.Length > 20 ? unit[..20] : unit,
                    UnitCost = cost,
                    Markup = markup,
                    DeliveryFee = delivery,
                    ClientGroupId = clientGroupId,
                    IsActive = true,
                    SKU = "TEMP-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()
                };
                newProducts.Add(p);
            }

            if (newProducts.Count > 0)
            {
                _context.Products.AddRange(newProducts);
                await _context.SaveChangesAsync(HttpContext.RequestAborted);
                
                // Fix SKUs to V-XXX standard
                foreach (var p in newProducts)
                {
                    p.SKU = $"V-{p.Id:D4}";
                }
                await _context.SaveChangesAsync(HttpContext.RequestAborted);
                _cacheInvalidator.InvalidateProducts();
            }

            TempData["SuccessMessage"] = $"Successfully imported {newProducts.Count} products.";
            return RedirectToAction(nameof(Index), new { clientGroupId });
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Unable to read Excel file: {ex.Message}";
            return RedirectToAction(nameof(Index), new { clientGroupId });
        }
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> UndoMCIAAImport()
    {
        var targetGroup = await _context.ClientGroups.FirstOrDefaultAsync(g => g.Name.Contains("MCIAA"));
        if (targetGroup == null)
            return Content("MCIAA group not found.");

        var productsToDelete = await _context.Products.Where(p => p.ClientGroupId == targetGroup.Id).ToListAsync();
        _context.Products.RemoveRange(productsToDelete);
        
        await _context.SaveChangesAsync();
        return Content($"Successfully deleted {productsToDelete.Count} products from MCIAA.");
    }
}
