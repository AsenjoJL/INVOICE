using HazelInvoice.Data;
using HazelInvoice.Helpers;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class AttendanceController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly ILookupCacheService _lookupCache;

    public AttendanceController(ApplicationDbContext context, ILookupCacheService lookupCache)
    {
        _context = context;
        _lookupCache = lookupCache;
    }

    [HttpGet]
    public async Task<IActionResult> Daily(DateTime? date)
    {
        var workDate = (date ?? BusinessDate.Today()).Date;
        var laborers = await _lookupCache.GetActiveLaborersAsync(HttpContext.RequestAborted);

        var laborerIds = laborers.Select(l => l.Id).ToList();
        var existing = await _context.AttendanceRecords
            .AsNoTracking()
            .Include(a => a.PayrollPeriod)
            .Where(a => a.WorkDate == workDate && laborerIds.Contains(a.LaborerId))
            .ToListAsync();

        var existingMap = existing.ToDictionary(a => a.LaborerId, a => a);
        var model = new AttendanceDailyViewModel
        {
            WorkDate = workDate
        };

        foreach (var laborer in laborers)
        {
            if (existingMap.TryGetValue(laborer.Id, out var record))
            {
                model.Entries.Add(new AttendanceEntryViewModel
                {
                    AttendanceRecordId = record.Id,
                    LaborerId = laborer.Id,
                    LaborerName = laborer.FullName,
                    DailyRate = laborer.DailyRate,
                    Status = record.Status,
                    Notes = record.Notes,
                    WageAmount = record.WageAmount,
                    IsInPayroll = record.PayrollPeriodId != null
                });
            }
            else
            {
                var multiplier = GetMultiplier(AttendanceStatus.Present);
                var wage = laborer.DailyRate * multiplier;
                model.Entries.Add(new AttendanceEntryViewModel
                {
                    LaborerId = laborer.Id,
                    LaborerName = laborer.FullName,
                    DailyRate = laborer.DailyRate,
                    Status = AttendanceStatus.Present,
                    WageAmount = wage
                });
            }
        }

        model.TotalWage = model.Entries.Sum(e => e.WageAmount);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Daily(AttendanceDailyViewModel model)
    {
        var workDate = model.WorkDate.Date;
        var laborerIds = model.Entries.Select(e => e.LaborerId).ToList();
        var laborers = await _context.Laborers
            .Where(l => laborerIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l);

        var existing = await _context.AttendanceRecords
            .Include(a => a.PayrollPeriod)
            .Where(a => a.WorkDate == workDate && laborerIds.Contains(a.LaborerId))
            .ToListAsync();

        var existingMap = existing.ToDictionary(a => a.LaborerId, a => a);
        var payrollPeriodIdsToRecalculate = new HashSet<int>();

        foreach (var entry in model.Entries)
        {
            if (!laborers.TryGetValue(entry.LaborerId, out var laborer))
            {
                continue;
            }

            var normalizedStatus = entry.Status == AttendanceStatus.Absent
                ? AttendanceStatus.Absent
                : AttendanceStatus.Present;
            var multiplier = GetMultiplier(normalizedStatus);
            var wage = laborer.DailyRate * multiplier;

            if (existingMap.TryGetValue(entry.LaborerId, out var record))
            {
                record.Status = normalizedStatus;
                record.RateSnapshot = laborer.DailyRate;
                record.Multiplier = multiplier;
                record.WageAmount = wage;
                record.Notes = entry.Notes;
                if (record.PayrollPeriodId.HasValue)
                {
                    payrollPeriodIdsToRecalculate.Add(record.PayrollPeriodId.Value);
                }
            }
            else
            {
                _context.AttendanceRecords.Add(new AttendanceRecord
                {
                    LaborerId = entry.LaborerId,
                    WorkDate = workDate,
                    Status = normalizedStatus,
                    RateSnapshot = laborer.DailyRate,
                    Multiplier = multiplier,
                    WageAmount = wage,
                    Notes = entry.Notes
                });
            }
        }

        if (payrollPeriodIdsToRecalculate.Count > 0)
        {
            await RecalculatePayrollPeriodsAsync(payrollPeriodIdsToRecalculate);
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Daily), new { date = workDate.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, DateTime workDate)
    {
        var record = await _context.AttendanceRecords
            .FirstOrDefaultAsync(a => a.Id == id);
        if (record == null)
        {
            TempData["AttendanceError"] = "Attendance entry not found.";
            return RedirectToAction(nameof(Daily), new { date = workDate.ToString("yyyy-MM-dd") });
        }

        var payrollPeriodId = record.PayrollPeriodId;
        _context.AttendanceRecords.Remove(record);

        if (payrollPeriodId.HasValue)
        {
            await RecalculatePayrollPeriodsAsync(new[] { payrollPeriodId.Value });
        }

        await _context.SaveChangesAsync();
        TempData["AttendanceSuccess"] = "Attendance entry deleted.";
        return RedirectToAction(nameof(Daily), new { date = workDate.ToString("yyyy-MM-dd") });
    }

    private async Task RecalculatePayrollPeriodsAsync(IEnumerable<int> payrollPeriodIds)
    {
        var ids = payrollPeriodIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var periods = await _context.PayrollPeriods
            .Include(p => p.AttendanceRecords)
            .Include(p => p.Adjustments)
            .Where(p => ids.Contains(p.Id))
            .ToListAsync();

        foreach (var period in periods)
        {
            period.TotalDays = period.AttendanceRecords.Count;
            period.TotalWage = period.AttendanceRecords.Sum(a => a.WageAmount);

            var payableTotal = period.TotalWage + period.AdjustmentTotal;
            ApplyPaymentStatus(period, payableTotal);
        }
    }

    private static decimal GetMultiplier(AttendanceStatus status)
    {
        return status == AttendanceStatus.Absent ? 0.0m : 1.0m;
    }

    private static void ApplyPaymentStatus(PayrollPeriod period, decimal payableTotal)
    {
        var remaining = payableTotal - period.PaidAmount;
        if (remaining <= 0)
        {
            period.Status = PaymentStatus.Paid;
        }
        else if (period.PaidAmount > 0)
        {
            period.Status = PaymentStatus.Partial;
        }
        else
        {
            period.Status = PaymentStatus.Unpaid;
        }
    }
}
