using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace PlaywrightTestStudio;

/// <summary>
/// The results file written at the end of a run: how every test case in the
/// project last did, row of test data by row of test data.
///
/// Sheets:
///   Summary  - the headline numbers, the charts, and the tables behind them
///   Results  - one line per test case per data row
///   Checks   - one line per assertion, with what the page actually showed
///
/// ClosedXML writes the cells; it cannot draw charts, so those are added to the
/// saved file through the Open XML SDK it is built on.
/// </summary>
internal static class ResultsBook
{
    private const string SummarySheet = "Summary";

    private const string Passed = "Passed";
    private const string Failed = "Failed";
    private const string Error = "Error";
    private const string NotRun = "Not run";

    private static readonly string[] Outcomes = { Passed, Failed, Error, NotRun };

    // One colour per outcome, the same in every chart. They sit beside a
    // written label everywhere, so the colour never has to carry the meaning
    // alone.
    private static readonly Dictionary<string, string> OutcomeColor = new()
    {
        [Passed] = "0CA30C",
        [Failed] = "D03B3B",
        [Error] = "EC835A",
        [NotRun] = "B4B2AD"
    };

    private const string DurationColor = "005A9E";

    private static readonly XLColor HeaderFill = XLColor.FromArgb(0, 90, 158);
    private static readonly XLColor Muted = XLColor.FromArgb(96, 96, 96);
    private static readonly XLColor TileFill = XLColor.FromArgb(243, 244, 246);

    /// <summary>One test case on one row of test data, and how that went.</summary>
    private sealed record Line(TestCase Test, TestResult? Result)
    {
        public string Outcome => Result?.Status is Passed or Failed or Error ? Result!.Status : NotRun;
    }

    private sealed record Chart(string Xml, int FromColumn, int FromRow, int ToColumn, int ToRow);

    private sealed record Series(string NameCell, string Values, string Color);

    /// <summary>
    /// Writes the results of every test case in <paramref name="tests"/> and
    /// returns the path of the file.
    /// </summary>
    public static string Write(ProjectStore store, IReadOnlyList<TestCase> tests, string? path = null)
    {
        path ??= store.NewResultsFile();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var ordered = tests.OrderBy(t => t.CreatedUtc).ToList();
        var lines = ordered.SelectMany(LinesOf).ToList();
        List<Chart> charts;

        using (var book = new XLWorkbook())
        {
            charts = WriteSummary(book, store, ordered, lines);
            WriteResults(book, lines);
            WriteChecks(book, lines);

            book.SaveAs(path);
        }

        AddCharts(path, charts);

        return path;
    }

    private static IEnumerable<Line> LinesOf(TestCase test)
    {
        var results = test.LastResults.Count > 0
            ? test.LastResults
            : test.LastResult is null ? new List<TestResult>() : new List<TestResult> { test.LastResult };

        return results.Count > 0
            ? results.Select(r => new Line(test, r))
            : new[] { new Line(test, null) };
    }

    // -----------------------------------------------------------------------
    // Summary
    // -----------------------------------------------------------------------

    private static List<Chart> WriteSummary(
        XLWorkbook book, ProjectStore store, IReadOnlyList<TestCase> tests, IReadOnlyList<Line> lines)
    {
        var ws = book.Worksheets.Add(SummarySheet);

        int Count(string outcome) => lines.Count(l => l.Outcome == outcome);

        var ran = lines.Count - Count(NotRun);
        var seconds = lines.Sum(l => l.Result?.Seconds ?? 0);
        var lastRun = lines.Where(l => l.Result is not null).Select(l => l.Result!.RanUtc).DefaultIfEmpty().Max();

        ws.Cell(1, 1).Value = $"Test results - {store.Name}";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 16;
        ws.Cell(1, 1).Style.Font.FontColor = HeaderFill;

        ws.Cell(2, 1).Value = lastRun == default
            ? "Nothing has been run yet."
            : $"Last run {lastRun.ToLocalTime():yyyy-MM-dd HH:mm}    -    written {DateTime.Now:yyyy-MM-dd HH:mm}";
        ws.Cell(2, 1).Style.Font.FontColor = Muted;

        // The headline numbers, as a row of tiles.
        var tiles = new (string Label, XLCellValue Value, string Format)[]
        {
            ("Test cases", tests.Count, "0"),
            ("Data rows", lines.Count, "0"),
            (Passed, Count(Passed), "0"),
            (Failed, Count(Failed), "0"),
            (Error, Count(Error), "0"),
            (NotRun, Count(NotRun), "0"),
            ("Pass rate", ran == 0 ? (XLCellValue)"-" : (double)Count(Passed) / ran, "0%"),
            ("Time taken", Duration(seconds), "@")
        };

        for (var i = 0; i < tiles.Length; i++)
        {
            var label = ws.Cell(4, i + 1);
            label.Value = tiles[i].Label;
            label.Style.Font.FontColor = Muted;

            var value = ws.Cell(5, i + 1);
            value.Value = tiles[i].Value;
            value.Style.NumberFormat.Format = tiles[i].Format;
            value.Style.Font.Bold = true;
            value.Style.Font.FontSize = 18;
            value.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

            ws.Range(4, i + 1, 5, i + 1).Style.Fill.BackgroundColor = TileFill;
        }

        ws.Row(5).Height = 30;

        ws.Cell(6, 1).Value = "Failed: a check did not hold.   Error: a step could not be carried out, so the checks were never reached.";
        ws.Cell(6, 1).Style.Font.FontColor = Muted;
        ws.Cell(6, 1).Style.Font.Italic = true;

        // Charts sit under the tiles; the tables they are drawn from sit under
        // the charts. A chart of forty test cases needs more room than one of
        // three.
        var chartRows = Math.Clamp(tests.Count + 9, 16, 40);
        var firstChartRow = 7;                                  // zero-based: sheet row 8
        var secondChartRow = firstChartRow + chartRows + 1;
        var row = secondChartRow + chartRows + 3;

        Heading(ws.Cell(row, 1), "The numbers behind the charts");
        row += 2;

        // ---- outcome of every data row ------------------------------------
        Header(ws, row, "Outcome", "Data rows");
        ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        var outcomeHeader = row++;
        var outcomeFirst = row;

        foreach (var outcome in Outcomes)
        {
            ws.Cell(row, 1).Value = outcome;
            ws.Cell(row, 2).Value = Count(outcome);
            row++;
        }

        var outcomeLast = row - 1;
        row += 2;

        // ---- each test case ------------------------------------------------
        Header(ws, row, "Test case", Passed, Failed, Error, NotRun, "Seconds", "Result");
        ws.Range(row, 2, row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        var caseHeader = row++;
        var caseFirst = row;

        foreach (var test in tests)
        {
            var own = lines.Where(l => l.Test == test).ToList();

            ws.Cell(row, 1).Value = test.Name;

            for (var c = 0; c < Outcomes.Length; c++)
            {
                ws.Cell(row, 2 + c).Value = own.Count(l => l.Outcome == Outcomes[c]);
            }

            ws.Cell(row, 6).Value = Math.Round(own.Sum(l => l.Result?.Seconds ?? 0), 1);
            ws.Cell(row, 6).Style.NumberFormat.Format = "0.0";

            Status(ws.Cell(row, 7), Overall(own));
            row++;
        }

        var caseLast = Math.Max(caseFirst, row - 1);

        ws.Column(1).Width = 34;
        ws.Columns(2, 8).Width = 14;
        ws.ShowGridLines = false;

        string Cell(string column, int r) => $"{SummarySheet}!${column}${r}";
        string Range(string column, int first, int last) => $"{SummarySheet}!${column}${first}:${column}${last}";

        var mostRows = tests.Count == 0 ? 0 : tests.Max(t => lines.Count(l => l.Test == t));

        return new List<Chart>
        {
            new(BarChartXml(
                    title: "How the data rows did",
                    horizontal: false,
                    stacked: false,
                    categories: Range("A", outcomeFirst, outcomeLast),
                    series: new[] { new Series(Cell("B", outcomeHeader), Range("B", outcomeFirst, outcomeLast), OutcomeColor[Passed]) },
                    pointColors: Outcomes.Select(o => OutcomeColor[o]).ToList(),
                    labelFormat: "0;;",
                    wholeSteps: Outcomes.Max(Count) <= 8),
                FromColumn: 0, FromRow: firstChartRow, ToColumn: 3, ToRow: firstChartRow + chartRows),

            new(BarChartXml(
                    title: "Each test case, by outcome of its data rows",
                    horizontal: true,
                    stacked: true,
                    categories: Range("A", caseFirst, caseLast),
                    series: Outcomes.Select((o, i) => new Series(
                        Cell(Column(2 + i), caseHeader), Range(Column(2 + i), caseFirst, caseLast), OutcomeColor[o])).ToList(),
                    pointColors: null,
                    labelFormat: null,
                    wholeSteps: mostRows <= 8),
                FromColumn: 3, FromRow: firstChartRow, ToColumn: 8, ToRow: firstChartRow + chartRows),

            new(BarChartXml(
                    title: "Time taken by each test case (seconds)",
                    horizontal: true,
                    stacked: false,
                    categories: Range("A", caseFirst, caseLast),
                    series: new[] { new Series(Cell("F", caseHeader), Range("F", caseFirst, caseLast), DurationColor) },
                    pointColors: null,
                    labelFormat: "0.0;;",
                    wholeSteps: false),
                FromColumn: 0, FromRow: secondChartRow, ToColumn: 8, ToRow: secondChartRow + chartRows)
        };
    }

    /// <summary>A test case passed when every one of its rows did; it has not run until one of them has.</summary>
    private static string Overall(IReadOnlyList<Line> own)
    {
        if (own.All(l => l.Outcome == NotRun))
        {
            return NotRun;
        }

        return own.Any(l => l.Outcome == Error) ? Error
            : own.Any(l => l.Outcome == Failed) ? Failed
            : own.Any(l => l.Outcome == NotRun) ? NotRun
            : Passed;
    }

    private static string Duration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);

        return span.TotalMinutes >= 1
            ? $"{(int)span.TotalMinutes} min {span.Seconds} s"
            : $"{seconds:0.0} s";
    }

    // -----------------------------------------------------------------------
    // Results and checks
    // -----------------------------------------------------------------------

    private static void WriteResults(XLWorkbook book, IReadOnlyList<Line> lines)
    {
        var ws = book.Worksheets.Add("Results");

        Header(ws, 1, "Test case", "Data set", "Result", "What happened", "Checks passed", "Checks",
            "Seconds", "Ran at", "Test data used", "Screenshot", "Link");

        var row = 2;

        foreach (var line in lines)
        {
            var result = line.Result;

            ws.Cell(row, 1).Value = line.Test.Name;
            ws.Cell(row, 2).Value = result?.DataSet ?? "";
            Status(ws.Cell(row, 3), line.Outcome);
            ws.Cell(row, 4).Value = result?.Message ?? (line.Test.Enabled ? "Has not been run." : "Disabled, so it is not run.");
            ws.Cell(row, 11).Value = line.Test.Url;

            if (result is not null)
            {
                ws.Cell(row, 5).Value = result.Assertions.Count(a => a.Passed);
                ws.Cell(row, 6).Value = result.Assertions.Count;
                ws.Cell(row, 7).Value = result.Seconds;
                ws.Cell(row, 7).Style.NumberFormat.Format = "0.0";
                ws.Cell(row, 8).Value = result.RanUtc == default ? "" : result.RanUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                ws.Cell(row, 9).Value = DataUsed(line.Test, result.DataSet);

                if (result.ScreenshotPath.Length > 0 && File.Exists(result.ScreenshotPath))
                {
                    ws.Cell(row, 10).Value = "Open";
                    ws.Cell(row, 10).SetHyperlink(new XLHyperlink(result.ScreenshotPath));
                }
            }

            row++;
        }

        Finish(ws);
    }

    /// <summary>The values a data row supplied, so a failure can be read without the data file to hand.</summary>
    private static string DataUsed(TestCase test, string dataSet)
    {
        var set = test.DataSets.FirstOrDefault(d => d.Name == dataSet);

        return set is null
            ? ""
            : string.Join("; ", test.Parameters()
                .Where(set.Values.ContainsKey)
                .Select(p => $"{p} = {set.Values[p]}"));
    }

    private static void WriteChecks(XLWorkbook book, IReadOnlyList<Line> lines)
    {
        var ws = book.Worksheets.Add("Checks");

        Header(ws, 1, "Test case", "Data set", "Check", "Result", "What the page showed");

        var row = 2;

        foreach (var line in lines.Where(l => l.Result is not null))
        {
            foreach (var assertion in line.Result!.Assertions)
            {
                ws.Cell(row, 1).Value = line.Test.Name;
                ws.Cell(row, 2).Value = line.Result.DataSet;
                ws.Cell(row, 3).Value = assertion.Description;
                Status(ws.Cell(row, 4), assertion.Passed ? Passed : Failed);
                ws.Cell(row, 5).Value = assertion.Actual;
                row++;
            }
        }

        Finish(ws);
    }

    // -----------------------------------------------------------------------
    // Cell plumbing
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

    private static void Status(IXLCell cell, string outcome)
    {
        cell.Value = outcome;
        cell.Style.Font.Bold = true;
        cell.Style.Font.FontColor = outcome switch
        {
            Passed => XLColor.FromArgb(16, 124, 16),
            Failed or Error => XLColor.FromArgb(196, 43, 28),
            _ => Muted
        };
    }

    private static void Finish(IXLWorksheet ws)
    {
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, 500, 8, 70);
    }

    /// <summary>Column letter for a one-based column number, as far as Z - the summary never goes further.</summary>
    private static string Column(int number) => ((char)('A' + number - 1)).ToString();

    // -----------------------------------------------------------------------
    // Charts
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds the charts to the summary sheet of the saved file. Each chart is a
    /// part of its own, placed on the sheet through a drawing part.
    /// </summary>
    private static void AddCharts(string path, IReadOnlyList<Chart> charts)
    {
        using var document = SpreadsheetDocument.Open(path, isEditable: true);

        var workbook = document.WorkbookPart!;
        var sheet = workbook.Workbook.Descendants<S.Sheet>().First(s => s.Name == SummarySheet);
        var worksheet = (WorksheetPart)workbook.GetPartById(sheet.Id!);
        var drawings = worksheet.AddNewPart<DrawingsPart>();

        var anchors = new StringBuilder();

        for (var i = 0; i < charts.Count; i++)
        {
            var part = drawings.AddNewPart<ChartPart>();
            Feed(part, charts[i].Xml);

            anchors.Append(AnchorXml(charts[i], drawings.GetIdOfPart(part), i + 1));
        }

        Feed(drawings,
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
            anchors +
            "</xdr:wsDr>");

        // The schema fixes where <drawing> may sit among a worksheet's
        // children: after the cells and page setup, before these.
        var drawing = new S.Drawing { Id = worksheet.GetIdOfPart(drawings) };

        var after = worksheet.Worksheet.ChildElements.FirstOrDefault(e =>
            e is S.LegacyDrawing or S.LegacyDrawingHeaderFooter or S.Picture or S.OleObjects or S.Controls
                or S.WebPublishItems or S.TableParts or S.WorksheetExtensionList);

        if (after is null)
        {
            worksheet.Worksheet.Append(drawing);
        }
        else
        {
            worksheet.Worksheet.InsertBefore(drawing, after);
        }

        worksheet.Worksheet.Save();
    }

    private static void Feed(OpenXmlPart part, string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        part.FeedData(stream);
    }

    private static string AnchorXml(Chart chart, string relationshipId, int number) =>
        "<xdr:twoCellAnchor>" +
        $"<xdr:from><xdr:col>{chart.FromColumn}</xdr:col><xdr:colOff>0</xdr:colOff>" +
        $"<xdr:row>{chart.FromRow}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:from>" +
        $"<xdr:to><xdr:col>{chart.ToColumn}</xdr:col><xdr:colOff>0</xdr:colOff>" +
        $"<xdr:row>{chart.ToRow}</xdr:row><xdr:rowOff>0</xdr:rowOff></xdr:to>" +
        "<xdr:graphicFrame macro=\"\">" +
        $"<xdr:nvGraphicFramePr><xdr:cNvPr id=\"{number + 1}\" name=\"Chart {number}\"/><xdr:cNvGraphicFramePr/></xdr:nvGraphicFramePr>" +
        "<xdr:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/></xdr:xfrm>" +
        "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/chart\">" +
        "<c:chart xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" " +
        "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
        $"r:id=\"{relationshipId}\"/>" +
        "</a:graphicData></a:graphic>" +
        "</xdr:graphicFrame>" +
        "<xdr:clientData/>" +
        "</xdr:twoCellAnchor>";

    /// <summary>
    /// A bar chart reading its numbers from the sheet. Upright or lying down,
    /// one series or several stacked; the element order is the one the chart
    /// schema insists on.
    /// </summary>
    private static string BarChartXml(
        string title,
        bool horizontal,
        bool stacked,
        string categories,
        IReadOnlyList<Series> series,
        IReadOnlyList<string>? pointColors,
        string? labelFormat,
        bool wholeSteps)
    {
        const string hairline = "E3E3E0";
        const string ink = "404040";

        string Fill(string color) =>
            $"<c:spPr><a:solidFill><a:srgbClr val=\"{color}\"/></a:solidFill>" +
            // A thin white edge keeps stacked segments and neighbouring bars apart.
            "<a:ln w=\"12700\"><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill></a:ln></c:spPr>";

        var xml = new StringBuilder();

        xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        xml.Append("<c:chartSpace xmlns:c=\"http://schemas.openxmlformats.org/drawingml/2006/chart\" ");
        xml.Append("xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" ");
        xml.Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
        xml.Append("<c:roundedCorners val=\"0\"/>");
        xml.Append("<c:chart>");

        xml.Append("<c:title><c:tx><c:rich><a:bodyPr/><a:lstStyle/><a:p>");
        xml.Append("<a:pPr><a:defRPr sz=\"1100\" b=\"1\"/></a:pPr>");
        xml.Append($"<a:r><a:rPr lang=\"en-US\" sz=\"1100\" b=\"1\"/><a:t>{System.Security.SecurityElement.Escape(title)}</a:t></a:r>");
        xml.Append("</a:p></c:rich></c:tx><c:overlay val=\"0\"/></c:title>");
        xml.Append("<c:autoTitleDeleted val=\"0\"/>");

        xml.Append("<c:plotArea><c:layout/>");
        xml.Append("<c:barChart>");
        xml.Append($"<c:barDir val=\"{(horizontal ? "bar" : "col")}\"/>");
        xml.Append($"<c:grouping val=\"{(stacked ? "stacked" : "clustered")}\"/>");
        xml.Append("<c:varyColors val=\"0\"/>");

        for (var i = 0; i < series.Count; i++)
        {
            xml.Append("<c:ser>");
            xml.Append($"<c:idx val=\"{i}\"/><c:order val=\"{i}\"/>");
            xml.Append($"<c:tx><c:strRef><c:f>{series[i].NameCell}</c:f></c:strRef></c:tx>");
            xml.Append(Fill(series[i].Color));
            xml.Append("<c:invertIfNegative val=\"0\"/>");

            if (pointColors is not null)
            {
                for (var p = 0; p < pointColors.Count; p++)
                {
                    xml.Append($"<c:dPt><c:idx val=\"{p}\"/><c:invertIfNegative val=\"0\"/><c:bubble3D val=\"0\"/>");
                    xml.Append(Fill(pointColors[p]));
                    xml.Append("</c:dPt>");
                }
            }

            if (labelFormat is not null)
            {
                xml.Append("<c:dLbls>");
                xml.Append($"<c:numFmt formatCode=\"{labelFormat}\" sourceLinked=\"0\"/>");
                xml.Append("<c:spPr><a:noFill/><a:ln><a:noFill/></a:ln></c:spPr>");
                xml.Append("<c:showLegendKey val=\"0\"/><c:showVal val=\"1\"/><c:showCatName val=\"0\"/>");
                xml.Append("<c:showSerName val=\"0\"/><c:showPercent val=\"0\"/><c:showBubbleSize val=\"0\"/>");
                xml.Append("</c:dLbls>");
            }

            xml.Append($"<c:cat><c:strRef><c:f>{categories}</c:f></c:strRef></c:cat>");
            xml.Append($"<c:val><c:numRef><c:f>{series[i].Values}</c:f></c:numRef></c:val>");
            xml.Append("</c:ser>");
        }

        xml.Append("<c:gapWidth val=\"90\"/>");

        if (stacked)
        {
            xml.Append("<c:overlap val=\"100\"/>");
        }

        xml.Append("<c:axId val=\"500001\"/><c:axId val=\"500002\"/>");
        xml.Append("</c:barChart>");

        // Categories. Lying down, the first test case belongs at the top, which
        // means counting the axis backwards.
        xml.Append("<c:catAx><c:axId val=\"500001\"/>");
        xml.Append($"<c:scaling><c:orientation val=\"{(horizontal ? "maxMin" : "minMax")}\"/></c:scaling>");
        xml.Append($"<c:delete val=\"0\"/><c:axPos val=\"{(horizontal ? "l" : "b")}\"/>");
        xml.Append("<c:numFmt formatCode=\"General\" sourceLinked=\"0\"/>");
        xml.Append("<c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
        xml.Append($"<c:spPr><a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"{hairline}\"/></a:solidFill></a:ln></c:spPr>");
        xml.Append("<c:crossAx val=\"500002\"/><c:crosses val=\"autoZero\"/>");
        xml.Append("<c:auto val=\"1\"/><c:lblAlgn val=\"ctr\"/><c:lblOffset val=\"100\"/><c:noMultiLvlLbl val=\"0\"/>");
        xml.Append("</c:catAx>");

        // Values. With the categories counted backwards, the scale has to cross
        // at their far end to stay along the bottom.
        xml.Append("<c:valAx><c:axId val=\"500002\"/>");
        xml.Append("<c:scaling><c:orientation val=\"minMax\"/><c:min val=\"0\"/></c:scaling>");
        xml.Append($"<c:delete val=\"0\"/><c:axPos val=\"{(horizontal ? "b" : "l")}\"/>");
        xml.Append($"<c:majorGridlines><c:spPr><a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"{hairline}\"/></a:solidFill></a:ln></c:spPr></c:majorGridlines>");
        xml.Append("<c:numFmt formatCode=\"General\" sourceLinked=\"0\"/>");
        xml.Append("<c:majorTickMark val=\"none\"/><c:minorTickMark val=\"none\"/><c:tickLblPos val=\"nextTo\"/>");
        xml.Append("<c:spPr><a:ln><a:noFill/></a:ln></c:spPr>");
        xml.Append($"<c:crossAx val=\"500001\"/><c:crosses val=\"{(horizontal ? "max" : "autoZero")}\"/>");
        xml.Append("<c:crossBetween val=\"between\"/>");

        if (wholeSteps)
        {
            // Counts of a handful: no half test cases on the scale.
            xml.Append("<c:majorUnit val=\"1\"/>");
        }

        xml.Append("</c:valAx>");
        xml.Append("</c:plotArea>");

        // One series is named by the title. Several need a legend to be told apart.
        if (series.Count > 1)
        {
            xml.Append("<c:legend><c:legendPos val=\"b\"/><c:overlay val=\"0\"/></c:legend>");
        }

        xml.Append("<c:plotVisOnly val=\"1\"/><c:dispBlanksAs val=\"gap\"/>");
        xml.Append("</c:chart>");

        xml.Append("<c:spPr><a:solidFill><a:srgbClr val=\"FFFFFF\"/></a:solidFill>");
        xml.Append($"<a:ln w=\"6350\"><a:solidFill><a:srgbClr val=\"{hairline}\"/></a:solidFill></a:ln></c:spPr>");
        xml.Append("<c:txPr><a:bodyPr/><a:lstStyle/><a:p><a:pPr>");
        xml.Append($"<a:defRPr sz=\"900\"><a:solidFill><a:srgbClr val=\"{ink}\"/></a:solidFill></a:defRPr>");
        xml.Append("</a:pPr><a:endParaRPr lang=\"en-US\"/></a:p></c:txPr>");
        xml.Append("</c:chartSpace>");

        return xml.ToString();
    }
}
