using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class PayrollController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public PayrollController(ApplicationDbContext context, IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
    }

    public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate, PaymentStatus? status)
    {
        ViewData["StartDate"] = startDate;
        ViewData["EndDate"] = endDate;
        ViewData["Status"] = status?.ToString() ?? "";

        var query = _context.PayrollPeriods
            .AsNoTracking()
            .Include(p => p.Laborer)
            .Include(p => p.PayrollRun)
            .AsQueryable();

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
        if (status.HasValue)
        {
            query = query.Where(p => p.Status == status.Value);
        }

        var periods = await query.OrderByDescending(p => p.PeriodEnd).ToListAsync();
        return View(periods);
    }

    [HttpGet]
    public async Task<IActionResult> Generate(DateTime? startDate, DateTime? endDate)
    {
        var start = (startDate ?? DateTime.Today.AddDays(-14)).Date;
        var end = (endDate ?? DateTime.Today).Date;
        var endExclusive = end.AddDays(1);

        var attendance = await _context.AttendanceRecords
            .AsNoTracking()
            .Include(a => a.Laborer)
            .Where(a => a.WorkDate >= start && a.WorkDate < endExclusive && a.PayrollPeriodId == null)
            .ToListAsync();

        var rows = attendance
            .GroupBy(a => a.LaborerId)
            .Select(g => new PayrollGenerateRow
            {
                LaborerId = g.Key,
                LaborerName = g.First().Laborer?.FullName ?? "Unknown",
                TotalDays = g.Count(),
                TotalWage = g.Sum(x => x.WageAmount)
            })
            .OrderBy(r => r.LaborerName)
            .ToList();

        var exactCutoff = await _context.PayrollCutoffs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.StartDate == start && c.EndDate == end);

        var hasOverlappingLockedCutoff = await _context.PayrollCutoffs
            .AsNoTracking()
            .AnyAsync(c =>
                c.IsLocked &&
                c.StartDate <= end &&
                c.EndDate >= start &&
                !(c.StartDate == start && c.EndDate == end));

        var existingRun = await _context.PayrollRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.PeriodStart == start && r.PeriodEnd == end);

        var cutoffMessage = hasOverlappingLockedCutoff
            ? "Another locked cutoff overlaps this range. Use the exact locked range."
            : exactCutoff?.IsLocked == true
                ? $"Cutoff is locked ({start:MMM dd, yyyy} - {end:MMM dd, yyyy})."
                : "Cutoff is not locked. Lock this date range before generating payroll.";

        var model = new PayrollGenerateViewModel
        {
            StartDate = start,
            EndDate = end,
            Rows = rows,
            SelectedLaborerIds = rows.Select(r => r.LaborerId).ToList(),
            CutoffId = exactCutoff?.Id,
            IsCutoffLocked = exactCutoff?.IsLocked == true,
            HasOverlappingLockedCutoff = hasOverlappingLockedCutoff,
            CutoffMessage = cutoffMessage,
            CanUnlockCutoff = exactCutoff?.IsLocked == true && existingRun == null,
            ExistingRunId = existingRun?.Id,
            ExistingRunStatus = existingRun?.Status,
            CanApproveRun = existingRun?.Status == PayrollRunStatus.Generated,
            CanCloseRun = existingRun?.Status == PayrollRunStatus.Approved
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(PayrollGenerateViewModel model)
    {
        var start = model.StartDate.Date;
        var end = model.EndDate.Date;
        if (start > end)
        {
            TempData["PayrollError"] = "Invalid cutoff range.";
            return RedirectToAction(nameof(Generate), new { startDate = end.ToString("yyyy-MM-dd"), endDate = start.ToString("yyyy-MM-dd") });
        }

        var cutoffValidationMessage = await ValidateCutoffForGeneration(start, end);
        if (!string.IsNullOrWhiteSpace(cutoffValidationMessage))
        {
            TempData["PayrollError"] = cutoffValidationMessage;
            return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
        }

        var existingRun = await _context.PayrollRuns
            .FirstOrDefaultAsync(r => r.PeriodStart == start && r.PeriodEnd == end);
        if (existingRun != null)
        {
            TempData["PayrollError"] = "Payroll for this locked cutoff has already been generated.";
            return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
        }

        var endExclusive = end.AddDays(1);

        if (model.SelectedLaborerIds == null || model.SelectedLaborerIds.Count == 0)
        {
            return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
        }

        var attendance = await _context.AttendanceRecords
            .Include(a => a.Laborer)
            .Where(a => a.WorkDate >= start && a.WorkDate < endExclusive &&
                        a.PayrollPeriodId == null &&
                        model.SelectedLaborerIds.Contains(a.LaborerId))
            .ToListAsync();

        var groups = attendance.GroupBy(a => a.LaborerId).ToList();
        if (groups.Count == 0)
        {
            return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
        }

        await using var tx = await _context.Database.BeginTransactionAsync();

        var run = new PayrollRun
        {
            PeriodStart = start,
            PeriodEnd = end,
            Status = PayrollRunStatus.Generated,
            GeneratedAt = DateTime.Now,
            GeneratedBy = User.Identity?.Name
        };
        _context.PayrollRuns.Add(run);
        await _context.SaveChangesAsync();

        var periods = new List<PayrollPeriod>();
        foreach (var group in groups)
        {
            var totalWage = group.Sum(x => x.WageAmount);
            periods.Add(new PayrollPeriod
            {
                PayrollRunId = run.Id,
                LaborerId = group.Key,
                PeriodStart = start,
                PeriodEnd = end,
                TotalDays = group.Count(),
                TotalWage = totalWage,
                AdjustmentTotal = 0,
                PaidAmount = 0,
                Status = PaymentStatus.Unpaid,
                GeneratedAt = DateTime.Now
            });
        }

        _context.PayrollPeriods.AddRange(periods);
        await _context.SaveChangesAsync();

        var periodMap = periods.ToDictionary(p => p.LaborerId, p => p.Id);
        foreach (var record in attendance)
        {
            if (periodMap.TryGetValue(record.LaborerId, out var periodId))
            {
                record.PayrollPeriodId = periodId;
            }
        }

        await _context.SaveChangesAsync();
        await tx.CommitAsync();
        TempData["PayrollSuccess"] = "Payroll generated successfully.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> LockCutoff(DateTime startDate, DateTime endDate)
    {
        var start = startDate.Date;
        var end = endDate.Date;
        if (start > end)
        {
            TempData["PayrollError"] = "Invalid cutoff range.";
            return RedirectToAction(nameof(Generate), new { startDate = end.ToString("yyyy-MM-dd"), endDate = start.ToString("yyyy-MM-dd") });
        }

        var hasOverlappingLockedCutoff = await _context.PayrollCutoffs
            .AsNoTracking()
            .AnyAsync(c =>
                c.IsLocked &&
                c.StartDate <= end &&
                c.EndDate >= start &&
                !(c.StartDate == start && c.EndDate == end));

        if (hasOverlappingLockedCutoff)
        {
            TempData["PayrollError"] = "Cannot lock this range because it overlaps an existing locked cutoff.";
            return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
        }

        var cutoff = await _context.PayrollCutoffs
            .FirstOrDefaultAsync(c => c.StartDate == start && c.EndDate == end);

        if (cutoff == null)
        {
            cutoff = new PayrollCutoff
            {
                StartDate = start,
                EndDate = end
            };
            _context.PayrollCutoffs.Add(cutoff);
        }

        cutoff.IsLocked = true;
        cutoff.LockedAt = DateTime.Now;
        cutoff.LockedBy = User.Identity?.Name;

        await _context.SaveChangesAsync();
        TempData["PayrollSuccess"] = $"Cutoff locked: {start:MMM dd, yyyy} - {end:MMM dd, yyyy}.";
        return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnlockCutoff(int cutoffId)
    {
        var cutoff = await _context.PayrollCutoffs.FirstOrDefaultAsync(c => c.Id == cutoffId);
        if (cutoff == null)
        {
            TempData["PayrollError"] = "Cutoff not found.";
            return RedirectToAction(nameof(Generate));
        }

        var hasGeneratedRunForCutoff = await _context.PayrollRuns
            .AsNoTracking()
            .AnyAsync(r => r.PeriodStart == cutoff.StartDate && r.PeriodEnd == cutoff.EndDate);

        if (hasGeneratedRunForCutoff)
        {
            TempData["PayrollError"] = "Cannot unlock a cutoff that already has generated payroll.";
            return RedirectToAction(nameof(Generate), new { startDate = cutoff.StartDate.ToString("yyyy-MM-dd"), endDate = cutoff.EndDate.ToString("yyyy-MM-dd") });
        }

        cutoff.IsLocked = false;
        cutoff.LockedAt = null;
        cutoff.LockedBy = null;

        await _context.SaveChangesAsync();
        TempData["PayrollSuccess"] = "Cutoff unlocked.";
        return RedirectToAction(nameof(Generate), new { startDate = cutoff.StartDate.ToString("yyyy-MM-dd"), endDate = cutoff.EndDate.ToString("yyyy-MM-dd") });
    }

    public async Task<IActionResult> Details(int id)
    {
        var period = await _context.PayrollPeriods
            .Include(p => p.Laborer)
            .Include(p => p.PayrollRun)
            .Include(p => p.Payments)
            .Include(p => p.Adjustments)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (period == null)
        {
            return NotFound();
        }

        var attendance = await _context.AttendanceRecords
            .AsNoTracking()
            .Where(a => a.PayrollPeriodId == id)
            .OrderBy(a => a.WorkDate)
            .ToListAsync();

        var payments = period.Payments.OrderByDescending(p => p.Date).ToList();
        var adjustments = period.Adjustments.OrderByDescending(a => a.Date).ThenByDescending(a => a.CreatedAt).ToList();
        var payableTotal = period.TotalWage + period.AdjustmentTotal;
        var remaining = payableTotal - period.PaidAmount;

        var model = new PayrollDetailsViewModel
        {
            Period = period,
            AttendanceRecords = attendance,
            Payments = payments,
            Adjustments = adjustments,
            RemainingBalance = remaining,
            PayableTotal = payableTotal,
            NewPayment = new PayrollPayment
            {
                PayrollPeriodId = period.Id,
                Date = DateTime.Today,
                PaymentMethod = PaymentMethod.Cash
            }
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPayment(PayrollPayment payment)
    {
        var period = await _context.PayrollPeriods
            .Include(p => p.PayrollRun)
            .Include(p => p.Laborer)
            .FirstOrDefaultAsync(p => p.Id == payment.PayrollPeriodId);
        if (period == null)
        {
            return NotFound();
        }

        if (period.PayrollRun?.Status == PayrollRunStatus.Closed)
        {
            TempData["PayrollError"] = "This payroll run is closed. Payments are locked.";
            return RedirectToAction(nameof(Details), new { id = payment.PayrollPeriodId });
        }

        if (payment.Amount <= 0)
        {
            ModelState.AddModelError(nameof(PayrollPayment.Amount), "Amount must be greater than 0.");
        }

        if (!ModelState.IsValid)
        {
            return RedirectToAction(nameof(Details), new { id = payment.PayrollPeriodId });
        }

        payment.RecordedById = User.Identity?.Name;
        _context.PayrollPayments.Add(payment);

        var laborerName = period.Laborer?.FullName ?? "Laborer";
        _context.Expenses.Add(new Expense
        {
            Date = payment.Date,
            Category = "Payroll",
            Vendor = laborerName,
            Amount = payment.Amount,
            PaymentMethod = payment.PaymentMethod,
            ReferenceNo = payment.ReferenceNo,
            Description = $"Payroll: {laborerName} ({period.PeriodStart:MMM dd, yyyy} - {period.PeriodEnd:MMM dd, yyyy})",
            RecordedById = payment.RecordedById
        });
        period.PaidAmount += payment.Amount;

        var payableTotal = period.TotalWage + period.AdjustmentTotal;
        ApplyPaymentStatus(period, payableTotal);

        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();
        TempData["PayrollSuccess"] = "Payment saved.";
        return RedirectToAction(nameof(Details), new { id = payment.PayrollPeriodId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAdjustment(int payrollPeriodId, decimal amount, string? reason, DateTime? date)
    {
        var period = await _context.PayrollPeriods
            .Include(p => p.PayrollRun)
            .FirstOrDefaultAsync(p => p.Id == payrollPeriodId);
        if (period == null)
        {
            return NotFound();
        }

        if (period.PayrollRun?.Status == PayrollRunStatus.Closed)
        {
            TempData["PayrollError"] = "This payroll run is closed. Adjustments are locked.";
            return RedirectToAction(nameof(Details), new { id = payrollPeriodId });
        }

        if (amount == 0)
        {
            TempData["PayrollError"] = "Adjustment amount cannot be zero.";
            return RedirectToAction(nameof(Details), new { id = payrollPeriodId });
        }

        var adjustment = new PayrollAdjustment
        {
            PayrollPeriodId = payrollPeriodId,
            Date = (date ?? DateTime.Today).Date,
            Amount = amount,
            Reason = string.IsNullOrWhiteSpace(reason) ? "Manual adjustment" : reason.Trim(),
            CreatedBy = User.Identity?.Name,
            CreatedAt = DateTime.Now
        };
        _context.PayrollAdjustments.Add(adjustment);

        period.AdjustmentTotal += amount;
        var payableTotal = period.TotalWage + period.AdjustmentTotal;
        ApplyPaymentStatus(period, payableTotal);

        await _context.SaveChangesAsync();
        TempData["PayrollSuccess"] = "Adjustment saved.";
        return RedirectToAction(nameof(Details), new { id = payrollPeriodId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveRun(int runId)
    {
        var run = await _context.PayrollRuns.FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null)
        {
            TempData["PayrollError"] = "Payroll run not found.";
            return RedirectToAction(nameof(Generate));
        }

        if (run.Status != PayrollRunStatus.Generated)
        {
            TempData["PayrollError"] = "Only generated runs can be approved.";
            return RedirectToAction(nameof(Generate), new { startDate = run.PeriodStart.ToString("yyyy-MM-dd"), endDate = run.PeriodEnd.ToString("yyyy-MM-dd") });
        }

        run.Status = PayrollRunStatus.Approved;
        run.ApprovedAt = DateTime.Now;
        await _context.SaveChangesAsync();
        TempData["PayrollSuccess"] = "Payroll run approved.";
        return RedirectToAction(nameof(Generate), new { startDate = run.PeriodStart.ToString("yyyy-MM-dd"), endDate = run.PeriodEnd.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseRun(int runId)
    {
        var run = await _context.PayrollRuns
            .Include(r => r.PayrollPeriods)
            .FirstOrDefaultAsync(r => r.Id == runId);
        if (run == null)
        {
            TempData["PayrollError"] = "Payroll run not found.";
            return RedirectToAction(nameof(Generate));
        }

        if (run.Status != PayrollRunStatus.Approved)
        {
            TempData["PayrollError"] = "Only approved runs can be closed.";
            return RedirectToAction(nameof(Generate), new { startDate = run.PeriodStart.ToString("yyyy-MM-dd"), endDate = run.PeriodEnd.ToString("yyyy-MM-dd") });
        }

        var hasUnpaid = run.PayrollPeriods.Any(p => p.Status != PaymentStatus.Paid);
        if (hasUnpaid)
        {
            TempData["PayrollError"] = "Cannot close run while there are unpaid laborers.";
            return RedirectToAction(nameof(Generate), new { startDate = run.PeriodStart.ToString("yyyy-MM-dd"), endDate = run.PeriodEnd.ToString("yyyy-MM-dd") });
        }

        run.Status = PayrollRunStatus.Closed;
        run.ClosedAt = DateTime.Now;
        await _context.SaveChangesAsync();
        TempData["PayrollSuccess"] = "Payroll run closed.";
        return RedirectToAction(nameof(Generate), new { startDate = run.PeriodStart.ToString("yyyy-MM-dd"), endDate = run.PeriodEnd.ToString("yyyy-MM-dd") });
    }

    private async Task<string?> ValidateCutoffForGeneration(DateTime start, DateTime end)
    {
        var exactLockedCutoffExists = await _context.PayrollCutoffs
            .AsNoTracking()
            .AnyAsync(c => c.IsLocked && c.StartDate == start && c.EndDate == end);
        if (!exactLockedCutoffExists)
        {
            return "Lock this exact cutoff range before generating payroll.";
        }

        var hasOverlappingLockedCutoff = await _context.PayrollCutoffs
            .AsNoTracking()
            .AnyAsync(c =>
                c.IsLocked &&
                c.StartDate <= end &&
                c.EndDate >= start &&
                !(c.StartDate == start && c.EndDate == end));
        if (hasOverlappingLockedCutoff)
        {
            return "Another locked cutoff overlaps this range. Use the exact locked cutoff only.";
        }

        return null;
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
