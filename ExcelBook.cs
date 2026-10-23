using ClosedXML.Excel;

namespace PlaywrightTestStudio;

/// <summary>
/// The test data file a project is downloaded to and uploaded from. It is the
/// format a tester works in: record once, download, copy the recorded row, type
/// more test data underneath it, upload, and every row runs.
///
/// The file holds test data and nothing else: one sheet per test case, a column
/// for every value that was entered while recording, and a row per data set.
/// How the test drives the page - its steps, selectors and checks - stays in the
/// project, where a stray edit in Excel cannot break it.
///
/// Results are not in here either; a run writes its own file (see
/// <see cref="ResultsBook"/>).
/// </summary>
internal static class ExcelBook
{
    private const string IdHeader = "Test ID";
    private const string NameHeader = "Data Set";
    private const string EnabledHeader = "Enabled";

    /// <summary>First column that holds a value the test enters.</summary>
    private const int FirstDataColumn = 4;

    // Sheets of the workbook this tool wrote before the data-only file. One of
    // those can still be uploaded.
    private const string TestCasesSheet = "Test Cases";
    private const string StepsSheet = "Steps";
    private const string AssertionsSheet = "Assertions";

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
            $"{DataSets} row(s) of test data read for {TestCases} test case(s)." +
            (NewTestCases > 0 ? $" {NewTestCases} test case(s) were new." : "");
    }

    // -----------------------------------------------------------------------
    // Download
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes the test data file and returns how many test cases went into it.
    /// A test case that enters no data has nothing to put in a sheet and is left
    /// out; when that is all of them, no file is written.
    /// </summary>
    public static int Export(IReadOnlyList<TestCase> tests, string path)
    {
        using var book = new XLWorkbook();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var test in tests.Where(t => t.Parameters().Count > 0))
        {
            WriteData(book, test, names);
        }

        if (book.Worksheets.Count > 0)
        {
            book.SaveAs(path);
        }

        return book.Worksheets.Count;
    }

    /// <summary>
    /// One sheet per test case: the recorded values on the first row, and room
    /// underneath for as many more rows as the tester wants.
    /// </summary>
    private static void WriteData(XLWorkbook book, TestCase test, HashSet<string> names)
    {
        var ws = book.Worksheets.Add(SheetName(test, names));
        var parameters = test.Parameters();

        var headers = new List<string> { IdHeader, NameHeader, EnabledHeader };
        headers.AddRange(parameters);

        for (var c = 0; c < headers.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Fill.BackgroundColor = HeaderFill;
        }

        // Whatever is typed under a data column is taken as written. Without
        // this Excel turns 2026-10-01 into a date and 007 into 7.
        ws.Range(2, FirstDataColumn, 1000, Math.Max(FirstDataColumn, headers.Count))
          .Style.NumberFormat.Format = "@";

        var row = 2;

        foreach (var set in test.DataSets)
        {
            ws.Cell(row, 1).Value = test.Id;
            ws.Cell(row, 2).Value = set.Name;
            ws.Cell(row, 3).Value = set.Enabled;

            for (var c = 0; c < parameters.Count; c++)
            {
                ws.Cell(row, FirstDataColumn + c).Value = set.Values.TryGetValue(parameters[c], out var v) ? v : "";
            }

            row++;
        }

        ws.Cell(1, 2).CreateComment().AddText(
            $"Test case: {test.Name}\n\n" +
            "Each row is one run. Copy a row, change its values and give it a new name here. " +
            "Rows with Enabled = FALSE are skipped.");

        // The id ties the sheet back to its test case. It is there when a row
        // is copied, and out of the way the rest of the time.
        ws.Column(1).Hide();

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents(1, 200, 10, 60);
    }

    // -----------------------------------------------------------------------
    // Upload
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads test data back into the project. The rows on a sheet replace the
    /// rows its test case had. Nothing else about a test case changes, and a
    /// test case with no sheet in the file is left alone.
    /// </summary>
    public static ImportReport Import(ProjectStore store, string path)
    {
        var report = new ImportReport();

        using var book = new XLWorkbook(path);

        var existing = store.LoadAll().ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var data = ReadData(book, report);

        if (book.Worksheets.Any(w => w.Name == TestCasesSheet))
        {
            ImportFullWorkbook(store, book, existing, data, report);
            return report;
        }

        if (data.Count == 0)
        {
            report.Problems.Add("No test data was found in that file. Upload a file made with \"Download test data\".");
            return report;
        }

        foreach (var (id, sets) in data)
        {
            if (!existing.TryGetValue(id, out var test))
            {
                report.Problems.Add(
                    $"{sets.Count} row(s) belong to a test case that is not in this project (id {id}); they were skipped.");
                report.DataSets -= sets.Count;
                continue;
            }

            test.DataSets = sets;
            store.Save(test);
            report.TestCases++;
        }

        return report;
    }

    /// <summary>
    /// Reads every test data sheet - any sheet whose first header is the test
    /// id. Column headers after Enabled are the parameter names.
    /// </summary>
    private static Dictionary<string, List<DataSet>> ReadData(XLWorkbook book, ImportReport report)
    {
        var map = new Dictionary<string, List<DataSet>>(StringComparer.OrdinalIgnoreCase);

        foreach (var ws in book.Worksheets)
        {
            if (ws.Name is TestCasesSheet or StepsSheet or AssertionsSheet ||
                !string.Equals(Text(ws.Cell(1, 1)), IdHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var headers = new List<string>();

            for (var c = FirstDataColumn; c <= 200; c++)
            {
                var name = Text(ws.Cell(1, c)).Trim();

                if (name.Length == 0)
                {
                    break;
                }

                headers.Add(name);
            }

            // A row typed in from scratch has no id in the hidden column; it
            // belongs to the test case the rest of the sheet belongs to.
            var sheetId = Rows(ws).Select(r => Text(ws.Cell(r, 1))).FirstOrDefault(id => id.Length > 0) ?? "";

            foreach (var row in Rows(ws))
            {
                var values = headers.Select((_, c) => Text(ws.Cell(row, FirstDataColumn + c))).ToList();
                var name = Text(ws.Cell(row, 2)).Trim();

                if (name.Length == 0 && values.All(v => v.Length == 0))
                {
                    continue;
                }

                var id = Text(ws.Cell(row, 1));

                if (id.Length == 0)
                {
                    id = sheetId;
                }

                if (id.Length == 0)
                {
                    report.Problems.Add($"Sheet \"{ws.Name}\" has lost its test id, so its rows cannot be matched to a test case.");
                    break;
                }

                var set = new DataSet
                {
                    Name = name.Length > 0 ? name : $"Row {row - 1}",
                    Enabled = Flag(ws.Cell(row, 3), fallback: true)
                };

                for (var c = 0; c < headers.Count; c++)
                {
                    set.Values[headers[c]] = values[c];
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
    // The older workbook, which also carried the steps and the checks
    // -----------------------------------------------------------------------

    private static void ImportFullWorkbook(
        ProjectStore store,
        XLWorkbook book,
        Dictionary<string, TestCase> existing,
        Dictionary<string, List<DataSet>> data,
        ImportReport report)
    {
        var steps = ReadSteps(book, report);
        var assertions = ReadAssertions(book, report);
        var cases = book.Worksheets.First(w => w.Name == TestCasesSheet);

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
                report.Problems.Add($"{AssertionsSheet} row {row}: \"{kind}\" is not a check the tool knows.");
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

    // -----------------------------------------------------------------------
    // Sheet plumbing
    // -----------------------------------------------------------------------

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
            if (cell.IsEmpty())
            {
                return "";
            }

            // A date typed into Excel is a number underneath. The page wants
            // it the way a date box holds it.
            if (cell.DataType == XLDataType.DateTime)
            {
                var when = cell.GetDateTime();

                return when.TimeOfDay == TimeSpan.Zero
                    ? when.ToString("yyyy-MM-dd")
                    : when.ToString("yyyy-MM-ddTHH:mm");
            }

            return cell.Value.ToString()?.Trim() ?? "";
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
    private static string SheetName(TestCase test, HashSet<string> used)
    {
        var clean = new string(test.Name
            .Where(ch => char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_')
            .ToArray())
            .Trim();

        if (clean.Length == 0)
        {
            clean = "Test data";
        }

        var name = clean.Length <= 31 ? clean : clean[..31].Trim();
        var n = 2;

        while (!used.Add(name))
        {
            var suffix = $" ({n++})";
            name = (clean.Length <= 31 - suffix.Length ? clean : clean[..(31 - suffix.Length)].Trim()) + suffix;
        }

        return name;
    }
}
