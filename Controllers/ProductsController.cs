using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class ProductsController : Controller
{
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
    public async Task<IActionResult> Index(string? q = null)
    {
        var query = _context.Products
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(p =>
                p.Name.Contains(term) ||
                p.SKU.Contains(term) ||
                (p.Category != null && p.Category.Contains(term)) ||
                p.Unit.Contains(term));
        }

        ViewBag.SearchTerm = q ?? string.Empty;
        return View(await query.OrderBy(p => p.Name).ThenBy(p => p.SKU).ToListAsync());
    }

    // GET: Products/Create
    public async Task<IActionResult> Create()
    {
        ViewBag.CategoryOptions = await GetCategoryOptionsAsync();
        ViewBag.SupplierOptions = await GetSupplierOptionsAsync();
        return View();
    }

    // POST: Products/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,SKU,Name,Category,SupplierId,Unit,UnitCost,DeliveryFee,IsActive")] Product product)
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
        return View(product);
    }

    // POST: Products/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,SKU,Name,Category,SupplierId,Unit,UnitCost,DeliveryFee,IsActive")] Product product, string? returnUrl = null, int? returnClientPage = null)
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
    public async Task<IActionResult> DeleteConfirmed(int id, string? returnUrl = null, int? returnClientPage = null)
    {
        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();

        product.IsActive = false;
        _context.Update(product);
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateProducts();
        _cacheInvalidator.InvalidateWeeklyPrices();

        TempData["Message"] = $"Deactivated {product.Name}.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(BuildReturnUrl(returnUrl, returnClientPage));

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePermanent(int id, string? returnUrl = null, int? returnClientPage = null)
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
                return LocalRedirect(BuildReturnUrl(returnUrl, returnClientPage));

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
            return LocalRedirect(BuildReturnUrl(returnUrl, returnClientPage));

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

    private static string BuildReturnUrl(string returnUrl, int? returnClientPage)
    {
        if (!returnClientPage.HasValue || returnClientPage.Value <= 1)
        {
            return returnUrl;
        }

        return QueryHelpers.AddQueryString(returnUrl, "restorePage", returnClientPage.Value.ToString());
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
}
