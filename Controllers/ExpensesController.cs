using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Expenses;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class ExpensesController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;
    private readonly IExpenseCategoryCatalogService _expenseCategoryCatalog;

    public ExpensesController(
        ApplicationDbContext context,
        IAppCacheInvalidator cacheInvalidator,
        IExpenseCategoryCatalogService expenseCategoryCatalog)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
        _expenseCategoryCatalog = expenseCategoryCatalog;
    }

    // GET: Expenses
    public async Task<IActionResult> Index()
    {
        var expenses = await _context.Expenses
            .AsNoTracking()
            .OrderByDescending(e => e.Date)
            .ThenBy(e => e.Category)
            .ToListAsync();

        var categoryMap = await _expenseCategoryCatalog.GetGroupMapAsync();

        var groups = ExpenseCategoryCatalog.OrderedGroups
            .Select(group => new ExpenseLedgerGroupViewModel
            {
                Label = ExpenseCategoryCatalog.GetLabel(group),
                Items = expenses
                    .Where(expense =>
                    {
                        var normalized = ExpenseCategoryCatalog.NormalizeName(expense.Category);
                        var resolvedGroup = categoryMap.TryGetValue(normalized, out var mapped)
                            ? mapped
                            : ExpenseCategoryGroup.Other;
                        return resolvedGroup == group;
                    })
                    .ToList()
            })
            .Where(group => group.Items.Count > 0)
            .ToList();

        foreach (var group in groups)
        {
            group.Total = group.Items.Sum(item => item.Amount);
        }

        var model = new ExpenseLedgerViewModel
        {
            Groups = groups,
            GrandTotal = groups.Sum(group => group.Total)
        };

        return View(model);
    }

    // GET: Expenses/Create
    public async Task<IActionResult> Create()
    {
        await PopulateExpenseOptionsAsync(ExpenseCategoryGroup.Other);
        return View();
    }

    // POST: Expenses/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,Date,Category,Amount,PaymentMethod,ReferenceNo,Description")] Expense expense, ExpenseCategoryGroup categoryGroup)
    {
        expense.Category = ExpenseCategoryCatalog.NormalizeName(expense.Category);
        expense.Vendor = null;

        if (string.IsNullOrWhiteSpace(expense.Category))
        {
            ModelState.AddModelError(nameof(expense.Category), "Category is required.");
        }

        if (ModelState.IsValid)
        {
            await _expenseCategoryCatalog.UpsertCategoryAsync(expense.Category, categoryGroup);
            expense.RecordedById = User.Identity?.Name;
            _context.Add(expense);
            await _context.SaveChangesAsync();
            _cacheInvalidator.InvalidateDashboard();
            _cacheInvalidator.InvalidateProfitReports();
            return RedirectToAction(nameof(Index));
        }
        await PopulateExpenseOptionsAsync(categoryGroup);
        return View(expense);
    }

    private async Task PopulateExpenseOptionsAsync(ExpenseCategoryGroup selectedGroup)
    {
        var definitions = await _expenseCategoryCatalog.GetDefinitionsAsync();
        var categoryGroups = ExpenseCategoryCatalog.OrderedGroups
            .Select(group => new ExpenseCategoryGroupViewModel(
                ExpenseCategoryCatalog.GetLabel(group),
                definitions
                    .Where(item => item.Group == group)
                    .Select(item => item.Name)
                    .OrderBy(item => item)
                    .ToList()))
            .ToList();

        ViewBag.CategoryGroups = categoryGroups;
        ViewBag.CategoryDefinitions = definitions
            .Select(item => new ExpenseCategoryDefinitionViewModel(item.Name, item.Group.ToString(), ExpenseCategoryCatalog.GetLabel(item.Group)))
            .ToList();
        ViewBag.SelectedCategoryGroup = selectedGroup.ToString();
    }

    public sealed record ExpenseCategoryGroupViewModel(string Label, List<string> Items);
    public sealed record ExpenseCategoryDefinitionViewModel(string Name, string GroupValue, string GroupLabel);
}
