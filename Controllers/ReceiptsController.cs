using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services;
using HazelInvoice.Services.Printing;
using HazelInvoice.Services.Receipts;
using HazelInvoice.Services.Settings;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Pricing;
using HazelInvoice.ViewModels;
using HazelInvoice.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class ReceiptsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IReceiptService _receiptService;
    private readonly IInvoicePrintManager _invoicePrintManager;
    private readonly IReceiptQueryService _receiptQuery;
    private readonly IAppSettingStore _appSettings;
    private readonly ILookupCacheService _lookupCache;
    private readonly IAppCacheInvalidator _cacheInvalidator;
    private readonly IProductPricingService _productPricing;

    public ReceiptsController(
        ApplicationDbContext context,
        IReceiptService receiptService,
        IInvoicePrintManager invoicePrintManager,
        IReceiptQueryService receiptQuery,
        IAppSettingStore appSettings,
        ILookupCacheService lookupCache,
        IAppCacheInvalidator cacheInvalidator,
        IProductPricingService productPricing)
    {
        _context = context;
        _receiptService = receiptService;
        _invoicePrintManager = invoicePrintManager;
        _receiptQuery = receiptQuery;
        _appSettings = appSettings;
        _lookupCache = lookupCache;
        _cacheInvalidator = cacheInvalidator;
        _productPricing = productPricing;
    }

    // GET: Receipts
    public async Task<IActionResult> Index(string? q, DateTime? date = null, DateTime? dateFrom = null, DateTime? dateTo = null, string? viewMode = null, int page = 1, int pageSize = 50)
    {
        var vm = await _receiptQuery.QueryAsync(new ReceiptQueryOptions
        {
            Query = q,
            Date = date?.Date,
            DateFrom = dateFrom?.Date,
            DateTo = dateTo?.Date,
            Page = page,
            PageSize = pageSize,
            UnpaidOnly = false
        }, HttpContext.RequestAborted);
        ViewData["TitleHeader"] = string.Equals(viewMode, "sales", StringComparison.OrdinalIgnoreCase)
            ? "Sales"
            : "Receipts & Deliveries";
        ViewData["ViewMode"] = viewMode;
        return View(vm);
    }

    // GET: Receipts/Unpaid
    public async Task<IActionResult> Unpaid(string? q, DateTime? date = null, DateTime? dateFrom = null, DateTime? dateTo = null, string? viewMode = null, int page = 1, int pageSize = 50)
    {
        var vm = await _receiptQuery.QueryAsync(new ReceiptQueryOptions
        {
            Query = q,
            Date = date?.Date,
            DateFrom = dateFrom?.Date,
            DateTo = dateTo?.Date,
            Page = page,
            PageSize = pageSize,
            UnpaidOnly = true
        }, HttpContext.RequestAborted);

        ViewData["TitleHeader"] = string.Equals(viewMode, "sales", StringComparison.OrdinalIgnoreCase)
            ? "Unpaid Sales"
            : "Unpaid Receipts";
        ViewData["ViewMode"] = viewMode;
        return View("Index", vm);
    }

    // GET: Receipts/Create
    public async Task<IActionResult> Create(DateTime? date)
    {
        var deliveryDate = NormalizeReceiptDate(date ?? DateTime.Today.AddDays(1));
        var receipt = new Receipt
        {
            Date = deliveryDate,
            Type = ReceiptType.Delivery,
            PaidAmount = 0m
        };

        await PopulateReceiptFormLookupsAsync(receipt.Date, HttpContext.RequestAborted);

        return View(receipt);
    }

    // POST: Receipts/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Receipt receipt, string action)
    {
        ModelState.Remove("ReceiptNumber"); // Generated server-side
        ModelState.Remove("CustomerName"); // Set server-side from CustomerId

        if (ModelState.IsValid)
        {
            if (receipt.CustomerId == null)
            {
                ModelState.AddModelError("CustomerId", "Please select a customer.");
            }
            else
            {
                var customer = await _context.Customers.FindAsync(receipt.CustomerId.Value);
                if (customer == null)
                {
                    ModelState.AddModelError("CustomerId", "Customer not found.");
                }
                else
                {
                    receipt.CustomerName = customer.Name;

                    if (string.IsNullOrWhiteSpace(receipt.CustomerAddress))
                        receipt.CustomerAddress = customer.Address;

                    if (string.IsNullOrWhiteSpace(receipt.ContactNumber))
                        receipt.ContactNumber = customer.ContactNumber;
                }
            }

            // Trap duplicate lines in the same submitted receipt (same product/service repeated).
            var duplicateLinesInRequest = FindDuplicateLineNamesInRequest(receipt);
            if (duplicateLinesInRequest.Count > 0)
            {
                ModelState.AddModelError(
                    string.Empty,
                    $"Duplicate item lines in this sale: {string.Join(", ", duplicateLinesInRequest)}. " +
                    "Use only one line per product/service.");
            }

            // Trap duplicate order against existing same-day sales for same outlet.
            if (ModelState.IsValid && receipt.CustomerId.HasValue)
            {
                var deliveryDate = NormalizeReceiptDate(receipt.Date);
                var duplicateItems = await FindSameDayDuplicateItemsAsync(
                    receipt.CustomerId.Value,
                    receipt.CustomerName,
                    deliveryDate,
                    receipt.Lines);

                if (duplicateItems.Count > 0)
                {
                    ModelState.AddModelError(
                        string.Empty,
                        $"Duplicate order detected for this outlet on {deliveryDate:MMM dd, yyyy}. Existing items: {string.Join(", ", duplicateItems)}. " +
                        "Please edit the existing order instead of adding duplicates.");
                }
            }
        }

        if (ModelState.IsValid)
        {
            var deliveryDate = NormalizeReceiptDate(receipt.Date);

            // Generate Number
            receipt.ReceiptNumber = await _receiptService.GenerateNextReceiptNumberAsync();
            receipt.CreatedById = User.Identity?.Name; // Or GetUserId
            receipt.Date = deliveryDate;

            // Snapshot cost + normalize line values so Profit & Sales is stable even if product costs change later.
            var productIds = receipt.Lines.Where(l => l.ProductId.HasValue).Select(l => l.ProductId!.Value).Distinct().ToList();
            var serviceIds = receipt.Lines.Where(l => l.ServiceId.HasValue).Select(l => l.ServiceId!.Value).Distinct().ToList();

            var products = await _context.Products
                .AsNoTracking()
                .Where(p => productIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.Unit, p.UnitCost })
                .ToDictionaryAsync(x => x.Id, x => x);

            var services = await _context.Services
                .AsNoTracking()
                .Where(s => serviceIds.Contains(s.Id))
                .Select(s => new { s.Id, s.Name, s.Cost })
                .ToDictionaryAsync(x => x.Id, x => x);

            var day = receipt.Date.Date;
            var priceMap = await _productPricing.GetEffectivePricesAsync(
                productIds,
                day,
                HttpContext.RequestAborted);

            decimal GetEffectiveCost(int pid)
            {
                if (priceMap.TryGetValue(pid, out var price)) return price.Cost;
                return products.TryGetValue(pid, out var p) ? p.UnitCost : 0m;
            }

            foreach (var line in receipt.Lines)
            {
                // Normalize snapshots for printing/reporting
                if (line.ProductId.HasValue && products.TryGetValue(line.ProductId.Value, out var p))
                {
                    line.ItemName = p.Name;
                    if (!string.IsNullOrWhiteSpace(p.Unit)) line.Unit = p.Unit;
                    line.CostPriceSnapshot = GetEffectiveCost(line.ProductId.Value);
                }
                else if (line.ServiceId.HasValue && services.TryGetValue(line.ServiceId.Value, out var s))
                {
                    line.ItemName = s.Name;
                    line.Unit = "srv";
                    // Use configured service cost if available; otherwise 0.
                    line.CostPriceSnapshot = s.Cost ?? 0m;
                }

                // Always recompute Amount from Qty/Price (UI is readonly but don't trust the client).
                line.Amount = Math.Round(line.Quantity * line.Price, 2, MidpointRounding.AwayFromZero);
            }

            // Recalculate totals to be safe
            receipt.TotalAmount = receipt.Lines.Sum(l => l.Amount);
            receipt.PaidAmount = NormalizePaidAmount(receipt.PaidAmount, receipt.TotalAmount);
            // Handle Payment Status
            if (receipt.PaidAmount >= receipt.TotalAmount) receipt.Status = PaymentStatus.Paid;
            else if (receipt.PaidAmount > 0) receipt.Status = PaymentStatus.Partial;
            else receipt.Status = PaymentStatus.Unpaid;

            _context.Add(receipt);
            
            // Deduct Stock
            foreach (var line in receipt.Lines)
            {
                if (line.ProductId.HasValue)
                {
                    var movement = new ProductStockMovement
                    {
                        ProductId = line.ProductId.Value,
                        Date = receipt.Date,
                        Quantity = -line.Quantity, // Deduct
                        Type = "Sale",
                        Reference = receipt.ReceiptNumber,
                        RecordedById = receipt.CreatedById
                    };
                    _context.ProductStockMovements.Add(movement);
                }
            }

            var initialPaidAmount = receipt.PaidAmount;
            receipt.PaidAmount = 0m;
            receipt.Status = PaymentStatus.Unpaid;

            await _context.SaveChangesAsync();

            if (initialPaidAmount > 0m)
            {
                await _receiptService.RecordReceiptPaymentAsync(
                    receipt.Id,
                    initialPaidAmount,
                    User.Identity?.Name,
                    HttpContext.RequestAborted);
            }

            _cacheInvalidator.InvalidateDashboard();
            _cacheInvalidator.InvalidateProfitReports();

            if (action == "Print")
            {
                return RedirectToAction(nameof(Print), new { id = receipt.Id });
            }
            return RedirectToAction(nameof(Index));
        }
        
        await PopulateReceiptFormLookupsAsync(receipt.Date, HttpContext.RequestAborted);

        return View(receipt);
    }

    // GET: Receipts/Print/5
    public async Task<IActionResult> Print(int? id, string? returnUrl = null)
    {
        if (id == null) return NotFound();

        var receipt = await _context.Receipts
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(m => m.Id == id);

        if (receipt == null) return NotFound();

        var prep = await _invoicePrintManager.PrepareForInvoicePrintAsync();
        if (!prep.IsOk)
        {
            TempData["ErrorMessage"] = prep.Message ?? "Selected printer not found. Please update printer settings.";
            return RedirectToAction("Index", "PrinterSettings", new
            {
                returnUrl = Url.Action(nameof(Print), new { id = receipt.Id, returnUrl })
            });
        }

        var borderless = await _appSettings.GetAsync(PrinterSettingKeys.ReceiptBorderless, HttpContext.RequestAborted);
        ViewBag.ReceiptBorderless = string.Equals(borderless, "true", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(borderless, "1", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(borderless, "yes", StringComparison.OrdinalIgnoreCase);

        var paperSize = (await _appSettings.GetAsync(PrinterSettingKeys.PaperSize, HttpContext.RequestAborted))?.Trim();
        ViewBag.PaperSize = string.Equals(paperSize, "A4", StringComparison.OrdinalIgnoreCase) ? "A4" : "Letter";
        ViewBag.ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : Url.Action(nameof(Index));

        return View(receipt);
    }

    // GET: Receipts/Details/5
    public async Task<IActionResult> Details(int? id, string? returnUrl = null)
    {
        if (id == null) return NotFound();

        var receipt = await _context.Receipts
            .Include(r => r.Lines)
            .Include(r => r.Payments)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (receipt == null) return NotFound();

        receipt.Lines = ReceiptLineOrdering.ByParticulars(receipt.Lines);

        ViewBag.Partners = await _context.PartnerBalanceConfigs
            .AsNoTracking()
            .OrderBy(p => p.PartnerName)
            .Select(p => p.PartnerName)
            .ToListAsync(HttpContext.RequestAborted);
        ViewBag.ReturnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? returnUrl
            : Url.Action(nameof(Index));

        return View(receipt);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id, string? returnUrl = null, int? returnScrollY = null)
    {
        var deleted = await _receiptService.DeleteReceiptAsync(id, User?.Identity?.Name, HttpContext.RequestAborted);

        if (!deleted) return NotFound();

        TempData["Message"] = "Receipt deleted. Dashboard sales/profit and stock have been updated.";
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            var redirectUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(returnUrl, "highlightReceiptId", id.ToString());
            if (returnScrollY.HasValue && returnScrollY.Value > 0)
            {
                redirectUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(redirectUrl, "restoreScrollY", returnScrollY.Value.ToString());
            }

            return LocalRedirect(redirectUrl);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAsPaid(int id, string? returnUrl = null)
    {
        var markedPaid = await _receiptService.MarkReceiptPaidAsync(id, User.Identity?.Name, HttpContext.RequestAborted);
        if (!markedPaid)
            return NotFound();

        TempData["Message"] = "Receipt marked as paid.";

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoPaid(int id, string? returnUrl = null)
    {
        var reverted = await _receiptService.RevertReceiptToUnpaidAsync(id, HttpContext.RequestAborted);
        if (!reverted)
            return NotFound();

        TempData["Message"] = "Receipt returned to unpaid.";

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLinePartner(int receiptId, int lineId, string? partnerName, string? returnUrl = null)
    {
        var receipt = await _context.Receipts
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == receiptId, HttpContext.RequestAborted);

        if (receipt == null) return NotFound();

        var line = receipt.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return NotFound();

        partnerName = string.IsNullOrWhiteSpace(partnerName) ? null : partnerName.Trim();
        line.PartnerName = partnerName;

        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        _cacheInvalidator.InvalidateProfitReports();

        return RedirectToReceiptDetails(receiptId, returnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateLinePrice(int receiptId, int lineId, decimal price, string? returnUrl = null)
    {
        if (price <= 0m)
        {
            TempData["ErrorMessage"] = "Manual price must be greater than zero.";
            return RedirectToReceiptDetails(receiptId, returnUrl);
        }

        var receipt = await _context.Receipts
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == receiptId, HttpContext.RequestAborted);

        if (receipt == null) return NotFound();

        if (receipt.Status is PaymentStatus.Paid or PaymentStatus.Void)
        {
            TempData["ErrorMessage"] = "Paid or void receipts cannot be edited. Return the receipt to unpaid first if you need to change prices.";
            return RedirectToReceiptDetails(receiptId, returnUrl);
        }

        var line = receipt.Lines.FirstOrDefault(l => l.Id == lineId);
        if (line == null) return NotFound();

        line.Price = price;
        RecalculateReceiptTotals(receipt);

        await _context.SaveChangesAsync(HttpContext.RequestAborted);
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();

        TempData["Message"] = $"Updated {line.ItemName} price to ₱{line.Price:N2}.";
        return RedirectToReceiptDetails(receiptId, returnUrl);
    }

    private static decimal NormalizePaidAmount(decimal paidAmount, decimal totalAmount)
    {
        if (totalAmount <= 0m)
            return 0m;

        if (paidAmount <= 0m)
            return 0m;

        return Math.Min(paidAmount, totalAmount);
    }

    private static void RecalculateReceiptTotals(Receipt receipt)
    {
        foreach (var line in receipt.Lines)
        {
            line.Amount = Math.Round(line.Quantity * line.Price, 2, MidpointRounding.AwayFromZero);
        }

        receipt.TotalAmount = receipt.Lines.Sum(l => l.Amount);
        receipt.PaidAmount = NormalizePaidAmount(receipt.PaidAmount, receipt.TotalAmount);
        ApplyReceiptStatus(receipt);
    }

    private static void ApplyReceiptStatus(Receipt receipt)
    {
        if (receipt.Status == PaymentStatus.Void)
            return;

        if (receipt.TotalAmount <= 0m || receipt.PaidAmount <= 0m)
        {
            receipt.Status = PaymentStatus.Unpaid;
        }
        else if (receipt.PaidAmount >= receipt.TotalAmount)
        {
            receipt.Status = PaymentStatus.Paid;
        }
        else
        {
            receipt.Status = PaymentStatus.Partial;
        }
    }

    private IActionResult RedirectToReceiptDetails(int receiptId, string? returnUrl)
        => RedirectToAction(nameof(Details), new
        {
            id = receiptId,
            returnUrl = !string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null
        });

    private static List<string> FindDuplicateLineNamesInRequest(Receipt receipt)
    {
        var lines = receipt.Lines ?? new List<ReceiptLine>();

        var duplicateProducts = lines
            .Where(l => l.ProductId.HasValue && l.Quantity > 0)
            .GroupBy(l => l.ProductId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.First().ItemName)
            .ToList();

        var duplicateServices = lines
            .Where(l => l.ServiceId.HasValue && l.Quantity > 0)
            .GroupBy(l => l.ServiceId!.Value)
            .Where(g => g.Count() > 1)
            .Select(g => g.First().ItemName)
            .ToList();

        return duplicateProducts
            .Concat(duplicateServices)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .Take(10)
            .ToList();
    }

    private async Task<List<string>> FindSameDayDuplicateItemsAsync(
        int customerId,
        string customerName,
        DateTime day,
        List<ReceiptLine> incomingLines)
    {
        var incomingProductIds = incomingLines
            .Where(l => l.ProductId.HasValue && l.Quantity > 0)
            .Select(l => l.ProductId!.Value)
            .Distinct()
            .ToList();

        var incomingServiceIds = incomingLines
            .Where(l => l.ServiceId.HasValue && l.Quantity > 0)
            .Select(l => l.ServiceId!.Value)
            .Distinct()
            .ToList();

        if (incomingProductIds.Count == 0 && incomingServiceIds.Count == 0)
            return new List<string>();

        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1);

        var matchedItems = await _context.Receipts
            .AsNoTracking()
            .Where(r => r.Date >= dayStart && r.Date < dayEnd &&
                        r.Status != PaymentStatus.Void &&
                        ((r.CustomerId.HasValue && r.CustomerId.Value == customerId) ||
                         (!r.CustomerId.HasValue && r.CustomerName == customerName)))
            .SelectMany(r => r.Lines)
            .Where(l =>
                (l.ProductId.HasValue && incomingProductIds.Contains(l.ProductId.Value)) ||
                (l.ServiceId.HasValue && incomingServiceIds.Contains(l.ServiceId.Value)))
            .Select(l => l.ItemName)
            .Distinct()
            .OrderBy(n => n)
            .Take(10)
            .ToListAsync();

        return matchedItems;
    }

    private static DateTime NormalizeReceiptDate(DateTime input)
        => BusinessDate.NormalizeNextDeliveryDate(input);

    private async Task PopulateReceiptFormLookupsAsync(DateTime targetDate, CancellationToken ct)
    {
        var day = NormalizeReceiptDate(targetDate);
        ViewBag.Products = await _lookupCache.GetActiveProductsAsync(ct);
        ViewBag.Services = await _lookupCache.GetActiveServicesAsync(ct);
        ViewBag.PriceList = await _lookupCache.GetWeeklyPricesForDayAsync(day, ct);
        ViewBag.Customers = await _lookupCache.GetActiveCustomersAsync(ct);
        ViewBag.Partners = await _lookupCache.GetPartnerNamesAsync(ct);
    }
}
