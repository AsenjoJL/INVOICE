using HazelInvoice.Data;
using HazelInvoice.Configuration;
using HazelInvoice.Helpers;
using HazelInvoice.Services.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace HazelInvoice.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IDashboardMetricsService _metrics;
    private readonly IOptions<FeaturesOptions> _features;
    private readonly IWebHostEnvironment _environment;

    public DashboardController(
        ApplicationDbContext context,
        IDashboardMetricsService metrics,
        IOptions<FeaturesOptions> features,
        IWebHostEnvironment environment)
    {
        _context = context;
        _metrics = metrics;
        _features = features;
        _environment = environment;
    }

    public async Task<IActionResult> Index()
    {
        var model = await _metrics.BuildAsync(BusinessDate.Today(), HttpContext.RequestAborted);
        return View(model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize]
    public async Task<IActionResult> ResetDatabase(string confirmText)
    {
        if (!_environment.IsDevelopment() && !_features.Value.AllowDangerousDatabaseReset)
        {
            TempData["ResetError"] = "Database reset is disabled in this environment.";
            return RedirectToAction(nameof(Index));
        }

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
