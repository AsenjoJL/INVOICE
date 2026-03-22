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
        new("FOOD ALLOWANCE", ExpenseCategoryGroup.Daily),
        new("DRIVER FEE", ExpenseCategoryGroup.Daily),
        new("PLASTIC/SUPPLIES", ExpenseCategoryGroup.Daily),
        new("DIESEL", ExpenseCategoryGroup.Daily),
        new("DAILY DUES", ExpenseCategoryGroup.Daily),
        new("CASH INTEREST", ExpenseCategoryGroup.Weekly),
        new("PUSH CART", ExpenseCategoryGroup.Weekly),
        new("TENT", ExpenseCategoryGroup.Weekly),
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
