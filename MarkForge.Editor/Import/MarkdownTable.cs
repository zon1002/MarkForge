using System.Globalization;
using System.Text;

namespace MarkForge.Editor.Import;

/// <summary>
/// Turns a grid of cell strings (from CSV/TSV text, the clipboard, or a spreadsheet) into a
/// GFM pipe table. The first row becomes the header — GFM has no header-less table — and
/// columns are padded to a common display width (CJK counted as two) so the source stays
/// readable in the editor, not only in the preview.
/// </summary>
internal static class MarkdownTable
{
    /// <summary>Builds the table, or returns null when there's nothing tabular left after
    /// trimming empty trailing rows/columns.</summary>
    public static string? Build(IReadOnlyList<IReadOnlyList<string>> rawRows)
    {
        var rows = Normalize(rawRows);
        if (rows.Count == 0) return null;

        int columns = rows[0].Length;
        var cells = rows.Select(r => r.Select(EscapeCell).ToArray()).ToList();

        var rightAlign = new bool[columns];
        for (int c = 0; c < columns; c++)
        {
            var body = cells.Skip(1).Select(r => r[c]).Where(v => v.Length > 0).ToList();
            rightAlign[c] = body.Count > 0 && body.All(IsNumeric);
        }

        var widths = new int[columns];
        foreach (var row in cells)
        {
            for (int c = 0; c < columns; c++)
                widths[c] = Math.Max(widths[c], DisplayWidth(row[c]));
        }
        for (int c = 0; c < columns; c++) widths[c] = Math.Max(widths[c], 3);

        var sb = new StringBuilder();
        AppendRow(sb, cells[0], widths, rightAlign);

        sb.Append('|');
        for (int c = 0; c < columns; c++)
        {
            sb.Append(' ');
            sb.Append(rightAlign[c] ? new string('-', widths[c] - 1) + ":" : new string('-', widths[c]));
            sb.Append(" |");
        }
        sb.Append('\n');

        foreach (var row in cells.Skip(1)) AppendRow(sb, row, widths, rightAlign);
        return sb.ToString();
    }

    /// <summary>Parses delimited text (RFC 4180 quoting: "a ""b"" c", embedded newlines
    /// inside quotes). Excel puts exactly this on the clipboard with a tab delimiter.</summary>
    public static List<IReadOnlyList<string>> ParseDelimited(string text, char delimiter)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;
        bool fieldStarted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
                continue;
            }

            if (ch == '"' && !fieldStarted)
            {
                inQuotes = true;
                fieldStarted = true;
            }
            else if (ch == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
                rows.Add(row);
                row = new List<string>();
            }
            else
            {
                field.Append(ch);
                fieldStarted = true;
            }
        }

        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Guesses a CSV file's delimiter from its first line: whichever of , ; tab
    /// occurs most (outside quotes is close enough for a heuristic).</summary>
    public static char GuessDelimiter(string text)
    {
        int end = text.IndexOfAny(new[] { '\r', '\n' });
        string firstLine = end < 0 ? text : text[..end];
        char[] candidates = { ',', '\t', ';' };
        return candidates.OrderByDescending(c => firstLine.Count(ch => ch == c)).First();
    }

    /// <summary>
    /// True when plain text looks like a copied grid: at least two lines and every non-empty
    /// line carrying the same (non-zero) number of tabs. Lines may not start with a tab, which
    /// keeps tab-indented code from being mistaken for a table.
    /// </summary>
    public static bool LooksLikeTsv(string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length < 2) return false;

        int? tabs = null;
        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            if (line[0] == '\t' || line.Contains('"')) return false;
            int count = line.Count(ch => ch == '\t');
            if (count == 0) return false;
            tabs ??= count;
            if (tabs != count) return false;
        }
        return tabs is not null;
    }

    private static List<string[]> Normalize(IReadOnlyList<IReadOnlyList<string>> rawRows)
    {
        var rows = rawRows
            .Select(r => r.Select(c => (c ?? string.Empty).Trim()).ToArray())
            .ToList();

        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
        while (rows.Count > 0 && rows[0].All(string.IsNullOrEmpty)) rows.RemoveAt(0);
        if (rows.Count == 0) return rows;

        int columns = rows.Max(r => r.Length);
        while (columns > 0 && rows.All(r => r.Length < columns || string.IsNullOrEmpty(r[columns - 1]))) columns--;
        if (columns == 0) return new List<string[]>();

        return rows.Select(r =>
        {
            var padded = new string[columns];
            for (int c = 0; c < columns; c++) padded[c] = c < r.Length ? r[c] : string.Empty;
            return padded;
        }).ToList();
    }

    private static string EscapeCell(string value) => value
        .Replace("\r\n", "\n")
        .Replace("|", "\\|")
        .Replace("\n", "<br>");

    private static bool IsNumeric(string value)
    {
        string v = value.Replace(",", string.Empty).Replace("%", string.Empty).Replace("$", string.Empty).Trim();
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static void AppendRow(StringBuilder sb, string[] row, int[] widths, bool[] rightAlign)
    {
        sb.Append('|');
        for (int c = 0; c < row.Length; c++)
        {
            string pad = new string(' ', widths[c] - DisplayWidth(row[c]));
            sb.Append(' ');
            sb.Append(rightAlign[c] ? pad + row[c] : row[c] + pad);
            sb.Append(" |");
        }
        sb.Append('\n');
    }

    /// <summary>Monospace column width: East Asian wide/fullwidth characters take two cells.</summary>
    internal static int DisplayWidth(string s)
    {
        int width = 0;
        foreach (char ch in s)
        {
            width += ch switch
            {
                >= '\u1100' and <= '\u115F' => 2,
                >= '\u2E80' and <= '\uA4CF' => 2,
                >= '\uAC00' and <= '\uD7A3' => 2,
                >= '\uF900' and <= '\uFAFF' => 2,
                >= '\uFE30' and <= '\uFE4F' => 2,
                >= '\uFF00' and <= '\uFF60' => 2,
                >= '\uFFE0' and <= '\uFFE6' => 2,
                _ => 1,
            };
        }
        return width;
    }
}
