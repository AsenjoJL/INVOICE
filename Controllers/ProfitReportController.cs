using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Reports;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;

namespace HazelInvoice.Controllers;

[Authorize]
public class ProfitReportController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IProfitReportService _profitReportService;

    public ProfitReportController(ApplicationDbContext context, IProfitReportService profitReportService)
    {
        _context = context;
        _profitReportService = profitReportService;
    }

    // GET: ProfitReport
    public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate, bool includeUnpaid = true, decimal percentFee = 1.0m, decimal split1 = 40m)
    {
        var start = startDate ?? DateTime.Today.AddDays(-14);
        var end = endDate ?? DateTime.Today;

        var vm = await _profitReportService.BuildAsync(
            new ProfitReportQueryOptions(
                StartDate: start,
                EndDate: end,
                IncludeUnpaid: includeUnpaid,
                PercentFee: percentFee,
                Partner1SharePercent: split1),
            HttpContext.RequestAborted);

        return View(vm);
    }
    
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDeduction(Deduction model, string returnUrl)
    {
        if(ModelState.IsValid)
        {
            // Default applied to General if unset
            if(string.IsNullOrEmpty(model.AppliedTo)) model.AppliedTo = "General";
            if(string.IsNullOrEmpty(model.Category)) model.Category = "General";
            
            _context.Deductions.Add(model);
            await _context.SaveChangesAsync();
        }
        return Redirect(returnUrl);
    }
    
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCapital(PartnerCapital model, string returnUrl)
    {
         if (model == null) return Redirect(returnUrl);

        if (string.IsNullOrWhiteSpace(model.FundName))
        {
            model.FundName = string.IsNullOrWhiteSpace(model.Description) ? "Capital Fund" : model.Description.Trim();
        }

        if (string.IsNullOrWhiteSpace(model.Description))
        {
            model.Description = model.FundName ?? "Capital Fund";
        }

        if (model.Amount <= 0)
        {
            var amountStr = Request.Form["Amount"].ToString();
            if (!string.IsNullOrWhiteSpace(amountStr))
            {
                if (!decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) &&
                    !decimal.TryParse(amountStr, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed))
                {
                    var normalized = amountStr.Replace(",", "");
                    decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed);
                }
                if (parsed > 0) model.Amount = parsed;
            }
        }

        if (model.Date == default)
        {
            model.Date = DateTime.Today;
        }

        if (model.Amount > 0)
        {
            _context.PartnerCapitals.Add(model);
            await _context.SaveChangesAsync();
        }
        return Redirect(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPurchase(PartnerPurchase model, string returnUrl)
    {
         if(ModelState.IsValid)
        {
            _context.PartnerPurchases.Add(model);
            await _context.SaveChangesAsync();
        }
        return Redirect(returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveReceivedAmount(DateTime startDate, DateTime endDate, decimal amount, string returnUrl)
    {
        var rangeStart = startDate.Date;
        var rangeEnd = endDate.Date;

        var existing = await _context.CollectionReceivedOverrides
            .Where(o => o.StartDate == rangeStart && o.EndDate == rangeEnd)
            .ToListAsync();

        if (amount <= 0)
        {
            if (existing.Any())
            {
                _context.CollectionReceivedOverrides.RemoveRange(existing);
                await _context.SaveChangesAsync();
            }
            return Redirect(returnUrl);
        }

        var model = new CollectionReceivedOverride
        {
            StartDate = rangeStart,
            EndDate = rangeEnd,
            Amount = amount,
            CreatedAt = DateTime.Now
        };

        _context.CollectionReceivedOverrides.Add(model);
        await _context.SaveChangesAsync();

        return Redirect(returnUrl);
    }
}
