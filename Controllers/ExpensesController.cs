using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class ExpensesController : Controller
{
    private static readonly (string Group, string[] Items)[] ExpensePresetGroups =
    {
        ("Daily Expenses", new[]
        {
            "FOOD ALLOWANCE",
            "DRIVER FEE",
            "PLASTIC/SUPPLIES",
            "DIESEL",
            "DAILY DUES"
        }),
        ("Weekly Expenses", new[]
        {
            "CASH INTEREST",
            "PUSH CART",
            "TENT",
            "LABOR",
            "CARD INC"
        }),
        ("Monthly Expenses", new[]
        {
            "ASIALINK CORP",
            "RAFI CORP",
            "BOARDING HOUSE",
            "PARKING FEE",
            "TRUCK MAINTENANCE"
        })
    };

    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public ExpensesController(ApplicationDbContext context, IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
    }

    // GET: Expenses
    public async Task<IActionResult> Index()
    {
        return View(await _context.Expenses.AsNoTracking().OrderByDescending(e => e.Date).ToListAsync());
    }

    // GET: Expenses/Create
    public async Task<IActionResult> Create()
    {
        await PopulateExpenseOptionsAsync();
        return View();
    }

    // POST: Expenses/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Date,Category,Vendor,Amount,PaymentMethod,ReferenceNo,Description")] Expense expense)
    {
        if (ModelState.IsValid)
        {
            expense.RecordedById = User.Identity?.Name;
            _context.Add(expense);
            await _context.SaveChangesAsync();
            _cacheInvalidator.InvalidateDashboard();
            _cacheInvalidator.InvalidateProfitReports();
            return RedirectToAction(nameof(Index));
        }
        await PopulateExpenseOptionsAsync();
        return View(expense);
    }

    private async Task PopulateExpenseOptionsAsync()
    {
        var categories = await _context.Expenses
            .AsNoTracking()
            .Where(e => !string.IsNullOrWhiteSpace(e.Category))
            .Select(e => e.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        var vendors = await _context.Expenses
            .AsNoTracking()
            .Where(e => !string.IsNullOrWhiteSpace(e.Vendor))
            .Select(e => e.Vendor!)
            .Distinct()
            .OrderBy(v => v)
            .ToListAsync();

        var categoryGroups = ExpensePresetGroups
            .Select(group => new ExpenseCategoryGroupViewModel(
                group.Group,
                group.Items
                    .Concat(categories)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(item => item)
                    .ToList()))
            .ToList();

        var uncategorized = categories
            .Where(category => !ExpensePresetGroups.Any(group =>
                group.Items.Contains(category, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(category => category)
            .ToList();

        if (uncategorized.Count > 0)
        {
            categoryGroups.Add(new ExpenseCategoryGroupViewModel("More Expense Types", uncategorized));
        }

        ViewBag.CategoryOptions = categories;
        ViewBag.CategoryGroups = categoryGroups;
        ViewBag.VendorOptions = vendors;
    }

    public sealed record ExpenseCategoryGroupViewModel(string Label, List<string> Items);
}
