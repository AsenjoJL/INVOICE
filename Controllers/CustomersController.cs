using HazelInvoice.Data;
using HazelInvoice.Configuration;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Clients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HazelInvoice.Controllers;

[Authorize]
public class CustomersController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;
    private readonly IClientGroupService _clientGroups;
    private readonly string _defaultOutletGroup;

    public CustomersController(
        ApplicationDbContext context,
        IAppCacheInvalidator cacheInvalidator,
        IClientGroupService clientGroups,
        IOptions<OperationsOptions> operations)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
        _clientGroups = clientGroups;
        _defaultOutletGroup = operations.Value.DefaultOutletGroup;
    }

    public async Task<IActionResult> Index()
    {
        var customers = await _context.Customers
            .AsNoTracking()
            .OrderBy(c => c.GroupName)
            .ThenBy(c => c.Name)
            .ToListAsync();

        return View(customers);
    }

    public async Task<IActionResult> Create()
    {
        ViewBag.OutletGroups = (await _clientGroups.GetOutletGroupNamesAsync(HttpContext.RequestAborted)).ToArray();
        return View(new Customer { GroupName = _defaultOutletGroup, IsActive = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Customer customer)
    {
        if (string.IsNullOrWhiteSpace(customer.GroupName))
            customer.GroupName = _defaultOutletGroup;

        customer.Name = (customer.Name ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(customer.Name))
        {
            var nameKey = customer.Name.ToLower();
            var nameExists = await _context.Customers
                .AsNoTracking()
                .AnyAsync(c => c.Name != null && c.Name.ToLower() == nameKey);
            if (nameExists)
                ModelState.AddModelError(nameof(Customer.Name), "Outlet name already exists. Please use a unique name.");
        }

        if (ModelState.IsValid)
        {
            _context.Add(customer);
            await _context.SaveChangesAsync();
            _cacheInvalidator.InvalidateCustomers();
            return RedirectToAction(nameof(Index));
        }

        ViewBag.OutletGroups = (await _clientGroups.GetOutletGroupNamesAsync(HttpContext.RequestAborted)).ToArray();
        return View(customer);
    }

    public async Task<IActionResult> Edit(int? id, string? returnUrl = null)
    {
        if (id == null) return NotFound();

        var customer = await _context.Customers.FindAsync(id);
        if (customer == null) return NotFound();

        ViewBag.ReturnUrl = returnUrl;
        ViewBag.OutletGroups = (await _clientGroups.GetOutletGroupNamesAsync(HttpContext.RequestAborted)).ToArray();
        return View(customer);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, Customer customer, string? returnUrl = null, int? returnScrollY = null)
    {
        if (id != customer.Id) return NotFound();

        if (string.IsNullOrWhiteSpace(customer.GroupName))
            customer.GroupName = _defaultOutletGroup;

        customer.Name = (customer.Name ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(customer.Name))
        {
            var nameKey = customer.Name.ToLower();
            var nameExists = await _context.Customers
                .AsNoTracking()
                .AnyAsync(c => c.Id != customer.Id && c.Name != null && c.Name.ToLower() == nameKey);
            if (nameExists)
                ModelState.AddModelError(nameof(Customer.Name), "Outlet name already exists. Please use a unique name.");
        }

        if (ModelState.IsValid)
        {
            _context.Update(customer);
            await _context.SaveChangesAsync();
            _cacheInvalidator.InvalidateCustomers();
            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                var redirectUrl = returnUrl;
                redirectUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(redirectUrl, "highlightCustomerId", customer.Id.ToString());
                if (returnScrollY.HasValue && returnScrollY.Value > 0)
                {
                    redirectUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(redirectUrl, "restoreScrollY", returnScrollY.Value.ToString());
                }

                return LocalRedirect(redirectUrl);
            }

            return RedirectToAction(nameof(Index));
        }

        ViewBag.ReturnUrl = returnUrl;
        ViewBag.OutletGroups = (await _clientGroups.GetOutletGroupNamesAsync(HttpContext.RequestAborted)).ToArray();
        return View(customer);
    }

    public async Task<IActionResult> Delete(int? id, string? returnUrl = null)
    {
        if (id == null) return NotFound();

        var customer = await _context.Customers.FindAsync(id);
        if (customer == null) return NotFound();

        ViewBag.ReturnUrl = returnUrl;
        return View(customer);
    }

    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(int id, string? returnUrl = null, int? returnScrollY = null)
    {
        var customer = await _context.Customers.FindAsync(id);
        if (customer == null) return NotFound();

        customer.IsActive = false;
        _context.Update(customer);
        await _context.SaveChangesAsync();
        _cacheInvalidator.InvalidateCustomers();

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            var redirectUrl = returnUrl;
            if (returnScrollY.HasValue && returnScrollY.Value > 0)
            {
                redirectUrl = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(redirectUrl, "restoreScrollY", returnScrollY.Value.ToString());
            }

            return LocalRedirect(redirectUrl);
        }

        return RedirectToAction(nameof(Index));
    }
}
