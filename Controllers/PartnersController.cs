using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class PartnersController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public PartnersController(ApplicationDbContext context, IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
    }

    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var partners = await _context.PartnerBalanceConfigs
            .AsNoTracking()
            .OrderBy(p => p.PartnerName)
            .ToListAsync(HttpContext.RequestAborted);

        return View(partners);
    }

    [HttpGet]
    public IActionResult Create()
    {
        return View(new PartnerBalanceConfig { AsOfDate = DateTime.Today });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(PartnerBalanceConfig model)
    {
        model.PartnerName = (model.PartnerName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(model.PartnerName))
            ModelState.AddModelError(nameof(model.PartnerName), "Partner name is required.");

        if (!ModelState.IsValid)
            return View(model);

        var exists = await _context.PartnerBalanceConfigs
            .AsNoTracking()
            .AnyAsync(p => p.PartnerName.ToLower() == model.PartnerName.ToLower(), HttpContext.RequestAborted);

        if (exists)
        {
            ModelState.AddModelError(nameof(model.PartnerName), "Partner already exists.");
            return View(model);
        }

        _context.PartnerBalanceConfigs.Add(model);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        _cacheInvalidator.InvalidatePartners();
        _cacheInvalidator.InvalidateProfitReports();

        TempData["SuccessMessage"] = "Partner added.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var partner = await _context.PartnerBalanceConfigs
            .FirstOrDefaultAsync(p => p.Id == id, HttpContext.RequestAborted);

        if (partner == null) return NotFound();
        return View(partner);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, PartnerBalanceConfig model)
    {
        var partner = await _context.PartnerBalanceConfigs
            .FirstOrDefaultAsync(p => p.Id == id, HttpContext.RequestAborted);

        if (partner == null) return NotFound();

        model.PartnerName = (model.PartnerName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(model.PartnerName))
            ModelState.AddModelError(nameof(model.PartnerName), "Partner name is required.");

        if (!ModelState.IsValid)
            return View(model);

        var exists = await _context.PartnerBalanceConfigs
            .AsNoTracking()
            .AnyAsync(p => p.Id != id && p.PartnerName.ToLower() == model.PartnerName.ToLower(), HttpContext.RequestAborted);

        if (exists)
        {
            ModelState.AddModelError(nameof(model.PartnerName), "Partner name is already used.");
            return View(model);
        }

        partner.PartnerName = model.PartnerName;
        partner.OpeningBalance = model.OpeningBalance;
        partner.AsOfDate = model.AsOfDate;

        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        _cacheInvalidator.InvalidatePartners();
        _cacheInvalidator.InvalidateProfitReports();

        TempData["SuccessMessage"] = "Partner updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var partner = await _context.PartnerBalanceConfigs
            .FirstOrDefaultAsync(p => p.Id == id, HttpContext.RequestAborted);

        if (partner == null) return NotFound();

        _context.PartnerBalanceConfigs.Remove(partner);
        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        _cacheInvalidator.InvalidatePartners();
        _cacheInvalidator.InvalidateProfitReports();

        TempData["SuccessMessage"] = "Partner removed.";
        return RedirectToAction(nameof(Index));
    }
}
