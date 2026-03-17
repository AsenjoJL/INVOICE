using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services;
using HazelInvoice.Services.Printing;
using HazelInvoice.Services.Receipts;
using HazelInvoice.Services.Settings;
using HazelInvoice.ViewModels;
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

    public ReceiptsController(
        ApplicationDbContext context,
        IReceiptService receiptService,
        IInvoicePrintManager invoicePrintManager,
        IReceiptQueryService receiptQuery,
        IAppSettingStore appSettings)
    {
        _context = context;
        _receiptService = receiptService;
        _invoicePrintManager = invoicePrintManager;
        _receiptQuery = receiptQuery;
        _appSettings = appSettings;
    }

    // GET: Receipts
    public async Task<IActionResult> Index(string? q, int page = 1, int pageSize = 50)
    {
        var vm = await _receiptQuery.QueryAsync(new ReceiptQueryOptions
        {
            Query = q,
            Page = page,
            PageSize = pageSize,
            UnpaidOnly = false
        }, HttpContext.RequestAborted);
        ViewData["TitleHeader"] = "Receipts & Deliveries";
        return View(vm);
    }

    // GET: Receipts/Unpaid
    public async Task<IActionResult> Unpaid(string? q, int page = 1, int pageSize = 50)
    {
        var vm = await _receiptQuery.QueryAsync(new ReceiptQueryOptions
        {
            Query = q,
            Page = page,
            PageSize = pageSize,
            UnpaidOnly = true
        }, HttpContext.RequestAborted);

        ViewData["TitleHeader"] = "Unpaid Receipts";
        return View("Index", vm);
    }

    // GET: Receipts/Create
    public async Task<IActionResult> Create()
    {
        // Pass products and services for dropdowns
        ViewBag.Products = await _context.Products.Where(p => p.IsActive).ToListAsync();
        ViewBag.Services = await _context.Services.Where(s => s.IsActive).ToListAsync();
        var today = DateTime.Today;
        ViewBag.PriceList = await _context.WeeklyPrices
            .Include(p => p.Product)
            .Where(w => w.EffectiveFrom <= today && w.EffectiveTo >= today)
            .ToListAsync();

        ViewBag.Customers = await _context.Customers.Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync();
        ViewBag.Partners = await _context.PartnerBalanceConfigs
            .AsNoTracking()
            .OrderBy(p => p.PartnerName)
            .Select(p => p.PartnerName)
            .ToListAsync();

        return View();
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
                var duplicateItems = await FindSameDayDuplicateItemsAsync(
                    receipt.CustomerId.Value,
                    receipt.CustomerName,
                    DateTime.Today,
                    receipt.Lines);

                if (duplicateItems.Count > 0)
                {
                    ModelState.AddModelError(
                        string.Empty,
                        $"Duplicate order detected for this outlet today. Existing items: {string.Join(", ", duplicateItems)}. " +
                        "Please edit the existing order instead of adding duplicates.");
                }
            }
        }

        if (ModelState.IsValid)
        {
            // Generate Number
            receipt.ReceiptNumber = await _receiptService.GenerateNextReceiptNumberAsync();
            receipt.CreatedById = User.Identity?.Name; // Or GetUserId
            receipt.Date = DateTime.Now;

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
            var endExclusive = day.AddDays(1);
            var weeklyCostOverrides = (await _context.WeeklyPrices
                    .AsNoTracking()
                    .Where(w => productIds.Contains(w.ProductId) &&
                                w.EffectiveFrom <= day && w.EffectiveTo >= day &&
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

            await _context.SaveChangesAsync();

            if (action == "Print")
            {
                return RedirectToAction(nameof(Print), new { id = receipt.Id });
            }
            return RedirectToAction(nameof(Index));
        }
        
        // Reload filtered lists on failure
        ViewBag.Products = await _context.Products.Where(p => p.IsActive).ToListAsync();
        ViewBag.Services = await _context.Services.Where(s => s.IsActive).ToListAsync();
        var today = DateTime.Today;
        ViewBag.PriceList = await _context.WeeklyPrices
            .Include(p => p.Product)
            .Where(w => w.EffectiveFrom <= today && w.EffectiveTo >= today)
            .ToListAsync();
        ViewBag.Customers = await _context.Customers.Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync();
        ViewBag.Partners = await _context.PartnerBalanceConfigs
            .AsNoTracking()
            .OrderBy(p => p.PartnerName)
            .Select(p => p.PartnerName)
            .ToListAsync();

        return View(receipt);
    }

    // GET: Receipts/Print/5
    public async Task<IActionResult> Print(int? id)
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
                returnUrl = Url.Action(nameof(Print), new { id = receipt.Id })
            });
        }

        var borderless = await _appSettings.GetAsync(PrinterSettingKeys.ReceiptBorderless, HttpContext.RequestAborted);
        ViewBag.ReceiptBorderless = string.Equals(borderless, "true", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(borderless, "1", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(borderless, "yes", StringComparison.OrdinalIgnoreCase);

        var paperSize = (await _appSettings.GetAsync(PrinterSettingKeys.PaperSize, HttpContext.RequestAborted))?.Trim();
        ViewBag.PaperSize = string.Equals(paperSize, "A4", StringComparison.OrdinalIgnoreCase) ? "A4" : "Letter";

        return View(receipt);
    }

    // GET: Receipts/Details/5
    public async Task<IActionResult> Details(int? id)
    {
        if (id == null) return NotFound();

        var receipt = await _context.Receipts
            .Include(r => r.Lines)
            .Include(r => r.Payments)
            .FirstOrDefaultAsync(r => r.Id == id);

        if (receipt == null) return NotFound();

        ViewBag.Partners = await _context.PartnerBalanceConfigs
            .AsNoTracking()
            .OrderBy(p => p.PartnerName)
            .Select(p => p.PartnerName)
            .ToListAsync(HttpContext.RequestAborted);

        return View(receipt);
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

        if (!string.IsNullOrWhiteSpace(returnUrl))
            return Redirect(returnUrl);

        return RedirectToAction(nameof(Details), new { id = receiptId });
    }

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
}
