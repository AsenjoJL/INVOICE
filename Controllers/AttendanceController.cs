using HazelInvoice.Data;
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
        var workDate = (date ?? DateTime.Today).Date;
        var (isDateLocked, lockReason) = await GetDateLockState(workDate);

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
            WorkDate = workDate,
            IsDateLocked = isDateLocked,
            DateLockReason = lockReason
        };

        foreach (var laborer in laborers)
        {
            if (existingMap.TryGetValue(laborer.Id, out var record))
            {
                model.Entries.Add(new AttendanceEntryViewModel
                {
                    LaborerId = laborer.Id,
                    LaborerName = laborer.FullName,
                    DailyRate = laborer.DailyRate,
                    Status = record.Status,
                    Notes = record.Notes,
                    WageAmount = record.WageAmount,
                    IsLocked = isDateLocked,
                    LockReason = lockReason
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
                    WageAmount = wage,
                    IsLocked = isDateLocked,
                    LockReason = lockReason
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
        var (isDateLocked, lockReason) = await GetDateLockState(workDate);
        if (isDateLocked)
        {
            TempData["AttendanceError"] = lockReason ?? "Attendance is locked for this date.";
            return RedirectToAction(nameof(Daily), new { date = workDate.ToString("yyyy-MM-dd") });
        }

        var laborerIds = model.Entries.Select(e => e.LaborerId).ToList();
        var laborers = await _context.Laborers
            .Where(l => laborerIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l);

        var existing = await _context.AttendanceRecords
            .Include(a => a.PayrollPeriod)
            .Where(a => a.WorkDate == workDate && laborerIds.Contains(a.LaborerId))
            .ToListAsync();

        var existingMap = existing.ToDictionary(a => a.LaborerId, a => a);

        foreach (var entry in model.Entries)
        {
            if (!laborers.TryGetValue(entry.LaborerId, out var laborer))
            {
                continue;
            }

            var multiplier = GetMultiplier(entry.Status);
            var wage = laborer.DailyRate * multiplier;

            if (existingMap.TryGetValue(entry.LaborerId, out var record))
            {
                record.Status = entry.Status;
                record.RateSnapshot = laborer.DailyRate;
                record.Multiplier = multiplier;
                record.WageAmount = wage;
                record.Notes = entry.Notes;
            }
            else
            {
                _context.AttendanceRecords.Add(new AttendanceRecord
                {
                    LaborerId = entry.LaborerId,
                    WorkDate = workDate,
                    Status = entry.Status,
                    RateSnapshot = laborer.DailyRate,
                    Multiplier = multiplier,
                    WageAmount = wage,
                    Notes = entry.Notes
                });
            }
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Daily), new { date = workDate.ToString("yyyy-MM-dd") });
    }

    private async Task<(bool IsLocked, string? Reason)> GetDateLockState(DateTime workDate)
    {
        var cutoff = await _context.PayrollCutoffs
            .AsNoTracking()
            .Where(c => c.IsLocked && c.StartDate <= workDate && c.EndDate >= workDate)
            .OrderByDescending(c => c.LockedAt)
            .FirstOrDefaultAsync();

        if (cutoff != null)
        {
            return (true, $"Locked by cutoff {cutoff.StartDate:MMM dd, yyyy} - {cutoff.EndDate:MMM dd, yyyy}.");
        }

        var hasPaidAttendance = await _context.AttendanceRecords
            .AsNoTracking()
            .AnyAsync(a => a.WorkDate == workDate &&
                           a.PayrollPeriodId != null &&
                           a.PayrollPeriod != null &&
                           a.PayrollPeriod.Status == PaymentStatus.Paid);

        if (hasPaidAttendance)
        {
            return (true, "Locked because attendance on this date is already in a paid payroll.");
        }

        return (false, null);
    }

    private static decimal GetMultiplier(AttendanceStatus status)
    {
        return status switch
        {
            AttendanceStatus.Present => 1.0m,
            AttendanceStatus.Late => 0.75m,
            AttendanceStatus.HalfDay => 0.50m,
            AttendanceStatus.Absent => 0.0m,
            _ => 1.0m
        };
    }
}
