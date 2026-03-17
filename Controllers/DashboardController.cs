using HazelInvoice.Data;
using HazelInvoice.Services.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace HazelInvoice.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IDashboardMetricsService _metrics;

    public DashboardController(ApplicationDbContext context, IDashboardMetricsService metrics)
    {
        _context = context;
        _metrics = metrics;
    }

    public async Task<IActionResult> Index()
    {
        var model = await _metrics.BuildAsync(DateTime.Today, HttpContext.RequestAborted);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> ResetDatabase(string confirmText)
    {
        if (!string.Equals(confirmText?.Trim(), "RESET", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ResetError"] = "Reset cancelled. Please type RESET to confirm.";
            return RedirectToAction(nameof(Index));
        }

        var tables = new[]
        {
            "ReceiptLines",
            "Payments",
            "Receipts",
            "ReceiptSequences",
            "PurchaseLines",
            "PurchasePayments",
            "Purchases",
            "PurchaseSequences",
            "Expenses",
            "Deductions",
            "PartnerPurchases",
            "PartnerBalanceConfigs",
            "PartnerCapitals",
            "CollectionReceivedOverrides",
            "Services",
            "Supplies",
            "ProductStockMovements",
            "SupplyStockMovements",
            "Goals",
            "Laborers",
            "AttendanceRecords",
            "PayrollAdjustments",
            "PayrollPayments",
            "PayrollPeriods",
            "PayrollRuns",
            "PayrollCutoffs"
        };

        var sql = "TRUNCATE TABLE " + string.Join(", ", tables.Select(t => $"\"{t}\"")) + " RESTART IDENTITY CASCADE;";
        await _context.Database.ExecuteSqlRawAsync(sql);

        TempData["ResetSuccess"] = "Database reset completed.";
        return RedirectToAction(nameof(Index));
    }
}
