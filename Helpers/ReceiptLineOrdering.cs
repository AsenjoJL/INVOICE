using HazelInvoice.Models;

namespace HazelInvoice.Helpers;

public static class ReceiptLineOrdering
{
    public static List<ReceiptLine> ByParticulars(IEnumerable<ReceiptLine>? lines)
    {
        return (lines ?? Enumerable.Empty<ReceiptLine>())
            .OrderBy(line => NormalizeParticular(line.ItemName), StringComparer.OrdinalIgnoreCase)
            .ThenBy(line => line.Id)
            .ToList();
    }

    private static string NormalizeParticular(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
}
