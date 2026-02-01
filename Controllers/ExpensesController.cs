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
    public IActionResult Create()
    {
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
        return View(expense);
    }
}
