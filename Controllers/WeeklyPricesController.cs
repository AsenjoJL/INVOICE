using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

using HazelInvoice.ViewModels;

namespace HazelInvoice.Controllers;

[Authorize]
public class WeeklyPricesController : Controller
{
    private readonly ApplicationDbContext _context;

    public WeeklyPricesController(ApplicationDbContext context)
    {
        _context = context;
    }

    // GET: WeeklyPrices/PriceVersus
    public async Task<IActionResult> PriceVersus(DateTime? date)
    {
        var targetDate = date ?? DateTime.Today;
        
        // Calculate Week (Monday to Sunday)
        int diff = (7 + (targetDate.DayOfWeek - DayOfWeek.Monday)) % 7;
        var weekStart = targetDate.AddDays(-1 * diff).Date;
        var weekEnd = weekStart.AddDays(6).Date;

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var weeklyPrices = await _context.WeeklyPrices
            .AsNoTracking()
            .Where(w => w.EffectiveFrom.Date <= targetDate && w.EffectiveTo.Date >= targetDate)
            .ToListAsync();

        var weeklyMap = weeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(g => g.Key, g => g.First());

        var items = new List<PriceVersusItem>();
        foreach (var p in products)
        {
            decimal cost = p.UnitCost;
            decimal markup = p.Markup;
            bool hasRec = false;

            if (weeklyMap.TryGetValue(p.Id, out var wp))
            {
                hasRec = true;
                markup = wp.Markup != 0 ? wp.Markup : (wp.DeliveryPrice - cost);
            }

            items.Add(new PriceVersusItem
            {
                ProductId = p.Id,
                ProductName = p.Name,
                Unit = p.Unit,
                Cost = cost,
                Markup = markup,
                HasWeeklyRecord = hasRec
            });
        }

        var vm = new PriceVersusViewModel
        {
            TargetDate = targetDate,
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            Items = items
        };

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PriceVersus(PriceVersusViewModel model)
    {
        int diff = (7 + (model.TargetDate.DayOfWeek - DayOfWeek.Monday)) % 7;
        var weekStart = model.TargetDate.AddDays(-1 * diff).Date;
        var weekEnd = weekStart.AddDays(6).Date;

        if (model.Items != null)
        {
            var pids = model.Items.Select(i => i.ProductId).ToList();
            
            var existing = await _context.WeeklyPrices
                .Where(w => pids.Contains(w.ProductId) && 
                            w.EffectiveFrom <= model.TargetDate && w.EffectiveTo >= model.TargetDate)
                .ToListAsync();

            var productMap = await _context.Products.ToDictionaryAsync(p=>p.Id, p=>p);

            foreach (var item in model.Items)
            {
                // Verify Product existence and bind Cost if needed (though we rely on posted cost or product cost?)
                // Use Product Cost from DB to be safe for calculations
                if(!productMap.TryGetValue(item.ProductId, out var prod)) continue;
                
                decimal cost = prod.UnitCost; // Always use Master cost
                decimal price = cost + item.Markup;

                var wp = existing.FirstOrDefault(w => w.ProductId == item.ProductId);

                if (wp != null)
                {
                    if(wp.Markup != item.Markup || wp.DeliveryPrice != price) 
                    {
                        wp.Markup = item.Markup;
                        wp.DeliveryPrice = price;
                        wp.BasePrice = price;
                        _context.Update(wp);
                    }
                }
                else
                {
                    // Create new record for this week
                    var newWp = new WeeklyPrice
                    {
                        ProductId = item.ProductId,
                        EffectiveFrom = weekStart,
                        EffectiveTo = weekEnd,
                        BasePrice = price,
                        DeliveryPrice = price,
                        Markup = item.Markup
                    };
                    _context.Add(newWp);
                }
            }
            await _context.SaveChangesAsync();
        }

        return RedirectToAction(nameof(PriceVersus), new { date = model.TargetDate.ToString("yyyy-MM-dd") });
    }

    // GET: WeeklyPrices
    public async Task<IActionResult> Index()
    {
        var prices = await _context.WeeklyPrices
            .Include(w => w.Product)
            .OrderByDescending(w => w.EffectiveFrom)
            .ToListAsync();
        return View(prices);
    }

    // GET: WeeklyPrices/Create
    public async Task<IActionResult> Create()
    {
        ViewData["ProductId"] = new SelectList(await _context.Products.Where(p => p.IsActive).ToListAsync(), "Id", "Name");
        // Default to this week
        var now = DateTime.Now;
        var startOfWeek = now.AddDays(-(int)now.DayOfWeek + 1); // Monday
        var endOfWeek = startOfWeek.AddDays(6); // Sunday

        return View(new WeeklyPrice { EffectiveFrom = startOfWeek, EffectiveTo = endOfWeek });
    }

    // POST: WeeklyPrices/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind("Id,ProductId,EffectiveFrom,EffectiveTo,BasePrice,DeliveryPrice")] WeeklyPrice weeklyPrice)
    {
        if (ModelState.IsValid)
        {
            _context.Add(weeklyPrice);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }
        ViewData["ProductId"] = new SelectList(await _context.Products.Where(p => p.IsActive).ToListAsync(), "Id", "Name", weeklyPrice.ProductId);
        return View(weeklyPrice);
    }

    // GET: WeeklyPrices/Edit/5
    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var weeklyPrice = await _context.WeeklyPrices.FindAsync(id);
        if (weeklyPrice == null) return NotFound();
        ViewData["ProductId"] = new SelectList(await _context.Products.Where(p => p.IsActive).ToListAsync(), "Id", "Name", weeklyPrice.ProductId);
        return View(weeklyPrice);
    }

    // POST: WeeklyPrices/Edit/5
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, [Bind("Id,ProductId,EffectiveFrom,EffectiveTo,BasePrice,DeliveryPrice")] WeeklyPrice weeklyPrice)
    {
        if (id != weeklyPrice.Id) return NotFound();

        if (ModelState.IsValid)
        {
            try
            {
                _context.Update(weeklyPrice);
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!WeeklyPriceExists(weeklyPrice.Id)) return NotFound();
                else throw;
            }
            return RedirectToAction(nameof(Index));
        }
        ViewData["ProductId"] = new SelectList(await _context.Products.Where(p => p.IsActive).ToListAsync(), "Id", "Name", weeklyPrice.ProductId);
        return View(weeklyPrice);
    }

    // Clone last week's prices to this week
    public async Task<IActionResult> CloneLastWeek()
    {
        var lastWeekStart = DateTime.Now.AddDays(-(int)DateTime.Now.DayOfWeek + 1 - 7).Date;
        var thisWeekStart = DateTime.Now.AddDays(-(int)DateTime.Now.DayOfWeek + 1).Date;
        var thisWeekEnd = thisWeekStart.AddDays(6);

        var lastWeekPrices = await _context.WeeklyPrices
            .Where(w => w.EffectiveFrom.Date == lastWeekStart)
            .ToListAsync();

        foreach (var price in lastWeekPrices)
        {
            // Check if exists for this week
            var exists = await _context.WeeklyPrices.AnyAsync(w => w.ProductId == price.ProductId && w.EffectiveFrom.Date == thisWeekStart);
            if (!exists)
            {
                var newPrice = new WeeklyPrice
                {
                    ProductId = price.ProductId,
                    EffectiveFrom = thisWeekStart,
                    EffectiveTo = thisWeekEnd,
                    BasePrice = price.BasePrice,
                    DeliveryPrice = price.DeliveryPrice
                };
                _context.Add(newPrice);
            }
        }
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }


    private bool WeeklyPriceExists(int id)
    {
        return _context.WeeklyPrices.Any(e => e.Id == id);
    }
}
