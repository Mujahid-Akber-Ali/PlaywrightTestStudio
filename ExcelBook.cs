using ClosedXML.Excel;

namespace PlaywrightTestStudio;

/// <summary>
/// The workbook a project is downloaded to and uploaded from. It is the format
/// a tester works in: record once, download, copy the recorded row, type more
/// test data underneath it, upload, and every row runs.
///
/// Sheets:
///   How to use   - instructions, so the file explains itself
///   Test Cases   - one row per test case
///   Steps        - the actions, in order, with the data column each one reads
///   Assertions   - the checks that decide pass or fail
///   Data - ...   - one sheet per test case, one row per data set
///   Results      - what happened on the last run
/// </summary>
internal static class ExcelBook
{
    private const string TestCasesSheet = "Test Cases";
    private const string StepsSheet = "Steps";
    private const string AssertionsSheet = "Assertions";
    private const string ResultsSheet = "Results";
    private const string DataSheetPrefix = "Data";

    private static readonly XLColor HeaderFill = XLColor.FromArgb(0, 90, 158);

    internal sealed class ImportReport
    {
        public int TestCases { get; set; }
        public int NewTestCases { get; set; }
        public int Steps { get; set; }
        public int DataSets { get; set; }
        public int Assertions { get; set; }
        public List<string> Problems { get; } = new();

        public string Summary =>
            $"{TestCases} test case(s) read ({NewTestCases} new), " +
            $"{Steps} step(s), {DataSets} data set(s), {Assertions} assertion(s).";
    }

    // -----------------------------------------------------------------------
    // Download
    // -----------------------------------------------------------------------

    public static void Export(ProjectStore store, IReadOnlyList<TestCase> tests, string path)
    {
        using var book = new XLWorkbook();

        WriteInstructions(book, store);
        WriteTestCases(book, tests);
        WriteSteps(book, tests);
        WriteAssertions(book, tests);

        for (var i = 0; i < tests.Count; i++)
        {
            WriteData(book, tests[i], i + 1);
        }

        WriteResults(book, tests);

        book.SaveAs(path);
    }

    private static void WriteInstructions(XLWorkbook book, ProjectStore store)
    {
        var ws = book.Worksheets.Add("How to use");

        var lines = new (string Heading, string Body)[]
        {
            ($"Project: {store.Name}", $"Downloaded {DateTime.Now:yyyy-MM-dd HH:mm}"),
            ("", ""),
            ("What this file is for",
                "Each test case here was recorded in a browser. To run it against more data, " +
                "you do not need to record again - just add rows of data to its Data sheet and upload this file."),
            ("", ""),
            ("1. Find the Data sheet for your test case",
                "There is one 'Data - ...' sheet per test case. The first row is the data that was recorded."),
            ("2. Add a row per case you want to run",
                "Copy the recorded row, paste it underneath, give it a new name in the 'Data Set' column, " +
                "and change the values. Every column after 'Enabled' is one value the test enters."),
            ("3. Set Enabled",
                "TRUE means the row runs. FALSE means it is skipped. Leave a row out entirely and it never runs."),
            ("4. Upload this file",
                "In the studio, use 'Upload Excel'. Every enabled row runs as its own test, and each one passes " +
                "or fails on its own."),
            ("", ""),
            ("Expected values change too",
                "On the Assertions sheet, put a data column name in the 'Parameter' column and the expected " +
                "text will come from that column instead of being fixed. That is how row 1 can expect one result " +
                "and row 2 another."),
            ("", ""),
            ("What not to change",
                "Leave the 'Test ID' columns alone - they are how the studio matches this file back to its test " +
                "cases. Changing a Target on the Steps sheet changes which element the test drives, so only do " +
                "that if you know the page."),
            ("", ""),
            ("Results",
                "The Results sheet shows the last run at the time of download. It is read only - uploading does " +
                "not change any results.")
        };

        var row = 1;

        foreach (var (heading, body) in lines)
        {
            if (heading.Length > 0)
            {
                ws.Cell(row, 1).Value = heading;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = body;
            }

            row++;
        }

        ws.Column(1).Width = 36;
        ws.Column(2).Width = 110;
        ws.Column(2).Style.Alignment.WrapText = true;
    }

    private static void WriteTestCases(XLWorkbook book, IReadOnlyList<TestCase> tests)
    {
        var ws = book.Worksheets.Add(TestCasesSheet);

        Header(ws, "Test ID", "Test Name", "Link", "Enabled", "Data Sets", "Steps", "Assertions", "Source", "Notes");

        var row = 2;

        foreach (var test in tests)
        {
            ws.Cell(row, 1).Value = test.Id;
            ws.Cell(row, 2).Value = test.Name;
            ws.Cell(row, 3).Value = test.Url;
            ws.Cell(row, 4).Value = test.Enabled;
            ws.Cell(row, 5).Value = test.DataSets.Count;
            ws.Cell(row, 6).Value = test.Steps.Count;
            ws.Cell(row, 7).Value = test.Assertions.Count;
            ws.Cell(row, 8).Value = test.Source;
            ws.Cell(row, 9).Value = test.Notes;
            row++;
        }

        // Counts are a read-only summary; the real lists live on their own sheets.
        ws.Columns(5, 8).Style.Font.FontColor = XLColor.Gray;
        Finish(ws);
    }

    private static void WriteSteps(XLWorkbook book, IReadOnlyList<TestCase> tests)
    {
        var ws = book.Worksheets.Add(StepsSheet);

        Header(ws, "Test ID", "Test Name", "Step", "Use", "Action", "Target", "Label",
            "Recorded Value", "Parameter", "Fallback Text", "Wait After");

        var row = 2;

        foreach (var test in tests)
        {
            var n = 1;

            foreach (var step in test.Steps)
            {
                ws.Cell(row, 1).Value = test.Id;
                ws.Cell(row, 2).Value = test.Name;
                ws.Cell(row, 3).Value = n++;
                ws.Cell(row, 4).Value = step.Use;
                ws.Cell(row, 5).Value = step.Action;
                ws.Cell(row, 6).Value = step.Selector;
                ws.Cell(row, 7).Value = step.Label;
                ws.Cell(row, 8).Value = step.Value;
                ws.Cell(row, 9).Value = step.Parameter;
                ws.Cell(row, 10).Value = step.FallbackText;
                ws.Cell(row, 11).Value = step.WaitAfter;

                ws.Cell(row, 6).Style.Font.FontName = "Consolas";
                row++;
            }
        }

        Finish(ws);
    }

    private static void WriteAssertions(XLWorkbook book, IReadOnlyList<TestCase> tests)
    {
        var ws = book.Worksheets.Add(AssertionsSheet);

        Header(ws, "Test ID", "Test Name", "#", "Check", "Selector", "Expected", "Parameter");

        var row = 2;

        foreach (var test in tests)
        {
            var n = 1;

            foreach (var assertion in test.Assertions)
            {
                ws.Cell(row, 1).Value = test.Id;
                ws.Cell(row, 2).Value = test.Name;
                ws.Cell(row, 3).Value = n++;
                ws.Cell(row, 4).Value = assertion.Kind;
                ws.Cell(row, 5).Value = assertion.Selector;
                ws.Cell(row, 6).Value = assertion.Expected;
                ws.Cell(row, 7).Value = assertion.Parameter;

                ws.Cell(row, 5).Style.Font.FontName = "Consolas";
                row++;
            }
        }

        // Spelling a check wrongly is the easiest way to break an upload, so the
        // allowed wording is offered as a dropdown.
        if (row > 2)
        {
            ws.Range(2, 4, Math.Max(row + 40, 60), 4)
              .CreateDataValidation()
              .List(string.Join(",", AssertionKinds.All), inCellDropdown: true);
        }

        Finish(ws);
    }

    /// <summary>
    /// One sheet per test case: the recorded values on the first row, and room
    /// underneath for as many more rows as the tester wants.
    /// </summary>
    private static void WriteData(XLWorkbook book, TestCase test, int index)
    {
        var ws = book.Worksheets.Add(DataSheetName(test, index));
        var parameters = test.Parameters();

        var headers = new List<string> { "Test ID", "Data Set", "Enabled" };
        headers.AddRange(parameters);

        Header(ws, headers.ToArray());

        var row = 2;

        foreach (var set in test.DataSets)
        {
            ws.Cell(row, 1).Value = test.Id;
            ws.Cell(row, 2).Value = set.Name;
            ws.Cell(row, 3).Value = set.Enabled;

            for (var c = 0; c < parameters.Count; c++)
            {
                ws.Cell(row, 4 + c).Value = set.Values.TryGetValue(parameters[c], out var v) ? v : "";
            }

            row++;
        }

        var note = ws.Cell(row + 1, 1);
        note.Value = "Copy a row above, change the values, and give it a new Data Set name. Every enabled row runs.";
        note.Style.Font.Italic = true;
        note.Style.Font.FontColor = XLColor.Gray;

        ws.Column(1).Style.Font.FontColor = XLColor.Gray;
        Finish(ws);
    }

    private static void WriteResults(XLWorkbook book, IReadOnlyList<TestCase> tests)
    {
        var ws = book.Worksheets.Add(ResultsSheet);

        Header(ws, "Test ID", "Test Name", "Data Set", "Status", "Message", "Ran At", "Seconds", "Screenshot");

        var row = 2;

        foreach (var test in tests)
        {
            var results = test.LastResults.Count > 0
                ? test.LastResults
                : test.LastResult is null ? new List<TestResult>() : new List<TestResult> { test.LastResult };

            foreach (var result in results)
            {
                ws.Cell(row, 1).Value = test.Id;
                ws.Cell(row, 2).Value = test.Name;
                ws.Cell(row, 3).Value = result.DataSet;
                ws.Cell(row, 4).Value = result.Status;
                ws.Cell(row, 5).Value = result.Message;
                ws.Cell(row, 6).Value = result.RanUtc == default ? "" : result.RanUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                ws.Cell(row, 7).Value = result.Seconds;
                ws.Cell(row, 8).Value = result.ScreenshotPath;

                ws.Cell(row, 4).Style.Font.Bold = true;
                ws.Cell(row, 4).Style.Font.FontColor = result.IsPass
                    ? XLColor.FromArgb(16, 124, 16)
                    : XLColor.FromArgb(196, 43, 28);

                row++;
            }
        }

        Finish(ws);
    }

    // -----------------------------------------------------------------------
    // Upload
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads a workbook back into the project. Test cases already in the project
    /// are updated in place; unknown ids are added. Nothing is deleted - a test
    /// case left out of the file is simply left alone.
    /// </summary>
    public static ImportReport Import(ProjectStore store, string path)
    {
        var report = new ImportReport();

        using var book = new XLWorkbook(path);

        var existing = store.LoadAll().ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var steps = ReadSteps(book, report);
        var assertions = ReadAssertions(book, report);
        var data = ReadData(book, report);

        var cases = book.Worksheets.FirstOrDefault(w => w.Name == TestCasesSheet);

        if (cases is null)
        {
            report.Problems.Add($"No \"{TestCasesSheet}\" sheet - is this a workbook this tool wrote?");
            return report;
        }

        foreach (var row in Rows(cases))
        {
            var id = Text(cases.Cell(row, 1));

            if (id.Length == 0)
            {
                continue;
            }

            if (!existing.TryGetValue(id, out var test))
            {
                test = new TestCase { Id = id, Source = "excel" };
                report.NewTestCases++;
            }

            test.Name = Text(cases.Cell(row, 2));
            test.Url = Text(cases.Cell(row, 3));
            test.Enabled = Flag(cases.Cell(row, 4), fallback: true);
            test.Notes = Text(cases.Cell(row, 9));

            if (steps.TryGetValue(id, out var ownSteps))
            {
                test.Steps = ownSteps;
            }

            if (assertions.TryGetValue(id, out var ownAssertions))
            {
                test.Assertions = ownAssertions;
            }

            if (data.TryGetValue(id, out var ownData))
            {
                test.DataSets = ownData;
            }

            if (test.Name.Length == 0)
            {
                report.Problems.Add($"Test {id} has no name; it was skipped.");
                continue;
            }

            if (test.Url.Length == 0)
            {
                report.Problems.Add($"\"{test.Name}\" has no link; it was skipped.");
                continue;
            }

            store.Save(test);
            report.TestCases++;
        }

        return report;
    }

    private static Dictionary<string, List<TestStep>> ReadSteps(XLWorkbook book, ImportReport report)
    {
        var map = new Dictionary<string, List<TestStep>>(StringComparer.OrdinalIgnoreCase);
        var ws = book.Worksheets.FirstOrDefault(w => w.Name == StepsSheet);

        if (ws is null)
        {
            return map;
        }

        foreach (var row in Rows(ws))
        {
            var id = Text(ws.Cell(row, 1));

            if (id.Length == 0)
            {
                continue;
            }

            var action = Text(ws.Cell(row, 5)).Trim().ToLowerInvariant();

            if (!StepActions.All.Contains(action))
            {
                report.Problems.Add(
                    $"{StepsSheet} row {row}: \"{action}\" is not an action. " +
                    $"Use one of: {string.Join(", ", StepActions.All)}.");
                continue;
            }

            if (!map.TryGetValue(id, out var list))
            {
                map[id] = list = new List<TestStep>();
            }

            list.Add(new TestStep
            {
                Use = Flag(ws.Cell(row, 4), fallback: true),
                Action = action,
                Selector = Text(ws.Cell(row, 6)),
                Label = Text(ws.Cell(row, 7)),
                Value = Text(ws.Cell(row, 8)),
                Parameter = Text(ws.Cell(row, 9)).Trim(),
                FallbackText = Text(ws.Cell(row, 10)),
                WaitAfter = Flag(ws.Cell(row, 11), fallback: false)
            });

            report.Steps++;
        }

        // The sheet does not carry the "this click picks an item" flag - it is
        // derived, so a hand-edited file behaves like a recorded one.
        foreach (var list in map.Values)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (list[i].Action != StepActions.Click)
                {
                    continue;
                }

                list[i].Pick = list[i].Parameter.Length > 0 ||
                               (i > 0 && list[i - 1].Action == StepActions.Type);
            }
        }

        return map;
    }

    private static Dictionary<string, List<AssertionSpec>> ReadAssertions(XLWorkbook book, ImportReport report)
    {
        var map = new Dictionary<string, List<AssertionSpec>>(StringComparer.OrdinalIgnoreCase);
        var ws = book.Worksheets.FirstOrDefault(w => w.Name == AssertionsSheet);

        if (ws is null)
        {
            return map;
        }

        foreach (var row in Rows(ws))
        {
            var id = Text(ws.Cell(row, 1));

            if (id.Length == 0)
            {
                continue;
            }

            var kind = Text(ws.Cell(row, 4)).Trim();
            var match = AssertionKinds.All.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                report.Problems.Add(
                    $"{AssertionsSheet} row {row}: \"{kind}\" is not a check the tool knows. " +
                    "Pick one from the dropdown in that column.");
                continue;
            }

            if (!map.TryGetValue(id, out var list))
            {
                map[id] = list = new List<AssertionSpec>();
            }

            list.Add(new AssertionSpec
            {
                Kind = match,
                Selector = Text(ws.Cell(row, 5)),
                Expected = Text(ws.Cell(row, 6)),
                Parameter = Text(ws.Cell(row, 7)).Trim()
            });

            report.Assertions++;
        }

        return map;
    }

    /// <summary>
    /// Reads every "Data - ..." sheet. Column headers after Enabled are the
    /// parameter names, so a tester can add a column simply by naming it.
    /// </summary>
    private static Dictionary<string, List<DataSet>> ReadData(XLWorkbook book, ImportReport report)
    {
        var map = new Dictionary<string, List<DataSet>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ws in book.Worksheets.Where(w => w.Name.StartsWith(DataSheetPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.Equals(Text(ws.Cell(1, 1)), "Test ID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var headers = new List<string>();

            for (var c = 4; c <= 200; c++)
            {
                var name = Text(ws.Cell(1, c)).Trim();

                if (name.Length == 0)
                {
                    break;
                }

                headers.Add(name);
            }

            foreach (var row in Rows(ws))
            {
                var id = Text(ws.Cell(row, 1));
                var name = Text(ws.Cell(row, 2)).Trim();

                if (id.Length == 0 || name.Length == 0)
                {
                    continue;
                }

                var set = new DataSet
                {
                    Name = name,
                    Enabled = Flag(ws.Cell(row, 3), fallback: true)
                };

                for (var c = 0; c < headers.Count; c++)
                {
                    set.Values[headers[c]] = Text(ws.Cell(row, 4 + c));
                }

                if (!map.TryGetValue(id, out var list))
                {
                    map[id] = list = new List<DataSet>();
                }

                list.Add(set);
                report.DataSets++;
            }
        }

        return map;
    }

    // -----------------------------------------------------------------------
    // Sheet plumbing
    // -----------------------------------------------------------------------

    private static void Header(IXLWorksheet ws, params string[] names)
    {
        for (var c = 0; c < names.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = names[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = HeaderFill;
        }
    }

    private static void Finish(IXLWorksheet ws)
    {
        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, 200, 8, 60);
    }

    /// <summary>Data rows of a sheet, skipping the header.</summary>
    private static IEnumerable<int> Rows(IXLWorksheet ws)
    {
        var last = ws.LastRowUsed()?.RowNumber() ?? 1;

        for (var row = 2; row <= last; row++)
        {
            yield return row;
        }
    }

    private static string Text(IXLCell cell)
    {
        try
        {
            return cell.IsEmpty() ? "" : cell.Value.ToString()?.Trim() ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// A tick box column typed by hand: TRUE/FALSE, yes/no, 1/0 all work, and an
    /// empty cell means whatever the caller says it means.
    /// </summary>
    private static bool Flag(IXLCell cell, bool fallback)
    {
        var text = Text(cell);

        if (text.Length == 0)
        {
            return fallback;
        }

        return text.ToLowerInvariant() is "true" or "yes" or "y" or "1" or "x";
    }

    /// <summary>
    /// A sheet name Excel will accept: 31 characters, no : \ / ? * [ ] and
    /// unique within the book.
    /// </summary>
    private static string DataSheetName(TestCase test, int index)
    {
        var clean = new string(test.Name
            .Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_')
            .ToArray())
            .Trim();

        if (clean.Length == 0)
        {
            clean = "test";
        }

        var prefix = $"{DataSheetPrefix} {index} - ";
        var room = 31 - prefix.Length;

        return prefix + (clean.Length <= room ? clean : clean[..room]);
    }
}
