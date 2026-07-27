namespace HazelInvoice.Configuration;

/// <summary>
/// Shared operational rules that should stay configurable rather than being scattered as hardcoded values.
/// Defaults preserve the current live behavior while making later adjustments data/config driven.
/// </summary>
public sealed class OperationsOptions
{
    public List<string> OutletGroups { get; set; } =
    [
        "EIGHT2EIGHT OUTLETS",
        "Taste 8 outlets",
        "MCIAA"
    ];

    public List<string> OutletSortTokens { get; set; } =
    [
        "autoliv", "autolive", "taiyo", "gmc", "global", "uct", "knowles", "knowless",
        "merasenko", "teradyne", "jpkitchen", "jpmorgan", "cebukitchen", "cebukit",
        "bakery", "wlahug", "mitsumi", "feeder", "mphokim", "phokim"
    ];

    public Dictionary<string, string> OutletImportAliases { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cebukit"] = "cebukitchen",
        ["cebu"] = "cebukitchen",
        ["mphokim"] = "mpt",
        ["phokim"] = "mpt",
        ["jpkitchen"] = "jpmorgan",
        ["jpebloc"] = "jpmorgan",
        ["jpcbloc"] = "jpmorgan"
    };

    public DateTime ProfitReportOpeningDate { get; set; } = new(2026, 3, 19);

    public List<string> VegetableNonOutletHeaderKeys { get; set; } =
    [
        "vegetables",
        "price",
        "total",
        "uom",
        "unit",
        "ponumber"
    ];

    public string VegetableTemplateProductHeader { get; set; } = "Vegetables";

    public string VegetableTemplatePriceHeader { get; set; } = "Price";

    public int VegetablePrintTargetSheets { get; set; } = 3;

    public int VegetablePrintMinRowsPerSheet { get; set; } = 41;

    public decimal VegetableDetailPercentFeeDefault { get; set; } = 1.0m;

    public string DefaultOutletGroup =>
        OutletGroups.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? "EIGHT2EIGHT OUTLETS";

    public string ResolveOutletGroupOrDefault(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return DefaultOutletGroup;

        var match = OutletGroups.FirstOrDefault(
            g => string.Equals(g, groupName.Trim(), StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(match) ? DefaultOutletGroup : match;
    }
}
