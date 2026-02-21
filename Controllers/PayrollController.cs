using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class PayrollController : Controller
{
    private readonly ApplicationDbContext _context;

    public PayrollController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index(DateTime? startDate, DateTime? endDate, PaymentStatus? status)
    {
        ViewData["StartDate"] = startDate;
        ViewData["EndDate"] = endDate;
        ViewData["Status"] = status?.ToString() ?? "";

        var query = _context.PayrollPeriods
            .AsNoTracking()
            .Include(p => p.Laborer)
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

        var model = new PayrollGenerateViewModel
        {
            StartDate = start,
            EndDate = end,
            Rows = rows,
            SelectedLaborerIds = rows.Select(r => r.LaborerId).ToList()
        };

        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(PayrollGenerateViewModel model)
    {
        var start = model.StartDate.Date;
        var end = model.EndDate.Date;
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

        var periods = new List<PayrollPeriod>();
        foreach (var group in groups)
        {
            var totalWage = group.Sum(x => x.WageAmount);
            var period = new PayrollPeriod
            {
                LaborerId = group.Key,
                PeriodStart = start,
                PeriodEnd = end,
                TotalDays = group.Count(),
                TotalWage = totalWage,
                PaidAmount = 0,
                Status = PaymentStatus.Unpaid,
                GeneratedAt = DateTime.Now
            };
            periods.Add(period);
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
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Details(int id)
    {
        var period = await _context.PayrollPeriods
            .Include(p => p.Laborer)
            .Include(p => p.Payments)
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
        var remaining = period.TotalWage - period.PaidAmount;

        var model = new PayrollDetailsViewModel
        {
            Period = period,
            AttendanceRecords = attendance,
            Payments = payments,
            RemainingBalance = remaining,
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
            .Include(p => p.Laborer)
            .FirstOrDefaultAsync(p => p.Id == payment.PayrollPeriodId);
        if (period == null)
        {
            return NotFound();
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

        var remaining = period.TotalWage - period.PaidAmount;
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

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id = payment.PayrollPeriodId });
    }
}
