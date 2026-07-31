using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace HazelInvoice.Services;

public sealed class SimpleXlsxSheet
{
    public Dictionary<(int Row, int Col), string> Cells { get; } = new();

    public string GetCell(int row, int col)
        => Cells.TryGetValue((row, col), out var value) ? value : string.Empty;

    public int MaxRow => Cells.Count == 0 ? 0 : Cells.Keys.Max(k => k.Row);
    public int MaxCol => Cells.Count == 0 ? 0 : Cells.Keys.Max(k => k.Col);
}

public static class SimpleXlsxReader
{
    public static SimpleXlsxSheet ReadFirstSheet(Stream xlsxStream)
    {
        xlsxStream.Position = 0;
        using var zip = new ZipArchive(xlsxStream, ZipArchiveMode.Read, leaveOpen: true);

        var sharedStrings = LoadSharedStrings(zip);
        var sheetPath = ResolveAllSheetPaths(zip).FirstOrDefault().Path;
        if (string.IsNullOrEmpty(sheetPath)) throw new InvalidOperationException("Worksheet not found.");

        var sheetEntry = zip.GetEntry(sheetPath)
            ?? throw new InvalidOperationException($"Worksheet not found: {sheetPath}");

        using var sheetStream = sheetEntry.Open();
        var xdoc = XDocument.Load(sheetStream);
        var ns = xdoc.Root?.Name.Namespace ?? XNamespace.None;

        var result = new SimpleXlsxSheet();
        var cells = xdoc.Descendants(ns + "c");
        foreach (var cell in cells)
        {
            var reference = (string?)cell.Attribute("r");
            if (string.IsNullOrWhiteSpace(reference))
                continue;

            if (!TryParseA1(reference, out var row, out var col))
                continue;

            var type = (string?)cell.Attribute("t");
            var value = ReadCellValue(cell, ns, type, sharedStrings);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            result.Cells[(row, col)] = value.Trim();
        }

        return result;
    }

    public static IReadOnlyList<(string SheetName, SimpleXlsxSheet Sheet)> ReadAllSheets(Stream xlsxStream)
    {
        xlsxStream.Position = 0;
        using var zip = new ZipArchive(xlsxStream, ZipArchiveMode.Read, leaveOpen: true);

        var sharedStrings = LoadSharedStrings(zip);
        var sheetDefinitions = ResolveAllSheetPaths(zip);
        
        var results = new List<(string SheetName, SimpleXlsxSheet Sheet)>();
        foreach (var def in sheetDefinitions)
        {
            var sheetEntry = zip.GetEntry(def.Path);
            if (sheetEntry == null) continue;

            using var sheetStream = sheetEntry.Open();
            var xdoc = XDocument.Load(sheetStream);
            var ns = xdoc.Root?.Name.Namespace ?? XNamespace.None;

            var sheetResult = new SimpleXlsxSheet();
            var cells = xdoc.Descendants(ns + "c");
            foreach (var cell in cells)
            {
                var reference = (string?)cell.Attribute("r");
                if (string.IsNullOrWhiteSpace(reference)) continue;
                if (!TryParseA1(reference, out var row, out var col)) continue;
                var type = (string?)cell.Attribute("t");
                var value = ReadCellValue(cell, ns, type, sharedStrings);
                if (string.IsNullOrWhiteSpace(value)) continue;
                sheetResult.Cells[(row, col)] = value.Trim();
            }

            results.Add((def.Name, sheetResult));
        }

        return results;
    }

    public static bool TryParseDecimal(string? raw, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var s = raw.Trim();
        // Basic normalization for common Excel/text formats.
        s = s.Replace("₱", "", StringComparison.Ordinal);
        s = s.Replace(",", "", StringComparison.Ordinal);

        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static string ReadCellValue(XElement cell, XNamespace ns, string? type, IReadOnlyList<string> sharedStrings)
    {
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
        {
            var sharedIndexText = cell.Element(ns + "v")?.Value;
            if (int.TryParse(sharedIndexText, out var sharedIndex) &&
                sharedIndex >= 0 &&
                sharedIndex < sharedStrings.Count)
            {
                return sharedStrings[sharedIndex];
            }

            return string.Empty;
        }

        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            var inlineString = cell.Element(ns + "is");
            if (inlineString != null)
            {
                var text = string.Concat(inlineString.Descendants(ns + "t").Select(t => t.Value));
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            return string.Empty;
        }

        return cell.Element(ns + "v")?.Value ?? string.Empty;
    }

    private static List<string> LoadSharedStrings(ZipArchive zip)
    {
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry == null)
            return new List<string>();

        using var stream = entry.Open();
        var xdoc = XDocument.Load(stream);
        var ns = xdoc.Root?.Name.Namespace ?? XNamespace.None;

        return xdoc.Descendants(ns + "si")
            .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value)))
            .ToList();
    }

    private static IReadOnlyList<(string Name, string Path)> ResolveAllSheetPaths(ZipArchive zip)
    {
        var workbookEntry = zip.GetEntry("xl/workbook.xml")
            ?? throw new InvalidOperationException("Invalid workbook: xl/workbook.xml not found.");
        var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels")
            ?? throw new InvalidOperationException("Invalid workbook: xl/_rels/workbook.xml.rels not found.");

        XDocument workbookDoc;
        using (var stream = workbookEntry.Open())
            workbookDoc = XDocument.Load(stream);

        XDocument relsDoc;
        using (var stream = relsEntry.Open())
            relsDoc = XDocument.Load(stream);

        var wbNs = workbookDoc.Root?.Name.Namespace ?? XNamespace.None;
        XNamespace relNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var relRootNs = relsDoc.Root?.Name.Namespace ?? XNamespace.None;
        var relElements = relsDoc.Descendants(relRootNs + "Relationship").ToList();

        var results = new List<(string Name, string Path)>();

        foreach (var sheet in workbookDoc.Descendants(wbNs + "sheet"))
        {
            var name = (string?)sheet.Attribute("name") ?? "Sheet";
            var relId = (string?)sheet.Attribute(relNs + "id");
            if (string.IsNullOrEmpty(relId)) continue;

            var rel = relElements.FirstOrDefault(r => string.Equals((string?)r.Attribute("Id"), relId, StringComparison.Ordinal));
            if (rel == null) continue;

            var target = (string?)rel.Attribute("Target");
            if (string.IsNullOrEmpty(target)) continue;

            var normalized = target.Replace('\\', '/');
            if (normalized.StartsWith("/"))
                normalized = normalized.TrimStart('/');
            else if (!normalized.StartsWith("xl/", StringComparison.OrdinalIgnoreCase))
                normalized = $"xl/{normalized}";

            results.Add((name, normalized));
        }

        if (results.Count == 0) throw new InvalidOperationException("Workbook has no valid sheets.");

        return results;
    }

    private static bool TryParseA1(string reference, out int row, out int col)
    {
        row = 0;
        col = 0;
        if (string.IsNullOrWhiteSpace(reference))
            return false;

        int i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
        {
            col = (col * 26) + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            i++;
        }

        if (col <= 0)
            return false;

        return int.TryParse(reference[i..], NumberStyles.Integer, CultureInfo.InvariantCulture, out row) && row > 0;
    }
}
