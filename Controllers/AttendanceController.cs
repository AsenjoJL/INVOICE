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
        var isRegularDayOff = IsRegularDayOff(workDate);
        var laborers = await _lookupCache.GetActiveLaborersAsync(HttpContext.RequestAborted);

        var laborerIds = laborers.Select(l => l.Id).ToList();
        var history = await _context.AttendanceRecords
            .AsNoTracking()
            .Where(a => laborerIds.Contains(a.LaborerId))
            .GroupBy(a => a.LaborerId)
            .Select(g => new AttendanceHistorySummary(
                g.Key,
                g.Min(x => x.WorkDate),
                g.Count(x => x.Status != AttendanceStatus.Absent),
                g.Count(x => x.Status == AttendanceStatus.Absent),
                g.Count()))
            .ToListAsync();

        var historyMap = history.ToDictionary(x => x.LaborerId, x => x);

        var existing = await _context.AttendanceRecords
            .AsNoTracking()
            .Where(a => a.WorkDate == workDate && laborerIds.Contains(a.LaborerId))
            .ToListAsync();

        var existingMap = existing.ToDictionary(a => a.LaborerId, a => a);
        var model = new AttendanceDailyViewModel
        {
            WorkDate = workDate,
            IsRegularDayOff = isRegularDayOff
        };

        foreach (var laborer in laborers)
        {
            if (existingMap.TryGetValue(laborer.Id, out var record))
            {
                historyMap.TryGetValue(laborer.Id, out var summary);
                model.Entries.Add(new AttendanceEntryViewModel
                {
                    AttendanceRecordId = record.Id,
                    LaborerId = laborer.Id,
                    LaborerName = laborer.FullName,
                    DailyRate = laborer.DailyRate,
                    Status = record.Status,
                    Notes = record.Notes,
                    WageAmount = record.WageAmount,
                    IsInPayroll = record.PayrollEntryId != null,
                    FirstWorkDate = summary?.FirstWorkDate ?? laborer.HiredDate.Date,
                    TotalDutyDays = summary?.TotalDutyDays ?? 0,
                    TotalAbsenceDays = summary?.TotalAbsenceDays ?? 0,
                    TotalTrackedDays = summary?.TotalTrackedDays ?? 0
                });
            }
            else
            {
                if (isRegularDayOff)
                {
                    continue;
                }

                historyMap.TryGetValue(laborer.Id, out var summary);
                var multiplier = GetMultiplier(AttendanceStatus.Present);
                var wage = laborer.DailyRate * multiplier;
                model.Entries.Add(new AttendanceEntryViewModel
                {
                    LaborerId = laborer.Id,
                    LaborerName = laborer.FullName,
                    DailyRate = laborer.DailyRate,
                    Status = AttendanceStatus.Present,
                    WageAmount = wage,
                    FirstWorkDate = summary?.FirstWorkDate ?? laborer.HiredDate.Date,
                    TotalDutyDays = summary?.TotalDutyDays ?? 0,
                    TotalAbsenceDays = summary?.TotalAbsenceDays ?? 0,
                    TotalTrackedDays = summary?.TotalTrackedDays ?? 0
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
        if (IsRegularDayOff(workDate))
        {
            TempData["AttendanceError"] = "Saturday is the regular day off. Attendance is not created for Saturday.";
            return RedirectToAction(nameof(Daily), new { date = workDate.ToString("yyyy-MM-dd") });
        }

        var laborerIds = model.Entries.Select(e => e.LaborerId).ToList();
        var laborers = await _context.Laborers
            .Where(l => laborerIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l);

        var existing = await _context.AttendanceRecords
            .Where(a => a.WorkDate == workDate && laborerIds.Contains(a.LaborerId))
            .ToListAsync();

        var existingMap = existing.ToDictionary(a => a.LaborerId, a => a);
        var payrollEntryIdsToRecalculate = new HashSet<int>();

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
                record.WageAmount = wage;
                record.Notes = entry.Notes;
                if (record.PayrollEntryId.HasValue)
                {
                    payrollEntryIdsToRecalculate.Add(record.PayrollEntryId.Value);
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
                    WageAmount = wage,
                    Notes = entry.Notes
                });
            }
        }

        if (payrollEntryIdsToRecalculate.Count > 0)
        {
            await RecalculatePayrollEntriesAsync(payrollEntryIdsToRecalculate);
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

        var payrollEntryId = record.PayrollEntryId;
        _context.AttendanceRecords.Remove(record);

        if (payrollEntryId.HasValue)
        {
            await RecalculatePayrollEntriesAsync(new[] { payrollEntryId.Value });
        }

        await _context.SaveChangesAsync();
        TempData["AttendanceSuccess"] = "Attendance entry deleted.";
        return RedirectToAction(nameof(Daily), new { date = workDate.ToString("yyyy-MM-dd") });
    }

    private async Task RecalculatePayrollEntriesAsync(IEnumerable<int> payrollEntryIds)
    {
        var ids = payrollEntryIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var entries = await _context.PayrollEntries
            .Include(e => e.AttendanceRecords)
            .Include(e => e.Adjustments)
            .Include(e => e.AdvanceDeductions)
            .Where(e => ids.Contains(e.Id))
            .ToListAsync();

        foreach (var entry in entries)
        {
            entry.TotalDays = entry.AttendanceRecords.Count(a => a.Status != AttendanceStatus.Absent);
            entry.GrossWage = entry.AttendanceRecords.Where(a => a.Status != AttendanceStatus.Absent).Sum(a => a.WageAmount);
            entry.TotalAdditions = entry.Adjustments.Where(a => a.Type == AdjustmentType.Addition).Sum(a => a.Amount);
            entry.TotalDeductions = entry.Adjustments.Where(a => a.Type == AdjustmentType.Deduction).Sum(a => a.Amount)
                + entry.AdvanceDeductions.Sum(d => d.DeductAmount);
            entry.NetPay = entry.GrossWage + entry.TotalAdditions - entry.TotalDeductions;
            ApplyEntryStatus(entry);
        }

        await _context.SaveChangesAsync();
    }

    private static decimal GetMultiplier(AttendanceStatus status)
    {
        return status == AttendanceStatus.Absent ? 0.0m : 1.0m;
    }

    private static bool IsRegularDayOff(DateTime workDate)
    {
        return workDate.DayOfWeek == DayOfWeek.Saturday;
    }

    private static void ApplyEntryStatus(PayrollEntry entry)
    {
        if (entry.RecordType == PayrollEntryRecordType.RecordOnly)
        {
            entry.Status = PaymentStatus.Unpaid;
            entry.PaidAmount = 0m;
            return;
        }

        var remaining = entry.NetPay - entry.PaidAmount;
        if (remaining <= 0)
        {
            entry.Status = PaymentStatus.Paid;
        }
        else if (entry.PaidAmount > 0)
        {
            entry.Status = PaymentStatus.Partial;
        }
        else
        {
            entry.Status = PaymentStatus.Unpaid;
        }
    }

    private sealed record AttendanceHistorySummary(
        int LaborerId,
        DateTime FirstWorkDate,
        int TotalDutyDays,
        int TotalAbsenceDays,
        int TotalTrackedDays);
}
