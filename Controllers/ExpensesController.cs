using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class ExpensesController : Controller
{
    private readonly ApplicationDbContext _context;

    public ExpensesController(ApplicationDbContext context)
    {
        _context = context;
    }

    // GET: Expenses
    public async Task<IActionResult> Index()
    {
        return View(await _context.Expenses.OrderByDescending(e => e.Date).ToListAsync());
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

        ViewBag.CategoryOptions = categories;
        ViewBag.VendorOptions = vendors;
        ViewBag.AmountOptions = new decimal[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000 };
    }
}
