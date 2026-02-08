using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class ReceiptsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IReceiptService _receiptService;

    public ReceiptsController(ApplicationDbContext context, IReceiptService receiptService)
    {
        _context = context;
        _receiptService = receiptService;
    }

    // GET: Receipts
    public async Task<IActionResult> Index()
    {
        var receipts = await _context.Receipts
            .Include(r => r.Payments)
            .OrderByDescending(r => r.Date)
            .ToListAsync();
        ViewData["TitleHeader"] = "Receipts & Deliveries";
        return View(receipts);
    }

    // GET: Receipts/Unpaid
    public async Task<IActionResult> Unpaid()
    {
        var unpaid = await _context.Receipts
            .Include(r => r.Payments)
            .Where(r => r.Status == PaymentStatus.Unpaid)
            .OrderByDescending(r => r.Date)
            .ToListAsync();

        ViewData["TitleHeader"] = "Unpaid Receipts";
        return View("Index", unpaid);
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
        }

        if (ModelState.IsValid)
        {
            // Generate Number
            receipt.ReceiptNumber = await _receiptService.GenerateNextReceiptNumberAsync();
            receipt.CreatedById = User.Identity?.Name; // Or GetUserId
            receipt.Date = DateTime.Now;

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

        return View(receipt);
    }
}
