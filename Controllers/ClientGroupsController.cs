using HazelInvoice.Data;
using HazelInvoice.Models;
using HazelInvoice.Services.Caching;
using HazelInvoice.Services.Clients;
using HazelInvoice.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Controllers;

[Authorize]
public class ClientGroupsController : Controller
{
    private readonly ApplicationDbContext _context;
    private readonly IAppCacheInvalidator _cacheInvalidator;

    public ClientGroupsController(
        ApplicationDbContext context,
        IAppCacheInvalidator cacheInvalidator)
    {
        _context = context;
        _cacheInvalidator = cacheInvalidator;
    }

    public async Task<IActionResult> Index()
    {
        var groups = await _context.ClientGroups
            .AsNoTracking()
            .OrderBy(g => g.DisplayOrder)
            .ThenBy(g => g.Name)
            .ToListAsync();

        var customers = await _context.Customers
            .AsNoTracking()
            .Where(c => c.GroupName != null && c.GroupName != "")
            .Select(c => new { c.GroupName, c.IsActive })
            .ToListAsync();

        var model = groups.Select(group =>
        {
            var outletGroups = ClientGroupService.ParseOutletGroupNames(group.OutletGroupNames)
                .DefaultIfEmpty(group.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return new ClientGroupListItemVm
            {
                Id = group.Id,
                Name = group.Name,
                OutletGroupNames = ClientGroupService.NormalizeOutletGroupNames(outletGroups),
                IsActive = group.IsActive,
                DisplayOrder = group.DisplayOrder,
                OutletCount = customers.Count(c => c.IsActive && outletGroups.Contains(c.GroupName))
            };
        }).ToList();

        return View(model);
    }

    public async Task<IActionResult> Create()
    {
        var nextOrder = await _context.ClientGroups
            .AsNoTracking()
            .Select(g => (int?)g.DisplayOrder)
            .MaxAsync() ?? 0;

        return View(new ClientGroupFormVm { DisplayOrder = nextOrder + 1, IsActive = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ClientGroupFormVm model)
    {
        NormalizeForm(model);
        await ValidateUniqueNameAsync(model);

        if (!ModelState.IsValid)
            return View(model);

        _context.ClientGroups.Add(new ClientGroup
        {
            Name = model.Name,
            OutletGroupNames = model.OutletGroupNames,
            IsActive = model.IsActive,
            DisplayOrder = model.DisplayOrder,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now
        });

        await _context.SaveChangesAsync();
        InvalidateGroupDependentCaches();
        TempData["SuccessMessage"] = "Client group added.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var group = await _context.ClientGroups.FindAsync(id);
        if (group == null) return NotFound();

        return View(new ClientGroupFormVm
        {
            Id = group.Id,
            Name = group.Name,
            OutletGroupNames = group.OutletGroupNames,
            IsActive = group.IsActive,
            DisplayOrder = group.DisplayOrder
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, ClientGroupFormVm model)
    {
        if (id != model.Id) return NotFound();

        NormalizeForm(model);
        await ValidateUniqueNameAsync(model);

        if (!ModelState.IsValid)
            return View(model);

        var group = await _context.ClientGroups.FindAsync(id);
        if (group == null) return NotFound();

        var oldName = group.Name;
        var oldOutletGroups = ClientGroupService.ParseOutletGroupNames(group.OutletGroupNames)
            .DefaultIfEmpty(oldName)
            .ToList();

        group.Name = model.Name;
        group.OutletGroupNames = model.OutletGroupNames;
        group.IsActive = model.IsActive;
        group.DisplayOrder = model.DisplayOrder;
        group.UpdatedAt = DateTime.Now;

        await SyncOneToOneOutletRenameAsync(oldName, oldOutletGroups, model);

        await _context.SaveChangesAsync();
        InvalidateGroupDependentCaches();
        TempData["SuccessMessage"] = "Client group updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deactivate(int id)
    {
        var group = await _context.ClientGroups.FindAsync(id);
        if (group == null) return NotFound();

        var outletGroups = ClientGroupService.ParseOutletGroupNames(group.OutletGroupNames)
            .DefaultIfEmpty(group.Name)
            .ToList();

        var activeOutletCount = await _context.Customers
            .AsNoTracking()
            .CountAsync(c => c.IsActive && outletGroups.Contains(c.GroupName));

        if (activeOutletCount > 0)
        {
            TempData["ErrorMessage"] = "Cannot deactivate this client group while active outlets still use it.";
            return RedirectToAction(nameof(Index));
        }

        group.IsActive = false;
        group.UpdatedAt = DateTime.Now;
        await _context.SaveChangesAsync();
        InvalidateGroupDependentCaches();
        TempData["SuccessMessage"] = "Client group deactivated.";
        return RedirectToAction(nameof(Index));
    }

    private async Task ValidateUniqueNameAsync(ClientGroupFormVm model)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            return;

        var nameKey = model.Name.ToLower();
        var exists = await _context.ClientGroups
            .AsNoTracking()
            .AnyAsync(g => g.Id != model.Id && g.Name.ToLower() == nameKey);

        if (exists)
            ModelState.AddModelError(nameof(ClientGroupFormVm.Name), "Client group already exists.");
    }

    private static void NormalizeForm(ClientGroupFormVm model)
    {
        model.Name = (model.Name ?? string.Empty).Trim();
        var outletGroups = ClientGroupService.ParseOutletGroupNames(model.OutletGroupNames)
            .DefaultIfEmpty(model.Name);
        model.OutletGroupNames = ClientGroupService.NormalizeOutletGroupNames(outletGroups);
        if (model.DisplayOrder < 0) model.DisplayOrder = 0;
    }

    private async Task SyncOneToOneOutletRenameAsync(string oldName, IReadOnlyList<string> oldOutletGroups, ClientGroupFormVm model)
    {
        var newOutletGroups = ClientGroupService.ParseOutletGroupNames(model.OutletGroupNames).ToList();
        var wasOneToOne = oldOutletGroups.Count == 1 &&
                          string.Equals(oldOutletGroups[0], oldName, StringComparison.OrdinalIgnoreCase);
        var isOneToOne = newOutletGroups.Count == 1 &&
                         string.Equals(newOutletGroups[0], model.Name, StringComparison.OrdinalIgnoreCase);

        if (!wasOneToOne || !isOneToOne || string.Equals(oldName, model.Name, StringComparison.OrdinalIgnoreCase))
            return;

        var outlets = await _context.Customers
            .Where(c => c.GroupName.ToLower() == oldName.ToLower())
            .ToListAsync();

        foreach (var outlet in outlets)
        {
            outlet.GroupName = model.Name;
        }
    }

    private void InvalidateGroupDependentCaches()
    {
        _cacheInvalidator.InvalidateCustomers();
        _cacheInvalidator.InvalidateDashboard();
        _cacheInvalidator.InvalidateProfitReports();
    }
}
