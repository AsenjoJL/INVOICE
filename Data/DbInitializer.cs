using HazelInvoice.Models;
using HazelInvoice.Configuration;
using HazelInvoice.Services.Clients;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Data;

public static class DbInitializer
{
    public static async Task Initialize(
        ApplicationDbContext context,
        BootstrapSeedOptions? seedOptions = null,
        ExpenseCatalogOptions? expenseCatalog = null,
        OperationsOptions? operations = null)
    {
        seedOptions ??= new BootstrapSeedOptions();
        expenseCatalog ??= new ExpenseCatalogOptions();
        operations ??= new OperationsOptions();

        // Ensure database is created
        await context.Database.MigrateAsync();

        var normalizedDefaults = (expenseCatalog.Defaults ?? [])
            .Select(item => new
            {
                Name = ExpenseCategoryCatalog.NormalizeName(item.Name),
                item.Group
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .DistinctBy(item => item.Name)
            .ToList();

        var defaultNames = normalizedDefaults.Select(item => item.Name).ToList();
        var existingDefinitions = await context.ExpenseCategoryDefinitions
            .Where(x => defaultNames.Contains(x.Name))
            .ToDictionaryAsync(x => x.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var item in normalizedDefaults)
        {
            if (!existingDefinitions.TryGetValue(item.Name, out var existing))
            {
                context.ExpenseCategoryDefinitions.Add(new ExpenseCategoryDefinition
                {
                    Name = item.Name,
                    Group = item.Group,
                    IsSystem = true
                });
            }
            else if (existing.Group != item.Group || !existing.IsSystem)
            {
                existing.Group = item.Group;
                existing.IsSystem = true;
            }
        }

        if (context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync();
        }

        await SeedClientGroupsAsync(context, operations);

        // 1. Seed Customers
        if (!await context.Customers.AnyAsync())
        {
            var customers = (seedOptions.CustomerNames ?? [])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new Customer { Name = name.Trim() })
                .ToList();

            await context.Customers.AddRangeAsync(customers);
            await context.SaveChangesAsync();
        }

        // 2. Seed Products
        if (!await context.Products.AnyAsync())
        {
            var products = (seedOptions.ProductNames ?? [])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(name => new Product
                {
                    Name = name.Trim(),
                    Unit = "pcs/kg",
                    UnitCost = 0,
                    IsActive = true,
                    Category = "General"
                })
                .ToList();

            await context.Products.AddRangeAsync(products);
            await context.SaveChangesAsync();
        }
    }

    private static async Task SeedClientGroupsAsync(ApplicationDbContext context, OperationsOptions operations)
    {
        var configuredClientGroups = operations.GetClientGroups();
        var existingOutletGroups = await context.Customers
            .AsNoTracking()
            .Where(c => c.GroupName != null && c.GroupName != "")
            .Select(c => c.GroupName)
            .Distinct()
            .ToListAsync();

        var mappedConfiguredOutletGroups = operations.ClientOutletGroups.Values
            .SelectMany(groups => groups)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var standaloneExistingOutletGroups = existingOutletGroups
            .Where(g => !mappedConfiguredOutletGroups.Contains(g))
            .ToList();

        var defaultGroups = configuredClientGroups
            .Concat(standaloneExistingOutletGroups)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingGroups = await context.ClientGroups
            .ToDictionaryAsync(g => g.Name.ToLower());

        var displayOrder = existingGroups.Count == 0 ? 0 : existingGroups.Values.Max(g => g.DisplayOrder);

        foreach (var groupName in defaultGroups)
        {
            var key = groupName.ToLower();
            var mappedOutletGroups = operations.ClientOutletGroups.TryGetValue(groupName, out var configuredMapping)
                ? configuredMapping
                : [groupName];

            var outletGroupNames = ClientGroupService.NormalizeOutletGroupNames(mappedOutletGroups);
            if (!existingGroups.TryGetValue(key, out var existing))
            {
                displayOrder++;
                context.ClientGroups.Add(new ClientGroup
                {
                    Name = groupName,
                    OutletGroupNames = string.IsNullOrWhiteSpace(outletGroupNames) ? groupName : outletGroupNames,
                    IsActive = true,
                    DisplayOrder = displayOrder,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                });
            }
            else if (string.IsNullOrWhiteSpace(existing.OutletGroupNames))
            {
                existing.OutletGroupNames = string.IsNullOrWhiteSpace(outletGroupNames) ? existing.Name : outletGroupNames;
                existing.UpdatedAt = DateTime.Now;
            }
        }

        if (context.ChangeTracker.HasChanges())
        {
            await context.SaveChangesAsync();
        }
    }
}
