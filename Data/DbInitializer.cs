using HazelInvoice.Models;
using HazelInvoice.Configuration;
using Microsoft.EntityFrameworkCore;

namespace HazelInvoice.Data;

public static class DbInitializer
{
    public static async Task Initialize(
        ApplicationDbContext context,
        BootstrapSeedOptions? seedOptions = null,
        ExpenseCatalogOptions? expenseCatalog = null)
    {
        seedOptions ??= new BootstrapSeedOptions();
        expenseCatalog ??= new ExpenseCatalogOptions();

        // Ensure database is created
        await context.Database.MigrateAsync();

        foreach (var item in expenseCatalog.Defaults)
        {
            var normalized = ExpenseCategoryCatalog.NormalizeName(item.Name);
            var existing = await context.ExpenseCategoryDefinitions
                .FirstOrDefaultAsync(x => x.Name == normalized);

            if (existing is null)
            {
                context.ExpenseCategoryDefinitions.Add(new ExpenseCategoryDefinition
                {
                    Name = normalized,
                    Group = item.Group,
                    IsSystem = true
                });
            }
            else if (existing.Group != item.Group)
            {
                existing.Group = item.Group;
                existing.IsSystem = true;
            }
        }

        await context.SaveChangesAsync();

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
}
