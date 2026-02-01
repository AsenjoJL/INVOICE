using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class ReportsController : Controller
{
    private readonly ApplicationDbContext _context;

    public ReportsController(ApplicationDbContext context)
    {
        _context = context;
    }

    public IActionResult Index()
    {
        return View();
    }

    // GET: Reports/IncomeStatement
    public async Task<IActionResult> IncomeStatement(int? month, int? year)
    {
        var now = DateTime.Now;
        int m = month ?? now.Month;
        int y = year ?? now.Year;

        ViewData["Month"] = m;
        ViewData["Year"] = y;

        // Sales for the month
        var sales = await _context.Receipts
             .Where(r => r.Date.Month == m && r.Date.Year == y && r.Status != PaymentStatus.Void)
             .Include(r => r.Lines)
             .ToListAsync();

        decimal revenue = sales.Sum(r => r.TotalAmount);

        // COGS (Approximate via lines)
        // Need to fetch costs. If cost isn't on line, fetch from product.
        // NOTE: Product cost might change, so ideal system snapshots cost.
        // Here we'll use current product cost if not doing FIFO/LIFO.
        // Optimization: Fetch all needed products to memory or join.
        var lineItems = sales.SelectMany(r => r.Lines).ToList();
        var productIds = lineItems.Where(l => l.ProductId.HasValue).Select(l => l.ProductId.Value).Distinct().ToList();
        var products = await _context.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        decimal cogs = 0;
        foreach(var line in lineItems)
        {
            decimal cost = line.CostPriceSnapshot;
            // Fallback for legacy data
            if (cost == 0 && line.ProductId.HasValue && products.TryGetValue(line.ProductId.Value, out var prod))
            {
                cost = prod.UnitCost;
            }
            cogs += cost * line.Quantity;
        }

        // Expenses
        decimal expenses = await _context.Expenses
            .Where(e => e.Date.Month == m && e.Date.Year == y)
            .SumAsync(e => e.Amount);

        var model = new IncomeStatementViewModel
        {
            Period = new DateTime(y, m, 1).ToString("MMMM yyyy"),
            Revenue = revenue,
            COGS = cogs,
            GrossProfit = revenue - cogs,
            Expenses = expenses,
            NetProfit = (revenue - cogs) - expenses
        };

        return View(model);
    }

    // GET: Reports/Inventory
    public async Task<IActionResult> Inventory()
    {
        // Calculate current stock
        // Start with all products
        var products = await _context.Products.Where(p => p.IsActive).ToListAsync();
        var stock = new List<InventoryReportItem>();

        foreach(var p in products)
        {
             var movement = await _context.ProductStockMovements
                 .Where(m => m.ProductId == p.Id)
                 .SumAsync(m => m.Quantity);
             
             stock.Add(new InventoryReportItem
             {
                 Product = p,
                 CurrentStock = movement, // Assuming stock started at 0 or adjustments included
                 Status = movement <= p.ReorderLevel ? "Low Stock" : "Good"
             });
        }

        return View(stock);
    }
}
