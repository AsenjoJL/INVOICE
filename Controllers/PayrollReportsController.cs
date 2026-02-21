using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class PayrollReportsController : Controller
{
    private readonly ApplicationDbContext _context;

    public PayrollReportsController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Unpaid(DateTime? startDate, DateTime? endDate)
    {
        var query = _context.PayrollPeriods
            .AsNoTracking()
            .Include(p => p.Laborer)
            .Where(p => p.Status != PaymentStatus.Paid);

        if (startDate.HasValue)
        {
            var start = startDate.Value.Date;
            query = query.Where(p => p.PeriodEnd >= start);
        }
        if (endDate.HasValue)
        {
            var end = endDate.Value.Date;
            query = query.Where(p => p.PeriodStart <= end);
        }

        var periods = await query
            .OrderByDescending(p => p.PeriodEnd)
            .ToListAsync();

        var model = new UnpaidPayrollViewModel
        {
            StartDate = startDate?.Date,
            EndDate = endDate?.Date,
            Periods = periods
        };

        return View(model);
    }

    public async Task<IActionResult> LaborCost(DateTime? startDate, DateTime? endDate)
    {
        var start = (startDate ?? DateTime.Today.AddDays(-30)).Date;
        var end = (endDate ?? DateTime.Today).Date;
        var endExclusive = end.AddDays(1);

        var attendance = await _context.AttendanceRecords
            .AsNoTracking()
            .Include(a => a.Laborer)
            .Where(a => a.WorkDate >= start && a.WorkDate < endExclusive)
            .ToListAsync();

        var rows = attendance
            .GroupBy(a => a.LaborerId)
            .Select(g => new LaborCostRow
            {
                LaborerId = g.Key,
                LaborerName = g.First().Laborer?.FullName ?? "Unknown",
                TotalDays = g.Count(),
                TotalWage = g.Sum(x => x.WageAmount)
            })
            .OrderBy(r => r.LaborerName)
            .ToList();

        var model = new LaborCostReportViewModel
        {
            StartDate = start,
            EndDate = end,
            Rows = rows,
            TotalCost = rows.Sum(r => r.TotalWage)
        };

        return View(model);
    }
}
