using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.ViewModels;
using Microsoft.EntityFrameworkCore;
using HazelInvoice.Configuration;
using HazelInvoice.Helpers;
using Microsoft.Extensions.Options;

namespace HazelInvoice.Services.Orders;

/// <summary>
/// Builds the Daily Vegetable Order Matrix view model.
/// Kept as a dedicated service so the controller stays thin and the logic is testable/maintainable.
/// </summary>
public sealed class VegetableMatrixService : IVegetableMatrixService
{
    private const int DefaultOutletPageSize = 12;
    private const int DefaultProductPageSize = 25;

    private readonly ApplicationDbContext _context;
    private readonly bool _partnersEnabled;
    private readonly HashSet<string> _outletGroups;
    private readonly string[] _outletOrderTokens;
    private readonly string _defaultOutletGroup;
    private readonly int _targetPrintSheets;
    private readonly int _minPrintRowsPerSheet;
    private readonly decimal _detailPercentFeeDefault;

    public VegetableMatrixService(
        ApplicationDbContext context,
        IOptions<FeaturesOptions> features,
        IOptions<OperationsOptions> operations)
    {
        _context = context;
        _partnersEnabled = features?.Value?.PartnersEnabled ?? false;
        _outletGroups = (operations.Value.OutletGroups ?? []).Where(g => !string.IsNullOrWhiteSpace(g))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _outletOrderTokens = (operations.Value.OutletSortTokens ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        _defaultOutletGroup = operations.Value.DefaultOutletGroup;
        _targetPrintSheets = operations.Value.VegetablePrintTargetSheets > 0 ? operations.Value.VegetablePrintTargetSheets : 3;
        _minPrintRowsPerSheet = operations.Value.VegetablePrintMinRowsPerSheet > 0 ? operations.Value.VegetablePrintMinRowsPerSheet : 41;
        _detailPercentFeeDefault = operations.Value.VegetableDetailPercentFeeDefault > 0 ? operations.Value.VegetableDetailPercentFeeDefault : 1.0m;
    }

    public async Task<VegetableMatrixViewModel> GetAsync(VegetableMatrixQueryOptions options, CancellationToken cancellationToken = default)
    {
        var targetDate = options.Date.Date;
        var dayStart = targetDate;
        var dayEnd = targetDate.AddDays(1);
        var applicableDay = WeeklyPriceCalendar.GetApplicablePriceDate(targetDate);
        var isResetDay = WeeklyPriceCalendar.IsResetDay(targetDate);

        var print = options.Print;
        var showDetails = options.Details && !print;

        var page = options.OutletPage;
        var productPage = options.ProductPage;

        var outletPageSize = DefaultOutletPageSize;
        var productPageSize = DefaultProductPageSize;

        if (print)
        {
            // Print view always starts at first page; the view model still carries page metadata.
            page = 1;
            productPage = 1;
        }

        if (page < 1) page = 1;
        if (productPage < 1) productPage = 1;

        // 1) OUTLETS (base query)
        var outletsAll = await _context.Customers
            .AsNoTracking()
            .Where(c => c.IsActive && c.GroupName != null && _outletGroups.Contains(c.GroupName))
            .ToListAsync(cancellationToken);

        var totalOutlets = outletsAll.Count;

        // Optional "self heal" (keep existing behavior)
        if (totalOutlets == 0)
        {
            var fix = await _context.Customers
                .Where(c => c.IsActive && (c.GroupName == null || c.GroupName == ""))
                .ToListAsync(cancellationToken);

            if (fix.Count > 0)
            {
                foreach (var c in fix) c.GroupName = _defaultOutletGroup;
                await _context.SaveChangesAsync(cancellationToken);

                outletsAll = await _context.Customers
                    .AsNoTracking()
                    .Where(c => c.IsActive && c.GroupName != null && _outletGroups.Contains(c.GroupName))
                    .ToListAsync(cancellationToken);

                totalOutlets = outletsAll.Count;
            }
        }

        // Filter outlets to only those with orders for the day (used for print filter + per-outlet indicator).
        var outletNameToIdAll = outletsAll
            .Where(o => !string.IsNullOrWhiteSpace(o.Name))
            .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var allOutletIdsPre = outletsAll.Select(o => o.Id).ToList();
        var allOutletNamesPre = outletsAll
            .Select(o => o.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();

        var outletHasOrdersAll = outletsAll.ToDictionary(o => o.Id, _ => false);

        var outletOrderRowsAll = await _context.Receipts
            .AsNoTracking()
            .Where(r => r.Date >= dayStart && r.Date < dayEnd &&
                        r.Status != PaymentStatus.Void &&
                        ((r.CustomerId.HasValue && allOutletIdsPre.Contains(r.CustomerId.Value)) ||
                         (!r.CustomerId.HasValue && allOutletNamesPre.Contains(r.CustomerName))))
            .Select(r => new { r.CustomerId, r.CustomerName })
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var row in outletOrderRowsAll)
        {
            int? cid = null;
            if (row.CustomerId.HasValue && outletHasOrdersAll.ContainsKey(row.CustomerId.Value))
                cid = row.CustomerId.Value;
            else if (!string.IsNullOrWhiteSpace(row.CustomerName) && outletNameToIdAll.TryGetValue(row.CustomerName, out var nameCid))
                cid = nameCid;

            if (cid == null) continue;
            outletHasOrdersAll[cid.Value] = true;
        }

        // Print-only filter: include only outlets that actually have orders for the day.
        // Keep normal screen behavior unchanged for browsing/paging.
        if (print)
        {
            outletsAll = outletsAll
                .Where(o => outletHasOrdersAll.TryGetValue(o.Id, out var hasOrder) && hasOrder)
                .ToList();
        }

        totalOutlets = outletsAll.Count;

        // Force all outlets into one page for printing
        if (print && totalOutlets > 0)
            outletPageSize = totalOutlets;

        var totalPages = (int)Math.Ceiling(totalOutlets / (double)outletPageSize);
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

        var totalProducts = await productsBaseQuery.CountAsync(cancellationToken);

        var totalProductPages = (int)Math.Ceiling(totalProducts / (double)productPageSize);
        if (totalProductPages < 1) totalProductPages = 1;
        if (productPage > totalProductPages) productPage = totalProductPages;

        var visibleProducts = await productsBaseQuery
            .Skip((productPage - 1) * productPageSize)
            .Take(productPageSize)
            .ToListAsync(cancellationToken);

        var visibleOutletIds = visibleOutlets.Select(o => o.Id).ToList();
        var visibleOutletNameSet = visibleOutlets.Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outletIdToName = orderedOutlets.ToDictionary(o => o.Id, o => o.Name);

        // 3) Load day lines once, then derive all aggregates in-memory.
        // Keep payload minimal unless detail section is explicitly requested.
        var dayLinesBaseQuery = _context.ReceiptLines
            .AsNoTracking()
            .Where(l => l.ProductId != null)
            .Join(_context.Receipts.AsNoTracking(),
                l => l.ReceiptId,
                r => r.Id,
                (l, r) => new { l, r })
            .Where(x => x.r.Date >= dayStart && x.r.Date < dayEnd &&
                        x.r.Status != PaymentStatus.Void &&
                        ((x.r.CustomerId.HasValue && allOutletIds.Contains(x.r.CustomerId.Value)) ||
                         (!x.r.CustomerId.HasValue && allOutletNames.Contains(x.r.CustomerName))));

        var dayLinesLite = await dayLinesBaseQuery
            .Select(x => new
            {
                ProductId = x.l.ProductId!.Value,
                Qty = x.l.Quantity,
                x.r.Status,
                x.r.CustomerId,
                x.r.CustomerName
            })
            .ToListAsync(cancellationToken);

        var qtyByProduct = dayLinesLite
            .GroupBy(x => x.ProductId)
            .Select(g => new
            {
                ProductId = g.Key,
                Qty = g.Sum(z => z.Qty)
            })
            .ToList();

        var productTotalQtyInGroup = qtyByProduct.ToDictionary(x => x.ProductId, x => x.Qty);
        var productIdsInDay = qtyByProduct.Select(x => x.ProductId).ToList();

        if (print)
        {
            // Print view: include only products that have orders for the selected day.
            // This keeps the printed matrix compact and focused on actual orders.
            var allProductsForPrint = await productsBaseQuery.ToListAsync(cancellationToken);
            visibleProducts = allProductsForPrint
                .Where(p => productTotalQtyInGroup.TryGetValue(p.Id, out var qty) && qty > 0m)
                .ToList();

            totalProducts = visibleProducts.Count;
            productPage = 1;
            totalProductPages = 1;

            productPageSize = Math.Max(
                _minPrintRowsPerSheet,
                (int)Math.Ceiling(totalProducts / (double)_targetPrintSheets));
        }

        // Final guard: always render products alphabetically in matrix (screen + print).
        visibleProducts = visibleProducts
            .OrderBy(p => (p.Name ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)
            .ToList();

        var visibleProductIds = visibleProducts.Select(p => p.Id).ToList();
        var visibleProductIdSet = visibleProductIds.ToHashSet();

        // 4) PRICES: only for visible + products-in-day
        var priceProductIds = visibleProductIds
            .Union(productIdsInDay)
            .Distinct()
            .ToList();

        var productsData = await _context.Products
            .AsNoTracking()
            .Where(p => priceProductIds.Contains(p.Id))
            .Select(p => new { p.Id, p.UnitCost, p.Markup, p.DeliveryFee })
            .ToDictionaryAsync(x => x.Id, x => x, cancellationToken);

        var weeklyPrices = applicableDay.HasValue
            ? await _context.WeeklyPrices
                .AsNoTracking()
                .Where(w => w.EffectiveFrom <= applicableDay.Value && w.EffectiveTo >= applicableDay.Value)
                .Where(w => priceProductIds.Contains(w.ProductId))
                .ToListAsync(cancellationToken)
            : new List<WeeklyPrice>();

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

            if (!isResetDay && productsData.TryGetValue(pid, out var pData))
            {
                cost = pData.UnitCost;
                markup = pData.Markup;
                deliveryFee = pData.DeliveryFee;
            }

            if (!isResetDay && weeklyPriceMap.TryGetValue(pid, out var wp))
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
                    deliveryFee = wp.DeliveryFee.Value;

                // Prefer stored delivery price when available
                if (wp.DeliveryPrice > 0)
                    productPrices[pid] = wp.DeliveryPrice;
                else
                    productPrices[pid] = cost + markup + deliveryFee;
            }
            else
            {
                productPrices[pid] = isResetDay ? 0m : cost + markup + deliveryFee;
            }

            productCosts[pid] = cost;
            productMarkups[pid] = markup;
        }

        var visibleNameToId = visibleOutlets
            .Where(o => !string.IsNullOrWhiteSpace(o.Name))
            .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var visibleOutletIdSet = visibleOutletIds.ToHashSet();

        // 5) MATRIX QUANTITIES (VISIBLE only)
        var matrixRows = dayLinesLite
            .Where(x => visibleProductIdSet.Contains(x.ProductId))
            .Select(x =>
            {
                int? cid = null;
                if (x.CustomerId.HasValue && visibleOutletIdSet.Contains(x.CustomerId.Value))
                {
                    cid = x.CustomerId.Value;
                }
                else if (!string.IsNullOrWhiteSpace(x.CustomerName) && visibleOutletNameSet.Contains(x.CustomerName))
                {
                    if (visibleNameToId.TryGetValue(x.CustomerName, out var mappedId))
                        cid = mappedId;
                }

                return new
                {
                    x.ProductId,
                    CustomerId = cid,
                    x.Qty,
                    x.Status
                };
            })
            .Where(x => x.CustomerId.HasValue)
            .GroupBy(x => new { x.ProductId, CustomerId = x.CustomerId!.Value })
            .Select(g => new
            {
                g.Key.ProductId,
                CustomerId = (int?)g.Key.CustomerId,
                CustomerName = string.Empty,
                Qty = g.Sum(z => z.Qty),
                Status = g.Any(z => z.Status == PaymentStatus.Unpaid)
                    ? PaymentStatus.Unpaid
                    : (g.Any(z => z.Status == PaymentStatus.Paid) ? PaymentStatus.Paid : g.Min(z => z.Status))
            })
            .ToList();

        var matrixQuantities = new Dictionary<string, decimal>(matrixRows.Count);
        var matrixStatuses = new Dictionary<string, string>(matrixRows.Count);

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

            var key = $"{row.ProductId}_{cid.Value}";
            matrixQuantities[key] = row.Qty;
            matrixStatuses[key] = row.Status.ToString().ToUpperInvariant();
        }

        string ResolveOutletName(int? customerId, string? customerName)
        {
            if (customerId.HasValue && outletIdToName.TryGetValue(customerId.Value, out var nameById))
                return nameById;
            if (!string.IsNullOrWhiteSpace(customerName))
                return customerName;
            return "Unknown";
        }

        var detailRows = new List<VegetableOrderDetailRow>();
        decimal totalExpenses = 0m;
        decimal totalDeductions = 0m;
        decimal totalDeliveryPayments = 0m;
        var deductionItems = new List<MoneyLineItem>();
        var expenseItems = new List<MoneyLineItem>();
        var partnerPurchaseGroups = new List<PartnerPurchaseGroupVm>();
        decimal totalPartnerPurchases = 0m;
        // Receipt-line partner tagging (who purchased/owned the item): ProductId -> (PartnerName -> Amount)
        var partnerPurchasesByProductId = new Dictionary<int, Dictionary<string, decimal>>();
        var partnerOrderTotals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var partnerSalesAttributionGroups = new List<PartnerSalesAttributionGroupVm>();
        decimal totalPartnerSalesAttributed = 0m;
        var partnerNames = new List<string>();

        if (showDetails)
        {
            var detailLines = await dayLinesBaseQuery
                .Select(x => new
                {
                    ProductId = x.l.ProductId!.Value,
                    x.l.ItemName,
                    Qty = x.l.Quantity,
                    x.l.Amount,
                    x.l.CostPriceSnapshot,
                    x.l.PartnerName,
                    x.r.Status,
                    x.r.CustomerId,
                    x.r.CustomerName
                })
                .ToListAsync(cancellationToken);

            var detailProductIds = detailLines.Select(x => x.ProductId).Distinct().ToList();
            var detailProducts = await _context.Products
                .AsNoTracking()
                .Where(p => detailProductIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.OwnerName, p.UnitCost, p.SupplierId })
                .ToDictionaryAsync(x => x.Id, x => x, cancellationToken);

            var supplierIds = detailProducts.Values
                .Where(p => p.SupplierId.HasValue)
                .Select(p => p.SupplierId!.Value)
                .Distinct()
                .ToList();

            var supplierNames = await _context.Suppliers
                .AsNoTracking()
                .Where(s => supplierIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

            detailRows = detailLines
                .GroupBy(x => new { x.ProductId, Outlet = ResolveOutletName(x.CustomerId, x.CustomerName) })
                .Select(g =>
                {
                    var prod = detailProducts.TryGetValue(g.Key.ProductId, out var p) ? p : null;
                    var costAmount = g.Sum(x =>
                    {
                        var unitCost = x.CostPriceSnapshot > 0 ? x.CostPriceSnapshot : (prod?.UnitCost ?? 0m);
                        return unitCost * x.Qty;
                    });
                    var amount = g.Sum(x => x.Amount);
                    var ownerName = "";
                    if (prod != null && prod.SupplierId.HasValue && supplierNames.TryGetValue(prod.SupplierId.Value, out var sname))
                        ownerName = sname;

                    return new VegetableOrderDetailRow
                    {
                        ProductId = g.Key.ProductId,
                        OutletName = g.Key.Outlet,
                        ProductName = prod?.Name ?? g.First().ItemName,
                        OwnerName = ownerName,
                        Quantity = g.Sum(x => x.Qty),
                        Amount = amount,
                        CostAmount = costAmount,
                        GrossProfit = amount - costAmount,
                        Status = g.Any(x => x.Status == PaymentStatus.Unpaid) ? "UNPAID" : "PAID"
                    };
                })
                .OrderBy(r => r.OutletName)
                .ThenBy(r => r.ProductName)
                .ToList();

            totalExpenses = await _context.Expenses
                .AsNoTracking()
                .Where(e => e.Date >= dayStart && e.Date < dayEnd)
                .SumAsync(e => e.Amount, cancellationToken);

            expenseItems = await _context.Expenses
                .AsNoTracking()
                .Where(e => e.Date >= dayStart && e.Date < dayEnd)
                .OrderBy(e => e.Date)
                .ThenBy(e => e.Id)
                .Select(e => new MoneyLineItem
                {
                    Label = string.IsNullOrWhiteSpace(e.Description) ? e.Category : $"{e.Category} - {e.Description}",
                    Amount = e.Amount
                })
                .ToListAsync(cancellationToken);

            totalDeductions = await _context.Deductions
                .AsNoTracking()
                .Where(d => d.Date >= dayStart && d.Date < dayEnd &&
                            (d.Category == null || d.Category.ToLower() != "note"))
                .SumAsync(d => d.Amount, cancellationToken);

            deductionItems = await _context.Deductions
                .AsNoTracking()
                .Where(d => d.Date >= dayStart && d.Date < dayEnd &&
                            (d.Category == null || d.Category.ToLower() != "note"))
                .OrderBy(d => d.Date)
                .ThenBy(d => d.Id)
                .Select(d => new MoneyLineItem
                {
                    Label = d.Description,
                    Amount = d.Amount
                })
                .ToListAsync(cancellationToken);

            if (_partnersEnabled)
            {
                // Partner purchases are treated like additional "sales" for fee calculation (matches Profit & Sales behavior).
                var dayPartnerPurchases = await _context.PartnerPurchases
                    .AsNoTracking()
                    .Where(p => p.Date >= dayStart && p.Date < dayEnd)
                    .OrderBy(p => p.PartnerName)
                    .ThenBy(p => p.Id)
                    .ToListAsync(cancellationToken);

                totalPartnerPurchases = dayPartnerPurchases.Sum(p => p.Amount);

                partnerPurchaseGroups = dayPartnerPurchases
                    .GroupBy(p => string.IsNullOrWhiteSpace(p.PartnerName) ? "Partner" : p.PartnerName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new PartnerPurchaseGroupVm
                    {
                        PartnerName = g.Key,
                        Items = g.Select(x => new MoneyLineItem
                        {
                            Label = string.IsNullOrWhiteSpace(x.Notes) ? "Partner purchase" : x.Notes!,
                            Amount = x.Amount
                        }).ToList()
                    })
                    .OrderBy(g => g.PartnerName)
                    .ToList();

                // Build partner-attributed totals from receipt lines (the user selects PartnerName per item when encoding sales).
                foreach (var dl in detailLines)
                {
                    if (dl.Amount <= 0) continue;
                    if (string.IsNullOrWhiteSpace(dl.PartnerName)) continue;

                    var pn = dl.PartnerName.Trim();

                    if (!partnerPurchasesByProductId.TryGetValue(dl.ProductId, out var map))
                    {
                        map = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                        partnerPurchasesByProductId[dl.ProductId] = map;
                    }

                    map[pn] = (map.TryGetValue(pn, out var existing) ? existing : 0m) + dl.Amount;
                    partnerOrderTotals[pn] = (partnerOrderTotals.TryGetValue(pn, out var t) ? t : 0m) + dl.Amount;
                }

                // Partner-attributed sales: group by partner and item name to create a clean list/grid.
                partnerSalesAttributionGroups = detailLines
                    .Where(x => x.Amount > 0m && !string.IsNullOrWhiteSpace(x.PartnerName))
                    .GroupBy(x => x.PartnerName!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(g => new PartnerSalesAttributionGroupVm
                    {
                        PartnerName = g.Key,
                        Items = g
                            .GroupBy(x => x.ItemName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                            .Select(gg => new MoneyLineItem
                            {
                                Label = gg.Key,
                                Amount = gg.Sum(z => z.Amount)
                            })
                            .Where(i => i.Amount > 0m)
                            .OrderByDescending(i => i.Amount)
                            .ThenBy(i => i.Label)
                            .ToList()
                    })
                    .OrderBy(x => x.PartnerName)
                    .ToList();

                totalPartnerSalesAttributed = partnerSalesAttributionGroups.Sum(g => g.TotalAmount);

                // Attach partner purchase values to the detail rows (shown once per product in the view).
                foreach (var row in detailRows)
                {
                    if (partnerPurchasesByProductId.TryGetValue(row.ProductId, out var map))
                        row.PartnerPurchaseByPartner = new Dictionary<string, decimal>(map, StringComparer.OrdinalIgnoreCase);
                }

                // Partner names: from config + from existing purchases (scalable).
                var configPartners = await _context.PartnerBalanceConfigs
                    .AsNoTracking()
                    .OrderBy(p => p.PartnerName)
                    .Select(p => p.PartnerName)
                    .ToListAsync(cancellationToken);

                // If partners aren't configured yet, fall back to whatever exists in PartnerPurchases (any date).
                // This keeps the UI stable without hardcoding names in the matrix.
                if (configPartners.Count == 0)
                {
                    configPartners = await _context.PartnerPurchases
                        .AsNoTracking()
                        .Where(p => !string.IsNullOrWhiteSpace(p.PartnerName))
                        .Select(p => p.PartnerName!)
                        .Distinct()
                        .OrderBy(p => p)
                        .ToListAsync(cancellationToken);
                }

                partnerNames = configPartners
                    .Concat(dayPartnerPurchases.Select(p => p.PartnerName))
                    .Concat(partnerOrderTotals.Keys)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(p => p)
                    .ToList();
            }

            totalDeliveryPayments = await _context.Receipts
                .AsNoTracking()
                .Where(r => r.Date >= dayStart && r.Date < dayEnd &&
                            r.Status != PaymentStatus.Void &&
                            r.Type == ReceiptType.Delivery &&
                            ((r.CustomerId.HasValue && allOutletIds.Contains(r.CustomerId.Value)) ||
                             (!r.CustomerId.HasValue && allOutletNames.Contains(r.CustomerName))))
                .SumAsync(r => r.TotalAmount, cancellationToken);
        }

        // 6) STATUS FLAGS per product for vegetable-column color coding.
        // Rules:
        // - NO_ORDERS: qty <= 0
        // - HIGHEST_QTY: highest positive qty among visible products
        // - LOWEST_QTY: lowest positive qty among visible products
        // - ONE_ORDER: ordered by one outlet only
        // - MANY_ORDERS: ordered by 2+ outlets
        int ResolveOutletId(int? customerId, string customerName)
        {
            if (customerId.HasValue) return customerId.Value;
            if (!string.IsNullOrWhiteSpace(customerName) && outletNameToIdAll.TryGetValue(customerName, out var mappedId))
                return mappedId;
            return -1;
        }

        var productOutletOrderCount = dayLinesLite
            .GroupBy(x => x.ProductId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(x => ResolveOutletId(x.CustomerId, x.CustomerName))
                    .Where(id => id > 0)
                    .Distinct()
                    .Count());

        var visiblePositiveQtys = visibleProducts
            .Select(vp => productTotalQtyInGroup.TryGetValue(vp.Id, out var q) ? q : 0m)
            .Where(q => q > 0m)
            .ToList();

        var lowestPositiveQty = visiblePositiveQtys.Count > 0 ? visiblePositiveQtys.Min() : 0m;
        var highestPositiveQty = visiblePositiveQtys.Count > 0 ? visiblePositiveQtys.Max() : 0m;

        var productStatuses = new Dictionary<int, string>(visibleProducts.Count);
        foreach (var vp in visibleProducts)
        {
            var pid = vp.Id;
            var qty = productTotalQtyInGroup.TryGetValue(pid, out var q) ? q : 0m;

            if (qty <= 0)
            {
                productStatuses[pid] = "NO_ORDERS";
                continue;
            }

            if (highestPositiveQty > 0m && qty == highestPositiveQty) productStatuses[pid] = "HIGHEST_QTY";
            else if (lowestPositiveQty > 0m && qty == lowestPositiveQty) productStatuses[pid] = "LOWEST_QTY";
            else if (productOutletOrderCount.TryGetValue(pid, out var outletCount) && outletCount <= 1) productStatuses[pid] = "ONE_ORDER";
            else if (productOutletOrderCount.TryGetValue(pid, out outletCount) && outletCount >= 2) productStatuses[pid] = "MANY_ORDERS";
            else productStatuses[pid] = "ONE_ORDER";
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

        return new VegetableMatrixViewModel
        {
            Date = targetDate,

            CurrentPage = page,
            PageSize = outletPageSize,
            TotalOutletsInGroup = totalOutlets,

            ProductPage = productPage,
            ProductPageSize = productPageSize,
            TotalProducts = totalProducts,
            IsPrint = print,
            ShowDetails = showDetails,

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
            GrandTotalAmount = grandTotalAmt,

            DetailRows = detailRows,
            DetailTotalSales = detailRows.Sum(r => r.Amount),
            DetailTotalCost = detailRows.Sum(r => r.CostAmount),
            DetailTotalGrossProfit = detailRows.Sum(r => r.GrossProfit),
            DetailTotalExpenses = totalExpenses,
            DetailTotalDeductions = totalDeductions,
            DetailTotalDeliveryPayments = totalDeliveryPayments,
            DetailPartnerNames = partnerNames,
            DetailPartnerPurchaseGroups = partnerPurchaseGroups,
            DetailTotalPartnerPurchases = totalPartnerPurchases,
            DetailSalesSubTotal = detailRows.Sum(r => r.Amount) + totalPartnerPurchases,
            DetailPartnerOrderTotals = partnerOrderTotals,
            DetailPartnerSalesAttributions = partnerSalesAttributionGroups,
            DetailTotalPartnerSalesAttributed = totalPartnerSalesAttributed,

            // Profit subtotal is still gross profit minus deductions/expenses.
            // 1% fee is computed on sales (+ partner purchases), then net profit subtracts that fee.
            DetailSubTotal = detailRows.Sum(r => r.GrossProfit) - totalExpenses - totalDeductions,
            DetailPercentFee = _detailPercentFeeDefault,
            DetailPercentFeeAmount = (detailRows.Sum(r => r.Amount) + totalPartnerPurchases) * (_detailPercentFeeDefault / 100m),
            DetailNetProfit = (detailRows.Sum(r => r.GrossProfit) - totalExpenses - totalDeductions) -
                              ((detailRows.Sum(r => r.Amount) + totalPartnerPurchases) * (_detailPercentFeeDefault / 100m)),
            DetailDeductionItems = deductionItems,
            DetailExpenseItems = expenseItems
        };
    }

    private int GetOutletOrderIndex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return int.MaxValue;
        var normalized = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        for (var i = 0; i < _outletOrderTokens.Length; i++)
        {
            var token = _outletOrderTokens[i];
            if (normalized.Contains(token) || token.Contains(normalized))
                return i;
        }

        return int.MaxValue;
    }
}
