using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

public class OrdersController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IReceiptService _receiptService;

    private static readonly string[] OutletOrderTokens = new[]
    {
        "autoliv", "autolive", "taiyo", "gmc", "global", "uct", "knowles", "knowless",
        "merasenko", "teradyne", "jpkitchen", "jpmorgan", "cebukitchen", "cebukit",
        "bakery", "wlahug", "mitsumi", "feeder", "mphokim", "phokim"
    };

    public OrdersController(ApplicationDbContext context, IReceiptService receiptService)
    {
        _context = context;
        _receiptService = receiptService;
    }

    // GET: Orders/VegetableMatrix?date=yyyy-MM-dd&page=1&productPage=1
    public async Task<IActionResult> VegetableMatrix(DateTime? date, int page = 1, int productPage = 1)
    {
        var targetDate = (date ?? DateTime.Today).Date;
        var dayStart = targetDate;
        var dayEnd = targetDate.AddDays(1);

        int outletPageSize = 12;
        int productPageSize = 25;

        if (page < 1) page = 1;
        if (productPage < 1) productPage = 1;

        // 1) OUTLETS (base query)
        var outletsBaseQuery = _context.Customers
            .AsNoTracking()
            .Where(c => c.IsActive &&
                        (c.GroupName == "EIGHT2EIGHT OUTLETS" || c.GroupName == "Taste 8 outlets"))
            .ToListAsync();

        var outletsAll = await outletsBaseQuery;
        int totalOutlets = outletsAll.Count;

        // Optional "self heal" (keep your behavior)
        if (totalOutlets == 0)
        {
            var fix = await _context.Customers
                .Where(c => c.IsActive && (c.GroupName == null || c.GroupName == ""))
                .ToListAsync();

            if (fix.Any())
            {
                foreach (var c in fix) c.GroupName = "EIGHT2EIGHT OUTLETS";
                await _context.SaveChangesAsync();

                outletsAll = await _context.Customers
                    .AsNoTracking()
                    .Where(c => c.IsActive &&
                                (c.GroupName == "EIGHT2EIGHT OUTLETS" || c.GroupName == "Taste 8 outlets"))
                    .ToListAsync();
                totalOutlets = outletsAll.Count;
            }
        }

        // Filter outlets to only those with orders for the day
        var outletNameToIdAll = outletsAll.ToDictionary(o => o.Name, o => o.Id, StringComparer.OrdinalIgnoreCase);
        var allOutletIdsPre = outletsAll.Select(o => o.Id).ToList();
        var allOutletNamesPre = outletsAll.Select(o => o.Name).ToList();
        var outletHasOrdersAll = outletsAll.ToDictionary(o => o.Id, _ => false);

        var outletOrderRowsAll = await _context.Receipts
            .AsNoTracking()
            .Where(r => r.Date >= dayStart && r.Date < dayEnd &&
                        r.Status != PaymentStatus.Void &&
                        ((r.CustomerId.HasValue && allOutletIdsPre.Contains(r.CustomerId.Value)) ||
                         (!r.CustomerId.HasValue && allOutletNamesPre.Contains(r.CustomerName))))
            .Select(r => new { r.CustomerId, r.CustomerName })
            .Distinct()
            .ToListAsync();

        foreach (var row in outletOrderRowsAll)
        {
            int? cid = null;
            if (row.CustomerId.HasValue && outletHasOrdersAll.ContainsKey(row.CustomerId.Value))
                cid = row.CustomerId.Value;
            else if (!string.IsNullOrWhiteSpace(row.CustomerName) && outletNameToIdAll.TryGetValue(row.CustomerName, out int nameCid))
                cid = nameCid;

            if (cid == null) continue;
            outletHasOrdersAll[cid.Value] = true;
        }

        outletsAll = outletsAll.Where(o => outletHasOrdersAll.TryGetValue(o.Id, out var hasOrder) && hasOrder).ToList();
        totalOutlets = outletsAll.Count;

        // Force all outlets into one page for printing
        if (totalOutlets > 0)
            outletPageSize = totalOutlets;

        int totalPages = (int)Math.Ceiling(totalOutlets / (double)outletPageSize);
        if (totalPages < 1) totalPages = 1;
        if (page > totalPages) page = totalPages;

        var orderedOutlets = outletsAll
            .OrderBy(c => GetOutletOrderIndex(c.Name))
            .ThenBy(c => c.Name)
            .ToList();

        var visibleOutlets = orderedOutlets
            .Skip((page - 1) * outletPageSize)
            .Take(outletPageSize)
            .ToList();

        // Needed for filtering receipts (all outlets in allowed groups)
        var allOutletList = orderedOutlets.Select(c => new { c.Id, c.Name }).ToList();
        var allOutletIds = allOutletList.Select(c => c.Id).ToList();
        var allOutletNames = allOutletList.Select(c => c.Name).ToList();

        // 2) PRODUCTS (paged)
        var productsBaseQuery = _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name);

        int totalProducts = await productsBaseQuery.CountAsync();

        int totalProductPages = (int)Math.Ceiling(totalProducts / (double)productPageSize);
        if (totalProductPages < 1) totalProductPages = 1;
        if (productPage > totalProductPages) productPage = totalProductPages;

        var visibleProducts = await productsBaseQuery
            .Skip((productPage - 1) * productPageSize)
            .Take(productPageSize)
            .ToListAsync();

        var visibleProductIds = visibleProducts.Select(p => p.Id).ToList();
        var visibleOutletIds = visibleOutlets.Select(o => o.Id).ToList();
        var visibleOutletNameSet = visibleOutlets.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 3) TOTAL QTY PER PRODUCT (ALL outlets in groups) – FAST SQL GROUP BY
        var qtyByProduct = await _context.ReceiptLines
            .AsNoTracking()
            .Where(l => l.ProductId != null)
            .Join(_context.Receipts.AsNoTracking(),
                l => l.ReceiptId,
                r => r.Id,
                (l, r) => new { l, r })
            .Where(x => x.r.Date >= dayStart && x.r.Date < dayEnd &&
                        x.r.Status != PaymentStatus.Void &&
                        ((x.r.CustomerId.HasValue && allOutletIds.Contains(x.r.CustomerId.Value)) ||
                         (!x.r.CustomerId.HasValue && allOutletNames.Contains(x.r.CustomerName))))
            .GroupBy(x => x.l.ProductId!.Value)
            .Select(g => new
            {
                ProductId = g.Key,
                Qty = g.Sum(z => (decimal)z.l.Quantity)
            })
            .ToListAsync();

        var productTotalQtyInGroup = qtyByProduct.ToDictionary(x => x.ProductId, x => x.Qty);

        // For grand totals, only products that appear today
        var productIdsInDay = qtyByProduct.Select(x => x.ProductId).ToList();

        // 4) PRICES: only for visible + products-in-day
        // 4) PRICES: only for visible + products-in-day
        var priceProductIds = visibleProductIds
            .Union(productIdsInDay)
            .Distinct()
            .ToList();

        var productsData = await _context.Products
            .AsNoTracking()
            .Where(p => priceProductIds.Contains(p.Id))
            .Select(p => new { p.Id, p.UnitCost, p.Markup, p.DeliveryFee })
            .ToDictionaryAsync(x => x.Id, x => x);

        var weeklyPrices = await _context.WeeklyPrices
            .AsNoTracking()
            .Where(w => w.EffectiveFrom <= dayStart && w.EffectiveTo >= dayStart)
            .Where(w => priceProductIds.Contains(w.ProductId))
            .ToListAsync();

        var weeklyPriceMap = weeklyPrices
            .GroupBy(x => x.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.EffectiveFrom)
                      .ThenByDescending(x => x.Id)
                      .First());

        var productPrices = new Dictionary<int, decimal>(priceProductIds.Count);
        var productCosts = new Dictionary<int, decimal>(priceProductIds.Count);
        var productMarkups = new Dictionary<int, decimal>(priceProductIds.Count);

        foreach (var pid in priceProductIds)
        {
            decimal cost = 0m;
            decimal markup = 0m;
            decimal deliveryFee = 0m;

            if (productsData.TryGetValue(pid, out var pData))
            {
                cost = pData.UnitCost;
                markup = pData.Markup;
                deliveryFee = pData.DeliveryFee;
            }

            if (weeklyPriceMap.TryGetValue(pid, out var wp))
            {
                if (wp.CostOverride.HasValue)
                    cost = wp.CostOverride.Value;

                // Override with WeeklyPrice logic if present
                if (wp.Markup != 0)
                {
                    markup = wp.Markup;
                }
                else if (wp.BasePrice > 0 && cost > 0)
                {
                    // Fallback for legacy records without stored Markup
                    markup = wp.BasePrice - cost;
                }

                if (wp.DeliveryFee.HasValue)
                {
                    deliveryFee = wp.DeliveryFee.Value;
                }

                // Prefer stored delivery price when available
                if (wp.DeliveryPrice > 0)
                {
                    productPrices[pid] = wp.DeliveryPrice;
                }
                else
                {
                    productPrices[pid] = cost + markup + deliveryFee;
                }
            }
            else
            {
                productPrices[pid] = cost + markup + deliveryFee;
            }

            productCosts[pid] = cost;
            productMarkups[pid] = markup;
        }

        // 5) MATRIX QUANTITIES (VISIBLE only) – FAST SQL GROUP BY
        var matrixRows = await _context.ReceiptLines
            .AsNoTracking()
            .Where(l => l.ProductId != null && visibleProductIds.Contains(l.ProductId.Value))
            .Join(_context.Receipts.AsNoTracking(),
                l => l.ReceiptId,
                r => r.Id,
                (l, r) => new { l, r })
            .Where(x => x.r.Date >= dayStart && x.r.Date < dayEnd &&
                        x.r.Status != PaymentStatus.Void &&
                        ((x.r.CustomerId.HasValue && visibleOutletIds.Contains(x.r.CustomerId.Value)) ||
                         (!x.r.CustomerId.HasValue && visibleOutletNameSet.Contains(x.r.CustomerName))))
            .GroupBy(x => new { ProductId = x.l.ProductId!.Value, x.r.CustomerId, x.r.CustomerName })
            .Select(g => new
            {
                g.Key.ProductId,
                g.Key.CustomerId,
                g.Key.CustomerName,
                Qty = g.Sum(z => (decimal)z.l.Quantity),
                Status = g.Min(z => z.r.Status) // Prioritize Unpaid(0) over Paid(2)
            })
            .ToListAsync();

        var visibleNameToId = visibleOutlets.ToDictionary(o => o.Name, o => o.Id, StringComparer.OrdinalIgnoreCase);

        var matrixQuantities = new Dictionary<string, decimal>(matrixRows.Count);
        var matrixStatuses = new Dictionary<string, string>(matrixRows.Count);

        var visibleOutletIdSet = visibleOutletIds.ToHashSet();

        var outletHasOrders = visibleOutlets.ToDictionary(
            o => o.Id,
            o => outletHasOrdersAll.TryGetValue(o.Id, out var hasOrder) && hasOrder);

        foreach (var row in matrixRows)
        {
            int? cid = null;
            if (row.CustomerId.HasValue && visibleOutletIdSet.Contains(row.CustomerId.Value))
                cid = row.CustomerId.Value;
            else if (!string.IsNullOrWhiteSpace(row.CustomerName) && visibleNameToId.TryGetValue(row.CustomerName, out int nameCid))
                cid = nameCid;

            if (cid == null) continue;

            string key = $"{row.ProductId}_{cid.Value}";
            matrixQuantities[key] = row.Qty;
            matrixStatuses[key] = row.Status.ToString().ToUpper();
        }

        // 6) STATUS FLAGS per product (PAID/UNPAID) – SQL aggregation
        var statusAgg = await _context.ReceiptLines
            .AsNoTracking()
            .Where(l => l.ProductId != null)
            .Join(_context.Receipts.AsNoTracking(),
                l => l.ReceiptId,
                r => r.Id,
                (l, r) => new { l, r })
            .Where(x => x.r.Date >= dayStart && x.r.Date < dayEnd &&
                        x.r.Status != PaymentStatus.Void &&
                        ((x.r.CustomerId.HasValue && allOutletIds.Contains(x.r.CustomerId.Value)) ||
                         (!x.r.CustomerId.HasValue && allOutletNames.Contains(x.r.CustomerName))))
            .GroupBy(x => x.l.ProductId!.Value)
            .Select(g => new
            {
                ProductId = g.Key,
                HasUnpaid = g.Any(z => z.r.Status == PaymentStatus.Unpaid),
                HasPaid = g.Any(z => z.r.Status == PaymentStatus.Paid)
            })
            .ToListAsync();

        var flagsMap = statusAgg.ToDictionary(x => x.ProductId, x => new { x.HasUnpaid, x.HasPaid });

        // Status dictionary for visible rows only (what you render)
        var productStatuses = new Dictionary<int, string>(visibleProducts.Count);
        foreach (var vp in visibleProducts)
        {
            int pid = vp.Id;
            decimal qty = productTotalQtyInGroup.TryGetValue(pid, out var q) ? q : 0m;

            if (qty <= 0)
            {
                productStatuses[pid] = "NO_ORDERS";
                continue;
            }

            if (flagsMap.TryGetValue(pid, out var fl))
            {
                if (fl.HasUnpaid) productStatuses[pid] = "UNPAID";
                else if (fl.HasPaid) productStatuses[pid] = "PAID";
                else if (qty >= 3) productStatuses[pid] = "THREE_PLUS";
                else productStatuses[pid] = "NORMAL";
            }
            else
            {
                productStatuses[pid] = qty >= 3 ? "THREE_PLUS" : "NORMAL";
            }
        }

        // 7) GRAND TOTALS
        decimal grandTotalQty = 0m;
        decimal grandTotalAmt = 0m;

        foreach (var x in qtyByProduct)
        {
            var pid = x.ProductId;
            var qty = x.Qty;
            var price = productPrices.TryGetValue(pid, out var p) ? p : 0m;

            grandTotalQty += qty;
            grandTotalAmt += (qty * price);
        }

        var viewModel = new VegetableMatrixViewModel
        {
            Date = targetDate,

            CurrentPage = page,
            PageSize = outletPageSize,
            TotalOutletsInGroup = totalOutlets,

            ProductPage = productPage,
            ProductPageSize = productPageSize,
            TotalProducts = totalProducts,

            SelectedGroupName = "All",

            VisibleOutlets = visibleOutlets,
            VisibleProducts = visibleProducts,

            ProductPrices = productPrices,
            ProductCosts = productCosts,
            ProductMarkups = productMarkups,
            MatrixQuantities = matrixQuantities,
            MatrixStatuses = matrixStatuses,
            ProductTotalQtyAllOutletsInGroup = productTotalQtyInGroup,
            OutletHasOrders = outletHasOrders,
            ProductStatuses = productStatuses,

            GrandTotalQty = grandTotalQty,
            GrandTotalAmount = grandTotalAmt
        };

        return View(viewModel);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMatrix(VegetableMatrixViewModel model)
    {
        if (model == null) return BadRequest("Model is null");
        var dayStart = model.Date.Date;
        var dayEnd = dayStart.AddDays(1);

        var affectedCustomerIds = model.MatrixQuantities.Keys
            .Select(k => int.Parse(k.Split('_')[1]))
            .Distinct()
            .ToList();

        var customers = await _context.Customers
            .Where(c => affectedCustomerIds.Contains(c.Id))
            .ToListAsync();
        var customerIdSet = customers.Select(c => c.Id).ToHashSet();
        var customerNameSet = customers.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var affectedProductIds = model.MatrixQuantities.Keys
            .Select(k => int.Parse(k.Split('_')[0]))
            .Distinct()
            .ToList();

        if (model.ProductPrices != null && model.ProductPrices.Any())
        {
            affectedProductIds = affectedProductIds
                .Union(model.ProductPrices.Keys)
                .Distinct()
                .ToList();
        }

        // Preload products (avoid FindAsync inside loops)
        var productMap = await _context.Products
            .AsNoTracking()
            .Where(p => affectedProductIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Unit, p.UnitCost, p.Markup, p.DeliveryFee })
            .ToDictionaryAsync(x => x.Id, x => x);

        var weeklyCostOverrides = (await _context.WeeklyPrices
                .AsNoTracking()
                .Where(w => affectedProductIds.Contains(w.ProductId) &&
                            w.EffectiveFrom <= dayStart && w.EffectiveTo >= dayStart &&
                            w.CostOverride != null)
                .Select(w => new { w.ProductId, w.CostOverride, w.EffectiveFrom, w.Id })
                .ToListAsync())
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.EffectiveFrom)
                      .ThenByDescending(x => x.Id)
                      .First()
                      .CostOverride!.Value);

        decimal GetEffectiveCost(int pid)
        {
            if (weeklyCostOverrides.TryGetValue(pid, out var c)) return c;
            return productMap.TryGetValue(pid, out var prod) ? prod.UnitCost : 0m;
        }

        int diff = (7 + (model.Date.DayOfWeek - DayOfWeek.Monday)) % 7;
        var weekStart = model.Date.AddDays(-1 * diff).Date;
        var weekEnd = weekStart.AddDays(6).Date;

        var weeklyPriceSnapshot = await _context.WeeklyPrices
            .AsNoTracking()
            .Where(w => affectedProductIds.Contains(w.ProductId) &&
                        w.EffectiveFrom <= dayStart && w.EffectiveTo >= dayStart)
            .ToListAsync();

        var weeklyPriceSnapshotMap = weeklyPriceSnapshot
            .GroupBy(w => w.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.EffectiveFrom)
                      .ThenByDescending(x => x.Id)
                      .First());

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var existingReceipts = await _context.Receipts
                    .Include(r => r.Lines)
                    .Where(r => r.Date >= dayStart && r.Date < dayEnd && r.Status != PaymentStatus.Void)
                    .ToListAsync();

                bool MatchesCustomer(Receipt r, Customer c) =>
                    (r.CustomerId.HasValue && r.CustomerId.Value == c.Id) ||
                    (!r.CustomerId.HasValue && r.CustomerName == c.Name);

                // 0) Update Weekly Prices (Price overrides) if provided
                await UpdateWeeklyPricesFromPricesAsync(model.Date, model.ProductPrices);

                foreach (var customer in customers)
                {
                    // 1) Calculate PAID quantities (Locked)
                    var paidLines = existingReceipts
                        .Where(r => MatchesCustomer(r, customer) && r.Status == PaymentStatus.Paid)
                        .SelectMany(r => r.Lines)
                        .Where(l => l.ProductId.HasValue)
                        .GroupBy(l => l.ProductId!.Value)
                        .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

                    var inputs = model.MatrixQuantities
                        .Where(k => k.Key.EndsWith($"_{customer.Id}"))
                        .ToDictionary(k => int.Parse(k.Key.Split('_')[0]), v => v.Value);

                    // 2) Find Existing UNPAID receipt or Create NEW
                    var unpaidReceipt = existingReceipts.FirstOrDefault(r => 
                        MatchesCustomer(r, customer) && r.Status == PaymentStatus.Unpaid);

                    // If no unpaid receipt, do we need one?
                    if (unpaidReceipt == null)
                    {
                        // Check if any input requires an unpaid delta
                        bool needsReceipt = inputs.Any(kvp => {
                            int pid = kvp.Key;
                            int target = (int)Math.Round(kvp.Value, MidpointRounding.AwayFromZero);
                            int paid = paidLines.TryGetValue(pid, out int p) ? p : 0;
                            return (target - paid) > 0;
                        });

                        if (!needsReceipt) continue;

                        unpaidReceipt = new Receipt
                        {
                            CustomerId = customer.Id,
                            CustomerName = customer.Name,
                            CustomerAddress = customer.Address,
                            ContactNumber = customer.ContactNumber,
                            Date = model.Date,
                            Type = ReceiptType.Delivery,
                            Status = PaymentStatus.Unpaid,
                            ReceiptNumber = await _receiptService.GenerateNextReceiptNumberAsync()
                        };
                        _context.Receipts.Add(unpaidReceipt);
                        existingReceipts.Add(unpaidReceipt); // Add to local list to prevent duplicates if calc repeats
                    }

                    var lines = unpaidReceipt.Lines.Where(l => l.ProductId.HasValue).ToList();

                    foreach (var kvp in inputs)
                    {
                        int pid = kvp.Key;
                        decimal targetQtyDec = kvp.Value;
                        if (targetQtyDec < 0) targetQtyDec = 0;

                        // Target Total
                        int targetQty = (int)Math.Round(targetQtyDec, MidpointRounding.AwayFromZero);
                        
                        // Subtract Paid (Locked)
                        int paidQty = paidLines.TryGetValue(pid, out int pq) ? pq : 0;
                        int unpaidQty = targetQty - paidQty;

                        // Cannot reduce below paid amount in this view (would require credit note or unpaying)
                        if (unpaidQty < 0) unpaidQty = 0; 

                        decimal price = model.ProductPrices.TryGetValue(pid, out var pr) ? pr : 0m;
                        if (price <= 0)
                        {
                            weeklyPriceSnapshotMap.TryGetValue(pid, out var wpSnap);
                            if (wpSnap != null && wpSnap.DeliveryPrice > 0)
                            {
                                price = wpSnap.DeliveryPrice;
                            }
                            else
                            {
                                var cost = GetEffectiveCost(pid);
                                var deliveryFee = wpSnap?.DeliveryFee
                                    ?? (productMap.TryGetValue(pid, out var prodFromMap2) ? prodFromMap2.DeliveryFee : 0m);

                                decimal markup = 0m;
                                if (wpSnap != null)
                                {
                                    if (wpSnap.Markup != 0)
                                        markup = wpSnap.Markup;
                                    else if (wpSnap.BasePrice > 0 && cost > 0)
                                        markup = wpSnap.BasePrice - cost;
                                    else if (productMap.TryGetValue(pid, out var prodFromMap3))
                                        markup = prodFromMap3.Markup;
                                }
                                else if (productMap.TryGetValue(pid, out var prodFromMap4))
                                {
                                    markup = prodFromMap4.Markup;
                                }

                                price = cost + markup + deliveryFee;
                            }
                        }

                        var line = lines.FirstOrDefault(l => l.ProductId == pid);

                        if (line != null)
                        {
                            if (unpaidQty > 0)
                            {
                                line.Quantity = unpaidQty;
                                line.Price = price;
                                line.Amount = unpaidQty * price;
                                // Update snapshot for draft/unpaid
                                line.CostPriceSnapshot = GetEffectiveCost(pid);
                            }
                            else
                            {
                                _context.ReceiptLines.Remove(line);
                            }
                        }
                        else if (unpaidQty > 0)
                        {
                            var prod = productMap.TryGetValue(pid, out var p) ? p : null;
                            unpaidReceipt.Lines.Add(new ReceiptLine
                            {
                                ProductId = pid,
                                ItemName = prod?.Name ?? "Unknown",
                                Unit = prod?.Unit ?? "pcs",
                                Quantity = unpaidQty,
                                Price = price,
                                Amount = unpaidQty * price,
                                CostPriceSnapshot = GetEffectiveCost(pid)
                            });
                        }
                    }
                }

                await _context.SaveChangesAsync();

                foreach (var r in existingReceipts)
                {
                    if ((r.CustomerId.HasValue && customerIdSet.Contains(r.CustomerId.Value)) ||
                        (!r.CustomerId.HasValue && customerNameSet.Contains(r.CustomerName)))
                        r.TotalAmount = r.Lines.Sum(l => l.Amount);
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        return RedirectToAction(nameof(VegetableMatrix), new
        {
            date = model.Date.ToString("yyyy-MM-dd"),
            page = model.CurrentPage,
            productPage = model.ProductPage
        });
    }

    // OUTLET ORDER GET
    public async Task<IActionResult> VegetableOutletOrder(DateTime? date, int? customerId, bool allOutlets = false)
    {
        var targetDate = (date ?? DateTime.Today).Date;
        var dayStart = targetDate;
        var dayEnd = targetDate.AddDays(1);

        var customers = await _context.Customers
            .AsNoTracking()
            .Where(c => c.IsActive)
            .ToListAsync();
        customers = customers
            .OrderBy(c => GetOutletOrderIndex(c.Name))
            .ThenBy(c => c.Name)
            .ToList();

        if (!customerId.HasValue && customers.Any())
            customerId = customers.First().Id;

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var productIds = products.Select(p => p.Id).ToList();

        var weeklyPrices = await _context.WeeklyPrices
            .AsNoTracking()
            .Where(w => w.EffectiveFrom <= dayStart && w.EffectiveTo >= dayStart)
            .Where(w => productIds.Contains(w.ProductId))
            .Select(w => new { w.ProductId, w.DeliveryPrice, w.EffectiveFrom, w.Id })
            .ToListAsync();

        var weeklyPriceMap = weeklyPrices
            .GroupBy(x => x.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.EffectiveFrom)
                      .ThenByDescending(x => x.Id)
                      .First()
                      .DeliveryPrice);

        var productPrices = new Dictionary<int, decimal>(products.Count);
        foreach (var p in products)
            productPrices[p.Id] = weeklyPriceMap.TryGetValue(p.Id, out var dp) ? dp : (p.UnitCost + p.Markup + p.DeliveryFee);

        var quantities = new Dictionary<int, decimal>();

        if (allOutlets)
        {
            var outletIds = customers.Select(c => c.Id).ToList();
            var outletNames = customers.Select(c => c.Name).ToList();

            var allReceipts = await _context.Receipts
                .AsNoTracking()
                .Include(r => r.Lines)
                .Where(r => r.Date >= dayStart && r.Date < dayEnd &&
                            r.Status != PaymentStatus.Void &&
                            ((r.CustomerId.HasValue && outletIds.Contains(r.CustomerId.Value)) ||
                             (!r.CustomerId.HasValue && outletNames.Contains(r.CustomerName))))
                .ToListAsync();

            foreach (var receipt in allReceipts)
            {
                foreach (var line in receipt.Lines)
                {
                    if (line.ProductId.HasValue)
                    {
                        if (!quantities.ContainsKey(line.ProductId.Value))
                            quantities[line.ProductId.Value] = 0;

                        quantities[line.ProductId.Value] += line.Quantity;

                        if (line.Price > 0 && !weeklyPriceMap.ContainsKey(line.ProductId.Value))
                            productPrices[line.ProductId.Value] = line.Price;
                    }
                }
            }
        }
        else if (customerId.HasValue)
        {
            var targetCustomer = customers.FirstOrDefault(c => c.Id == customerId.Value);
            if (targetCustomer != null)
            {
                var allReceipts = await _context.Receipts
                    .AsNoTracking()
                    .Include(r => r.Lines)
                    .Where(r => r.Date >= dayStart && r.Date < dayEnd &&
                                r.Status != PaymentStatus.Void &&
                                ((r.CustomerId.HasValue && r.CustomerId.Value == targetCustomer.Id) ||
                                 (!r.CustomerId.HasValue && r.CustomerName == targetCustomer.Name)))
                    .ToListAsync();

                foreach (var receipt in allReceipts)
                {
                    foreach (var line in receipt.Lines)
                    {
                        if (line.ProductId.HasValue)
                        {
                            if (!quantities.ContainsKey(line.ProductId.Value))
                                quantities[line.ProductId.Value] = 0;
                            
                            quantities[line.ProductId.Value] += line.Quantity;

                            if (line.Price > 0 && !weeklyPriceMap.ContainsKey(line.ProductId.Value))
                                productPrices[line.ProductId.Value] = line.Price;
                        }
                    }
                }
            }
        }

        var vm = new VegetableOutletOrderViewModel
        {
            Date = targetDate,
            ShowAllOutlets = allOutlets,
            SelectedCustomerId = customerId ?? 0,
            Customers = customers,
            Products = products,
            ProductPrices = productPrices,
            Quantities = quantities
        };

        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveOutletOrder(VegetableOutletOrderViewModel model)
    {
        if (model == null) return BadRequest("Model is null");
        var dayStart = model.Date.Date;
        var dayEnd = dayStart.AddDays(1);

        if (model.ShowAllOutlets)
        {
            await UpdateWeeklyPricesFromPricesAsync(model.Date, model.ProductPrices);
            return RedirectToAction(nameof(VegetableOutletOrder), new
            {
                date = model.Date.ToString("yyyy-MM-dd"),
                customerId = model.SelectedCustomerId,
                allOutlets = true
            });
        }

        // VALIDATION: Prevent disappearing prices
        // Check for Orphaned Prices (Price > 0 but Qty <= 0)
        // Note: We check the RAW Posted 'model.Quantities' to see what user entered.
        foreach (var kvp in model.ProductPrices)
        {
            if (kvp.Value > 0)
            {
                if (!model.Quantities.TryGetValue(kvp.Key, out var qty) || qty <= 0)
                {
ModelState.AddModelError("", $"You entered a Price for an item (ID: {kvp.Key}) but Quantity is 0. Please enter a Quantity.");
                }
            }
        }

        // Check Validity (Validation Errors or Binding Errors)
        if (!ModelState.IsValid)
        {
             // Repopulate Lists for View
             var customers = await _context.Customers.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync();
             var products = await _context.Products.AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Name).ToListAsync();
             
             model.Customers = customers;
             model.Products = products;
             
             return View("VegetableOutletOrder", model);
        }

        var customer = await _context.Customers.FindAsync(model.SelectedCustomerId);
        if (customer == null) return NotFound("Customer not found");

        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                // LOAD ALL RECEIPTS FOR CUSTOMER/DATE (Paid and Unpaid)
                var allReceipts = await _context.Receipts
                    .Include(r => r.Lines)
                    .Where(r => r.Date >= dayStart && r.Date < dayEnd &&
                                r.Status != PaymentStatus.Void &&
                                ((r.CustomerId.HasValue && r.CustomerId.Value == customer.Id) ||
                                 (!r.CustomerId.HasValue && r.CustomerName == customer.Name)))
                    .ToListAsync();
                    
                // Identify Locked Paid Qty
                var paidLines = allReceipts
                   .Where(r => r.Status == PaymentStatus.Paid)
                   .SelectMany(r => r.Lines)
                   .Where(l => l.ProductId.HasValue)
                   .GroupBy(l => l.ProductId!.Value)
                   .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
                   
                // Identify target Unpaid Receipt
                var unpaidReceipt = allReceipts.FirstOrDefault(r => r.Status == PaymentStatus.Unpaid);

                var quantities = model.Quantities
                    .Where(k => k.Value > 0)
                    .ToDictionary(k => k.Key, k => k.Value);

                // Check if we need unpaid receipt (any input > paid)
                bool needsReceipt = quantities.Any(kvp => {
                    int pid = kvp.Key;
                    int target = (int)Math.Round(kvp.Value, MidpointRounding.AwayFromZero);
                    int paid = paidLines.TryGetValue(pid, out int p) ? p : 0;
                    return (target - paid) > 0;
                });

                if (!quantities.Any() && unpaidReceipt == null)
                {
                    // Nothing to do
                }
                else
                {
                    if (unpaidReceipt == null)
                    {
                        if (needsReceipt)
                        {
                            unpaidReceipt = new Receipt
                            {
                                CustomerId = customer.Id,
                                CustomerName = customer.Name,
                                CustomerAddress = customer.Address,
                                ContactNumber = customer.ContactNumber,
                                Date = model.Date,
                                Type = ReceiptType.Delivery,
                                Status = PaymentStatus.Unpaid,
                                ReceiptNumber = await _receiptService.GenerateNextReceiptNumberAsync()
                            };
                            _context.Receipts.Add(unpaidReceipt);
                        }
                    }
                    
                    if (unpaidReceipt != null)
                    {
                        var currentLines = unpaidReceipt.Lines.Where(l => l.ProductId.HasValue).ToList();

                        var usedProductIds = quantities.Keys.ToList();
                        var prodMap = await _context.Products
                            .AsNoTracking()
                            .Where(p => usedProductIds.Contains(p.Id))
                            .Select(p => new { p.Id, p.Name, p.Unit, p.UnitCost, p.Markup, p.DeliveryFee })
                            .ToDictionaryAsync(x => x.Id, x => x);

                    var weeklyCostOverrides = (await _context.WeeklyPrices
                            .AsNoTracking()
                            .Where(w => usedProductIds.Contains(w.ProductId) &&
                                        w.EffectiveFrom <= dayStart && w.EffectiveTo >= dayStart &&
                                        w.CostOverride != null)
                            .Select(w => new { w.ProductId, w.CostOverride, w.EffectiveFrom, w.Id })
                            .ToListAsync())
                        .GroupBy(w => w.ProductId)
                        .ToDictionary(
                            g => g.Key,
                            g => g.OrderByDescending(x => x.EffectiveFrom)
                                  .ThenByDescending(x => x.Id)
                                  .First()
                                  .CostOverride!.Value);

                        decimal GetEffectiveCost(int pid)
                        {
                            if (weeklyCostOverrides.TryGetValue(pid, out var c)) return c;
                            return prodMap.TryGetValue(pid, out var prod) ? prod.UnitCost : 0m;
                        }

                        foreach (var kvp in quantities)
                        {
                            int productId = kvp.Key;
                            decimal targetQtyDec = kvp.Value;
                            if (targetQtyDec < 0) targetQtyDec = 0;
                            int targetQty = (int)Math.Round(targetQtyDec, MidpointRounding.AwayFromZero);
                            
                            // Delta
                            int paidQty = paidLines.TryGetValue(productId, out int pq) ? pq : 0;
                            int unpaidQty = targetQty - paidQty;
                            if (unpaidQty < 0) unpaidQty = 0;

                            decimal price = model.ProductPrices.TryGetValue(productId, out var pr) ? pr : 0m;
                            if (price <= 0)
                            {
                                var markup = prodMap.TryGetValue(productId, out var prodFromMap) ? prodFromMap.Markup : 0m;
                                var deliveryFee = prodMap.TryGetValue(productId, out var prodFromMap2) ? prodFromMap2.DeliveryFee : 0m;
                                price = GetEffectiveCost(productId) + markup + deliveryFee;
                            }

                            var existingLine = currentLines.FirstOrDefault(l => l.ProductId == productId);
                            if (existingLine != null)
                            {
                                if (unpaidQty > 0)
                                {
                                    existingLine.Quantity = unpaidQty;
                                    existingLine.Price = price;
                                    existingLine.Amount = unpaidQty * price;
                                    // Update snapshot
                                    existingLine.CostPriceSnapshot = GetEffectiveCost(productId);
                                }
                                else
                                {
                                    // Remove if no longer needed in unpaid
                                     // (Wait, we'll remove all remaining currentLines at end of loop? No, specific line logic)
                                    // Logic below removes 'existingLine' from 'currentLines' list so it DOESN'T get deleted.
                                    // But here we WANT to delete it if Qty is 0.
                                    // So we leave it in 'currentLines' (don't Remove from list), so it gets deleted at end.
                                    // Ah, previous logic: `currentLines.Remove(existingLine)` prevented deletion efficiently.
                                }
                                
                                if (unpaidQty > 0)
                                    currentLines.Remove(existingLine); // Mark as kept
                            }
                            else if (unpaidQty > 0)
                            {
                                var prod = prodMap.TryGetValue(productId, out var p) ? p : null;
                                unpaidReceipt.Lines.Add(new ReceiptLine
                                {
                                    ProductId = productId,
                                    ItemName = prod?.Name ?? "Unknown",
                                    Unit = prod?.Unit ?? "pcs",
                                    Quantity = unpaidQty,
                                    Price = price,
                                    Amount = unpaidQty * price,
                                    CostPriceSnapshot = GetEffectiveCost(productId)
                                });
                            }
                        }

                        // Remove lines that are no longer present in input or have 0 unpaid qty
                        if (currentLines.Any())
                            _context.ReceiptLines.RemoveRange(currentLines);

                        unpaidReceipt.TotalAmount = unpaidReceipt.Lines.Sum(l => l.Amount);
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });

        return RedirectToAction(nameof(VegetableOutletOrder), new
        {
            date = model.Date.ToString("yyyy-MM-dd"),
            customerId = model.SelectedCustomerId
        });
    }

    // GET: Orders/UnpaidOrders
    public async Task<IActionResult> UnpaidOrders(DateTime? date)
    {
        var targetDate = date ?? DateTime.Now.Date;
        return await GetOrdersByStatus(targetDate, PaymentStatus.Unpaid);
    }

    // GET: Orders/PaidOrders
    public async Task<IActionResult> PaidOrders(DateTime? date)
    {
        var targetDate = date ?? DateTime.Now.Date;
        return await GetOrdersByStatus(targetDate, PaymentStatus.Paid);
    }

    private async Task<IActionResult> GetOrdersByStatus(DateTime date, PaymentStatus status)
    {
        var dayStart = date.Date;
        var dayEnd = dayStart.AddDays(1);
        var receipts = await _context.Receipts
            .AsNoTracking()
            .Include(r => r.Lines)
            .Where(r => r.Date >= dayStart && r.Date < dayEnd && r.Status == status)
            .OrderBy(r => r.CustomerName)
            .ToListAsync();

        var model = new HazelInvoice.ViewModels.ReceiptListViewModel
        {
            Date = date,
            Status = status,
            Receipts = receipts,
            GrandTotal = receipts.Sum(r => r.TotalAmount)
        };

        return View("ReceiptList", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAsPaid(int id, DateTime returnDate)
    {
        var receipt = await _context.Receipts.FindAsync(id);
        if (receipt != null && receipt.Status == PaymentStatus.Unpaid)
        {
            receipt.Status = PaymentStatus.Paid;
            receipt.PaidAmount = receipt.TotalAmount;
            
            var payment = new Payment
            {
                ReceiptId = receipt.Id,
                Date = DateTime.Now,
                Amount = receipt.TotalAmount,
                Method = PaymentMethod.Cash,
                RecordedById = User.Identity?.Name ?? "System"
            };
            _context.Payments.Add(payment);
            
            await _context.SaveChangesAsync();
        }
        
        return RedirectToAction("PaidOrders", new { date = returnDate });
    }
    // GET: Orders/SummaryAll
    public async Task<IActionResult> SummaryAll(DateTime? startDate, DateTime? endDate, string status = "All", int? outletId = null)
    {
        var start = startDate ?? DateTime.Today;
        var end = endDate ?? DateTime.Today;
        var startDateOnly = start.Date;
        var endExclusive = end.Date.AddDays(1);
        int? targetOutletId = outletId;
        string? targetOutletName = null;

        if (outletId.HasValue)
        {
            var outlet = await _context.Customers.FindAsync(outletId.Value);
            targetOutletName = outlet?.Name;
        }

        // Base Query
        var query = _context.Receipts.AsNoTracking()
            .Where(r => r.Date >= startDateOnly && r.Date < endExclusive && r.Status != PaymentStatus.Void);

        if (targetOutletId.HasValue)
        {
            query = query.Where(r =>
                (r.CustomerId.HasValue && r.CustomerId.Value == targetOutletId.Value) ||
                (!r.CustomerId.HasValue && r.CustomerName == targetOutletName));
        }

        if (status == "Paid") query = query.Where(r => r.Status == PaymentStatus.Paid);
        else if (status == "Unpaid") query = query.Where(r => r.Status == PaymentStatus.Unpaid);
        
        var receipts = await query.ToListAsync();

        // 1. KPIs
        var totalSales = receipts.Sum(r => r.TotalAmount);
        
        var totalPaid = receipts.Where(r => r.Status == PaymentStatus.Paid).Sum(r => r.TotalAmount);
        var totalUnpaid = receipts.Where(r => r.Status == PaymentStatus.Unpaid).Sum(r => r.TotalAmount);
        var count = receipts.Count;

        // 2. Daily Trend
        var daily = receipts
            .GroupBy(r => r.Date.Date)
            .Select(g => new DailyTrendDto {
                Date = g.Key,
                TotalAmount = g.Sum(r => r.TotalAmount),
                PaidAmount = g.Where(r => r.Status == PaymentStatus.Paid).Sum(r => r.TotalAmount),
                UnpaidAmount = g.Where(r => r.Status == PaymentStatus.Unpaid).Sum(r => r.TotalAmount)
            })
            .OrderBy(d => d.Date)
            .ToList();

        // 3. Outlet Summary
        var outletStats = receipts
            .GroupBy(r => !string.IsNullOrEmpty(r.CustomerName) ? r.CustomerName : "Walk-in")
            .Select(g => new OutletSummaryDto {
                OutletName = g.Key,
                TotalAmount = g.Sum(r => r.TotalAmount),
                PaidAmount = g.Where(r => r.Status == PaymentStatus.Paid).Sum(r => r.TotalAmount),
                UnpaidAmount = g.Where(r => r.Status == PaymentStatus.Unpaid).Sum(r => r.TotalAmount)
            })
            .OrderByDescending(o => o.TotalAmount)
            .ToList();

        // 4. Top Items
        var lineQuery = _context.ReceiptLines.AsNoTracking()
            .Where(l => l.Receipt != null)
            .Where(l => l.Receipt!.Date >= startDateOnly && l.Receipt.Date < endExclusive && l.Receipt.Status != PaymentStatus.Void);
            
        if (targetOutletId.HasValue)
        {
            lineQuery = lineQuery.Where(l =>
                (l.Receipt!.CustomerId.HasValue && l.Receipt.CustomerId.Value == targetOutletId.Value) ||
                (!l.Receipt.CustomerId.HasValue && l.Receipt.CustomerName == targetOutletName));
        }

        if (status == "Paid") lineQuery = lineQuery.Where(l => l.Receipt!.Status == PaymentStatus.Paid);
        else if (status == "Unpaid") lineQuery = lineQuery.Where(l => l.Receipt!.Status == PaymentStatus.Unpaid);

        var totalItemsCount = await lineQuery.SumAsync(l => l.Quantity);

        var topItems = await lineQuery
            .GroupBy(l => l.ItemName)
            .Select(g => new TopItemDto {
                ItemName = g.Key,
                Quantity = g.Sum(l => l.Quantity),
                TotalAmount = g.Sum(l => l.Amount)
            })
            .OrderByDescending(x => x.Quantity)
            .Take(20)
            .ToListAsync();

        var outlets = await _context.Customers.OrderBy(c => c.Name).ToListAsync();

        var vm = new SummaryAllViewModel {
            StartDate = start,
            EndDate = end,
            StatusFilter = status,
            OutletId = outletId,
            TotalSales = totalSales,
            TotalPaid = totalPaid,
            TotalUnpaid = totalUnpaid,
            TotalCount = count,
            TotalItemsSold = totalItemsCount,
            DailyTrends = daily,
            OutletSummaries = outletStats,
            TopItems = topItems,
            Outlets = new Microsoft.AspNetCore.Mvc.Rendering.SelectList(outlets, "Id", "Name", outletId)
        };

        return View(vm);
    }

    private static int GetOutletOrderIndex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return int.MaxValue;
        var normalized = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        for (int i = 0; i < OutletOrderTokens.Length; i++)
        {
            var token = OutletOrderTokens[i];
            if (normalized.Contains(token) || token.Contains(normalized))
                return i;
        }
        return int.MaxValue;
    }

    private async Task UpdateWeeklyPricesFromPricesAsync(DateTime date, Dictionary<int, decimal>? postedPrices)
    {
        if (postedPrices == null || postedPrices.Count == 0) return;

        int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        var weekStart = date.AddDays(-1 * diff).Date;
        var weekEnd = weekStart.AddDays(6).Date;

        var pricePids = postedPrices.Keys.ToList();

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => pricePids.Contains(p.Id))
            .Select(p => new { p.Id, p.UnitCost, p.Markup, p.DeliveryFee })
            .ToListAsync();

        var productMap = products.ToDictionary(p => p.Id, p => p);

        var wps = await _context.WeeklyPrices
            .Where(w => pricePids.Contains(w.ProductId) &&
                        w.EffectiveFrom <= date && w.EffectiveTo >= date)
            .ToListAsync();

        var wpGroups = wps.GroupBy(w => w.ProductId).ToList();
        var wpMap = wpGroups.ToDictionary(
            g => g.Key,
            g => g.OrderByDescending(x => x.EffectiveFrom)
                  .ThenByDescending(x => x.Id)
                  .First());

        var duplicates = wpGroups
            .SelectMany(g => g.OrderByDescending(x => x.EffectiveFrom).ThenByDescending(x => x.Id).Skip(1))
            .ToList();

        if (duplicates.Count > 0)
        {
            _context.WeeklyPrices.RemoveRange(duplicates);
        }

        foreach (var kvp in postedPrices)
        {
            int pid = kvp.Key;
            decimal postedPrice = kvp.Value;

            if (postedPrice <= 0) continue;
            if (!productMap.TryGetValue(pid, out var prod)) continue;

            var wp = wpMap.TryGetValue(pid, out var existing) ? existing : null;

            decimal effectiveCost = wp?.CostOverride ?? prod.UnitCost;
            decimal effectiveDeliveryFee = wp?.DeliveryFee ?? prod.DeliveryFee;

            decimal markup = prod.Markup;
            if (wp != null)
            {
                if (wp.Markup != 0)
                    markup = wp.Markup;
                else if (wp.BasePrice > 0 && effectiveCost > 0)
                    markup = wp.BasePrice - effectiveCost;
            }

            decimal defaultPrice = (wp != null && wp.DeliveryPrice > 0)
                ? wp.DeliveryPrice
                : (effectiveCost + markup + effectiveDeliveryFee);

            if (Math.Abs(postedPrice - defaultPrice) < 0.005m) continue;

            decimal newMarkup = postedPrice - effectiveCost - effectiveDeliveryFee;
            decimal newBase = effectiveCost + newMarkup;

            if (wp != null)
            {
                wp.Markup = newMarkup;
                wp.BasePrice = newBase;
                wp.DeliveryPrice = postedPrice;
            }
            else
            {
                _context.WeeklyPrices.Add(new WeeklyPrice
                {
                    ProductId = pid,
                    EffectiveFrom = weekStart,
                    EffectiveTo = weekEnd,
                    BasePrice = newBase,
                    DeliveryPrice = postedPrice,
                    Markup = newMarkup
                });
            }
        }

        await _context.SaveChangesAsync();
    }
}
