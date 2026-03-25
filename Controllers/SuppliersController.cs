using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class SuppliersController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public SuppliersController(ApplicationDbContext context, IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
    }

    public async Task<IActionResult> Index()
    {
        var suppliers = await _context.Suppliers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync();

        return View(suppliers);
    }

    public IActionResult Create()
    {
        return View(new Supplier { IsActive = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Supplier supplier)
    {
        if (ModelState.IsValid)
        {
            _context.Add(supplier);
            await _context.SaveChangesAsync();
            _cacheInvalidator.InvalidateSuppliers();
            return RedirectToAction(nameof(Index));
        }

        return View(supplier);
    }

    public async Task<IActionResult> Edit(int? id, string? returnUrl = null)
    {
        if (id == null) return NotFound();

        var supplier = await _context.Suppliers.FindAsync(id);
        if (supplier == null) return NotFound();

        ViewBag.ReturnUrl = returnUrl;
        return View(supplier);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Supplier supplier, string? returnUrl = null, int? returnScrollY = null)
    {
        if (id != supplier.Id) return NotFound();

        if (ModelState.IsValid)
        {
            _context.Update(supplier);
            await _context.SaveChangesAsync();
            _cacheInvalidator.InvalidateSuppliers();

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                var redirectUrl = QueryHelpers.AddQueryString(returnUrl, "highlightSupplierId", supplier.Id.ToString());
                if (returnScrollY.HasValue && returnScrollY.Value > 0)
                {
                    redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "restoreScrollY", returnScrollY.Value.ToString());
                }

                return LocalRedirect(redirectUrl);
            }

            return RedirectToAction(nameof(Index));
        }

        ViewBag.ReturnUrl = returnUrl;
        return View(supplier);
    }

    public async Task<IActionResult> Delete(int? id, string? returnUrl = null)
    {
        if (id == null) return NotFound();

        var supplier = await _context.Suppliers.FindAsync(id);
        if (supplier == null) return NotFound();

        ViewBag.ReturnUrl = returnUrl;
        return View(supplier);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id, string? returnUrl = null, int? returnScrollY = null)
    {
        var supplier = await _context.Suppliers.FindAsync(id);
        if (supplier == null) return NotFound();

        supplier.IsActive = false;
        _context.Update(supplier);
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateSuppliers();

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            var redirectUrl = QueryHelpers.AddQueryString(returnUrl, "highlightSupplierId", supplier.Id.ToString());
            if (returnScrollY.HasValue && returnScrollY.Value > 0)
            {
                redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "restoreScrollY", returnScrollY.Value.ToString());
            }

            return LocalRedirect(redirectUrl);
        }

        return RedirectToAction(nameof(Index));
    }
}
