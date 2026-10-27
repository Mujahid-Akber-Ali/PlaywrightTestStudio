using ClosedXML.Excel;

namespace PlaywrightTestStudio;

/// <summary>
/// Writes a <see cref="ScrapeResult"/> to Excel: one sheet per table, the
/// paginated grid first. Every value is written as text, so a code like 007 or a
/// date like 2026-10-01 lands exactly as the page showed it rather than as a
/// number Excel decided to reformat.
/// </summary>
internal static class ScrapeBook
{
    private static readonly XLColor HeaderFill = XLColor.FromArgb(0, 90, 158);

    /// <summary>Writes the workbook and returns how many sheets it holds.</summary>
    public static int Write(ScrapeResult result, string path)
    {
        using var book = new XLWorkbook();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 1;

        // The grid the operator paged through is the one they came for; it has the
        // most data, so it leads.
        var tables = result.Tables
            .Where(t => t.Rows.Count > 0 || t.Headers.Count > 0)
            .OrderByDescending(t => t.Cells)
            .ToList();

        foreach (var table in tables)
        {
            WriteSheet(book, table, n++, used);
        }

        if (book.Worksheets.Count == 0)
        {
            var ws = book.Worksheets.Add("No data");
            ws.Cell(1, 1).Value = "No tables were found on the page.";
            ws.Cell(2, 1).Value = result.Url;
        }

        book.SaveAs(path);
        return book.Worksheets.Count;
    }

    private static void WriteSheet(XLWorkbook book, ScrapedTable table, int n, HashSet<string> used)
    {
        var ws = book.Worksheets.Add(SheetName(table.Title, n, used));

        var width = Math.Max(
            table.Headers.Count,
            table.Rows.Count > 0 ? table.Rows.Max(r => r.Count) : 0);

        var firstDataRow = 1;

        if (table.Headers.Count > 0)
        {
            for (var c = 0; c < table.Headers.Count; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = table.Headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = HeaderFill;
            }

            firstDataRow = 2;
        }

        // Force the data area to text before anything is written, so values are
        // kept verbatim. Done as one range rather than per cell to stay quick on
        // a grid of thousands of rows.
        if (width > 0 && table.Rows.Count > 0)
        {
            ws.Range(firstDataRow, 1, firstDataRow + table.Rows.Count - 1, width)
              .Style.NumberFormat.Format = "@";
        }

        var row = firstDataRow;

        foreach (var record in table.Rows)
        {
            for (var c = 0; c < record.Count; c++)
            {
                ws.Cell(row, c + 1).Value = record[c];
            }

            row++;
        }

        if (table.Headers.Count > 0)
        {
            ws.SheetView.FreezeRows(1);
        }

        // Measure against the first 200 rows only - enough to size the columns
        // without walking a huge grid end to end.
        ws.Columns().AdjustToContents(1, 200, 8, 80);
    }

    /// <summary>
    /// A sheet name Excel will accept: at most 31 characters, none of : \ / ? * [ ]
    /// and unique within the book.
    /// </summary>
    private static string SheetName(string title, int n, HashSet<string> used)
    {
        var clean = new string((title ?? "")
            .Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_')
            .ToArray())
            .Trim();

        if (clean.Length == 0)
        {
            clean = $"Table {n}";
        }

        var name = clean.Length <= 31 ? clean : clean[..31].Trim();
        var m = 2;

        while (!used.Add(name))
        {
            var suffix = $" ({m++})";
            name = (clean.Length <= 31 - suffix.Length ? clean : clean[..(31 - suffix.Length)].Trim()) + suffix;
        }

        return name;
    }
}
