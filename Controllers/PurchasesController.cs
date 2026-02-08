using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class PurchasesController : Controller
{
    private readonly ApplicationDbContext _context;

    public PurchasesController(ApplicationDbContext context)
    {
        _context = context;
    }

    // GET: Purchases
    public async Task<IActionResult> Index(string status = "All", DateTime? date = null)
    {
        var dayStart = (date ?? DateTime.Today).Date;
        var dayEnd = dayStart.AddDays(1);

        var query = _context.Purchases
            .AsNoTracking()
            .Include(p => p.Lines)
            .OrderByDescending(p => p.Date)
            .AsQueryable();

        if (date.HasValue)
        {
            query = query.Where(p => p.Date >= dayStart && p.Date < dayEnd);
        }

        if (!string.Equals(status, "All", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<PaymentStatus>(status, true, out var parsed))
        {
            query = query.Where(p => p.Status == parsed);
        }

        var purchases = await query.ToListAsync();

        var vm = new PurchaseListViewModel
        {
            Date = dayStart,
            StatusFilter = status,
            Purchases = purchases,
            GrandTotal = purchases.Sum(p => p.TotalAmount),
            TotalPaid = purchases.Sum(p => p.PaidAmount),
            TotalBalance = purchases.Sum(p => p.TotalAmount - p.PaidAmount)
        };

        return View(vm);
    }

    // GET: Purchases/Payables
    public async Task<IActionResult> Payables(DateTime? date = null)
    {
        var dayStart = (date ?? DateTime.Today).Date;
        var dayEnd = dayStart.AddDays(1);

        var query = _context.Purchases
            .AsNoTracking()
            .Include(p => p.Lines)
            .Where(p => p.Status != PaymentStatus.Paid && p.Status != PaymentStatus.Void);

        if (date.HasValue)
        {
            query = query.Where(p => p.Date >= dayStart && p.Date < dayEnd);
        }

        var purchases = await query.OrderByDescending(p => p.Date).ToListAsync();

        var suppliers = purchases
            .GroupBy(p => string.IsNullOrWhiteSpace(p.SupplierName) ? "Unknown" : p.SupplierName)
            .Select(g => new PayableSupplierSummary
            {
                SupplierName = g.Key,
                TotalAmount = g.Sum(x => x.TotalAmount),
                PaidAmount = g.Sum(x => x.PaidAmount),
                Balance = g.Sum(x => x.TotalAmount - x.PaidAmount),
                Purchases = g.OrderByDescending(x => x.Date).ToList()
            })
            .OrderByDescending(g => g.Balance)
            .ToList();

        var vm = new PayablesViewModel
        {
            Date = date,
            Suppliers = suppliers,
            TotalBalance = suppliers.Sum(s => s.Balance)
        };

        return View(vm);
    }

    // GET: Purchases/Create
    public async Task<IActionResult> Create()
    {
        await PopulateOptionsAsync();
        return View();
    }

    // POST: Purchases/Create
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Purchase purchase)
    {
        if (purchase == null) return BadRequest();
        ModelState.Remove(nameof(Purchase.PurchaseNumber)); // Generated server-side

        if (purchase.Lines == null || purchase.Lines.Count == 0)
        {
            ModelState.AddModelError("", "Please add at least one item.");
        }

        if (purchase.SupplierId == null && string.IsNullOrWhiteSpace(purchase.SupplierName))
        {
            ModelState.AddModelError("SupplierName", "Please select or enter a supplier.");
        }

        if (!ModelState.IsValid)
        {
            await PopulateOptionsAsync();
            return View(purchase);
        }

        Supplier? supplier = null;
        if (purchase.SupplierId.HasValue)
        {
            supplier = await _context.Suppliers.FindAsync(purchase.SupplierId.Value);
            if (supplier == null)
            {
                ModelState.AddModelError("SupplierId", "Supplier not found.");
                await PopulateOptionsAsync();
                return View(purchase);
            }
        }
        else if (!string.IsNullOrWhiteSpace(purchase.SupplierName))
        {
            supplier = await _context.Suppliers
                .FirstOrDefaultAsync(s => s.Name.ToLower() == purchase.SupplierName.ToLower());

            if (supplier == null)
            {
                supplier = new Supplier
                {
                    Name = purchase.SupplierName.Trim(),
                    Address = purchase.SupplierAddress,
                    ContactNumber = purchase.ContactNumber,
                    IsActive = true
                };
                _context.Suppliers.Add(supplier);
                await _context.SaveChangesAsync();
            }
            purchase.SupplierId = supplier.Id;
        }

        if (supplier != null)
        {
            purchase.SupplierName = supplier.Name;
            if (string.IsNullOrWhiteSpace(purchase.SupplierAddress))
                purchase.SupplierAddress = supplier.Address;
            if (string.IsNullOrWhiteSpace(purchase.ContactNumber))
                purchase.ContactNumber = supplier.ContactNumber;
        }

        purchase.CreatedById = User.Identity?.Name;
        purchase.PurchaseNumber = await GenerateNextPurchaseNumberAsync();

        var cleanedLines = new List<PurchaseLine>();
        foreach (var line in purchase.Lines)
        {
            if (line.Quantity <= 0) continue;
            if (line.Cost < 0) continue;

            if (line.ProductId.HasValue && string.IsNullOrWhiteSpace(line.ItemName))
            {
                var prod = await _context.Products.FindAsync(line.ProductId.Value);
                if (prod != null)
                {
                    line.ItemName = prod.Name;
                    if (string.IsNullOrWhiteSpace(line.Unit))
                        line.Unit = prod.Unit;
                }
            }

            line.Amount = line.Quantity * line.Cost;
            cleanedLines.Add(line);
        }

        if (cleanedLines.Count == 0)
        {
            ModelState.AddModelError("", "Please add at least one valid item.");
            await PopulateOptionsAsync();
            return View(purchase);
        }

        purchase.Lines = cleanedLines;
        purchase.TotalAmount = cleanedLines.Sum(l => l.Amount);

        if (purchase.PaidAmount < 0) purchase.PaidAmount = 0;
        if (purchase.PaidAmount >= purchase.TotalAmount)
            purchase.Status = PaymentStatus.Paid;
        else if (purchase.PaidAmount > 0)
            purchase.Status = PaymentStatus.Partial;
        else
            purchase.Status = PaymentStatus.Unpaid;

        _context.Purchases.Add(purchase);

        foreach (var line in purchase.Lines)
        {
            if (line.ProductId.HasValue)
            {
                _context.ProductStockMovements.Add(new ProductStockMovement
                {
                    ProductId = line.ProductId.Value,
                    Date = purchase.Date,
                    Quantity = line.Quantity,
                    Type = "StockIn",
                    Reference = purchase.PurchaseNumber,
                    RecordedById = purchase.CreatedById
                });
            }
        }

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }

    // GET: Purchases/Details/5
    public async Task<IActionResult> Details(int id)
    {
        var purchase = await _context.Purchases
            .Include(p => p.Lines)
            .Include(p => p.Payments)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (purchase == null) return NotFound();
        return View(purchase);
    }

    // POST: Purchases/AddPayment
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddPayment(int id, decimal amount, PaymentMethod paymentMethod, string? referenceNo)
    {
        var purchase = await _context.Purchases
            .Include(p => p.Payments)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (purchase == null) return NotFound();
        if (amount <= 0)
        {
            TempData["Error"] = "Payment amount must be greater than 0.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var payment = new PurchasePayment
        {
            PurchaseId = purchase.Id,
            Amount = amount,
            PaymentMethod = paymentMethod,
            ReferenceNo = referenceNo,
            RecordedById = User.Identity?.Name
        };

        purchase.Payments.Add(payment);
        purchase.PaidAmount += amount;

        if (purchase.PaidAmount >= purchase.TotalAmount)
            purchase.Status = PaymentStatus.Paid;
        else
            purchase.Status = PaymentStatus.Partial;

        await _context.SaveChangesAsync();
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST: Purchases/MarkAsPaid
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAsPaid(int id)
    {
        var purchase = await _context.Purchases.FindAsync(id);
        if (purchase == null) return NotFound();

        purchase.PaidAmount = purchase.TotalAmount;
        purchase.Status = PaymentStatus.Paid;
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }

    private async Task PopulateOptionsAsync()
    {
        ViewBag.Suppliers = await _context.Suppliers
            .AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .ToListAsync();

        ViewBag.Products = await _context.Products
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    private async Task<string> GenerateNextPurchaseNumberAsync()
    {
        int year = DateTime.Now.Year;
        var seq = await _context.PurchaseSequences.FirstOrDefaultAsync(s => s.Year == year);
        if (seq == null)
        {
            seq = new PurchaseSequence { Year = year, LastNumber = 0 };
            _context.PurchaseSequences.Add(seq);
        }

        seq.LastNumber += 1;
        await _context.SaveChangesAsync();
        return $"PO-{year}-{seq.LastNumber:000000}";
    }
}
