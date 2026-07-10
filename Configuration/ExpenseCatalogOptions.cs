using HazelInvoice.Models;

namespace HazelInvoice.Configuration;

/// <summary>
/// Starter/default expense category definitions. These are used to bootstrap the category catalog,
/// not to overwrite live expense records.
/// </summary>
public sealed class ExpenseCatalogOptions
{
    public List<ExpenseCategorySeedItem> Defaults { get; set; } =
    [
        new("MANPOWER", ExpenseCategoryGroup.Daily),
        new("PEOPLE 1", ExpenseCategoryGroup.Daily),
        new("PEOPLE 2", ExpenseCategoryGroup.Daily),
        new("PEOPLE 3", ExpenseCategoryGroup.Daily),
        new("DRIVER", ExpenseCategoryGroup.Daily),
        new("FOOD ALLOWANCE", ExpenseCategoryGroup.Daily),
        new("DRIVER FEE", ExpenseCategoryGroup.Daily),
        new("PLASTIC/SUPPLIES", ExpenseCategoryGroup.Daily),
        new("DIESEL", ExpenseCategoryGroup.Daily),
        new("MAINTENANCE", ExpenseCategoryGroup.Daily),
        new("DAILY DUES", ExpenseCategoryGroup.Daily),
        new("CASH INTEREST", ExpenseCategoryGroup.Weekly),
        new("PUSH CART", ExpenseCategoryGroup.Weekly),
        new("TENT", ExpenseCategoryGroup.Other),
        new("WEIGHING SCALE", ExpenseCategoryGroup.Other),
        new("LANTAY", ExpenseCategoryGroup.Other),
        new("MEALS", ExpenseCategoryGroup.Other),
        new("RENTAL FEE", ExpenseCategoryGroup.Other),
        new("EBIKE FEE", ExpenseCategoryGroup.Other),
        new("KAROMATA FEE", ExpenseCategoryGroup.Other),
        new("LABOR", ExpenseCategoryGroup.Weekly),
        new("CARD INC", ExpenseCategoryGroup.Weekly),
        new("ASIALINK CORP", ExpenseCategoryGroup.Monthly),
        new("RAFI CORP", ExpenseCategoryGroup.Monthly),
        new("BOARDING HOUSE", ExpenseCategoryGroup.Monthly),
        new("PARKING FEE", ExpenseCategoryGroup.Monthly),
        new("TRUCK MAINTENANCE", ExpenseCategoryGroup.Monthly)
    ];
}

public sealed record ExpenseCategorySeedItem(string Name, ExpenseCategoryGroup Group);
