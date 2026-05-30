using HazelInvoice.Data;
using HazelInvoice.Helpers;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace HazelInvoice.Controllers;

[Authorize]
public class PayrollController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    private static readonly IReadOnlyDictionary<string, PayrollAdjustmentOptionDefinition> AdjustmentOptionMap =
        new Dictionary<string, PayrollAdjustmentOptionDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["advance-cash"] = new("advance-cash", "Advance Cash", true),
            ["loan"] = new("loan", "Loan", true),
            ["sss"] = new("sss", "SSS Deduction", true),
            ["cash-advance"] = new("cash-advance", "Cash Advance", true),
            ["other-deduction"] = new("other-deduction", "Other Deduction", true),
            ["allowance"] = new("allowance", "Allowance", false),
            ["bonus"] = new("bonus", "Bonus", false),
            ["other-addition"] = new("other-addition", "Other Addition", false)
        };

    public PayrollController(ApplicationDbContext context, IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
    }

    public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate, PaymentStatus? status, int page = 1, int pageSize = 25)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 10, 100);

        var query = _context.PayrollEntries
            .AsNoTracking()
            .Include(e => e.Laborer)
            .Include(e => e.PayrollRun)
            .AsQueryable();

        if (startDate.HasValue)
        {
            var start = startDate.Value.Date;
            query = query.Where(e => e.PeriodEnd >= start);
        }

        if (endDate.HasValue)
        {
            var end = endDate.Value.Date;
            query = query.Where(e => e.PeriodStart <= end);
        }

        if (status.HasValue)
        {
            query = query.Where(e => e.Status == status.Value);
        }

        var totalEntries = await query.CountAsync();
        var unpaidCount = await query.CountAsync(e => e.Status == PaymentStatus.Unpaid);
        var totalBalance = await query.SumAsync(e => e.NetPay - e.PaidAmount);
        var totalPages = totalEntries == 0 ? 1 : (int)Math.Ceiling(totalEntries / (double)pageSize);
        if (page > totalPages) page = totalPages;

        var entries = await query
            .OrderByDescending(e => e.PeriodEnd)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var pageTotalBalance = entries.Sum(e => e.NetPay - e.PaidAmount);

        var model = new PayrollIndexViewModel
        {
            StartDate = startDate,
            EndDate = endDate,
            Status = status,
            CurrentPage = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalEntries = totalEntries,
            UnpaidCount = unpaidCount,
            TotalBalance = totalBalance,
            PageTotalBalance = pageTotalBalance,
            Entries = entries
        };

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Generate(DateTime? weekStart)
    {
        var start = GetWeekStart(weekStart ?? BusinessDate.Today());
        var end = start.AddDays(6);

        var attendance = await _context.AttendanceRecords
            .AsNoTracking()
            .Include(a => a.Laborer)
            .Where(a => a.WorkDate >= start && a.WorkDate <= end && a.PayrollEntryId == null)
            .ToListAsync();

        var rows = attendance
            .GroupBy(a => a.LaborerId)
            .Select(g => new PayrollRunPreviewRow
            {
                LaborerId = g.Key,
                LaborerName = g.First().Laborer?.FullName ?? "Unknown",
                TotalDays = g.Count(x => x.Status != AttendanceStatus.Absent),
                GrossWage = g.Where(x => x.Status != AttendanceStatus.Absent).Sum(x => x.WageAmount),
                PendingAdvanceDeductions = _context.CashAdvances
                    .Where(c => c.LaborerId == g.Key && c.RemainingBalance > 0)
                    .Sum(c => c.RemainingBalance),
                NetPay = g.Where(x => x.Status != AttendanceStatus.Absent).Sum(x => x.WageAmount)
            })
            .OrderBy(r => r.LaborerName)
            .ToList();

        var existingRun = await _context.PayrollRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.WeekStart == start && r.WeekEnd == end);

        var model = new CreatePayrollRunViewModel
        {
            WeekStart = start,
            WeekEnd = end,
            Preview = rows,
            HasExistingRun = existingRun != null
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRun(DateTime weekStart)
    {
        var start = GetWeekStart(weekStart);
        var end = start.AddDays(6);

        var existingRun = await _context.PayrollRuns
            .FirstOrDefaultAsync(r => r.WeekStart == start && r.WeekEnd == end);
        if (existingRun != null)
        {
            TempData["PayrollError"] = "Payroll for this week has already been generated.";
            return RedirectToAction(nameof(Generate), new { weekStart = start.ToString("yyyy-MM-dd") });
        }

        var attendance = await _context.AttendanceRecords
            .Include(a => a.Laborer)
            .Where(a => a.WorkDate >= start && a.WorkDate <= end && a.PayrollEntryId == null)
            .ToListAsync();

        if (attendance.Count == 0)
        {
            TempData["PayrollError"] = "No unassigned attendance records were found for the selected week.";
            return RedirectToAction(nameof(Generate), new { weekStart = start.ToString("yyyy-MM-dd") });
        }

        var grouped = attendance.GroupBy(a => a.LaborerId).ToList();
        if (grouped.Count == 0)
        {
            TempData["PayrollError"] = "No laborers were eligible for payroll generation.";
            return RedirectToAction(nameof(Generate), new { weekStart = start.ToString("yyyy-MM-dd") });
        }

        await using var tx = await _context.Database.BeginTransactionAsync();

        var run = new PayrollRun
        {
            WeekStart = start,
            WeekEnd = end,
            Status = PayrollRunStatus.Draft,
            CreatedAt = BusinessDate.Now(),
            CreatedBy = User.Identity?.Name
        };
        _context.PayrollRuns.Add(run);
        await _context.SaveChangesAsync();

        var entries = new List<PayrollEntry>();

        foreach (var group in grouped)
        {
            var grossWage = group.Where(x => x.Status != AttendanceStatus.Absent).Sum(x => x.WageAmount);
            var entry = new PayrollEntry
            {
                PayrollRunId = run.Id,
                LaborerId = group.Key,
                PeriodStart = start,
                PeriodEnd = end,
                TotalDays = group.Count(x => x.Status != AttendanceStatus.Absent),
                GrossWage = grossWage,
                TotalAdditions = 0m,
                TotalDeductions = 0m,
                NetPay = grossWage,
                PaidAmount = 0m,
                Status = PaymentStatus.Unpaid,
                GeneratedAt = BusinessDate.Now()
            };
            entries.Add(entry);
        }

        _context.PayrollEntries.AddRange(entries);
        await _context.SaveChangesAsync();

        var advanceDeductions = new List<AdvanceDeduction>();
        foreach (var entry in entries)
        {
            var remainingGross = entry.GrossWage;
            var advances = await _context.CashAdvances
                .Where(a => a.LaborerId == entry.LaborerId && a.RemainingBalance > 0)
                .OrderBy(a => a.Date)
                .ToListAsync();

            foreach (var advance in advances)
            {
                if (remainingGross <= 0m)
                    break;

                var deductAmount = Math.Min(advance.RemainingBalance, remainingGross);
                if (deductAmount <= 0m)
                    continue;

                advanceDeductions.Add(new AdvanceDeduction
                {
                    PayrollEntryId = entry.Id,
                    CashAdvanceId = advance.Id,
                    DeductAmount = deductAmount
                });

                advance.RemainingBalance -= deductAmount;
                remainingGross -= deductAmount;
            }

            entry.TotalDeductions = advanceDeductions.Where(d => d.PayrollEntryId == entry.Id).Sum(d => d.DeductAmount);
            entry.NetPay = entry.GrossWage - entry.TotalDeductions;
        }

        _context.AdvanceDeductions.AddRange(advanceDeductions);

        var entryMap = entries.ToDictionary(e => e.LaborerId, e => e.Id);
        foreach (var record in attendance)
        {
            if (entryMap.TryGetValue(record.LaborerId, out var entryId))
            {
                record.PayrollEntryId = entryId;
            }
        }

        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        TempData["PayrollSuccess"] = "Payroll run created successfully.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> DetailsModal(int id)
    {
        var model = await BuildPayrollDetailsViewModelAsync(id);
        if (model == null)
        {
            return NotFound();
        }

        return PartialView("_PayrollDetailsPartial", model);
    }

    private async Task<PayrollEntryDetailsViewModel?> BuildPayrollDetailsViewModelAsync(int id)
    {
        var entry = await _context.PayrollEntries
            .Include(e => e.Laborer)
            .Include(e => e.PayrollRun)
            .Include(e => e.Payments)
            .Include(e => e.Adjustments)
            .Include(e => e.AdvanceDeductions)
                .ThenInclude(d => d.CashAdvance)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (entry == null)
        {
            return null;
        }

        var attendance = await _context.AttendanceRecords
            .AsNoTracking()
            .Where(a => a.PayrollEntryId == id)
            .OrderBy(a => a.WorkDate)
            .ToListAsync();

        var remainingBalance = entry.NetPay - entry.PaidAmount;

        return new PayrollEntryDetailsViewModel
        {
            Entry = entry,
            AttendanceRecords = attendance,
            Payments = entry.Payments.OrderByDescending(p => p.Date).ToList(),
            Adjustments = entry.Adjustments.OrderByDescending(a => a.Date).ThenByDescending(a => a.CreatedAt).ToList(),
            AdvanceDeductions = entry.AdvanceDeductions.OrderByDescending(d => d.CashAdvance.Date).ToList(),
            RemainingBalance = remainingBalance,
            NewPayment = new PayrollPayment
            {
                PayrollEntryId = entry.Id,
                Date = BusinessDate.Today(),
                PaymentMethod = PaymentMethod.Cash
            },
            AdjustmentOptions = GetAdjustmentOptions()
        };
    }

    public async Task<IActionResult> Details(int id)
    {
        var model = await BuildPayrollDetailsViewModelAsync(id);
        if (model == null)
        {
            return NotFound();
        }

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Payslip(int entryId)
    {
        var model = await BuildPayslipViewModelAsync(entryId);
        if (model == null)
        {
            return NotFound();
        }

        return PartialView("_Payslip", model);
    }

    [HttpGet]
    public async Task<IActionResult> ExportRun(int runId)
    {
        var run = await _context.PayrollRuns
            .Include(r => r.Entries)
                .ThenInclude(e => e.Laborer)
            .FirstOrDefaultAsync(r => r.Id == runId);

        if (run == null)
        {
            return NotFound();
        }

        var sb = new StringBuilder();
        sb.AppendLine("Laborer,PeriodStart,PeriodEnd,GrossWage,TotalDeductions,TotalAdditions,NetPay,PaidAmount,Status");

        foreach (var entry in run.Entries.OrderBy(e => e.Laborer.FullName))
        {
            sb.AppendLine($"{Escape(entry.Laborer?.FullName)},{entry.PeriodStart:yyyy-MM-dd},{entry.PeriodEnd:yyyy-MM-dd},{entry.GrossWage:N2},{entry.TotalDeductions:N2},{entry.TotalAdditions:N2},{entry.NetPay:N2},{entry.PaidAmount:N2},{entry.Status}");
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"payroll-run-{run.Id}.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPayment(PayrollPayment payment)
    {
        var entry = await _context.PayrollEntries
            .Include(e => e.PayrollRun)
            .Include(e => e.Laborer)
            .FirstOrDefaultAsync(e => e.Id == payment.PayrollEntryId);

        if (entry == null)
        {
            return NotFound();
        }

        var isAjax = string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
        if (entry.PayrollRun?.Status == PayrollRunStatus.Closed)
        {
            TempData["PayrollError"] = "This payroll run is closed. Payments are locked.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payment.PayrollEntryId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payment.PayrollEntryId });
        }

        if (payment.Amount <= 0)
        {
            ModelState.AddModelError(nameof(PayrollPayment.Amount), "Amount must be greater than 0.");
        }

        var remainingBalance = entry.NetPay - entry.PaidAmount;
        if (payment.Amount > remainingBalance)
        {
            ModelState.AddModelError(nameof(PayrollPayment.Amount), $"Amount cannot exceed the remaining balance of {remainingBalance:N2}.");
        }

        if (!ModelState.IsValid)
        {
            TempData["PayrollError"] = ModelState.Values
                .SelectMany(v => v.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault() ?? "Payment could not be saved.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payment.PayrollEntryId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payment.PayrollEntryId });
        }

        payment.RecordedById = User.Identity?.Name;
        _context.PayrollPayments.Add(payment);
        _context.Expenses.Add(new Expense
        {
            Date = payment.Date,
            Category = "Payroll",
            Vendor = entry.Laborer?.FullName,
            Amount = payment.Amount,
            PaymentMethod = payment.PaymentMethod,
            ReferenceNo = payment.ReferenceNo,
            Description = $"Payroll: {entry.Laborer?.FullName} ({entry.PeriodStart:MMM dd, yyyy} - {entry.PeriodEnd:MMM dd, yyyy})",
            RecordedById = payment.RecordedById
        });

        entry.PaidAmount += payment.Amount;
        ApplyEntryStatus(entry);

        await _context.SaveChangesAsync();
        await TryCloseRun(entry.PayrollRunId);
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();
        TempData["PayrollSuccess"] = "Payment saved.";

        if (isAjax)
        {
            var model = await BuildPayrollDetailsViewModelAsync(payment.PayrollEntryId);
            return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
        }

        return RedirectToAction(nameof(Details), new { id = payment.PayrollEntryId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAdjustment(int payrollEntryId, string? adjustmentType, decimal amount, string? note, DateTime? date)
    {
        var entry = await _context.PayrollEntries
            .Include(e => e.PayrollRun)
            .FirstOrDefaultAsync(e => e.Id == payrollEntryId);
        if (entry == null)
        {
            return NotFound();
        }

        var isAjax = string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
        if (entry.PayrollRun?.Status == PayrollRunStatus.Closed)
        {
            TempData["PayrollError"] = "This payroll run is closed. Adjustments are locked.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payrollEntryId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payrollEntryId });
        }

        if (amount <= 0)
        {
            TempData["PayrollError"] = "Amount must be greater than zero.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payrollEntryId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payrollEntryId });
        }

        if (!TryGetAdjustmentOption(adjustmentType, out var option))
        {
            TempData["PayrollError"] = "Select a valid deduction or addition type.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payrollEntryId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payrollEntryId });
        }

        var adjustment = new Adjustment
        {
            PayrollEntryId = payrollEntryId,
            Type = option.IsDeduction ? AdjustmentType.Deduction : AdjustmentType.Addition,
            Amount = amount,
            Date = (date ?? BusinessDate.Today()).Date,
            Reason = BuildAdjustmentReason(option.Label, note),
            CreatedBy = User.Identity?.Name,
            CreatedAt = BusinessDate.Now()
        };

        _context.Adjustments.Add(adjustment);
        await _context.SaveChangesAsync();
        await RecalculateEntry(payrollEntryId);
        TempData["PayrollSuccess"] = $"{option.Label} saved.";

        if (isAjax)
        {
            var model = await BuildPayrollDetailsViewModelAsync(payrollEntryId);
            return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
        }

        return RedirectToAction(nameof(Details), new { id = payrollEntryId });
    }

    private async Task RecalculateEntry(int entryId)
    {
        var entry = await _context.PayrollEntries
            .Include(e => e.AttendanceRecords)
            .Include(e => e.Adjustments)
            .Include(e => e.AdvanceDeductions)
            .FirstOrDefaultAsync(e => e.Id == entryId);

        if (entry == null)
        {
            return;
        }

        entry.TotalDays = entry.AttendanceRecords.Count(a => a.Status == AttendanceStatus.Present);
        entry.GrossWage = entry.AttendanceRecords.Where(a => a.Status == AttendanceStatus.Present).Sum(a => a.WageAmount);
        entry.TotalAdditions = entry.Adjustments.Where(a => a.Type == AdjustmentType.Addition).Sum(a => a.Amount);
        entry.TotalDeductions = entry.Adjustments.Where(a => a.Type == AdjustmentType.Deduction).Sum(a => a.Amount)
            + entry.AdvanceDeductions.Sum(d => d.DeductAmount);
        entry.NetPay = entry.GrossWage + entry.TotalAdditions - entry.TotalDeductions;

        ApplyEntryStatus(entry);
        await _context.SaveChangesAsync();
        await TryCloseRun(entry.PayrollRunId);
    }

    private async Task<PayslipViewModel?> BuildPayslipViewModelAsync(int entryId)
    {
        var entry = await _context.PayrollEntries
            .Include(e => e.Laborer)
            .Include(e => e.AttendanceRecords)
            .Include(e => e.Adjustments)
            .Include(e => e.AdvanceDeductions)
                .ThenInclude(d => d.CashAdvance)
            .Include(e => e.Payments)
            .FirstOrDefaultAsync(e => e.Id == entryId);

        if (entry == null)
        {
            return null;
        }

        return new PayslipViewModel
        {
            LaborerName = entry.Laborer?.FullName ?? string.Empty,
            LaborerRole = entry.Laborer?.Role,
            PeriodStart = entry.PeriodStart,
            PeriodEnd = entry.PeriodEnd,
            TotalDays = entry.TotalDays,
            DailyRate = entry.Laborer?.DailyRate ?? 0m,
            GrossWage = entry.GrossWage,
            Deductions = entry.Adjustments.Where(a => a.Type == AdjustmentType.Deduction)
                .Select(a => (a.Reason ?? "Deduction", a.Amount)).Concat(
                    entry.AdvanceDeductions.Select(d => ($"Advance: {d.CashAdvance.Date:MMM dd}", d.DeductAmount)))
                .ToList(),
            Additions = entry.Adjustments.Where(a => a.Type == AdjustmentType.Addition)
                .Select(a => (a.Reason ?? "Addition", a.Amount))
                .ToList(),
            TotalDeductions = entry.TotalDeductions,
            TotalAdditions = entry.TotalAdditions,
            NetPay = entry.NetPay,
            PaidAmount = entry.PaidAmount,
            Balance = entry.NetPay - entry.PaidAmount,
            Status = entry.Status
        };
    }

    private static void ApplyEntryStatus(PayrollEntry entry)
    {
        var remaining = entry.NetPay - entry.PaidAmount;
        entry.Status = remaining <= 0
            ? PaymentStatus.Paid
            : entry.PaidAmount > 0
                ? PaymentStatus.Partial
                : PaymentStatus.Unpaid;
    }

    private async Task TryCloseRun(int runId)
    {
        var run = await _context.PayrollRuns
            .Include(r => r.Entries)
            .FirstOrDefaultAsync(r => r.Id == runId);

        if (run == null || run.Status == PayrollRunStatus.Closed)
        {
            return;
        }

        if (run.Entries.All(e => e.Status == PaymentStatus.Paid))
        {
            run.Status = PayrollRunStatus.Closed;
            run.ClosedAt = BusinessDate.Now();
            await _context.SaveChangesAsync();
        }
    }

    private static List<PayrollAdjustmentOption> GetAdjustmentOptions()
    {
        return AdjustmentOptionMap.Values
            .Select(option => new PayrollAdjustmentOption
            {
                Key = option.Key,
                Label = option.Label,
                IsDeduction = option.IsDeduction
            })
            .ToList();
    }

    private static bool TryGetAdjustmentOption(string? key, out PayrollAdjustmentOptionDefinition option)
    {
        if (!string.IsNullOrWhiteSpace(key) && AdjustmentOptionMap.TryGetValue(key, out option!))
        {
            return true;
        }

        option = null!;
        return false;
    }

    private static string BuildAdjustmentReason(string label, string? note)
    {
        return string.IsNullOrWhiteSpace(note)
            ? label
            : $"{label}: {note.Trim()}";
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;
        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static DateTime GetWeekStart(DateTime date)
    {
        var delta = date.DayOfWeek == DayOfWeek.Sunday ? -6 : DayOfWeek.Monday - date.DayOfWeek;
        return date.AddDays(delta).Date;
    }

    private sealed record PayrollAdjustmentOptionDefinition(string Key, string Label, bool IsDeduction);
}
