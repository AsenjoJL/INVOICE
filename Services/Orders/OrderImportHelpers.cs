using System.Globalization;
using System.Text.RegularExpressions;
using HazelInvoice.Models;

namespace HazelInvoice.Services.Orders;

/// <summary>
/// Helper methods used by OrdersController for matrix import, product resolution, and quantity parsing.
/// Extracted for testability; logic remains unchanged from controller implementation.
/// </summary>
public static class OrderImportHelpers
{
    public static Dictionary<int, Dictionary<int, decimal>> BuildMatrixInputsByCustomer(
        Dictionary<string, decimal> rawMatrix,
        out HashSet<int> productIds,
        out HashSet<int> customerIds)
    {
        var byCustomer = new Dictionary<int, Dictionary<int, decimal>>();
        productIds = new HashSet<int>();
        customerIds = new HashSet<int>();

        if (rawMatrix == null || rawMatrix.Count == 0)
            return byCustomer;

        foreach (var kvp in rawMatrix)
        {
            if (!TryParseMatrixKey(kvp.Key, out var productId, out var customerId))
                continue;

            productIds.Add(productId);
            customerIds.Add(customerId);

            if (!byCustomer.TryGetValue(customerId, out var productMap))
            {
                productMap = new Dictionary<int, decimal>();
                byCustomer[customerId] = productMap;
            }

            // Accumulate quantities when the same product appears multiple times for an outlet.
            if (productMap.ContainsKey(productId))
                productMap[productId] += kvp.Value;
            else
                productMap[productId] = kvp.Value;
        }

        return byCustomer;
    }

    public static bool TryResolveProductByName(
        string productName,
        Dictionary<string, Product> productMap,
        out Product product)
    {
        product = default!;
        var key = NormalizeKey(productName);
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (productMap.TryGetValue(key, out product))
            return true;

        // Fallback: best contains/prefix match for minor naming variants.
        var containMatches = productMap
            .Where(kvp => kvp.Key.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                          key.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value)
            .DistinctBy(p => p.Id)
            .ToList();

        if (containMatches.Count == 1)
        {
            product = containMatches[0];
            return true;
        }
        else if (containMatches.Count > 1)
        {
            // Pick the closest length match to reduce ambiguity.
            product = containMatches
                .OrderBy(p => Math.Abs(NormalizeKey(p.Name).Length - key.Length))
                .ThenBy(p => p.Name.Length)
                .First();
            return true;
        }

        return false;
    }

    public static bool TryParseQuantityLoose(string? raw, out decimal qty)
    {
        qty = 0m;
        if (SimpleXlsxReader.TryParseDecimal(raw, out qty))
            return true;

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var cleaned = Regex.Replace(raw, @"[^0-9\./-]+", "");
        if (string.IsNullOrWhiteSpace(cleaned))
            return false;

        // Handle simple fractions like 1/2 or 3/4
        if (cleaned.Contains('/'))
        {
            var parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                decimal.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var num) &&
                decimal.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var den) &&
                den != 0)
            {
                qty = num / den;
                return true;
            }
        }

        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out qty);
    }

    public static string NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        return Regex.Replace(raw.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "");
    }

    public static bool TryParseMatrixKey(string? key, out int productId, out int customerId)
    {
        productId = 0;
        customerId = 0;

        if (string.IsNullOrWhiteSpace(key))
            return false;

        var sep = key.IndexOf('_');
        if (sep <= 0 || sep >= key.Length - 1)
            return false;

        var left = key[..sep];
        var right = key[(sep + 1)..];

        return int.TryParse(left, out productId) && int.TryParse(right, out customerId);
    }
}
