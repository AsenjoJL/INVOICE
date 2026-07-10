using System.ComponentModel.DataAnnotations;

namespace HazelInvoice.Models;

public enum ExpenseCategoryGroup
{
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    Other = 4
}

public class ExpenseCategoryDefinition
{
    public int Id { get; set; }

    [Required]
    [StringLength(50)]
    public string Name { get; set; } = string.Empty;

    public ExpenseCategoryGroup Group { get; set; } = ExpenseCategoryGroup.Other;

    public bool IsSystem { get; set; }
}

public static class ExpenseCategoryCatalog
{
    public static readonly IReadOnlyList<(string Name, ExpenseCategoryGroup Group)> Defaults =
    [
        ("MANPOWER", ExpenseCategoryGroup.Daily),
        ("PEOPLE 1", ExpenseCategoryGroup.Daily),
        ("PEOPLE 2", ExpenseCategoryGroup.Daily),
        ("PEOPLE 3", ExpenseCategoryGroup.Daily),
        ("DRIVER", ExpenseCategoryGroup.Daily),
        ("FOOD ALLOWANCE", ExpenseCategoryGroup.Daily),
        ("DRIVER FEE", ExpenseCategoryGroup.Daily),
        ("PLASTIC/SUPPLIES", ExpenseCategoryGroup.Daily),
        ("DIESEL", ExpenseCategoryGroup.Daily),
        ("MAINTENANCE", ExpenseCategoryGroup.Daily),
        ("DAILY DUES", ExpenseCategoryGroup.Daily),
        ("CASH INTEREST", ExpenseCategoryGroup.Weekly),
        ("PUSH CART", ExpenseCategoryGroup.Weekly),
        ("TENT", ExpenseCategoryGroup.Other),
        ("WEIGHING SCALE", ExpenseCategoryGroup.Other),
        ("LANTAY", ExpenseCategoryGroup.Other),
        ("MEALS", ExpenseCategoryGroup.Other),
        ("RENTAL FEE", ExpenseCategoryGroup.Other),
        ("EBIKE FEE", ExpenseCategoryGroup.Other),
        ("KAROMATA FEE", ExpenseCategoryGroup.Other),
        ("LABOR", ExpenseCategoryGroup.Weekly),
        ("CARD INC", ExpenseCategoryGroup.Weekly),
        ("ASIALINK CORP", ExpenseCategoryGroup.Monthly),
        ("RAFI CORP", ExpenseCategoryGroup.Monthly),
        ("BOARDING HOUSE", ExpenseCategoryGroup.Monthly),
        ("PARKING FEE", ExpenseCategoryGroup.Monthly),
        ("TRUCK MAINTENANCE", ExpenseCategoryGroup.Monthly)
    ];

    public static readonly ExpenseCategoryGroup[] OrderedGroups =
    [
        ExpenseCategoryGroup.Daily,
        ExpenseCategoryGroup.Weekly,
        ExpenseCategoryGroup.Monthly,
        ExpenseCategoryGroup.Other
    ];

    public static string GetLabel(ExpenseCategoryGroup group) => group switch
    {
        ExpenseCategoryGroup.Daily => "Daily Expenses",
        ExpenseCategoryGroup.Weekly => "Weekly Expenses",
        ExpenseCategoryGroup.Monthly => "Monthly Expenses",
        _ => "Other Expenses"
    };

    public static string NormalizeName(string? name) =>
        string.IsNullOrWhiteSpace(name) ? string.Empty : name.Trim().ToUpperInvariant();
}
