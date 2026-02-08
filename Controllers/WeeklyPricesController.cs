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
        var vm = await BuildPriceVersusModelAsync(targetDate);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PriceVersus(PriceVersusViewModel model)
    {
        if (model.Items == null || model.Items.Count == 0)
        {
            return RedirectToAction(nameof(PriceVersus), new { date = model.TargetDate.ToString("yyyy-MM-dd") });
        }

        int diff = (7 + (model.TargetDate.DayOfWeek - DayOfWeek.Monday)) % 7;
        var weekStart = model.TargetDate.AddDays(-1 * diff).Date;
        var weekEnd = weekStart.AddDays(6).Date;

        for (int i = 0; i < model.Items.Count; i++)
        {
            if (model.Items[i].Cost < 0)
                ModelState.AddModelError($"Items[{i}].Cost", "Cost cannot be negative.");
            if (model.Items[i].Markup < 0)
                ModelState.AddModelError($"Items[{i}].Markup", "Markup cannot be negative.");
            if (model.Items[i].DeliveryFee < 0)
                ModelState.AddModelError($"Items[{i}].DeliveryFee", "Delivery fee cannot be negative.");
        }

        if (!ModelState.IsValid)
        {
            var vm = await BuildPriceVersusModelAsync(model.TargetDate, model.Items);
            vm.ApplyToMasterCost = model.ApplyToMasterCost;
            return View(vm);
        }

        var pids = model.Items.Select(i => i.ProductId).Distinct().ToList();

        var productMap = await _context.Products
            .Where(p => pids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p);

        var existing = await _context.WeeklyPrices
            .Where(w => pids.Contains(w.ProductId) &&
                        w.EffectiveFrom <= model.TargetDate && w.EffectiveTo >= model.TargetDate)
            .ToListAsync();

        var existingMap = existing.ToDictionary(w => w.ProductId, w => w);

        foreach (var item in model.Items)
        {
            if (!productMap.TryGetValue(item.ProductId, out var prod)) continue;

            var masterCost = prod.UnitCost;
            var masterMarkup = prod.Markup;
            var masterDeliveryFee = prod.DeliveryFee;

            decimal cost = item.Cost;
            decimal markup = item.Markup;
            decimal deliveryFee = item.DeliveryFee;

            if (model.ApplyToMasterCost && masterCost != cost)
            {
                prod.UnitCost = cost;
                masterCost = cost;
            }

            decimal? costOverride = null;
            if (!model.ApplyToMasterCost && cost != masterCost)
            {
                costOverride = cost;
            }

            decimal? deliveryFeeOverride = null;
            if (deliveryFee != masterDeliveryFee)
            {
                deliveryFeeOverride = deliveryFee;
            }

            var effectiveCost = costOverride ?? masterCost;
            var effectiveDeliveryFee = deliveryFeeOverride ?? masterDeliveryFee;
            var basePrice = effectiveCost + markup;
            var deliveryPrice = basePrice + effectiveDeliveryFee;

            var shouldHaveWeekly = costOverride.HasValue || markup != masterMarkup || deliveryFeeOverride.HasValue;

            if (existingMap.TryGetValue(item.ProductId, out var wp))
            {
                if (!shouldHaveWeekly)
                {
                    _context.WeeklyPrices.Remove(wp);
                    continue;
                }

                bool changed = false;
                if (wp.CostOverride != costOverride)
                {
                    wp.CostOverride = costOverride;
                    changed = true;
                }
                if (wp.DeliveryFee != deliveryFeeOverride)
                {
                    wp.DeliveryFee = deliveryFeeOverride;
                    changed = true;
                }
                if (wp.Markup != markup)
                {
                    wp.Markup = markup;
                    changed = true;
                }
                if (wp.BasePrice != basePrice || wp.DeliveryPrice != deliveryPrice)
                {
                    wp.BasePrice = basePrice;
                    wp.DeliveryPrice = deliveryPrice;
                    changed = true;
                }

                if (changed)
                    _context.Update(wp);
            }
            else if (shouldHaveWeekly)
            {
                var newWp = new WeeklyPrice
                {
                    ProductId = item.ProductId,
                    EffectiveFrom = weekStart,
                    EffectiveTo = weekEnd,
                    CostOverride = costOverride,
                    DeliveryFee = deliveryFeeOverride,
                    BasePrice = basePrice,
                    DeliveryPrice = deliveryPrice,
                    Markup = markup
                };
                _context.Add(newWp);
            }
        }

        await _context.SaveChangesAsync();

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
        var product = await _context.Products.FindAsync(weeklyPrice.ProductId);
        if (product == null)
        {
            ModelState.AddModelError("ProductId", "Product not found.");
        }

        if (weeklyPrice.BasePrice < 0)
            ModelState.AddModelError("BasePrice", "Base price cannot be negative.");
        if (weeklyPrice.DeliveryPrice < 0)
            ModelState.AddModelError("DeliveryPrice", "Delivery price cannot be negative.");

        if (product != null && ModelState.IsValid)
        {
            var markup = weeklyPrice.BasePrice - product.UnitCost;
            var deliveryFee = weeklyPrice.DeliveryPrice - weeklyPrice.BasePrice;

            if (markup < 0)
                ModelState.AddModelError("BasePrice", "Base price cannot be lower than cost.");
            if (deliveryFee < 0)
                ModelState.AddModelError("DeliveryPrice", "Delivery price cannot be lower than base price.");
        }

        if (ModelState.IsValid && product != null)
        {
            var markup = weeklyPrice.BasePrice - product.UnitCost;
            var deliveryFee = weeklyPrice.DeliveryPrice - weeklyPrice.BasePrice;

            weeklyPrice.Markup = markup;
            weeklyPrice.DeliveryFee = deliveryFee != product.DeliveryFee ? deliveryFee : null;

            var basePrice = product.UnitCost + markup;
            var effectiveDeliveryFee = weeklyPrice.DeliveryFee ?? product.DeliveryFee;
            weeklyPrice.BasePrice = basePrice;
            weeklyPrice.DeliveryPrice = basePrice + effectiveDeliveryFee;

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

        var existing = await _context.WeeklyPrices.FindAsync(id);
        if (existing == null) return NotFound();

        var product = await _context.Products.FindAsync(weeklyPrice.ProductId);
        if (product == null)
        {
            ModelState.AddModelError("ProductId", "Product not found.");
        }

        if (weeklyPrice.BasePrice < 0)
            ModelState.AddModelError("BasePrice", "Base price cannot be negative.");
        if (weeklyPrice.DeliveryPrice < 0)
            ModelState.AddModelError("DeliveryPrice", "Delivery price cannot be negative.");

        if (product != null && ModelState.IsValid)
        {
            var markup = weeklyPrice.BasePrice - product.UnitCost;
            var deliveryFee = weeklyPrice.DeliveryPrice - weeklyPrice.BasePrice;

            if (markup < 0)
                ModelState.AddModelError("BasePrice", "Base price cannot be lower than cost.");
            if (deliveryFee < 0)
                ModelState.AddModelError("DeliveryPrice", "Delivery price cannot be lower than base price.");
        }

        if (ModelState.IsValid && product != null)
        {
            var markup = weeklyPrice.BasePrice - product.UnitCost;
            var deliveryFee = weeklyPrice.DeliveryPrice - weeklyPrice.BasePrice;

            existing.ProductId = weeklyPrice.ProductId;
            existing.EffectiveFrom = weeklyPrice.EffectiveFrom;
            existing.EffectiveTo = weeklyPrice.EffectiveTo;
            existing.Markup = markup;
            existing.DeliveryFee = deliveryFee != product.DeliveryFee ? deliveryFee : null;

            var basePrice = product.UnitCost + markup;
            var effectiveDeliveryFee = existing.DeliveryFee ?? product.DeliveryFee;
            existing.BasePrice = basePrice;
            existing.DeliveryPrice = basePrice + effectiveDeliveryFee;

            try
            {
                _context.Update(existing);
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
        var lastWeekEnd = lastWeekStart.AddDays(1);
        var thisWeekEndExclusive = thisWeekStart.AddDays(1);

        var lastWeekPrices = await _context.WeeklyPrices
            .Where(w => w.EffectiveFrom >= lastWeekStart && w.EffectiveFrom < lastWeekEnd)
            .ToListAsync();

        foreach (var price in lastWeekPrices)
        {
            // Check if exists for this week
            var exists = await _context.WeeklyPrices.AnyAsync(w => w.ProductId == price.ProductId && w.EffectiveFrom >= thisWeekStart && w.EffectiveFrom < thisWeekEndExclusive);
            if (!exists)
            {
                var newPrice = new WeeklyPrice
                {
                    ProductId = price.ProductId,
                    EffectiveFrom = thisWeekStart,
                    EffectiveTo = thisWeekEnd,
                    CostOverride = price.CostOverride,
                    DeliveryFee = price.DeliveryFee,
                    BasePrice = price.BasePrice,
                    DeliveryPrice = price.DeliveryPrice,
                    Markup = price.Markup
                };
                _context.Add(newPrice);
            }
        }
        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    private async Task<PriceVersusViewModel> BuildPriceVersusModelAsync(DateTime targetDate, IEnumerable<PriceVersusItem>? postedItems = null)
    {
        var day = targetDate.Date;

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
            .Where(w => w.EffectiveFrom <= day && w.EffectiveTo >= day)
            .ToListAsync();

        var weeklyMap = weeklyPrices
            .GroupBy(w => w.ProductId)
            .ToDictionary(g => g.Key, g => g.First());

        var postedMap = postedItems?.ToDictionary(i => i.ProductId) ?? new Dictionary<int, PriceVersusItem>();

        var items = new List<PriceVersusItem>(products.Count);
        foreach (var p in products)
        {
            decimal masterCost = p.UnitCost;
            decimal masterMarkup = p.Markup;
            decimal masterDeliveryFee = p.DeliveryFee;
            decimal cost = masterCost;
            decimal markup = masterMarkup;
            decimal deliveryFee = masterDeliveryFee;
            bool hasRec = false;

            if (weeklyMap.TryGetValue(p.Id, out var wp))
            {
                hasRec = true;
                if (wp.CostOverride.HasValue)
                    cost = wp.CostOverride.Value;

                if (wp.DeliveryFee.HasValue)
                    deliveryFee = wp.DeliveryFee.Value;

                if (wp.Markup != 0)
                    markup = wp.Markup;
                else if (wp.BasePrice > 0)
                    markup = wp.BasePrice - cost;
            }

            if (postedMap.TryGetValue(p.Id, out var posted))
            {
                cost = posted.Cost;
                markup = posted.Markup;
                deliveryFee = posted.DeliveryFee;
            }

            items.Add(new PriceVersusItem
            {
                ProductId = p.Id,
                ProductName = p.Name,
                Unit = p.Unit,
                Cost = cost,
                Markup = markup,
                DeliveryFee = deliveryFee,
                MasterCost = masterCost,
                MasterMarkup = masterMarkup,
                MasterDeliveryFee = masterDeliveryFee,
                HasWeeklyRecord = hasRec
            });
        }

        return new PriceVersusViewModel
        {
            TargetDate = targetDate,
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            Items = items
        };
    }


    private bool WeeklyPriceExists(int id)
    {
        return _context.WeeklyPrices.Any(e => e.Id == id);
    }
}
