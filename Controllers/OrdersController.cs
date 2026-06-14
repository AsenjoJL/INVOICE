using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services;
using HazelInvoice.Services.Orders;
using HazelInvoice.Services.Printing;
using HazelInvoice.Services.Settings;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Pricing;
using HazelInvoice.ViewModels;
using HazelInvoice.Helpers;
using HazelInvoice.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HazelInvoice.Controllers;

[Authorize]
public class OrdersController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IReceiptService _receiptService;
    private readonly IInvoicePrintManager _invoicePrintManager;
    private readonly IVegetableMatrixService _vegetableMatrixService;
    private readonly IVegetableMatrixTemplateService _vegetableMatrixTemplateService;
    private readonly IAppSettingStore _appSettings;
    private readonly ILookupCacheService _lookupCache;
    private readonly IAppCacheInvalidator _cacheInvalidator;
    private readonly IProductPricingService _productPricing;
    private readonly HashSet<string> _outletGroups;
    private readonly string[] _outletOrderTokens;
    private readonly Dictionary<string, string> _outletImportAliasMap;
    private readonly HashSet<string> _vegetableNonOutletHeaderKeys;
    private readonly string _vegetableTemplateProductHeader;
    private readonly string _vegetableTemplatePriceHeader;

    public OrdersController(
        ApplicationDbContext context,
        IReceiptService receiptService,
        IInvoicePrintManager invoicePrintManager,
        IVegetableMatrixService vegetableMatrixService,
        IVegetableMatrixTemplateService vegetableMatrixTemplateService,
        IAppSettingStore appSettings,
        ILookupCacheService lookupCache,
        IAppCacheInvalidator cacheInvalidator,
        IProductPricingService productPricing,
        IOptions<OperationsOptions> operations)
    {
        _context = context;
        _receiptService = receiptService;
        _invoicePrintManager = invoicePrintManager;
        _vegetableMatrixService = vegetableMatrixService;
        _vegetableMatrixTemplateService = vegetableMatrixTemplateService;
        _appSettings = appSettings;
        _lookupCache = lookupCache;
        _cacheInvalidator = cacheInvalidator;
        _productPricing = productPricing;
        _outletGroups = (operations.Value.OutletGroups ?? []).Where(g => !string.IsNullOrWhiteSpace(g))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _outletOrderTokens = (operations.Value.OutletSortTokens ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
        _outletImportAliasMap = operations.Value.OutletImportAliases ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _vegetableNonOutletHeaderKeys = (operations.Value.VegetableNonOutletHeaderKeys ?? [])
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _vegetableTemplateProductHeader = string.IsNullOrWhiteSpace(operations.Value.VegetableTemplateProductHeader)
            ? "Vegetables"
            : operations.Value.VegetableTemplateProductHeader;
        _vegetableTemplatePriceHeader = string.IsNullOrWhiteSpace(operations.Value.VegetableTemplatePriceHeader)
            ? "Price"
            : operations.Value.VegetableTemplatePriceHeader;
    }

    // GET: Orders/VegetableMatrix?date=yyyy-MM-dd&page=1&productPage=1&print=false&details=false
    public async Task<IActionResult> VegetableMatrix(DateTime? date, int page = 1, int productPage = 1, bool print = false, bool details = false)
    {
        var targetDate = NormalizeOrderDate(date);

        if (print)
        {
            var prep = await _invoicePrintManager.PrepareForInvoicePrintAsync();
            if (!prep.IsOk)
            {
                TempData["ErrorMessage"] = prep.Message ?? "Selected printer not found. Please update printer settings.";
                return RedirectToAction("Index", "PrinterSettings", new
                {
                    returnUrl = Url.Action(nameof(VegetableMatrix), new
                    {
                        date = targetDate.ToString("yyyy-MM-dd"),
                        page = 1,
                        productPage = 1,
                        print = true
                    })
                });
            }

            page = 1;
            productPage = 1;
        }

        var viewModel = await _vegetableMatrixService.GetAsync(new VegetableMatrixQueryOptions
        {
            Date = targetDate,
            OutletPage = page,
            ProductPage = productPage,
            Print = print,
            Details = details
        }, HttpContext.RequestAborted);

        var paperSize = (await _appSettings.GetAsync(PrinterSettingKeys.PaperSize, HttpContext.RequestAborted))?.Trim();
        ViewBag.PaperSize = string.Equals(paperSize, "A4", StringComparison.OrdinalIgnoreCase) ? "A4" : "Letter";

        return View(viewModel);
    }

    [HttpGet]
    public async Task<IActionResult> DownloadVegetableMatrixTemplate(DateTime? date)
    {
        var targetDate = NormalizeOrderDate(date);
        var bytes = await _vegetableMatrixTemplateService.BuildTemplateAsync(targetDate, HttpContext.RequestAborted);

        var fileName = $"VegetableOrderTemplate_{targetDate:yyyy-MM-dd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMatrix(VegetableMatrixViewModel model, bool doPrint = false, bool forceWeeklyPriceRecords = false)
    {
        if (model == null) return BadRequest("Model is null");
        model.Date = NormalizeOrderDate(model.Date);
        model.ProductPrices ??= new Dictionary<int, decimal>();
        var dayStart = model.Date.Date;
        var dayEnd = dayStart.AddDays(1);

        var matrixByCustomer = OrderImportHelpers.BuildMatrixInputsByCustomer(
            model.MatrixQuantities,
            out var affectedProductIdsSet,
            out var affectedCustomerIdsSet);

        var affectedCustomerIds = affectedCustomerIdsSet.ToList();

        var customers = await _context.Customers
            .Where(c => affectedCustomerIds.Contains(c.Id))
            .ToListAsync();
        var customerIdSet = customers.Select(c => c.Id).ToHashSet();
        var customerNameSet = customers.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var affectedProductIds = affectedProductIdsSet.ToList();

        if (model.ProductPrices.Any())
        {
            affectedProductIds = affectedProductIds
                .Union(model.ProductPrices.Keys)
                .Distinct()
                .ToList();
        }

        // Preload products (avoid FindAsync inside loops)
        var productMap = await _context.Products
            .AsNoTracking()
            .Where(p => affectedProductIds.Contains(p.Id) && p.IsActive)
            .Select(p => new { p.Id, p.Name, p.Unit, p.UnitCost, p.Markup, p.DeliveryFee })
            .ToDictionaryAsync(x => x.Id, x => x);
        var activeProductIds = productMap.Keys.ToHashSet();

        var applicableCostDate = WeeklyPriceCalendar.GetApplicablePriceDate(dayStart);
        var weeklyCostOverrides = applicableCostDate.HasValue
            ? (await _context.WeeklyPrices
                    .AsNoTracking()
                    .Where(w => affectedProductIds.Contains(w.ProductId) &&
                                w.EffectiveFrom <= applicableCostDate.Value &&
                                w.EffectiveTo >= applicableCostDate.Value &&
                                w.CostOverride != null)
                    .Select(w => new { w.ProductId, w.CostOverride, w.EffectiveFrom, w.Id })
                    .ToListAsync())
                .GroupBy(w => w.ProductId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(x => x.EffectiveFrom)
                          .ThenByDescending(x => x.Id)
                          .First()
                          .CostOverride!.Value)
            : new Dictionary<int, decimal>();

        decimal GetEffectiveCost(int pid)
        {
            if (weeklyCostOverrides.TryGetValue(pid, out var c)) return c;
            return productMap.TryGetValue(pid, out var prod) ? prod.UnitCost : 0m;
        }

        var (weekStart, weekEnd) = WeeklyPriceCalendar.GetWeekRange(model.Date);

        var applicableDayStart = WeeklyPriceCalendar.GetApplicablePriceDate(dayStart);
        var weeklyPriceSnapshot = applicableDayStart.HasValue
            ? await _context.WeeklyPrices
                .AsNoTracking()
                .Where(w => affectedProductIds.Contains(w.ProductId) &&
                            w.EffectiveFrom <= applicableDayStart.Value && w.EffectiveTo >= applicableDayStart.Value)
                .ToListAsync()
            : new List<WeeklyPrice>();

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

                // Self-heal duplicates: keep at most one unpaid receipt per outlet/day and
                // one unpaid line per product in that receipt.
                MergeDuplicateUnpaidReceiptsAndLines(existingReceipts);

                bool MatchesCustomer(Receipt r, Customer c) =>
                    (r.CustomerId.HasValue && r.CustomerId.Value == c.Id) ||
                    (!r.CustomerId.HasValue && r.CustomerName == c.Name);

                // 0) Update Weekly Prices (Price overrides) if provided
                await UpdateWeeklyPricesFromPricesAsync(model.Date, model.ProductPrices, forceWeeklyPriceRecords);

                foreach (var customer in customers)
                {
                    // 1) Calculate PAID quantities (Locked)
                    var paidLines = existingReceipts
                        .Where(r => MatchesCustomer(r, customer) && r.Status == PaymentStatus.Paid)
                        .SelectMany(r => r.Lines)
                        .Where(l => l.ProductId.HasValue)
                        .GroupBy(l => l.ProductId!.Value)
                        .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));

                    if (!matrixByCustomer.TryGetValue(customer.Id, out var inputs))
                    {
                        // No posted rows for this outlet/customer
                        continue;
                    }
                    var filteredInputs = inputs
                        .Where(kvp => activeProductIds.Contains(kvp.Key))
                        .ToDictionary(k => k.Key, v => v.Value);
                    if (filteredInputs.Count == 0)
                    {
                        continue;
                    }

                    // 2) Find Existing UNPAID receipt or Create NEW
                    var unpaidReceipt = existingReceipts.FirstOrDefault(r => 
                        MatchesCustomer(r, customer) && r.Status == PaymentStatus.Unpaid);

                    // If no unpaid receipt, do we need one?
                    if (unpaidReceipt == null)
                    {
                        // Check if any input requires an unpaid delta
                        bool needsReceipt = filteredInputs.Any(kvp => {
                            int pid = kvp.Key;
                            decimal target = kvp.Value;
                            decimal paid = paidLines.TryGetValue(pid, out decimal p) ? p : 0m;
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
                    // Remove lines tied to inactive products so they don't appear in deliveries
                    var inactiveLines = lines.Where(l => !activeProductIds.Contains(l.ProductId!.Value)).ToList();
                    if (inactiveLines.Count > 0)
                    {
                        _context.ReceiptLines.RemoveRange(inactiveLines);
                        foreach (var rem in inactiveLines) lines.Remove(rem);
                    }

                    foreach (var kvp in filteredInputs)
                    {
                        int pid = kvp.Key;
                        decimal targetQty = kvp.Value;
                        if (targetQty < 0) targetQty = 0m;

                        // Subtract Paid (Locked)
                        decimal paidQty = paidLines.TryGetValue(pid, out decimal pq) ? pq : 0m;
                        decimal unpaidQty = targetQty - paidQty;

                        // Cannot reduce below paid amount in this view (would require credit note or unpaying)
                        if (unpaidQty < 0) unpaidQty = 0m; 

                        decimal price = model.ProductPrices.TryGetValue(pid, out var pr) ? pr : 0m;
                        if (price <= 0)
                        {
                            weeklyPriceSnapshotMap.TryGetValue(pid, out var wpSnap);
                            if (wpSnap != null && wpSnap.DeliveryPrice > 0)
                            {
                                price = wpSnap.DeliveryPrice;
                            }
                            else if (wpSnap != null && wpSnap.DeliveryPrice == 0m && wpSnap.BasePrice == 0m)
                            {
                                price = 0m;
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
                                if (productMap.TryGetValue(pid, out var existingProduct) &&
                                    !string.IsNullOrWhiteSpace(existingProduct.Unit))
                                {
                                    // Keep receipt line unit in sync with current product unit for editable (unpaid) orders.
                                    line.Unit = existingProduct.Unit;
                                }
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

        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();

        if (TempData.ContainsKey("AfterImportShowReceipts"))
        {
            TempData.Remove("AfterImportShowReceipts");
            return RedirectToAction("Index", "Receipts", new { date = model.Date.ToString("yyyy-MM-dd") });
        }

        return RedirectToAction(nameof(VegetableMatrix), new
        {
            date = model.Date.ToString("yyyy-MM-dd"),
            page = model.CurrentPage,
            productPage = model.ProductPage,
            print = doPrint,
            details = model.ShowDetails
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportVegetableMatrixExcel(IFormFile? importFile, DateTime date, int page = 1, int productPage = 1, bool details = false)
    {
        var targetDate = NormalizeOrderDate(date);

        if (importFile == null || importFile.Length == 0)
        {
            TempData["ErrorMessage"] = "Please choose an Excel (.xlsx) file.";
            return RedirectToAction(nameof(VegetableMatrix), new { date = targetDate.ToString("yyyy-MM-dd"), page, productPage, details });
        }

        if (!string.Equals(Path.GetExtension(importFile.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            TempData["ErrorMessage"] = "Invalid file type. Please upload an .xlsx file.";
            return RedirectToAction(nameof(VegetableMatrix), new { date = targetDate.ToString("yyyy-MM-dd"), page, productPage, details });
        }

        if (importFile.Length > 20 * 1024 * 1024)
        {
            TempData["ErrorMessage"] = "File is too large. Maximum size is 20 MB.";
            return RedirectToAction(nameof(VegetableMatrix), new { date = targetDate.ToString("yyyy-MM-dd"), page, productPage, details });
        }

        await using var stream = new MemoryStream();
        await importFile.CopyToAsync(stream);

        SimpleXlsxSheet sheet;
        try
        {
            sheet = SimpleXlsxReader.ReadFirstSheet(stream);
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = $"Unable to read Excel file: {ex.Message}";
            return RedirectToAction(nameof(VegetableMatrix), new { date = targetDate.ToString("yyyy-MM-dd"), page, productPage, details });
        }

        var headerRow = FindHeaderRow(sheet, _vegetableTemplateProductHeader);
        if (headerRow <= 0)
        {
            TempData["ErrorMessage"] = $"Could not find header row. Expected a row containing '{_vegetableTemplateProductHeader}'.";
            return RedirectToAction(nameof(VegetableMatrix), new { date = targetDate.ToString("yyyy-MM-dd"), page, productPage, details });
        }

        var priceCol = FindColumnByHeader(sheet, headerRow, _vegetableTemplatePriceHeader);
        var productCol = FindColumnByHeader(sheet, headerRow, _vegetableTemplateProductHeader);
        if (productCol <= 0)
            productCol = 1;

        var outletColumns = new Dictionary<int, string>();
        for (var c = 1; c <= sheet.MaxCol; c++)
        {
            var raw = sheet.GetCell(headerRow, c);
            var key = OrderImportHelpers.NormalizeKey(raw);
            if (string.IsNullOrWhiteSpace(key) || _vegetableNonOutletHeaderKeys.Contains(key))
                continue;

            outletColumns[c] = raw.Trim();
        }

        if (outletColumns.Count == 0)
        {
            TempData["ErrorMessage"] = "No outlet columns found in the Excel file.";
            return RedirectToAction(nameof(VegetableMatrix), new { date = targetDate.ToString("yyyy-MM-dd"), page, productPage, details });
        }

        var customers = await _context.Customers
            .AsNoTracking()
            .Where(c => c.IsActive && c.GroupName != null && _outletGroups.Contains(c.GroupName))
            .ToListAsync();

        var customerMap = customers
            .GroupBy(c => OrderImportHelpers.NormalizeKey(c.Name))
            .ToDictionary(g => g.Key, g => g.First());

        var matchedOutletCols = new Dictionary<int, Customer>();
        var unmatchedOutletHeaders = new List<string>();
        foreach (var kvp in outletColumns)
        {
            if (TryResolveOutletByHeader(kvp.Value, customerMap, _outletImportAliasMap, out var customer))
            {
                matchedOutletCols[kvp.Key] = customer;
            }
            else
            {
                if (ColumnHasPositiveQuantities(sheet, headerRow, kvp.Key))
                {
                    unmatchedOutletHeaders.Add(kvp.Value);
                }
            }
        }

        if (matchedOutletCols.Count == 0)
        {
            TempData["ErrorMessage"] = "No Excel outlets matched your Outlet list.";
            return RedirectToAction(nameof(VegetableMatrix), new { date = targetDate.ToString("yyyy-MM-dd"), page, productPage, details });
        }

        var products = await _context.Products
            .AsNoTracking()
            .ToListAsync();

        // Keep import matching aligned with SaveMatrix/receipt creation by using
        // the same active product set that can actually produce receipt lines.
        var activeProducts = products.Where(p => p.IsActive).ToList();
        var inactiveProducts = products.Where(p => !p.IsActive).ToList();

        // Map by normalized Name and SKU to improve matching coverage.
        var activeProductMap = OrderImportHelpers.BuildProductLookup(activeProducts);
        var inactiveProductMap = OrderImportHelpers.BuildProductLookup(inactiveProducts);
        var activeProductIds = activeProducts.Select(p => p.Id).ToList();
        var currentPriceMap = await _productPricing.GetEffectivePricesAsync(
            activeProductIds,
            targetDate,
            HttpContext.RequestAborted);

        var matrix = new Dictionary<string, decimal>();
        var prices = new Dictionary<int, decimal>();
        var matchedProductIds = new HashSet<int>();
        var unmatchedProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inactiveMatchedProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fractionalQtyCount = 0;

        for (var r = headerRow + 1; r <= sheet.MaxRow; r++)
        {
            var productNameRaw = sheet.GetCell(r, productCol);
            if (string.IsNullOrWhiteSpace(productNameRaw))
                continue;

            var nameKey = OrderImportHelpers.NormalizeKey(productNameRaw);
            if (string.IsNullOrWhiteSpace(nameKey) || nameKey == "grandtotal")
                continue;

            if (!OrderImportHelpers.TryResolveProductByName(productNameRaw, activeProductMap, out var product))
            {
                if (OrderImportHelpers.TryResolveProductByName(productNameRaw, inactiveProductMap, out _))
                {
                    inactiveMatchedProducts.Add(productNameRaw.Trim());
                }
                else
                {
                    unmatchedProducts.Add(productNameRaw.Trim());
                }

                continue;
            }

            matchedProductIds.Add(product.Id);
            var rowQuantities = new List<(int CustomerId, decimal Qty)>();

            foreach (var oc in matchedOutletCols)
            {
                var qtyRaw = sheet.GetCell(r, oc.Key);
                var qty = OrderImportHelpers.TryParseQuantityLoose(qtyRaw, out var parsedQty) ? parsedQty : 0m;
                if (qty != decimal.Truncate(qty))
                    fractionalQtyCount++;
                if (qty < 0) qty = 0m;

                rowQuantities.Add((oc.Value.Id, qty));
            }

            foreach (var rowQty in rowQuantities)
            {
                var key = $"{product.Id}_{rowQty.CustomerId}";
                if (matrix.TryGetValue(key, out var existingQty))
                    matrix[key] = existingQty + rowQty.Qty;
                else
                    matrix[key] = rowQty.Qty;
            }

            var rowHasPositiveQuantity = rowQuantities.Any(x => x.Qty > 0);
            if (priceCol > 0 &&
                SimpleXlsxReader.TryParseDecimal(sheet.GetCell(r, priceCol), out var price) &&
                price >= 0 &&
                ShouldCaptureVegetableTemplatePrice(product.Id, price, rowHasPositiveQuantity, currentPriceMap) &&
                (!prices.ContainsKey(product.Id) || rowHasPositiveQuantity))
            {
                prices[product.Id] = price;
            }
        }

        if (matrix.Count == 0)
        {
            TempData["ErrorMessage"] = "No matched product/outlet quantities were found to import.";
            return RedirectToAction(nameof(VegetableMatrix), new { date = targetDate.ToString("yyyy-MM-dd"), page, productPage, details });
        }

        var vm = new VegetableMatrixViewModel
        {
            Date = targetDate,
            CurrentPage = page,
            ProductPage = productPage,
            ShowDetails = details,
            MatrixQuantities = matrix,
            ProductPrices = prices
        };

        var matchedOutletCount = matchedOutletCols.Values.Select(x => x.Id).Distinct().Count();
        var matchedProductCount = matchedProductIds.Count;
        var unmatchedNote = unmatchedOutletHeaders.Count > 0
            ? $" Unmatched outlets: {string.Join(", ", unmatchedOutletHeaders.Take(8))}{(unmatchedOutletHeaders.Count > 8 ? "..." : "")}."
            : string.Empty;
        var unmatchedProductsNote = unmatchedProducts.Count > 0
            ? $" Unmatched products: {string.Join(", ", unmatchedProducts.Take(8))}{(unmatchedProducts.Count > 8 ? "..." : "")}."
            : string.Empty;
        var inactiveProductsNote = inactiveMatchedProducts.Count > 0
            ? $" Inactive products skipped: {string.Join(", ", inactiveMatchedProducts.Take(8))}{(inactiveMatchedProducts.Count > 8 ? "..." : "")}."
            : string.Empty;
        var fractionalNote = fractionalQtyCount > 0
            ? $" Fractional quantities detected: {fractionalQtyCount} cells."
            : string.Empty;
        TempData["SuccessMessage"] = $"Excel imported, weekly prices updated, and receipts updated for {targetDate:MMM dd, yyyy}: {matchedProductCount} products, {matchedOutletCount} outlets.{unmatchedNote}{unmatchedProductsNote}{inactiveProductsNote}{fractionalNote}";
        TempData["AfterImportShowReceipts"] = "true";

        return await SaveMatrix(vm, doPrint: false, forceWeeklyPriceRecords: true);
    }

    // OUTLET ORDER GET
    public async Task<IActionResult> VegetableOutletOrder(DateTime? date, int? customerId, bool allOutlets = false)
    {
        var targetDate = NormalizeOrderDate(date);
        var dayStart = targetDate;
        var dayEnd = targetDate.AddDays(1);

        var customers = (await _lookupCache.GetActiveCustomersAsync(HttpContext.RequestAborted)).ToList();
        customers = customers
            .OrderBy(c => GetOutletOrderIndex(c.Name))
            .ThenBy(c => c.Name)
            .ToList();

        if (!customerId.HasValue && customers.Any())
            customerId = customers.First().Id;

        var products = (await _lookupCache.GetActiveProductsAsync(HttpContext.RequestAborted)).ToList();

        var productIds = products.Select(p => p.Id).ToList();
        var priceMap = await _productPricing.GetEffectivePricesAsync(
            productIds,
            targetDate,
            HttpContext.RequestAborted);

        var productPrices = new Dictionary<int, decimal>(products.Count);
        foreach (var p in products)
        {
            productPrices[p.Id] = priceMap.TryGetValue(p.Id, out var price)
                ? price.DeliveryPrice
                : p.UnitCost + p.Markup + p.DeliveryFee;
        }

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

                        if (line.Price > 0 &&
                            priceMap.TryGetValue(line.ProductId.Value, out var linePrice) &&
                            !linePrice.HasWeeklyPrice &&
                            !linePrice.IsResetDay)
                        {
                            productPrices[line.ProductId.Value] = line.Price;
                        }
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

                            if (line.Price > 0 &&
                                priceMap.TryGetValue(line.ProductId.Value, out var linePrice) &&
                                !linePrice.HasWeeklyPrice &&
                                !linePrice.IsResetDay)
                            {
                                productPrices[line.ProductId.Value] = line.Price;
                            }
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
        model.Date = NormalizeOrderDate(model.Date);
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
             var customers = (await _lookupCache.GetActiveCustomersAsync(HttpContext.RequestAborted)).ToList();
             var products = (await _lookupCache.GetActiveProductsAsync(HttpContext.RequestAborted)).ToList();
             
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

                // Self-heal duplicates for this outlet/day before applying posted quantities.
                MergeDuplicateUnpaidReceiptsAndLines(allReceipts);
                    
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
                    decimal target = kvp.Value;
                    decimal paid = paidLines.TryGetValue(pid, out decimal p) ? p : 0m;
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

                        var priceMap = await _productPricing.GetEffectivePricesAsync(
                            usedProductIds,
                            model.Date,
                            HttpContext.RequestAborted);

                        foreach (var kvp in quantities)
                        {
                            int productId = kvp.Key;
                            decimal targetQty = kvp.Value;
                            if (targetQty < 0) targetQty = 0m;
                            
                            // Delta
                            decimal paidQty = paidLines.TryGetValue(productId, out decimal pq) ? pq : 0m;
                            decimal unpaidQty = targetQty - paidQty;
                            if (unpaidQty < 0) unpaidQty = 0m;

                            decimal price = model.ProductPrices.TryGetValue(productId, out var pr) ? pr : 0m;
                            if (price <= 0)
                            {
                                price = priceMap.TryGetValue(productId, out var effectivePrice)
                                    ? effectivePrice.DeliveryPrice
                                    : prodMap.TryGetValue(productId, out var prodFromMap)
                                        ? prodFromMap.UnitCost + prodFromMap.Markup + prodFromMap.DeliveryFee
                                        : 0m;
                            }

                            var costSnapshot = priceMap.TryGetValue(productId, out var effectiveCost)
                                ? effectiveCost.Cost
                                : prodMap.TryGetValue(productId, out var fallbackProduct)
                                    ? fallbackProduct.UnitCost
                                    : 0m;

                            var existingLine = currentLines.FirstOrDefault(l => l.ProductId == productId);
                            if (existingLine != null)
                            {
                                if (unpaidQty > 0)
                                {
                                    existingLine.Quantity = unpaidQty;
                                    existingLine.Price = price;
                                    existingLine.Amount = unpaidQty * price;
                                    if (prodMap.TryGetValue(productId, out var existingProduct) &&
                                        !string.IsNullOrWhiteSpace(existingProduct.Unit))
                                    {
                                        // Keep receipt line unit in sync with current product unit for editable (unpaid) orders.
                                        existingLine.Unit = existingProduct.Unit;
                                    }
                                    // Update snapshot
                                    existingLine.CostPriceSnapshot = costSnapshot;
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
                                    CostPriceSnapshot = costSnapshot
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
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();

        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();

        return RedirectToAction(nameof(VegetableOutletOrder), new
        {
            date = model.Date.ToString("yyyy-MM-dd"),
            customerId = model.SelectedCustomerId
        });
    }

    // GET: Orders/ReceiptList
    // Back-compat endpoint used by the Receipts page shortcut.
    // Defaults to the normalized delivery date (tomorrow if not specified).
    public async Task<IActionResult> ReceiptList(DateTime? date, string status = "Unpaid")
    {
        var targetDate = NormalizeOrderDate(date);

        var normalized = (status ?? string.Empty).Trim();
        var targetStatus = normalized.Equals("Paid", StringComparison.OrdinalIgnoreCase)
            ? PaymentStatus.Paid
            : normalized.Equals("Partial", StringComparison.OrdinalIgnoreCase)
                ? PaymentStatus.Partial
                : PaymentStatus.Unpaid;

        return await GetOrdersByStatus(targetDate, targetStatus);
    }

    // GET: Orders/UnpaidOrders
    public async Task<IActionResult> UnpaidOrders(DateTime? date)
    {
        return await GetOrdersByStatus(date, PaymentStatus.Unpaid);
    }

    // GET: Orders/PaidOrders
    public async Task<IActionResult> PaidOrders(DateTime? date)
    {
        return await GetOrdersByStatus(date, PaymentStatus.Paid);
    }

    private async Task<IActionResult> GetOrdersByStatus(DateTime? date, PaymentStatus status)
    {
        var query = _context.Receipts
            .AsNoTracking()
            .Include(r => r.Lines)
            .Where(r => r.Status != PaymentStatus.Void);

        query = status switch
        {
            PaymentStatus.Paid => query.Where(r => r.TotalAmount > 0m && r.PaidAmount >= r.TotalAmount),
            PaymentStatus.Partial => query.Where(r => r.PaidAmount > 0m && r.PaidAmount < r.TotalAmount),
            _ => query.Where(r => r.PaidAmount <= 0m)
        };

        DateTime? targetDate = null;
        if (date.HasValue)
        {
            targetDate = NormalizeOrderDate(date);
            var dayStart = targetDate.Value.Date;
            var dayEnd = dayStart.AddDays(1);
            query = query.Where(r => r.Date >= dayStart && r.Date < dayEnd);
        }

        var receipts = await query
            .OrderByDescending(r => r.Date)
            .ThenBy(r => r.CustomerName)
            .ToListAsync();

        foreach (var receipt in receipts)
            receipt.Status = ReceiptPaymentStatus.Resolve(receipt.TotalAmount, receipt.PaidAmount);

        var model = new HazelInvoice.ViewModels.ReceiptListViewModel
        {
            Date = targetDate,
            IsDateFiltered = targetDate.HasValue,
            Status = status,
            Receipts = receipts,
            DateGroups = receipts
                .GroupBy(r => r.Date.Date)
                .OrderByDescending(g => g.Key)
                .Select(g => new HazelInvoice.ViewModels.ReceiptDateGroupViewModel
                {
                    Date = g.Key,
                    Receipts = g.OrderBy(r => r.CustomerName).ThenBy(r => r.ReceiptNumber).ToList(),
                    TotalAmount = g.Sum(r => r.TotalAmount),
                    TotalItems = g.Sum(r => r.Lines.Sum(l => l.Quantity))
                })
                .ToList(),
            GrandTotal = receipts.Sum(r => r.TotalAmount)
        };

        return View("ReceiptList", model);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAsPaid(int id, DateTime returnDate)
    {
        await _receiptService.MarkReceiptPaidAsync(id, User.Identity?.Name, HttpContext.RequestAborted);
        
        return RedirectToAction("PaidOrders", new { date = returnDate.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkDateGroupAsPaid(DateTime deliveryDate)
    {
        var dayStart = deliveryDate.Date;
        var dayEnd = dayStart.AddDays(1);

        var receiptIds = await _context.Receipts
            .Where(r => r.Date >= dayStart && r.Date < dayEnd && r.Status == PaymentStatus.Unpaid)
            .Select(r => r.Id)
            .ToListAsync();

        await _receiptService.MarkReceiptsPaidAsync(receiptIds, User.Identity?.Name, HttpContext.RequestAborted);

        return RedirectToAction("PaidOrders", new { date = dayStart.ToString("yyyy-MM-dd") });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoPaid(int id, DateTime returnDate)
    {
        await _receiptService.RevertReceiptToUnpaidAsync(id, HttpContext.RequestAborted);

        return RedirectToAction("UnpaidOrders", new { date = returnDate.ToString("yyyy-MM-dd") });
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

        if (status == "Paid") query = query.Where(r => r.TotalAmount > 0m && r.PaidAmount >= r.TotalAmount);
        else if (status == "Unpaid") query = query.Where(r => r.PaidAmount <= 0m);
        
        var receipts = await query.ToListAsync();

        // 1. KPIs
        var totalSales = receipts.Sum(r => r.TotalAmount);
        
        var totalPaid = receipts.Where(r => r.TotalAmount > 0m && r.PaidAmount >= r.TotalAmount).Sum(r => r.TotalAmount);
        var totalUnpaid = receipts.Where(r => r.PaidAmount <= 0m).Sum(r => r.TotalAmount);
        var count = receipts.Count;

        // 2. Daily Trend
        var daily = receipts
            .GroupBy(r => r.Date.Date)
            .Select(g => new DailyTrendDto {
                Date = g.Key,
                TotalAmount = g.Sum(r => r.TotalAmount),
                PaidAmount = g.Where(r => r.TotalAmount > 0m && r.PaidAmount >= r.TotalAmount).Sum(r => r.TotalAmount),
                UnpaidAmount = g.Where(r => r.PaidAmount <= 0m).Sum(r => r.TotalAmount)
            })
            .OrderBy(d => d.Date)
            .ToList();

        // 3. Outlet Summary
        var outletStats = receipts
            .GroupBy(r => !string.IsNullOrEmpty(r.CustomerName) ? r.CustomerName : "Walk-in")
            .Select(g => new OutletSummaryDto {
                OutletName = g.Key,
                TotalAmount = g.Sum(r => r.TotalAmount),
                PaidAmount = g.Where(r => r.TotalAmount > 0m && r.PaidAmount >= r.TotalAmount).Sum(r => r.TotalAmount),
                UnpaidAmount = g.Where(r => r.PaidAmount <= 0m).Sum(r => r.TotalAmount)
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

        if (status == "Paid") lineQuery = lineQuery.Where(l => l.Receipt!.TotalAmount > 0m && l.Receipt.PaidAmount >= l.Receipt.TotalAmount);
        else if (status == "Unpaid") lineQuery = lineQuery.Where(l => l.Receipt!.PaidAmount <= 0m);

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

    private static int FindHeaderRow(SimpleXlsxSheet sheet, string productHeader)
    {
        var normalizedHeader = OrderImportHelpers.NormalizeKey(productHeader);
        for (var r = 1; r <= Math.Min(sheet.MaxRow, 50); r++)
        {
            if (OrderImportHelpers.NormalizeKey(sheet.GetCell(r, 1)) == normalizedHeader)
                return r;
        }

        return -1;
    }

    private static int FindColumnByHeader(SimpleXlsxSheet sheet, int headerRow, string header)
    {
        var key = OrderImportHelpers.NormalizeKey(header);
        for (var c = 1; c <= sheet.MaxCol; c++)
        {
            if (OrderImportHelpers.NormalizeKey(sheet.GetCell(headerRow, c)) == key)
                return c;
        }

        return -1;
    }

    private static bool TryResolveOutletByHeader(
        string header,
        Dictionary<string, Customer> customerMap,
        Dictionary<string, string> outletImportAliasMap,
        out Customer customer)
    {
        customer = default!;
        var key = OrderImportHelpers.NormalizeKey(header);
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (customerMap.TryGetValue(key, out var matchedCustomer) && matchedCustomer != null)
        {
            customer = matchedCustomer;
            return true;
        }

        if (outletImportAliasMap.TryGetValue(key, out var aliasKey) &&
            customerMap.TryGetValue(aliasKey, out matchedCustomer) &&
            matchedCustomer != null)
        {
            customer = matchedCustomer;
            return true;
        }

        // Fallback: unique contains-match (e.g., "cebukit" vs "cebukitchen")
        var containMatches = customerMap
            .Where(kvp => kvp.Key.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                          key.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value)
            .DistinctBy(c => c.Id)
            .ToList();

        if (containMatches.Count == 1)
        {
            customer = containMatches[0];
            return true;
        }

        return false;
    }

    private static bool ColumnHasPositiveQuantities(SimpleXlsxSheet sheet, int headerRow, int columnIndex)
    {
        for (var row = headerRow + 1; row <= sheet.MaxRow; row++)
        {
            var raw = sheet.GetCell(row, columnIndex);
            if (OrderImportHelpers.TryParseQuantityLoose(raw, out var qty) && qty > 0)
                return true;
        }

        return false;
    }

    private int GetOutletOrderIndex(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return int.MaxValue;
        var normalized = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        for (int i = 0; i < _outletOrderTokens.Length; i++)
        {
            var token = _outletOrderTokens[i];
            if (normalized.Contains(token) || token.Contains(normalized))
                return i;
        }
        return int.MaxValue;
    }

    private static DateTime NormalizeOrderDate(DateTime? date)
    {
        if (!date.HasValue || date.Value == default)
            return BusinessDate.Tomorrow();

        return date.Value.Date;
    }

    private async Task UpdateWeeklyPricesFromPricesAsync(DateTime date, Dictionary<int, decimal>? postedPrices, bool forceCreateWeeklyRecords = false)
    {
        if (postedPrices == null || postedPrices.Count == 0) return;

        var (weekStart, weekEnd) = WeeklyPriceCalendar.GetWeekRange(date);
        var applicableDate = WeeklyPriceCalendar.GetApplicablePriceDate(date) ?? weekStart;

        var pricePids = postedPrices.Keys.ToList();

        var products = await _context.Products
            .AsNoTracking()
            .Where(p => pricePids.Contains(p.Id))
            .Select(p => new { p.Id, p.UnitCost, p.Markup, p.DeliveryFee })
            .ToListAsync();

        var productMap = products.ToDictionary(p => p.Id, p => p);

        var wps = await _context.WeeklyPrices
            .Where(w => pricePids.Contains(w.ProductId) &&
                        w.EffectiveFrom <= applicableDate && w.EffectiveTo >= applicableDate)
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

            var matchesCurrentWeeklyPrice = Math.Abs(postedPrice - defaultPrice) < 0.005m;
            if (matchesCurrentWeeklyPrice && (wp != null || !forceCreateWeeklyRecords))
                continue;

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
        _cacheInvalidator.InvalidateWeeklyPrices();
        _cacheInvalidator.InvalidateProducts();
    }

    private static bool ShouldCaptureVegetableTemplatePrice(
        int productId,
        decimal templatePrice,
        bool rowHasPositiveQuantity,
        IReadOnlyDictionary<int, EffectiveProductPrice> currentPriceMap)
    {
        if (templatePrice <= 0m)
            return false;

        if (rowHasPositiveQuantity)
            return true;

        // The Vegetable template is also allowed to fill products that are
        // currently zero in Price Setup/Products. Avoid capturing every
        // prefilled non-zero row when the product was not ordered.
        return currentPriceMap.TryGetValue(productId, out var currentPrice) &&
               currentPrice.DeliveryPrice <= 0m &&
               !currentPrice.IsResetDay;
    }

    private static Dictionary<int, Dictionary<int, decimal>> BuildMatrixInputsByCustomer(
        Dictionary<string, decimal> rawMatrix,
        out HashSet<int> productIds,
        out HashSet<int> customerIds)
    {
        var byCustomer = new Dictionary<int, Dictionary<int, decimal>>();
        productIds = new HashSet<int>();
        customerIds = new HashSet<int>();

        if (rawMatrix == null || rawMatrix.Count == 0)
            return byCustomer;

        foreach (var kvp in rawMatrix)
        {
            if (!TryParseMatrixKey(kvp.Key, out var productId, out var customerId))
                continue;

            productIds.Add(productId);
            customerIds.Add(customerId);

            if (!byCustomer.TryGetValue(customerId, out var productMap))
            {
                productMap = new Dictionary<int, decimal>();
                byCustomer[customerId] = productMap;
            }

            // Accumulate quantities when the same product appears multiple times for an outlet (e.g., duplicate rows in Excel)
            if (productMap.ContainsKey(productId))
                productMap[productId] += kvp.Value;
            else
                productMap[productId] = kvp.Value;
        }

        return byCustomer;
    }

    private static bool TryParseMatrixKey(string? key, out int productId, out int customerId)
    {
        productId = 0;
        customerId = 0;

        if (string.IsNullOrWhiteSpace(key))
            return false;

        var sep = key.IndexOf('_');
        if (sep <= 0 || sep >= key.Length - 1)
            return false;

        var left = key[..sep];
        var right = key[(sep + 1)..];

        return int.TryParse(left, out productId) && int.TryParse(right, out customerId);
    }

    private void MergeDuplicateUnpaidReceiptsAndLines(List<Receipt> receipts)
    {
        if (receipts.Count == 0) return;

        static string CustomerKey(Receipt r)
        {
            if (r.CustomerId.HasValue) return $"ID:{r.CustomerId.Value}";
            return $"NAME:{(r.CustomerName ?? string.Empty).Trim().ToLowerInvariant()}";
        }

        var unpaidGroups = receipts
            .Where(r => r.Status == PaymentStatus.Unpaid)
            .GroupBy(CustomerKey)
            .ToList();

        foreach (var group in unpaidGroups)
        {
            var ordered = group
                .OrderBy(r => r.Id)
                .ToList();

            var keeper = ordered.First();

            // Merge additional unpaid receipts (same outlet/day) into keeper.
            foreach (var duplicateReceipt in ordered.Skip(1))
            {
                var duplicateLines = duplicateReceipt.Lines
                    .Where(l => l.ProductId.HasValue)
                    .ToList();

                foreach (var dupLine in duplicateLines)
                {
                    var existingLine = keeper.Lines.FirstOrDefault(l => l.ProductId == dupLine.ProductId);
                    if (existingLine == null)
                    {
                        keeper.Lines.Add(new ReceiptLine
                        {
                            ProductId = dupLine.ProductId,
                            ItemName = dupLine.ItemName,
                            Unit = dupLine.Unit,
                            Quantity = dupLine.Quantity,
                            Price = dupLine.Price,
                            Amount = dupLine.Amount,
                            CostPriceSnapshot = dupLine.CostPriceSnapshot
                        });
                    }
                    else
                    {
                        existingLine.Quantity += dupLine.Quantity;
                        existingLine.Amount += dupLine.Amount;
                        if (existingLine.CostPriceSnapshot <= 0 && dupLine.CostPriceSnapshot > 0)
                            existingLine.CostPriceSnapshot = dupLine.CostPriceSnapshot;
                        if (existingLine.Quantity > 0)
                            existingLine.Price = existingLine.Amount / existingLine.Quantity;
                    }
                }

                _context.ReceiptLines.RemoveRange(duplicateReceipt.Lines);
                _context.Receipts.Remove(duplicateReceipt);
                receipts.Remove(duplicateReceipt);
            }

            // Within keeper, merge duplicate lines by product.
            var duplicateLineGroups = keeper.Lines
                .Where(l => l.ProductId.HasValue)
                .GroupBy(l => l.ProductId!.Value)
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var lineGroup in duplicateLineGroups)
            {
                var canonical = lineGroup.First();
                foreach (var extra in lineGroup.Skip(1))
                {
                    canonical.Quantity += extra.Quantity;
                    canonical.Amount += extra.Amount;
                    if (canonical.CostPriceSnapshot <= 0 && extra.CostPriceSnapshot > 0)
                        canonical.CostPriceSnapshot = extra.CostPriceSnapshot;
                    _context.ReceiptLines.Remove(extra);
                    keeper.Lines.Remove(extra);
                }

                if (canonical.Quantity > 0)
                    canonical.Price = canonical.Amount / canonical.Quantity;
            }

            keeper.TotalAmount = keeper.Lines.Sum(l => l.Amount);
        }
    }
}
