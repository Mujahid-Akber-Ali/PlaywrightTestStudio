using ClosedXML.Excel;

namespace PlaywrightTestStudio;

/// <summary>
/// The workbook written when the operator downloads a page's performance:
///
///   Overview   - the headline numbers as tiles, then the load timeline, the
///                paint and web-vital milestones, and the bytes that came down
///   Requests   - every kind of request grouped, then the slowest individual
///                ones, so a slow page points at what made it slow
///
/// The numbers are the browser's own, read straight from the page's Performance
/// API, so what lands in the sheet is what the dev-tools panel would show.
/// </summary>
internal static class PerfBook
{
    private static readonly XLColor HeaderFill = XLColor.FromArgb(0, 90, 158);
    private static readonly XLColor Muted = XLColor.FromArgb(96, 96, 96);
    private static readonly XLColor TileFill = XLColor.FromArgb(243, 244, 246);

    public static string Write(PerfReport report, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var book = new XLWorkbook();

        WriteOverview(book, report);
        WriteRequests(book, report);

        book.SaveAs(path);

        return path;
    }

    // -----------------------------------------------------------------------
    // Overview
    // -----------------------------------------------------------------------

    private static void WriteOverview(XLWorkbook book, PerfReport report)
    {
        var ws = book.Worksheets.Add("Overview");

        double Timed(IReadOnlyList<PerfMetric> group, string label) =>
            group.FirstOrDefault(m => m.Label == label)?.Value ?? 0;

        ws.Cell(1, 1).Value = $"Performance - {report.Host}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 16;
        ws.Cell(1, 1).Style.Font.FontColor = HeaderFill;

        ws.Cell(2, 1).Value = report.Url;
        ws.Cell(2, 1).Style.Font.FontColor = Muted;

        ws.Cell(3, 1).Value =
            $"Captured {report.CapturedUtc.ToLocalTime():yyyy-MM-dd HH:mm}" +
            (report.HttpStatus > 0 ? $"    -    HTTP {report.HttpStatus}" : "");
        ws.Cell(3, 1).Style.Font.FontColor = Muted;

        // Headline tiles: what someone glances at first.
        var cls = Timed(report.Experience, "Cumulative layout shift");

        var tiles = new (string Label, XLCellValue Value, string Format)[]
        {
            ("Page load", Seconds(Timed(report.Timeline, "Page load")), "@"),
            ("First byte", Seconds(Timed(report.Timeline, "Time to first byte")), "@"),
            ("First paint", Seconds(Timed(report.Experience, "First contentful paint")), "@"),
            ("Largest paint", Seconds(Timed(report.Experience, "Largest contentful paint")), "@"),
            ("Layout shift", cls, "0.000"),
            ("Requests", report.ByType.Sum(g => g.Count) + 1, "0"),
            ("Transferred", Bytes(Timed(report.Transfer, "Resources transferred")), "@")
        };

        for (var i = 0; i < tiles.Length; i++)
        {
            var label = ws.Cell(5, i + 1);
            label.Value = tiles[i].Label;
            label.Style.Font.FontColor = Muted;

            var value = ws.Cell(6, i + 1);
            value.Value = tiles[i].Value;
            value.Style.NumberFormat.Format = tiles[i].Format;
            value.Style.Font.Bold = true;
            value.Style.Font.FontSize = 16;

            ws.Range(5, i + 1, 6, i + 1).Style.Fill.BackgroundColor = TileFill;
        }

        ws.Row(6).Height = 28;

        var row = 8;

        row = Table(ws, row, "Load timeline", report.Timeline);
        row = Table(ws, row, "Paint and web vitals", report.Experience);
        Table(ws, row, "Bytes down the wire", report.Transfer);

        ws.Cell(row + report.Transfer.Count + 2, 1).Value =
            "Layout shift is a score, not a time: below 0.1 is good, above 0.25 is poor. " +
            "Timings are the browser's own, and show 0 where the server does not allow them to be read.";
        ws.Cell(row + report.Transfer.Count + 2, 1).Style.Font.FontColor = Muted;
        ws.Cell(row + report.Transfer.Count + 2, 1).Style.Font.Italic = true;

        ws.Column(1).Width = 30;
        ws.Columns(2, 7).Width = 15;
        ws.ShowGridLines = false;
    }

    /// <summary>One labelled block of metric / value rows. Returns the next free row.</summary>
    private static int Table(IXLWorksheet ws, int row, string title, IReadOnlyList<PerfMetric> metrics)
    {
        Heading(ws.Cell(row, 1), title);
        row++;

        Header(ws, row, "Measurement", "Value");
        ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        row++;

        foreach (var metric in metrics)
        {
            ws.Cell(row, 1).Value = metric.Label;

            var value = ws.Cell(row, 2);
            value.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            switch (metric.Unit)
            {
                case "ms":
                    value.Value = metric.Value;
                    value.Style.NumberFormat.Format = "#,##0 \"ms\"";
                    break;

                case "bytes":
                    value.Value = Bytes(metric.Value);
                    break;

                case "score":
                    value.Value = metric.Value;
                    value.Style.NumberFormat.Format = "0.000";
                    break;

                default:
                    value.Value = metric.Value;
                    value.Style.NumberFormat.Format = "#,##0";
                    break;
            }

            row++;
        }

        return row + 1;
    }

    // -----------------------------------------------------------------------
    // Requests
    // -----------------------------------------------------------------------

    private static void WriteRequests(XLWorkbook book, PerfReport report)
    {
        var ws = book.Worksheets.Add("Requests");

        Heading(ws.Cell(1, 1), "By kind");
        Header(ws, 2, "Kind", "Requests", "Transferred", "Total time");
        ws.Range(2, 2, 2, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

        var row = 3;

        foreach (var group in report.ByType)
        {
            ws.Cell(row, 1).Value = group.Type;
            ws.Cell(row, 2).Value = group.Count;
            ws.Cell(row, 3).Value = Bytes(group.Bytes);
            ws.Cell(row, 4).Value = group.TotalMs;
            ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0 \"ms\"";
            ws.Range(row, 2, row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            row++;
        }

        if (report.ByType.Count == 0)
        {
            ws.Cell(row, 1).Value = "No requests after the document itself.";
            ws.Cell(row, 1).Style.Font.FontColor = Muted;
            row++;
        }

        row += 2;

        Heading(ws.Cell(row, 1), "Slowest requests");
        row++;
        Header(ws, row, "Request", "Kind", "Time", "Transferred");
        ws.Range(row, 3, row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        row++;

        foreach (var resource in report.Slowest)
        {
            ws.Cell(row, 1).Value = Shorten(resource.Name);
            ws.Cell(row, 2).Value = resource.Type;
            ws.Cell(row, 3).Value = resource.DurationMs;
            ws.Cell(row, 3).Style.NumberFormat.Format = "#,##0 \"ms\"";
            ws.Cell(row, 4).Value = Bytes(resource.Bytes);
            ws.Range(row, 3, row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
            row++;
        }

        if (report.Slowest.Count == 0)
        {
            ws.Cell(row, 1).Value = "No individual requests were timed.";
            ws.Cell(row, 1).Style.Font.FontColor = Muted;
        }

        ws.Columns(1, 4).AdjustToContents(1, 500, 10, 90);
        ws.SheetView.FreezeRows(1);
    }

    // -----------------------------------------------------------------------
    // Plumbing
    // -----------------------------------------------------------------------

    private static void Heading(IXLCell cell, string text)
    {
        cell.Value = text;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontSize = 12;
    }

    private static void Header(IXLWorksheet ws, int row, params string[] names)
    {
        for (var c = 0; c < names.Length; c++)
        {
            var cell = ws.Cell(row, c + 1);
            cell.Value = names[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = HeaderFill;
        }
    }

    /// <summary>A whole URL is rarely worth a wide column; the tail names the file.</summary>
    private static string Shorten(string url)
    {
        if (url.Length <= 90)
        {
            return url;
        }

        var tail = url[^70..];
        var slash = tail.IndexOf('/');

        return "..." + (slash > 0 ? tail[slash..] : tail);
    }

    private static string Seconds(double milliseconds) =>
        milliseconds <= 0 ? "-" : $"{milliseconds / 1000:0.00} s";

    /// <summary>Bytes in the unit a person would read them in.</summary>
    public static string Bytes(double bytes)
    {
        if (bytes <= 0)
        {
            return "-";
        }

        return bytes switch
        {
            >= 1024 * 1024 => $"{bytes / (1024 * 1024):0.0} MB",
            >= 1024 => $"{bytes / 1024:0.0} KB",
            _ => $"{bytes:0} B"
        };
    }
}
