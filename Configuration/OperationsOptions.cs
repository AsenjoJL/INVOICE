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

    public List<string> ClientGroups { get; set; } =
    [
        "EIGHT2EIGHT OUTLETS",
        "MCIAA"
    ];

    public Dictionary<string, List<string>> ClientOutletGroups { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EIGHT2EIGHT OUTLETS"] = ["EIGHT2EIGHT OUTLETS", "Taste 8 outlets"],
        ["MCIAA"] = ["MCIAA"]
    };

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

    public IReadOnlyList<string> GetClientGroups()
    {
        var groups = ClientGroups.Count > 0 ? ClientGroups : OutletGroups;
        return groups
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string DefaultClientGroup =>
        GetClientGroups().FirstOrDefault(g => !string.IsNullOrWhiteSpace(g)) ?? DefaultOutletGroup;

    public string ResolveClientGroupOrDefault(string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return DefaultClientGroup;

        var match = GetClientGroups().FirstOrDefault(
            g => string.Equals(g, groupName.Trim(), StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(match) ? DefaultClientGroup : match;
    }

    public IReadOnlyList<string> ResolveOutletGroupsForClient(string? clientGroup)
    {
        var selectedClient = ResolveClientGroupOrDefault(clientGroup);
        if (ClientOutletGroups.TryGetValue(selectedClient, out var mappedGroups) && mappedGroups.Count > 0)
        {
            return mappedGroups
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [selectedClient];
    }
}
