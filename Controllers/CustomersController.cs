using HazelInvoice.Data;
using HazelInvoice.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class CustomersController : Controller
{
    private readonly ApplicationDbContext _context;
    private static readonly string[] OutletGroups = { "EIGHT2EIGHT OUTLETS", "Taste 8 outlets" };

    public CustomersController(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<IActionResult> Index()
    {
        var customers = await _context.Customers
            .OrderBy(c => c.GroupName)
            .ThenBy(c => c.Name)
            .ToListAsync();

        return View(customers);
    }

    public IActionResult Create()
    {
        ViewBag.OutletGroups = OutletGroups;
        return View(new Customer { GroupName = OutletGroups[0], IsActive = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Customer customer)
    {
        if (string.IsNullOrWhiteSpace(customer.GroupName))
            customer.GroupName = OutletGroups[0];

        if (ModelState.IsValid)
        {
            _context.Add(customer);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        ViewBag.OutletGroups = OutletGroups;
        return View(customer);
    }

    public async Task<IActionResult> Edit(int? id)
    {
        if (id == null) return NotFound();

        var customer = await _context.Customers.FindAsync(id);
        if (customer == null) return NotFound();

        ViewBag.OutletGroups = OutletGroups;
        return View(customer);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Customer customer)
    {
        if (id != customer.Id) return NotFound();

        if (string.IsNullOrWhiteSpace(customer.GroupName))
            customer.GroupName = OutletGroups[0];

        if (ModelState.IsValid)
        {
            _context.Update(customer);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        ViewBag.OutletGroups = OutletGroups;
        return View(customer);
    }

    public async Task<IActionResult> Delete(int? id)
    {
        if (id == null) return NotFound();

        var customer = await _context.Customers.FindAsync(id);
        if (customer == null) return NotFound();

        return View(customer);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id)
    {
        var customer = await _context.Customers.FindAsync(id);
        if (customer == null) return NotFound();

        customer.IsActive = false;
        _context.Update(customer);
        await _context.SaveChangesAsync();

        return RedirectToAction(nameof(Index));
    }
}
