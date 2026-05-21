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

        var totalPeriods = await query.CountAsync();
        var filteredUnpaidCount = await query.CountAsync(p => p.Status == PaymentStatus.Unpaid);
        var filteredBalanceTotal = await query.SumAsync(p => (p.TotalWage + p.AdjustmentTotal) - p.PaidAmount);
        var totalPages = totalPeriods == 0 ? 1 : (int)Math.Ceiling(totalPeriods / (double)pageSize);
        if (page > totalPages) page = totalPages;

        var periods = await query
            .OrderByDescending(p => p.PeriodEnd)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var pageTotalBalance = periods.Sum(p => (p.TotalWage + p.AdjustmentTotal) - p.PaidAmount);

        var model = new PayrollIndexViewModel
        {
            StartDate = startDate,
            EndDate = endDate,
            Status = status,
            CurrentPage = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalPeriods = totalPeriods,
            FilteredUnpaidCount = filteredUnpaidCount,
            FilteredBalanceTotal = filteredBalanceTotal,
            PageTotalBalance = pageTotalBalance,
            Periods = periods
        };

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Generate(DateTime? startDate, DateTime? endDate)
    {
        var businessToday = BusinessDate.Today();
        var start = (startDate ?? businessToday.AddDays(-6)).Date;
        var end = (endDate ?? businessToday).Date;
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
                TotalDays = g.Count(x => x.Status != AttendanceStatus.Absent),
                TotalWage = g.Sum(x => x.WageAmount)
            })
            .OrderBy(r => r.LaborerName)
            .ToList();

        var existingRun = await _context.PayrollRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.PeriodStart == start && r.PeriodEnd == end);

        var model = new PayrollGenerateViewModel
        {
            StartDate = start,
            EndDate = end,
            Rows = rows,
            SelectedLaborerIds = rows.Select(r => r.LaborerId).ToList(),
            ExistingRunId = existingRun?.Id,
            ExistingRunStatus = existingRun?.Status
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

        var existingRun = await _context.PayrollRuns
            .FirstOrDefaultAsync(r => r.PeriodStart == start && r.PeriodEnd == end);
        if (existingRun != null)
        {
            TempData["PayrollError"] = "Payroll for this locked cutoff has already been generated.";
            return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
        }

        var endExclusive = end.AddDays(1);

        var selectedLaborerIds = model.SelectedLaborerIds?
            .Distinct()
            .ToList() ?? new List<int>();

        if (selectedLaborerIds.Count == 0)
        {
            TempData["PayrollError"] = "Select at least one laborer before generating payroll.";
            return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
        }

        try
        {
            var strategy = _context.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                var attendance = await _context.AttendanceRecords
                    .Include(a => a.Laborer)
                    .Where(a => a.WorkDate >= start && a.WorkDate < endExclusive &&
                                a.PayrollPeriodId == null &&
                                selectedLaborerIds.Contains(a.LaborerId))
                    .ToListAsync();

                var groups = attendance.GroupBy(a => a.LaborerId).ToList();
                if (groups.Count == 0)
                {
                    throw new InvalidOperationException("No unlocked attendance records were found for the selected laborers in this cutoff.");
                }

                await using var tx = await _context.Database.BeginTransactionAsync();

                var generatedAt = BusinessDate.Now();
                var run = new PayrollRun
                {
                    PeriodStart = start,
                    PeriodEnd = end,
                    Status = PayrollRunStatus.Generated,
                    GeneratedAt = generatedAt,
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
                        TotalDays = group.Count(x => x.Status != AttendanceStatus.Absent),
                        TotalWage = totalWage,
                        AdjustmentTotal = 0,
                        PaidAmount = 0,
                        Status = PaymentStatus.Unpaid,
                        GeneratedAt = generatedAt
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
            });

            TempData["PayrollSuccess"] = "Payroll generated successfully.";
            return RedirectToAction(nameof(Index));
        }
        catch (InvalidOperationException ex)
        {
            TempData["PayrollError"] = ex.Message;
            return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
        }
        catch (DbUpdateException)
        {
            TempData["PayrollError"] = "Payroll generation could not be saved. Refresh the page and try again. If the issue persists, check for duplicate or already-assigned attendance in this cutoff.";
            return RedirectToAction(nameof(Generate), new { startDate = start.ToString("yyyy-MM-dd"), endDate = end.ToString("yyyy-MM-dd") });
        }
    }

    [HttpGet]
    public async Task<IActionResult> ExportPayslipCsv(int id)
    {
        var model = await BuildPayrollDetailsViewModelAsync(id);
        if (model == null)
        {
            return NotFound();
        }

        var csv = BuildPayslipCsv(model);
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv);
        var fileName = $"payslip-{model.Period.Id}-{model.Period.Laborer?.FullName?.Replace(' ', '-') ?? "laborer"}.csv";
        return File(bytes, "text/csv", fileName);
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

    private async Task<PayrollDetailsViewModel?> BuildPayrollDetailsViewModelAsync(int id)
    {
        var period = await _context.PayrollPeriods
            .Include(p => p.Laborer)
            .Include(p => p.PayrollRun)
            .Include(p => p.Payments)
            .Include(p => p.Adjustments)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (period == null)
        {
            return null;
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

        return new PayrollDetailsViewModel
        {
            Period = period,
            AttendanceRecords = attendance,
            Payments = payments,
            Adjustments = adjustments,
            AdjustmentOptions = GetAdjustmentOptions(),
            RemainingBalance = remaining,
            PayableTotal = payableTotal,
            NewPayment = new PayrollPayment
            {
                PayrollPeriodId = period.Id,
                Date = BusinessDate.Today(),
                PaymentMethod = PaymentMethod.Cash
            }
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

        var isAjax = string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        if (period.PayrollRun?.Status == PayrollRunStatus.Closed)
        {
            TempData["PayrollError"] = "This payroll run is closed. Payments are locked.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payment.PayrollPeriodId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payment.PayrollPeriodId });
        }

        if (payment.Amount <= 0)
        {
            ModelState.AddModelError(nameof(PayrollPayment.Amount), "Amount must be greater than 0.");
        }

        var payableTotal = period.TotalWage + period.AdjustmentTotal;
        var remainingBalance = payableTotal - period.PaidAmount;
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
                var model = await BuildPayrollDetailsViewModelAsync(payment.PayrollPeriodId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payment.PayrollPeriodId });
        }

        payment.RecordedById = User.Identity?.Name;
        var laborerName = period.Laborer?.FullName ?? "Laborer";
        _context.PayrollPayments.Add(payment);
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
        ApplyPaymentStatus(period, payableTotal);

        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();
        TempData["PayrollSuccess"] = "Payment saved.";

        if (isAjax)
        {
            var model = await BuildPayrollDetailsViewModelAsync(payment.PayrollPeriodId);
            return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
        }

        return RedirectToAction(nameof(Details), new { id = payment.PayrollPeriodId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddAdjustment(int payrollPeriodId, string? adjustmentType, decimal amount, string? note, DateTime? date)
    {
        var period = await _context.PayrollPeriods
            .Include(p => p.PayrollRun)
            .FirstOrDefaultAsync(p => p.Id == payrollPeriodId);
        if (period == null)
        {
            return NotFound();
        }

        var isAjax = string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        if (period.PayrollRun?.Status == PayrollRunStatus.Closed)
        {
            TempData["PayrollError"] = "This payroll run is closed. Adjustments are locked.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payrollPeriodId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payrollPeriodId });
        }

        if (amount <= 0)
        {
            TempData["PayrollError"] = "Amount must be greater than zero.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payrollPeriodId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payrollPeriodId });
        }

        if (!TryGetAdjustmentOption(adjustmentType, out var option))
        {
            TempData["PayrollError"] = "Select a valid deduction or addition type.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payrollPeriodId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payrollPeriodId });
        }

        var signedAmount = option.IsDeduction ? -amount : amount;
        var payableTotalBeforeAdjustment = period.TotalWage + period.AdjustmentTotal;
        var payableTotalAfterAdjustment = payableTotalBeforeAdjustment + signedAmount;
        if (payableTotalAfterAdjustment < 0)
        {
            TempData["PayrollError"] = "Adjustment would make the payable total negative.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payrollPeriodId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payrollPeriodId });
        }

        var adjustment = new PayrollAdjustment
        {
            PayrollPeriodId = payrollPeriodId,
            Date = (date ?? BusinessDate.Today()).Date,
            Amount = signedAmount,
            Reason = BuildAdjustmentReason(option.Label, note),
            CreatedBy = User.Identity?.Name,
            CreatedAt = BusinessDate.Now()
        };
        _context.PayrollAdjustments.Add(adjustment);

        period.AdjustmentTotal += signedAmount;
        var payableTotal = period.TotalWage + period.AdjustmentTotal;
        ApplyPaymentStatus(period, payableTotal);

        await _context.SaveChangesAsync();
        TempData["PayrollSuccess"] = $"{option.Label} saved.";

        if (isAjax)
        {
            var model = await BuildPayrollDetailsViewModelAsync(payrollPeriodId);
            return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
        }

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
        run.ApprovedAt = BusinessDate.Now();
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
        run.ClosedAt = BusinessDate.Now();
        await _context.SaveChangesAsync();
        TempData["PayrollSuccess"] = "Payroll run closed.";
        return RedirectToAction(nameof(Generate), new { startDate = run.PeriodStart.ToString("yyyy-MM-dd"), endDate = run.PeriodEnd.ToString("yyyy-MM-dd") });
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

    private static string BuildPayslipCsv(PayrollDetailsViewModel model)
    {
        static string Escape(string? value)
        {
            value ??= string.Empty;
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        var sb = new StringBuilder();
        sb.AppendLine("PAYSLIP SUMMARY");
        sb.AppendLine("Field,Value");
        sb.AppendLine($"Laborer,{Escape(model.Period.Laborer?.FullName)}");
        sb.AppendLine($"Period,{Escape($"{model.Period.PeriodStart:MMM dd, yyyy} - {model.Period.PeriodEnd:MMM dd, yyyy}")}");
        sb.AppendLine($"Duty Days,{model.Period.TotalDays}");
        sb.AppendLine($"Gross Salary,{model.GrossSalary:N2}");
        sb.AppendLine($"Total Deductions,{model.TotalDeductions:N2}");
        sb.AppendLine($"Total Additions,{model.TotalAdditions:N2}");
        sb.AppendLine($"Net Salary,{model.NetSalary:N2}");
        sb.AppendLine($"Paid,{model.Period.PaidAmount:N2}");
        sb.AppendLine($"Balance,{model.RemainingBalance:N2}");
        sb.AppendLine();
        sb.AppendLine("ATTENDANCE");
        sb.AppendLine("Date,Status,Rate,Multiplier,Wage");

        foreach (var record in model.AttendanceRecords)
        {
            sb.AppendLine($"{record.WorkDate:yyyy-MM-dd},{record.Status},{record.RateSnapshot:N2},{record.Multiplier:0.00},{record.WageAmount:N2}");
        }

        sb.AppendLine();
        sb.AppendLine("ADJUSTMENTS");
        sb.AppendLine("Date,Amount,Reason");
        foreach (var adjustment in model.Adjustments)
        {
            sb.AppendLine($"{adjustment.Date:yyyy-MM-dd},{adjustment.Amount:N2},{Escape(adjustment.Reason)}");
        }

        sb.AppendLine();
        sb.AppendLine("PAYMENTS");
        sb.AppendLine("Date,Amount,Method,Reference");
        foreach (var payment in model.Payments)
        {
            sb.AppendLine($"{payment.Date:yyyy-MM-dd},{payment.Amount:N2},{payment.PaymentMethod},{Escape(payment.ReferenceNo)}");
        }

        return sb.ToString();
    }

    private sealed record PayrollAdjustmentOptionDefinition(string Key, string Label, bool IsDeduction);
}
