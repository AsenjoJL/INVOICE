using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class ProductsController : Controller
{
    private readonly ApplicationDbContext _context;

    public ProductsController(ApplicationDbContext context)
    {
        _context = context;
    }

    // GET: Products
    public async Task<IActionResult> Index()
    {
        return View(await _context.Products.OrderBy(p => p.SKU).ThenBy(p => p.Name).ToListAsync());
    }

    // GET: Products/Create
    public async Task<IActionResult> Create()
    {
        ViewBag.CategoryOptions = await GetCategoryOptionsAsync();
        return View();
    }

    // POST: Products/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,SKU,Name,Category,Unit,UnitCost,DeliveryFee,IsActive")] Product product)
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
            return RedirectToAction(nameof(Index));
        }
        ViewBag.CategoryOptions = await GetCategoryOptionsAsync();
        return View(product);
    }

    // GET: Products/Edit/5
    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();
        ViewBag.CategoryOptions = await GetCategoryOptionsAsync();
        return View(product);
    }

    // POST: Products/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,SKU,Name,Category,Unit,UnitCost,DeliveryFee,IsActive")] Product product)
    {
        if (id != product.Id) return NotFound();

        if (ModelState.IsValid)
        {
            try
            {
                _context.Update(product);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!ProductExists(product.Id)) return NotFound();
                else throw;
            }
            return RedirectToAction(nameof(Index));
        }
        ViewBag.CategoryOptions = await GetCategoryOptionsAsync();
        return View(product);
    }

    // GET: Products/Delete/5
    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();

        return View(product);
    }

    // POST: Products/Delete/5
    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var product = await _context.Products.FindAsync(id);
        if (product == null) return NotFound();

        product.IsActive = false;
        _context.Update(product);
        await _context.SaveChangesAsync();

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
        TempData["Message"] = $"Renumbered {products.Count} products. First: {products.FirstOrDefault()?.Name} ({products.FirstOrDefault()?.SKU})";
        return RedirectToAction(nameof(Index));
    }

    private bool ProductExists(int id)
    {
        return _context.Products.Any(e => e.Id == id);
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
}
