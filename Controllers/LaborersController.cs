using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class LaborersController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public LaborersController(ApplicationDbContext context, IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
    }

    public async Task<IActionResult> Index(bool showArchived = false)
    {
        ViewData["ShowArchived"] = showArchived;
        var query = _context.Laborers.AsNoTracking();
        if (!showArchived)
        {
            query = query.Where(l => l.IsActive);
        }
        var laborers = await query.OrderBy(l => l.FullName).ToListAsync();
        return View(laborers);
    }

    public IActionResult Create()
    {
        return View(new Laborer { IsActive = true, HiredDate = DateTime.Today });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Laborer laborer)
    {
        if (!ModelState.IsValid)
        {
            return View(laborer);
        }

        _context.Laborers.Add(laborer);
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateLaborers();
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id, string? returnUrl = null)
    {
        var laborer = await _context.Laborers.FindAsync(id);
        if (laborer == null)
        {
            return NotFound();
        }
        ViewBag.ReturnUrl = returnUrl;
        return View(laborer);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Laborer laborer, string? returnUrl = null, int? returnScrollY = null)
    {
        if (id != laborer.Id)
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return View(laborer);
        }

        _context.Update(laborer);
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateLaborers();

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            var redirectUrl = QueryHelpers.AddQueryString(returnUrl, "highlightLaborerId", laborer.Id.ToString());
            if (returnScrollY.HasValue && returnScrollY.Value > 0)
            {
                redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "restoreScrollY", returnScrollY.Value.ToString());
            }

            return LocalRedirect(redirectUrl);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Archive(int id, string? returnUrl, int? returnScrollY = null)
    {
        var laborer = await _context.Laborers.FindAsync(id);
        if (laborer == null)
        {
            return NotFound();
        }

        laborer.IsActive = false;
        laborer.ArchivedAt = DateTime.Now;
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateLaborers();

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            var redirectUrl = QueryHelpers.AddQueryString(returnUrl, "highlightLaborerId", laborer.Id.ToString());
            if (returnScrollY.HasValue && returnScrollY.Value > 0)
            {
                redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "restoreScrollY", returnScrollY.Value.ToString());
            }

            return LocalRedirect(redirectUrl);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reactivate(int id, string? returnUrl, int? returnScrollY = null)
    {
        var laborer = await _context.Laborers.FindAsync(id);
        if (laborer == null)
        {
            return NotFound();
        }

        laborer.IsActive = true;
        laborer.ArchivedAt = null;
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateLaborers();

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            var redirectUrl = QueryHelpers.AddQueryString(returnUrl, "highlightLaborerId", laborer.Id.ToString());
            if (returnScrollY.HasValue && returnScrollY.Value > 0)
            {
                redirectUrl = QueryHelpers.AddQueryString(redirectUrl, "restoreScrollY", returnScrollY.Value.ToString());
            }

            return LocalRedirect(redirectUrl);
        }
        return RedirectToAction(nameof(Index));
    }
}
