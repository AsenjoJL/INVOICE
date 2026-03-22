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
        "Taste 8 outlets"
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

    public string DefaultOutletGroup =>
        OutletGroups.FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? "EIGHT2EIGHT OUTLETS";
}
