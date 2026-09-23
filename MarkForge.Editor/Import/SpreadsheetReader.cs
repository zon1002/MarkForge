using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ExcelDataReader;

namespace MarkForge.Editor.Import;

/// <summary>A worksheet reduced to its cell text — all a Markdown table can carry anyway.</summary>
internal sealed record SheetData(string Name, List<IReadOnlyList<string>> Rows);

/// <summary>
/// Reads spreadsheet files dropped onto the editor: .xlsx/.xlsm/.xls through ExcelDataReader,
/// .ods (OpenDocument, which ExcelDataReader doesn't cover) by walking its content.xml, and
/// .csv/.tsv as delimited text. Cell values come back as their displayed text where the
/// format makes that cheap (dates as yyyy-MM-dd, numbers invariant) rather than raw doubles.
/// </summary>
internal static class SpreadsheetReader
{
    private static readonly string[] ExcelExtensions = { ".xlsx", ".xlsm", ".xls" };
    private static readonly string[] DelimitedExtensions = { ".csv", ".tsv" };

    static SpreadsheetReader()
    {
        // Legacy .xls (BIFF) stores text in ANSI code pages that .NET Core doesn't ship
        // enabled by default.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static bool IsSupported(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ExcelExtensions.Contains(ext) || DelimitedExtensions.Contains(ext) || ext == ".ods";
    }

    public static List<SheetData> Read(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (DelimitedExtensions.Contains(ext)) return new List<SheetData> { ReadDelimited(path, ext) };
        if (ext == ".ods") return ReadOds(path);
        return ReadExcel(path);
    }

    private static SheetData ReadDelimited(string path, string ext)
    {
        string text = ReadTextDetectEncoding(path);
        char delimiter = ext == ".tsv" ? '\t' : MarkdownTable.GuessDelimiter(text);
        return new SheetData(Path.GetFileNameWithoutExtension(path), MarkdownTable.ParseDelimited(text, delimiter));
    }

    /// <summary>CSV exported by Excel on a Traditional-Chinese Windows is often Big5 (no BOM),
    /// so fall back to the system ANSI code page when the bytes aren't valid UTF-8.</summary>
    private static string ReadTextDetectEncoding(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            string text = strictUtf8.GetString(bytes);
            return text.TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            var ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            return ansi.GetString(bytes);
        }
    }

    private static List<SheetData> ReadExcel(string path)
    {
        var sheets = new List<SheetData>();
        // FileShare.ReadWrite: the workbook is very often still open in Excel while dragged.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);
        do
        {
            var rows = new List<IReadOnlyList<string>>();
            while (reader.Read())
            {
                var row = new string[reader.FieldCount];
                for (int c = 0; c < reader.FieldCount; c++)
                    row[c] = FormatCell(reader.GetValue(c));
                rows.Add(row);
            }
            sheets.Add(new SheetData(reader.Name, rows));
        } while (reader.NextResult());
        return sheets;
    }

    private static string FormatCell(object? value) => value switch
    {
        null => string.Empty,
        DateTime dt when dt.TimeOfDay == TimeSpan.Zero => dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        double d => d.ToString("0.##########", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static readonly XNamespace OdsTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace OdsText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";

    private static List<SheetData> ReadOds(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("content.xml") ?? throw new InvalidDataException("ODS 檔案缺少 content.xml");
        XDocument doc;
        using (var content = entry.Open()) doc = XDocument.Load(content);

        var sheets = new List<SheetData>();
        foreach (var table in doc.Descendants(OdsTable + "table"))
        {
            var rows = new List<IReadOnlyList<string>>();
            foreach (var rowEl in table.Descendants(OdsTable + "table-row"))
            {
                var row = new List<string>();
                foreach (var cell in rowEl.Elements())
                {
                    if (cell.Name != OdsTable + "table-cell" && cell.Name != OdsTable + "covered-table-cell") continue;
                    string text = string.Join("\n", cell.Elements(OdsText + "p").Select(p => p.Value));
                    // Repeat counts pad out the sheet's used range (often to 1024 columns /
                    // a million rows); they're only honoured when the cell has content —
                    // Normalize() trims the trailing empties anyway.
                    int repeat = ParseRepeat(cell.Attribute(OdsTable + "number-columns-repeated"));
                    for (int i = 0; i < (text.Length > 0 ? repeat : Math.Min(repeat, 1)); i++) row.Add(text);
                }
                int rowRepeat = ParseRepeat(rowEl.Attribute(OdsTable + "number-rows-repeated"));
                bool hasContent = row.Any(c => c.Length > 0);
                for (int i = 0; i < (hasContent ? rowRepeat : 1); i++) rows.Add(row);
            }
            sheets.Add(new SheetData(table.Attribute(OdsTable + "name")?.Value ?? "Sheet", rows));
        }
        return sheets;
    }

    private static int ParseRepeat(XAttribute? attr)
        => attr is not null && int.TryParse(attr.Value, out int n) && n > 0 ? Math.Min(n, 1000) : 1;
}
