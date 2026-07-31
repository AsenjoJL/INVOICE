using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace HazelInvoice.Services;

public sealed class SimpleXlsxSheetOptions
{
    public IReadOnlyDictionary<int, double> ColumnWidths { get; init; } = new Dictionary<int, double>();
    public bool AutoFitColumns { get; init; }
    public double MinimumColumnWidth { get; init; } = 10;
    public double MaximumColumnWidth { get; init; } = 42;
    public double ColumnPadding { get; init; } = 2;
}

public sealed class SimpleXlsxWorksheetDef
{
    public string SheetName { get; init; } = "Sheet";
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = Array.Empty<IReadOnlyList<string>>();
    public SimpleXlsxSheetOptions? Options { get; init; }
}

/// <summary>
/// Minimal .xlsx writer (single worksheet) with inline strings.
/// Avoids external dependencies (ClosedXML/EPPlus) for easier packaging/offline builds.
/// </summary>
public static class SimpleXlsxWriter
{
    public static byte[] WriteSingleSheet(string sheetName, IReadOnlyList<IReadOnlyList<string>> rows)
        => WriteSingleSheet(sheetName, rows, null);

    public static byte[] WriteSingleSheet(
        string sheetName,
        IReadOnlyList<IReadOnlyList<string>> rows,
        SimpleXlsxSheetOptions? options)
    {
        return WriteMultipleSheets(new[]
        {
            new SimpleXlsxWorksheetDef
            {
                SheetName = sheetName,
                Rows = rows,
                Options = options
            }
        });
    }

    public static byte[] WriteMultipleSheets(IReadOnlyList<SimpleXlsxWorksheetDef> sheets)
    {
        if (sheets == null || sheets.Count == 0)
            throw new ArgumentException("At least one sheet is required.", nameof(sheets));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteContentTypes(zip, sheets.Count);
            WriteRootRels(zip);
            WriteWorkbook(zip, sheets);
            WriteWorkbookRels(zip, sheets.Count);
            
            for (int i = 0; i < sheets.Count; i++)
            {
                var sheet = sheets[i];
                WriteSheet(zip, sheet.Rows, sheet.Options, i + 1);
            }
            
            WriteDocProps(zip);
        }

        return ms.ToArray();
    }

    private static void WriteContentTypes(ZipArchive zip, int sheetCount)
    {
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var doc = new XDocument(
            new XElement(ct + "Types",
                new XElement(ct + "Default",
                    new XAttribute("Extension", "rels"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ct + "Default",
                    new XAttribute("Extension", "xml"),
                    new XAttribute("ContentType", "application/xml")),
                new XElement(ct + "Override",
                    new XAttribute("PartName", "/xl/workbook.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"))
            )
        );

        for (int i = 1; i <= sheetCount; i++)
        {
            doc.Root!.Add(new XElement(ct + "Override",
                new XAttribute("PartName", $"/xl/worksheets/sheet{i}.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
        }

        doc.Root!.Add(
            new XElement(ct + "Override",
                new XAttribute("PartName", "/docProps/core.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.core-properties+xml")),
            new XElement(ct + "Override",
                new XAttribute("PartName", "/docProps/app.xml"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.extended-properties+xml"))
        );

        WriteXml(zip, "[Content_Types].xml", doc);
    }

    private static void WriteRootRels(ZipArchive zip)
    {
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var doc = new XDocument(
            new XElement(rel + "Relationships",
                new XElement(rel + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml"))
            )
        );

        WriteXml(zip, "_rels/.rels", doc);
    }

    private static void WriteWorkbook(ZipArchive zip, IReadOnlyList<SimpleXlsxWorksheetDef> sheets)
    {
        XNamespace ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        var sheetsElement = new XElement(ss + "sheets");
        for (int i = 0; i < sheets.Count; i++)
        {
            sheetsElement.Add(
                new XElement(ss + "sheet",
                    new XAttribute("name", string.IsNullOrWhiteSpace(sheets[i].SheetName) ? $"Sheet{i + 1}" : sheets[i].SheetName),
                    new XAttribute("sheetId", i + 1),
                    new XAttribute(r + "id", $"rId{i + 1}"))
            );
        }

        var doc = new XDocument(
            new XElement(ss + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", r),
                sheetsElement
            )
        );

        WriteXml(zip, "xl/workbook.xml", doc);
    }

    private static void WriteWorkbookRels(ZipArchive zip, int sheetCount)
    {
        XNamespace rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        var doc = new XDocument(
            new XElement(rel + "Relationships")
        );

        for (int i = 1; i <= sheetCount; i++)
        {
            doc.Root!.Add(
                new XElement(rel + "Relationship",
                    new XAttribute("Id", $"rId{i}"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", $"worksheets/sheet{i}.xml"))
            );
        }

        WriteXml(zip, "xl/_rels/workbook.xml.rels", doc);
    }

    private static void WriteSheet(
        ZipArchive zip,
        IReadOnlyList<IReadOnlyList<string>> rows,
        SimpleXlsxSheetOptions? options,
        int sheetIndex)
    {
        XNamespace ss = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        var sheetData = new XElement(ss + "sheetData");

        for (var r = 0; r < rows.Count; r++)
        {
            var rowValues = rows[r];
            var rowEl = new XElement(ss + "row", new XAttribute("r", r + 1));

            for (var c = 0; c < rowValues.Count; c++)
            {
                var value = rowValues[c] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var cellRef = $"{ToA1Col(c + 1)}{r + 1}";

                // Inline string: keeps file simple (no sharedStrings table).
                var cell = new XElement(ss + "c",
                    new XAttribute("r", cellRef),
                    new XAttribute("t", "inlineStr"),
                    new XElement(ss + "is",
                        new XElement(ss + "t", SanitizeForXml(value)))
                );

                rowEl.Add(cell);
            }

            sheetData.Add(rowEl);
        }

        var doc = new XDocument(
            new XElement(ss + "worksheet",
                BuildColumnsElement(ss, rows, options),
                sheetData
            )
        );

        WriteXml(zip, $"xl/worksheets/sheet{sheetIndex}.xml", doc);
    }

    private static XElement? BuildColumnsElement(
        XNamespace ss,
        IReadOnlyList<IReadOnlyList<string>> rows,
        SimpleXlsxSheetOptions? options)
    {
        var widths = ResolveColumnWidths(rows, options);
        if (widths.Count == 0)
            return null;

        var columns = widths
            .Where(pair => pair.Key > 0 && pair.Value > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair => new XElement(ss + "col",
                new XAttribute("min", pair.Key),
                new XAttribute("max", pair.Key),
                new XAttribute("width", pair.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("customWidth", 1)));

        return new XElement(ss + "cols", columns);
    }

    private static IReadOnlyDictionary<int, double> ResolveColumnWidths(
        IReadOnlyList<IReadOnlyList<string>> rows,
        SimpleXlsxSheetOptions? options)
    {
        if (options?.ColumnWidths != null && options.ColumnWidths.Count > 0)
            return options.ColumnWidths;

        if (options?.AutoFitColumns != true)
            return new Dictionary<int, double>();

        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        var widths = new Dictionary<int, double>();
        for (var col = 0; col < columnCount; col++)
        {
            var longestValue = rows
                .Select(row => col < row.Count ? row[col] ?? string.Empty : string.Empty)
                .DefaultIfEmpty(string.Empty)
                .Max(value => value.Length);

            var width = Math.Clamp(
                longestValue + options.ColumnPadding,
                options.MinimumColumnWidth,
                options.MaximumColumnWidth);
            widths[col + 1] = width;
        }

        return widths;
    }

    private static void WriteDocProps(ZipArchive zip)
    {
        // Core
        XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace dcterms = "http://purl.org/dc/terms/";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var core = new XDocument(
            new XElement(cp + "coreProperties",
                new XAttribute(XNamespace.Xmlns + "dc", dc),
                new XAttribute(XNamespace.Xmlns + "dcterms", dcterms),
                new XAttribute(XNamespace.Xmlns + "xsi", xsi),
                new XElement(dc + "creator", "HazelInvoice"),
                new XElement(cp + "lastModifiedBy", "HazelInvoice"),
                new XElement(dcterms + "created",
                    new XAttribute(xsi + "type", "dcterms:W3CDTF"),
                    DateTime.UtcNow.ToString("O")),
                new XElement(dcterms + "modified",
                    new XAttribute(xsi + "type", "dcterms:W3CDTF"),
                    DateTime.UtcNow.ToString("O"))
            )
        );

        WriteXml(zip, "docProps/core.xml", core);

        // App
        XNamespace ep = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
        XNamespace vt = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";
        var app = new XDocument(
            new XElement(ep + "Properties",
                new XAttribute(XNamespace.Xmlns + "vt", vt),
                new XElement(ep + "Application", "HazelInvoice")
            )
        );

        WriteXml(zip, "docProps/app.xml", app);
    }

    private static void WriteXml(ZipArchive zip, string path, XDocument doc)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        doc.Save(writer, SaveOptions.DisableFormatting);
    }

    private static string ToA1Col(int col)
    {
        var sb = new StringBuilder();
        var n = col;
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)('A' + (n % 26)));
            n /= 26;
        }
        return sb.ToString();
    }

    private static string SanitizeForXml(string value)
        => value.Replace("\u0000", string.Empty, StringComparison.Ordinal);
}
