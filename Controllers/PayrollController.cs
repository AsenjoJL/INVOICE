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

    public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate, PaymentStatus? status, PayrollEntryRecordType? recordType, int page = 1, int pageSize = 25)
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

        if (recordType.HasValue)
        {
            query = query.Where(e => e.RecordType == recordType.Value);
        }

        var totalEntries = await query.CountAsync();
        var unpaidCount = await query.CountAsync(e => e.RecordType == PayrollEntryRecordType.Payable && e.Status == PaymentStatus.Unpaid);
        var totalBalance = await query
            .Where(e => e.RecordType == PayrollEntryRecordType.Payable)
            .SumAsync(e => e.NetPay - e.PaidAmount);
        var totalPages = totalEntries == 0 ? 1 : (int)Math.Ceiling(totalEntries / (double)pageSize);
        if (page > totalPages) page = totalPages;

        var entries = await query
            .OrderByDescending(e => e.PeriodEnd)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var pageTotalBalance = entries.Sum(e => e.NetPay - e.PaidAmount);
        var pendingStart = GetDefaultUnpaidPayrollStart();
        var pendingEnd = BusinessDate.Today();
        var pendingRows = await BuildPendingPayrollRowsAsync(pendingStart, pendingEnd);

        var model = new PayrollIndexViewModel
        {
            StartDate = startDate,
            EndDate = endDate,
            Status = status,
            RecordType = recordType,
            CurrentPage = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            TotalEntries = totalEntries,
            UnpaidCount = unpaidCount,
            TotalBalance = totalBalance,
            PageTotalBalance = pageTotalBalance,
            Entries = entries,
            PendingStartDate = pendingStart,
            PendingEndDate = pendingEnd,
            PendingRows = pendingRows
        };

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Generate(DateTime? weekStart)
    {
        var start = GetWeekStart(weekStart ?? BusinessDate.Today());
        var end = GetWorkPeriodEnd(start);

        var attendance = await _context.AttendanceRecords
            .AsNoTracking()
            .Include(a => a.Laborer)
            .Where(a => a.WorkDate >= start && a.WorkDate <= end && a.PayrollEntryId == null)
            .ToListAsync();

        var laborerIds = attendance.Select(a => a.LaborerId).Distinct().ToList();
        var advanceBalanceMap = await _context.CashAdvances
            .AsNoTracking()
            .Where(c => laborerIds.Contains(c.LaborerId) && c.RemainingBalance > 0)
            .GroupBy(c => c.LaborerId)
            .Select(g => new { LaborerId = g.Key, Balance = g.Sum(c => c.RemainingBalance) })
            .ToDictionaryAsync(x => x.LaborerId, x => x.Balance);

        var rows = attendance
            .GroupBy(a => a.LaborerId)
            .Select(g =>
            {
                var grossWage = g.Where(x => x.Status != AttendanceStatus.Absent).Sum(x => x.WageAmount);
                var pendingAdvances = advanceBalanceMap.GetValueOrDefault(g.Key);
                var estimatedDeductions = Math.Min(grossWage, pendingAdvances);

                return new PayrollRunPreviewRow
                {
                    LaborerId = g.Key,
                    LaborerName = g.First().Laborer?.FullName ?? "Unknown",
                    TotalDays = g.Count(x => x.Status != AttendanceStatus.Absent),
                    GrossWage = grossWage,
                    PendingAdvanceDeductions = estimatedDeductions,
                    NetPay = grossWage - estimatedDeductions
                };
            })
            .OrderBy(r => r.LaborerName)
            .ToList();

        var existingRun = await _context.PayrollRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.WeekStart == start && r.WeekEnd == end);

        var currentYear = BusinessDate.Today().Year;
        var model = new CreatePayrollRunViewModel
        {
            WeekStart = start,
            WeekEnd = end,
            Preview = rows,
            HasExistingRun = existingRun != null,
            HistoricalStartDate = new DateTime(currentYear, 1, 1),
            HistoricalEndDate = BusinessDate.Today(),
            RecordOnlyThrough = new DateTime(currentYear, 2, DateTime.DaysInMonth(currentYear, 2)),
            PaidThrough = new DateTime(currentYear, 5, 16),
            UnpaidFrom = new DateTime(currentYear, 5, 18)
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRun(DateTime weekStart)
    {
        var start = GetWeekStart(weekStart);
        var end = GetWorkPeriodEnd(start);

        var existingRun = await _context.PayrollRuns
            .FirstOrDefaultAsync(r => r.WeekStart == start && r.WeekEnd == end);
        if (existingRun != null)
        {
            TempData["PayrollError"] = "Payroll for this week has already been generated.";
            return RedirectToAction(nameof(Generate), new { weekStart = start.ToString("yyyy-MM-dd") });
        }

        var result = await CreatePayrollRunForPeriodAsync(start, end, PayrollGenerationPolicy.Unpaid);
        TempData[result.CreatedEntries > 0 ? "PayrollSuccess" : "PayrollError"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateHistoricalRuns(
        DateTime startDate,
        DateTime endDate,
        DateTime recordOnlyThrough,
        DateTime paidThrough,
        DateTime unpaidFrom)
    {
        startDate = startDate.Date;
        endDate = endDate.Date;
        recordOnlyThrough = recordOnlyThrough.Date;
        paidThrough = paidThrough.Date;
        unpaidFrom = unpaidFrom.Date;

        if (startDate > endDate)
        {
            TempData["PayrollError"] = "Historical start date cannot be after the end date.";
            return RedirectToAction(nameof(Generate));
        }

        if (recordOnlyThrough >= paidThrough || paidThrough >= unpaidFrom)
        {
            TempData["PayrollError"] = "Historical status dates are invalid. Record-only must end before paid-through, and paid-through must be before unpaid starts.";
            return RedirectToAction(nameof(Generate));
        }

        var createdRuns = 0;
        var createdEntries = 0;
        var skippedPeriods = 0;

        var periodStart = startDate;
        while (periodStart <= endDate)
        {
            var periodEnd = GetHistoricalPeriodEnd(periodStart, endDate, recordOnlyThrough, paidThrough, unpaidFrom);
            var policy = ResolveHistoricalPolicy(periodStart, periodEnd, recordOnlyThrough, paidThrough, unpaidFrom);

            var result = await CreatePayrollRunForPeriodAsync(
                periodStart,
                periodEnd,
                policy,
                skipExistingPeriod: true,
                createEmptyLaborerEntries: true);
            if (result.CreatedEntries > 0)
            {
                createdRuns++;
                createdEntries += result.CreatedEntries;
            }
            else
            {
                skippedPeriods++;
            }

            periodStart = periodEnd.AddDays(1);
        }

        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();
        TempData["PayrollSuccess"] = $"Historical payroll finished: {createdRuns} run(s), {createdEntries} entry/entries created, {skippedPeriods} period(s) skipped.";
        return RedirectToAction(nameof(Index), new { startDate = startDate.ToString("yyyy-MM-dd"), endDate = endDate.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GeneratePendingPayroll(DateTime startDate, DateTime endDate)
    {
        startDate = startDate.Date;
        endDate = endDate.Date;

        if (startDate > endDate)
        {
            TempData["PayrollError"] = "Pending payroll start date cannot be after the end date.";
            return RedirectToAction(nameof(Index));
        }

        var pendingRows = await BuildPendingPayrollRowsAsync(startDate, endDate);
        if (pendingRows.Count == 0)
        {
            TempData["PayrollError"] = "No pending duty days were found to generate payroll.";
            return RedirectToAction(nameof(Index));
        }

        await using var tx = await _context.Database.BeginTransactionAsync();

        var run = new PayrollRun
        {
            WeekStart = pendingRows.Min(r => r.PeriodStart),
            WeekEnd = pendingRows.Max(r => r.PeriodEnd),
            Status = PayrollRunStatus.Draft,
            CreatedAt = BusinessDate.Now(),
            CreatedBy = User.Identity?.Name,
            Notes = "Pending payroll generated from unpaid duty days."
        };

        _context.PayrollRuns.Add(run);
        await _context.SaveChangesAsync();

        var laborerIds = pendingRows.Select(r => r.LaborerId).ToList();
        var laborers = await _context.Laborers
            .Where(l => laborerIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id);

        var entries = new List<PayrollEntry>();
        var recordsToLink = new List<(AttendanceRecord Record, int LaborerId)>();

        foreach (var row in pendingRows)
        {
            if (!laborers.TryGetValue(row.LaborerId, out var laborer))
            {
                continue;
            }

            var attendanceRecords = await EnsureAttendanceRecordsForPendingPayrollAsync(laborer, row.PeriodStart, row.PeriodEnd);
            var payableAttendance = attendanceRecords
                .Where(a => !IsRegularDayOff(a.WorkDate) && a.Status != AttendanceStatus.Absent)
                .ToList();

            var grossWage = payableAttendance.Sum(a => a.WageAmount);
            var entry = new PayrollEntry
            {
                PayrollRunId = run.Id,
                LaborerId = laborer.Id,
                Laborer = laborer,
                PeriodStart = row.PeriodStart,
                PeriodEnd = row.PeriodEnd,
                TotalDays = payableAttendance.Count,
                GrossWage = grossWage,
                TotalAdditions = 0m,
                TotalDeductions = 0m,
                NetPay = grossWage,
                PaidAmount = 0m,
                RecordType = PayrollEntryRecordType.Payable,
                Status = PaymentStatus.Unpaid,
                GeneratedAt = BusinessDate.Now(),
                Notes = "Generated from pending duty days."
            };

            entries.Add(entry);
            recordsToLink.AddRange(attendanceRecords.Select(record => (record, laborer.Id)));
        }

        if (entries.Count == 0)
        {
            await tx.RollbackAsync();
            TempData["PayrollError"] = "No pending payroll entries could be generated.";
            return RedirectToAction(nameof(Index));
        }

        _context.PayrollEntries.AddRange(entries);
        await _context.SaveChangesAsync();

        await ApplyAutomaticAdvanceDeductionsAsync(entries);

        var entryMap = entries.ToDictionary(e => e.LaborerId, e => e.Id);
        foreach (var item in recordsToLink)
        {
            if (entryMap.TryGetValue(item.LaborerId, out var entryId))
            {
                item.Record.PayrollEntryId = entryId;
            }
        }

        foreach (var entry in entries)
        {
            ApplyEntryStatus(entry);
        }

        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        TempData["PayrollSuccess"] = $"Pending payroll generated for {entries.Count} laborer(s). You can now mark each row as paid.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<PayrollGenerationResult> CreatePayrollRunForPeriodAsync(
        DateTime periodStart,
        DateTime periodEnd,
        PayrollGenerationPolicy policy,
        bool skipExistingPeriod = false,
        bool createEmptyLaborerEntries = false)
    {
        periodStart = periodStart.Date;
        periodEnd = periodEnd.Date;

        var existingRun = skipExistingPeriod
            ? await _context.PayrollRuns.FirstOrDefaultAsync(r => r.WeekStart <= periodEnd && r.WeekEnd >= periodStart)
            : await _context.PayrollRuns.FirstOrDefaultAsync(r => r.WeekStart == periodStart && r.WeekEnd == periodEnd);

        if (existingRun != null)
        {
            return skipExistingPeriod
                ? PayrollGenerationResult.Skipped("Payroll for this period already exists.")
                : PayrollGenerationResult.Failed("Payroll for this period has already been generated.");
        }

        var attendance = await _context.AttendanceRecords
            .Include(a => a.Laborer)
            .Where(a => a.WorkDate >= periodStart && a.WorkDate <= periodEnd && a.PayrollEntryId == null)
            .ToListAsync();

        if (attendance.Count == 0 && !createEmptyLaborerEntries)
        {
            return PayrollGenerationResult.Skipped("No unassigned attendance records were found for this period.");
        }

        var attendanceByLaborer = attendance
            .GroupBy(a => a.LaborerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var eligibleLaborers = await BuildEligibleLaborerListAsync(periodStart, periodEnd, attendance, createEmptyLaborerEntries);
        if (eligibleLaborers.Count == 0)
        {
            return PayrollGenerationResult.Skipped("No laborers were eligible for payroll generation.");
        }

        await using var tx = await _context.Database.BeginTransactionAsync();

        var run = new PayrollRun
        {
            WeekStart = periodStart,
            WeekEnd = periodEnd,
            Status = policy == PayrollGenerationPolicy.Unpaid ? PayrollRunStatus.Draft : PayrollRunStatus.Closed,
            CreatedAt = BusinessDate.Now(),
            ClosedAt = policy == PayrollGenerationPolicy.Unpaid ? null : BusinessDate.Now(),
            CreatedBy = User.Identity?.Name,
            Notes = policy == PayrollGenerationPolicy.RecordOnly
                ? "Historical payroll record only."
                : policy == PayrollGenerationPolicy.Paid
                    ? "Historical payroll marked paid."
                    : null
        };

        _context.PayrollRuns.Add(run);
        await _context.SaveChangesAsync();

        var entries = new List<PayrollEntry>();
        foreach (var laborer in eligibleLaborers)
        {
            attendanceByLaborer.TryGetValue(laborer.Id, out var laborerAttendance);
            laborerAttendance ??= new List<AttendanceRecord>();
            var grossWage = laborerAttendance.Where(x => x.Status != AttendanceStatus.Absent).Sum(x => x.WageAmount);
            entries.Add(new PayrollEntry
            {
                PayrollRunId = run.Id,
                LaborerId = laborer.Id,
                Laborer = laborer,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                TotalDays = laborerAttendance.Count(x => x.Status != AttendanceStatus.Absent),
                GrossWage = grossWage,
                TotalAdditions = 0m,
                TotalDeductions = 0m,
                NetPay = grossWage,
                PaidAmount = 0m,
                RecordType = policy == PayrollGenerationPolicy.RecordOnly
                    ? PayrollEntryRecordType.RecordOnly
                    : PayrollEntryRecordType.Payable,
                Status = PaymentStatus.Unpaid,
                GeneratedAt = BusinessDate.Now(),
                Notes = policy == PayrollGenerationPolicy.RecordOnly ? "Record only" : null
            });
        }

        _context.PayrollEntries.AddRange(entries);
        await _context.SaveChangesAsync();

        if (policy != PayrollGenerationPolicy.RecordOnly)
        {
            await ApplyAutomaticAdvanceDeductionsAsync(entries);
        }

        var entryMap = entries.ToDictionary(e => e.LaborerId, e => e.Id);
        foreach (var record in attendance)
        {
            if (entryMap.TryGetValue(record.LaborerId, out var entryId))
            {
                record.PayrollEntryId = entryId;
            }
        }

        if (policy == PayrollGenerationPolicy.Paid)
        {
            ApplyHistoricalPaidPayments(entries, periodStart, periodEnd);
        }
        else
        {
            foreach (var entry in entries)
            {
                ApplyEntryStatus(entry);
            }
        }

        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        if (policy != PayrollGenerationPolicy.Unpaid)
        {
            _cacheInvalidator.InvalidateDashboard();
            _cacheInvalidator.InvalidateProfitReports();
        }

        return PayrollGenerationResult.Created(entries.Count, "Payroll run created successfully.");
    }

    private async Task<List<Laborer>> BuildEligibleLaborerListAsync(
        DateTime periodStart,
        DateTime periodEnd,
        List<AttendanceRecord> attendance,
        bool includeEmptyLaborers)
    {
        var laborersFromAttendance = attendance
            .Where(a => a.Laborer != null)
            .Select(a => a.Laborer)
            .GroupBy(l => l.Id)
            .Select(g => g.First())
            .ToDictionary(l => l.Id);

        if (includeEmptyLaborers)
        {
            var laborers = await _context.Laborers
                .Where(l => l.HiredDate <= periodEnd && (l.ArchivedAt == null || l.ArchivedAt >= periodStart))
                .OrderBy(l => l.FullName)
                .ToListAsync();

            foreach (var laborer in laborers)
            {
                laborersFromAttendance[laborer.Id] = laborer;
            }
        }

        return laborersFromAttendance.Values
            .OrderBy(l => l.FullName)
            .ToList();
    }

    private async Task<List<PendingPayrollRow>> BuildPendingPayrollRowsAsync(DateTime startDate, DateTime endDate)
    {
        startDate = startDate.Date;
        endDate = endDate.Date;
        if (startDate > endDate)
        {
            return new List<PendingPayrollRow>();
        }

        var laborers = await _context.Laborers
            .AsNoTracking()
            .Where(l => l.HiredDate <= endDate && (l.ArchivedAt == null || l.ArchivedAt >= startDate))
            .OrderBy(l => l.FullName)
            .ToListAsync();

        if (laborers.Count == 0)
        {
            return new List<PendingPayrollRow>();
        }

        var laborerIds = laborers.Select(l => l.Id).ToList();
        var latestPayrollEnd = await _context.PayrollEntries
            .AsNoTracking()
            .Where(e => laborerIds.Contains(e.LaborerId) && e.PeriodEnd >= startDate)
            .GroupBy(e => e.LaborerId)
            .Select(g => new { LaborerId = g.Key, LastEnd = g.Max(e => e.PeriodEnd) })
            .ToDictionaryAsync(x => x.LaborerId, x => x.LastEnd.Date);

        var attendance = await _context.AttendanceRecords
            .AsNoTracking()
            .Where(a => laborerIds.Contains(a.LaborerId) && a.WorkDate >= startDate && a.WorkDate <= endDate)
            .ToListAsync();

        var attendanceByLaborer = attendance
            .Where(a => !IsRegularDayOff(a.WorkDate))
            .GroupBy(a => a.LaborerId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var advanceBalanceMap = await _context.CashAdvances
            .AsNoTracking()
            .Where(c => laborerIds.Contains(c.LaborerId) && c.RemainingBalance > 0)
            .GroupBy(c => c.LaborerId)
            .Select(g => new { LaborerId = g.Key, Balance = g.Sum(c => c.RemainingBalance) })
            .ToDictionaryAsync(x => x.LaborerId, x => x.Balance);

        var rows = new List<PendingPayrollRow>();
        foreach (var laborer in laborers)
        {
            var periodStart = MaxDate(startDate, laborer.HiredDate.Date);
            if (latestPayrollEnd.TryGetValue(laborer.Id, out var lastEnd))
            {
                periodStart = MaxDate(periodStart, lastEnd.AddDays(1));
            }

            if (periodStart > endDate)
            {
                continue;
            }

            attendanceByLaborer.TryGetValue(laborer.Id, out var laborerAttendance);
            laborerAttendance ??= new List<AttendanceRecord>();

            var absenceDays = laborerAttendance
                .Count(a => a.WorkDate >= periodStart && a.WorkDate <= endDate && a.Status == AttendanceStatus.Absent);
            var totalDutyDays = CountWorkingDays(periodStart, endDate) - absenceDays;
            if (totalDutyDays <= 0)
            {
                continue;
            }

            var grossWage = totalDutyDays * laborer.DailyRate;
            var pendingDeductions = Math.Min(grossWage, advanceBalanceMap.GetValueOrDefault(laborer.Id));

            rows.Add(new PendingPayrollRow
            {
                LaborerId = laborer.Id,
                LaborerName = laborer.FullName,
                PeriodStart = periodStart,
                PeriodEnd = endDate,
                DailyRate = laborer.DailyRate,
                TotalDays = totalDutyDays,
                AbsenceDays = absenceDays,
                GrossWage = grossWage,
                PendingAdvanceDeductions = pendingDeductions,
                NetPay = grossWage - pendingDeductions
            });
        }

        return rows;
    }

    private async Task<List<AttendanceRecord>> EnsureAttendanceRecordsForPendingPayrollAsync(
        Laborer laborer,
        DateTime periodStart,
        DateTime periodEnd)
    {
        var existingRecords = await _context.AttendanceRecords
            .Where(a => a.LaborerId == laborer.Id && a.WorkDate >= periodStart && a.WorkDate <= periodEnd)
            .ToListAsync();

        var existingMap = existingRecords.ToDictionary(a => a.WorkDate.Date, a => a);
        var payrollRecords = new List<AttendanceRecord>();

        for (var date = periodStart.Date; date <= periodEnd.Date; date = date.AddDays(1))
        {
            if (IsRegularDayOff(date))
            {
                continue;
            }

            if (existingMap.TryGetValue(date, out var existing))
            {
                if (existing.PayrollEntryId == null)
                {
                    payrollRecords.Add(existing);
                }
                continue;
            }

            var record = new AttendanceRecord
            {
                LaborerId = laborer.Id,
                WorkDate = date,
                Status = AttendanceStatus.Present,
                Source = AttendanceSource.Auto,
                RateSnapshot = laborer.DailyRate,
                WageAmount = laborer.DailyRate,
                Notes = "Auto-created from pending payroll generation.",
                RecordedById = User.Identity?.Name
            };

            _context.AttendanceRecords.Add(record);
            payrollRecords.Add(record);
        }

        return payrollRecords;
    }

    private async Task ApplyAutomaticAdvanceDeductionsAsync(List<PayrollEntry> entries)
    {
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
    }

    private void ApplyHistoricalPaidPayments(List<PayrollEntry> entries, DateTime periodStart, DateTime periodEnd)
    {
        foreach (var entry in entries)
        {
            entry.PaidAmount = Math.Max(0m, entry.NetPay);
            entry.Status = PaymentStatus.Paid;

            if (entry.PaidAmount <= 0m)
            {
                continue;
            }

            _context.PayrollPayments.Add(new PayrollPayment
            {
                PayrollEntryId = entry.Id,
                Date = periodEnd,
                Amount = entry.PaidAmount,
                PaymentMethod = PaymentMethod.Cash,
                ReferenceNo = "Historical payroll",
                RecordedById = User.Identity?.Name
            });

            _context.Expenses.Add(new Expense
            {
                Date = periodEnd,
                Category = "Payroll",
                Vendor = entry.Laborer?.FullName,
                Amount = entry.PaidAmount,
                PaymentMethod = PaymentMethod.Cash,
                ReferenceNo = "Historical payroll",
                Description = $"Payroll: {entry.Laborer?.FullName} ({periodStart:MMM dd, yyyy} - {periodEnd:MMM dd, yyyy})",
                RecordedById = User.Identity?.Name
            });
        }
    }

    private static PayrollGenerationPolicy ResolveHistoricalPolicy(
        DateTime periodStart,
        DateTime periodEnd,
        DateTime recordOnlyThrough,
        DateTime paidThrough,
        DateTime unpaidFrom)
    {
        if (periodEnd <= recordOnlyThrough)
        {
            return PayrollGenerationPolicy.RecordOnly;
        }

        if (periodStart >= unpaidFrom)
        {
            return PayrollGenerationPolicy.Unpaid;
        }

        if (periodStart <= paidThrough)
        {
            return PayrollGenerationPolicy.Paid;
        }

        return PayrollGenerationPolicy.Unpaid;
    }

    private static DateTime GetHistoricalPeriodEnd(
        DateTime periodStart,
        DateTime requestedEnd,
        DateTime recordOnlyThrough,
        DateTime paidThrough,
        DateTime unpaidFrom)
    {
        var naturalWeekEnd = periodStart.Month <= 4
            ? new DateTime(periodStart.Year, periodStart.Month, DateTime.DaysInMonth(periodStart.Year, periodStart.Month))
            : GetWorkPeriodEnd(GetWeekStart(periodStart));
        var periodEnd = naturalWeekEnd > requestedEnd ? requestedEnd : naturalWeekEnd;

        // Keep each generated run inside one business meaning: record-only, paid, or unpaid.
        if (periodStart <= recordOnlyThrough)
        {
            return MinDate(periodEnd, recordOnlyThrough);
        }

        if (periodStart <= paidThrough)
        {
            return MinDate(periodEnd, paidThrough);
        }

        if (periodStart < unpaidFrom)
        {
            return MinDate(periodEnd, unpaidFrom.AddDays(-1));
        }

        return periodEnd;
    }

    private static DateTime MinDate(params DateTime[] dates)
    {
        return dates.Min();
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

        var remainingBalance = entry.RecordType == PayrollEntryRecordType.RecordOnly
            ? 0m
            : entry.NetPay - entry.PaidAmount;

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
    public async Task<IActionResult> ExportPayslipCsv(int id)
    {
        var model = await BuildPayslipViewModelAsync(id);
        if (model == null)
        {
            return NotFound();
        }

        var sb = new StringBuilder();
        sb.AppendLine("Laborer,PeriodStart,PeriodEnd,DutyDays,DailyRate,GrossWage,TotalDeductions,TotalAdditions,NetPay,PaidAmount,Balance,Status");
        sb.AppendLine($"{Escape(model.LaborerName)},{model.PeriodStart:yyyy-MM-dd},{model.PeriodEnd:yyyy-MM-dd},{model.TotalDays},{model.DailyRate:N2},{model.GrossWage:N2},{model.TotalDeductions:N2},{model.TotalAdditions:N2},{model.NetPay:N2},{model.PaidAmount:N2},{model.Balance:N2},{model.DisplayStatus}");

        if (model.Deductions.Any())
        {
            sb.AppendLine();
            sb.AppendLine("Deduction,Amount");
            foreach (var deduction in model.Deductions)
            {
                sb.AppendLine($"{Escape(deduction.Label)},{deduction.Amount:N2}");
            }
        }

        if (model.Additions.Any())
        {
            sb.AppendLine();
            sb.AppendLine("Addition,Amount");
            foreach (var addition in model.Additions)
            {
                sb.AppendLine($"{Escape(addition.Label)},{addition.Amount:N2}");
            }
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"payslip-{id}-{model.PeriodEnd:yyyyMMdd}.csv");
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
        sb.AppendLine("Laborer,PeriodStart,PeriodEnd,RecordType,GrossWage,TotalDeductions,TotalAdditions,NetPay,PaidAmount,Status");

        foreach (var entry in run.Entries.OrderBy(e => e.Laborer.FullName))
        {
            var status = entry.RecordType == PayrollEntryRecordType.RecordOnly ? "Record Only" : entry.Status.ToString();
            sb.AppendLine($"{Escape(entry.Laborer?.FullName)},{entry.PeriodStart:yyyy-MM-dd},{entry.PeriodEnd:yyyy-MM-dd},{entry.RecordType},{entry.GrossWage:N2},{entry.TotalDeductions:N2},{entry.TotalAdditions:N2},{entry.NetPay:N2},{entry.PaidAmount:N2},{status}");
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(sb.ToString());
        return File(bytes, "text/csv", $"payroll-run-{run.Id}.csv");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkPaid(int id, string? returnUrl = null)
    {
        var entry = await _context.PayrollEntries
            .Include(e => e.PayrollRun)
            .Include(e => e.Laborer)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (entry == null)
        {
            return NotFound();
        }

        if (entry.PayrollRun?.Status == PayrollRunStatus.Closed &&
            entry.Status != PaymentStatus.Paid &&
            entry.RecordType != PayrollEntryRecordType.RecordOnly)
        {
            TempData["PayrollError"] = "This payroll run is closed. Payment changes are locked.";
            return SafePayrollRedirect(returnUrl);
        }

        if (entry.RecordType == PayrollEntryRecordType.RecordOnly)
        {
            entry.RecordType = PayrollEntryRecordType.Payable;
            entry.Notes = AppendNote(entry.Notes, "Converted from record-only when marked paid.");
        }

        var balance = entry.NetPay - entry.PaidAmount;
        if (balance > 0m)
        {
            var payment = new PayrollPayment
            {
                PayrollEntryId = entry.Id,
                Date = BusinessDate.Today(),
                Amount = balance,
                PaymentMethod = PaymentMethod.Cash,
                ReferenceNo = "Marked paid",
                RecordedById = User.Identity?.Name
            };

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

            entry.PaidAmount += balance;
        }

        ApplyEntryStatus(entry);
        await _context.SaveChangesAsync();
        await TryCloseRun(entry.PayrollRunId);
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();

        TempData["PayrollSuccess"] = "Payroll marked as paid.";
        return SafePayrollRedirect(returnUrl);
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
        if (entry.RecordType == PayrollEntryRecordType.RecordOnly)
        {
            TempData["PayrollError"] = "Record-only payroll is kept for history and cannot receive payments.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payment.PayrollEntryId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payment.PayrollEntryId });
        }

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
        if (entry.RecordType == PayrollEntryRecordType.RecordOnly)
        {
            TempData["PayrollError"] = "Record-only payroll is kept for history and cannot be adjusted.";
            if (isAjax)
            {
                var model = await BuildPayrollDetailsViewModelAsync(payrollEntryId);
                return model == null ? NotFound() : PartialView("_PayrollDetailsPartial", model);
            }
            return RedirectToAction(nameof(Details), new { id = payrollEntryId });
        }

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

        if (entry.Status == PaymentStatus.Paid && entry.PaidAmount > 0)
        {
            TempData["PayrollError"] = "This payroll is already paid. Add deductions before recording payment.";
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
            Balance = entry.RecordType == PayrollEntryRecordType.RecordOnly ? 0m : entry.NetPay - entry.PaidAmount,
            Status = entry.Status,
            DisplayStatus = entry.RecordType == PayrollEntryRecordType.RecordOnly ? "Record Only" : entry.Status.ToString()
        };
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

        if (run.Entries.All(e => e.RecordType == PayrollEntryRecordType.RecordOnly || e.Status == PaymentStatus.Paid))
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

    private static string AppendNote(string? existing, string note)
    {
        return string.IsNullOrWhiteSpace(existing)
            ? note
            : $"{existing.Trim()} {note}";
    }

    private IActionResult SafePayrollRedirect(string? returnUrl)
    {
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Index));
    }

    private static DateTime GetWeekStart(DateTime date)
    {
        return date.AddDays(-(int)date.DayOfWeek).Date;
    }

    private static DateTime GetWorkPeriodEnd(DateTime periodStart)
    {
        return periodStart.Date.AddDays(5);
    }

    private static DateTime GetDefaultUnpaidPayrollStart()
    {
        return new DateTime(BusinessDate.Today().Year, 5, 18);
    }

    private static bool IsRegularDayOff(DateTime date)
    {
        return date.DayOfWeek == DayOfWeek.Saturday;
    }

    private static int CountWorkingDays(DateTime startDate, DateTime endDate)
    {
        var count = 0;
        for (var date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
        {
            if (!IsRegularDayOff(date))
            {
                count++;
            }
        }

        return count;
    }

    private static DateTime MaxDate(params DateTime[] dates)
    {
        return dates.Max();
    }

    private enum PayrollGenerationPolicy
    {
        RecordOnly,
        Paid,
        Unpaid
    }

    private sealed record PayrollGenerationResult(int CreatedEntries, string Message)
    {
        public static PayrollGenerationResult Created(int createdEntries, string message) => new(createdEntries, message);
        public static PayrollGenerationResult Skipped(string message) => new(0, message);
        public static PayrollGenerationResult Failed(string message) => new(0, message);
    }

    private sealed record PayrollAdjustmentOptionDefinition(string Key, string Label, bool IsDeduction);
}
