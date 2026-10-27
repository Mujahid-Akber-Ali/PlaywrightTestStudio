using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace PlaywrightTestStudio;

/// <summary>
/// End-to-end check of the engine with no UI in the way. It walks the whole
/// journey the tool is for: record a session, download the test data, add a row
/// the way a tester would, upload it, run every row, and write the results file.
///
/// Run with <c>PlaywrightTestStudio.exe --selftest</c>; the log lands in %TEMP%.
/// </summary>
internal static class SelfTest
{
    private const string ProjectName = "_selftest";

    public static int Run()
    {
        var log = new StringBuilder();
        var failures = 0;

        void Check(string what, bool ok, string detail = "")
        {
            if (!ok)
            {
                failures++;
            }

            log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? "  ->  " + detail : "")}");
        }

        var logFile = Path.Combine(Path.GetTempPath(), "playwright-test-studio-selftest.log");
        var store = Storage.OpenProject(ProjectName);
        var workbook = Path.Combine(Path.GetTempPath(), "pts-selftest-book.xlsx");
        var resultsFile = Path.Combine(Path.GetTempPath(), "pts-selftest-results.xlsx");

        try
        {
            var pageFile = WriteSamplePage();
            var url = new Uri(pageFile).AbsoluteUri;

            log.AppendLine($"Sample page : {pageFile}");

            // ---------------------------------------------------------------
            // 1. Record a session, driving the page the way a person would.
            // ---------------------------------------------------------------
            var recording = RecordAsync(url, store, log).GetAwaiter().GetResult();
            var test = recording.Test;

            Check("Esc in the page stops the recording", recording.StoppedByEscape);
            Check("Esc was not itself recorded as a step",
                !test.Steps.Any(s => s.Action == StepActions.Press && s.Value == "Escape"));

            log.AppendLine($"Recorded    : {test.Steps.Count} step(s)");

            foreach (var step in test.Steps)
            {
                log.AppendLine($"              {step.Action,-7} {step.Selector,-30} " +
                               $"value=\"{step.RecordedValue}\"  param={step.Parameter}");
            }

            Check("typing was recorded", test.Steps.Any(s => s.Action == StepActions.Fill && s.Selector == "#username"));
            Check("the dropdown choice was recorded",
                test.Steps.Any(s => s.Action == StepActions.Select && s.Selector == "#role"));
            Check("the radio button was recorded",
                test.Steps.Any(s => s.Action == StepActions.Check && s.Selector.Contains("plan")));
            Check("the checkbox was recorded",
                test.Steps.Any(s => s.Action == StepActions.Check && s.Selector == "#terms"));
            Check("the item lookup was recorded as a type-ahead, not a plain fill",
                test.Steps.Any(s => s.Action == StepActions.Type && s.Selector == "#itemSearch"));
            Check("picking the search result was recorded",
                test.Steps.Any(s => s.Action == StepActions.Click && s.FallbackText.Contains("Widget clamp")));
            Check("the submit click was recorded",
                test.Steps.Any(s => s.Action == StepActions.Click && s.Selector == "#submit"));
            Check("keystrokes collapsed into one step per field",
                test.Steps.Count(s => s.Selector == "#username") == 1,
                $"{test.Steps.Count(s => s.Selector == "#username")} step(s) for #username");

            // What must NOT be in the recording matters as much: each of these
            // replays as a step with nothing to find.
            Check("a change the page made to its own dropdown was not recorded",
                test.Steps.Count(s => s.Selector == "#role") == 1,
                $"{test.Steps.Count(s => s.Selector == "#role")} step(s) for #role");
            Check("clicks on empty page were not recorded",
                test.Steps.Count(s => s.Action == StepActions.Click) == 3,
                string.Join(" | ", test.Steps.Where(s => s.Action == StepActions.Click).Select(s => s.Selector)));
            Check("a bare label above a field names it",
                test.Steps.Any(s => s.Selector == "#purposeBox" && s.Label == "Purpose"),
                test.Steps.FirstOrDefault(s => s.Selector == "#purposeBox")?.Label ?? "(no step)");
            Check("a plain box that turned out to be a lookup is replayed key by key",
                test.Steps.Any(s => s.Selector == "#vendor" && s.Action == StepActions.Type),
                test.Steps.FirstOrDefault(s => s.Selector == "#vendor")?.Action ?? "(no step)");
            Check("the result picked from that lookup is a pick, named after its field",
                test.Steps.Any(s => s.Pick && s.FallbackText == "Acme Tools" && s.Label == "Vendor result"));

            var parameters = test.Parameters();
            log.AppendLine($"Parameters  : {string.Join(", ", parameters)}");

            Check("every entered value became a data column", parameters.Count == 7,
                string.Join(", ", parameters));
            Check("the picked result is named after its field, not its value",
                parameters.Any(p => p.StartsWith("item", StringComparison.OrdinalIgnoreCase) && p.Contains("result", StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", parameters));
            Check("pressing a button did NOT become a data column",
                !parameters.Any(p => p.Contains("submit", StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", parameters));
            Check("the recorded values became the first data row",
                test.DataSets.Count == 1 && test.DataSets[0].Values.Count == 7);

            // ---------------------------------------------------------------
            // 2. Assertions, including one that varies per data row.
            // ---------------------------------------------------------------
            var expectedColumn = "expectedResult";

            test.Assertions.Add(new AssertionSpec
            {
                Kind = AssertionKinds.PageTextContains,
                Parameter = expectedColumn,
                Expected = "Submitted: saif / Manager / pro / true / Widget clamp"
            });

            test.Assertions.Add(new AssertionSpec
            {
                Kind = AssertionKinds.ElementVisible,
                Selector = "#result"
            });

            test.Assertions.Add(new AssertionSpec
            {
                Kind = AssertionKinds.ElementValueEquals,
                Selector = "#vendor",
                Expected = "Acme Tools"
            });

            RecordDialogs.SyncRecordedRow(test);
            store.Save(test);
            TestCaseDialogs.RegenerateCode(store, test);

            Check("the parameterised check got a column on the recorded row",
                test.DataSets[0].Values.ContainsKey(expectedColumn));

            // ---------------------------------------------------------------
            // 3. Run the recorded row.
            // ---------------------------------------------------------------
            var first = Runner.RunAllDataSetsAsync(test, store, headless: true).GetAwaiter().GetResult();

            log.AppendLine($"First run   : {first[0].Status} in {first[0].Seconds}s - {first[0].Message}");

            foreach (var assertion in first[0].Assertions)
            {
                log.AppendLine($"              {(assertion.Passed ? "ok  " : "FAIL")} {assertion.Description}");
            }

            Check("the recorded session replays and passes", first.Count == 1 && first[0].IsPass, first[0].Message);

            // ---------------------------------------------------------------
            // 4. Download the test data.
            // ---------------------------------------------------------------
            var sheets = ExcelBook.Export(new[] { test }, workbook);

            Check("the test data file was written", File.Exists(workbook) && sheets == 1);

            using (var book = new XLWorkbook(workbook))
            {
                var names = book.Worksheets.Select(w => w.Name).ToList();
                log.AppendLine($"Sheets      : {string.Join(", ", names)}");

                var ws = book.Worksheets.First();
                var headers = Enumerable.Range(1, 3 + parameters.Count).Select(c => ws.Cell(1, c).GetString()).ToList();

                Check("the file holds test data and nothing else: one sheet per test case", names.Count == 1,
                    string.Join(", ", names));
                Check("its columns are the values entered while recording",
                    headers.Skip(3).SequenceEqual(parameters), string.Join(", ", headers));
                Check("its first row is what was recorded",
                    ws.Cell(2, 2).GetString() == "Recorded" &&
                    Enumerable.Range(0, parameters.Count).All(c =>
                        ws.Cell(2, 4 + c).GetString() == test.DataSets[0].Values[parameters[c]]));
                Check("the test id is kept out of sight", ws.Column(1).IsHidden);
            }

            // ---------------------------------------------------------------
            // 5. A tester adds a second row of data by hand.
            // ---------------------------------------------------------------
            AddDataRow(workbook, test, expectedColumn, log);

            // ---------------------------------------------------------------
            // 6. Upload it again.
            // ---------------------------------------------------------------
            var report = ExcelBook.Import(store, workbook);

            log.AppendLine($"Import      : {report.Summary}");

            foreach (var problem in report.Problems)
            {
                log.AppendLine($"              problem: {problem}");
            }

            Check("the upload reported no problems", report.Problems.Count == 0,
                string.Join("; ", report.Problems));
            Check("the upload read one test case back", report.TestCases == 1);
            Check("the upload added nothing unexpected", report.NewTestCases == 0);
            Check("a row typed in without the hidden test id still found its test case", report.DataSets == 2,
                $"{report.DataSets} row(s)");

            var reloaded = store.LoadAll().Single(t => t.Id == test.Id);

            Check("the second data row came through", reloaded.DataSets.Count == 2,
                $"{reloaded.DataSets.Count} row(s)");
            Check("the steps survived the round trip", reloaded.Steps.Count == test.Steps.Count,
                $"{reloaded.Steps.Count} vs {test.Steps.Count}");
            Check("the assertions survived the round trip", reloaded.Assertions.Count == 3);
            Check("the per-row expected value survived",
                reloaded.Assertions.Any(a => a.Parameter == expectedColumn));

            // ---------------------------------------------------------------
            // 7. Run both rows. Row two enters different data and expects a
            //    different result, which is the whole point of the workbook.
            // ---------------------------------------------------------------
            var both = Runner.RunAllDataSetsAsync(reloaded, store, headless: true).GetAwaiter().GetResult();

            log.AppendLine($"Data-driven : {both.Count} run(s)");

            foreach (var result in both)
            {
                log.AppendLine($"              {result.Status,-6} [{result.DataSet}] {result.Message}");
            }

            Check("both data rows ran", both.Count == 2, $"{both.Count} run(s)");
            Check("both data rows passed", both.All(r => r.IsPass),
                string.Join(" | ", both.Where(r => !r.IsPass).Select(r => $"{r.DataSet}: {r.Message}")));
            Check("each row was reported separately",
                both.Select(r => r.DataSet).Distinct().Count() == 2);
            Check("a row that picks a different item does not wait out a stale selector",
                both.All(r => r.Seconds < 8), string.Join(", ", both.Select(r => $"{r.DataSet}: {r.Seconds}s")));

            store.Save(reloaded);
            TestCaseDialogs.RegenerateCode(store, reloaded);

            // A row that expects the wrong thing has to fail, or none of this
            // proves anything.
            reloaded.DataSets[1].Values[expectedColumn] = "this text is definitely not on the page";

            var negative = Runner.RunAllDataSetsAsync(reloaded, store, headless: true).GetAwaiter().GetResult();

            Check("a row expecting the wrong thing fails on its own",
                negative[0].IsPass && !negative[1].IsPass,
                $"row1={negative[0].Status}, row2={negative[1].Status}");
            Check("one failing row fails the test case overall",
                reloaded.LastResult?.Status == "Failed", reloaded.LastResult?.Status ?? "");

            // A step that cannot be carried out has to say which one and why.
            var broken = reloaded.Steps.First(s => s.Selector == "#username");
            broken.Selector = "#no-such-field";

            var missing = Runner.RunAllDataSetsAsync(reloaded, store, headless: true).GetAwaiter().GetResult();

            log.AppendLine($"Broken step : {missing[0].Message}");

            Check("a step that cannot find its field is an Error that names the step",
                missing[0].Status == "Error" && missing[0].Message.StartsWith("Step 1 of") &&
                missing[0].Message.Contains("#no-such-field"), missing[0].Message);

            broken.Selector = "#username";

            // ---------------------------------------------------------------
            // 8. The results file, written at the end of a run.
            // ---------------------------------------------------------------
            Runner.RunAllDataSetsAsync(reloaded, store, headless: true).GetAwaiter().GetResult();
            ResultsBook.Write(store, new[] { reloaded }, resultsFile);

            using (var book = new XLWorkbook(resultsFile))
            {
                var names = book.Worksheets.Select(w => w.Name).ToList();

                Check("the results file has a summary, the results and the checks",
                    names.SequenceEqual(new[] { "Summary", "Results", "Checks" }), string.Join(", ", names));

                var results = book.Worksheet("Results");

                Check("it has a line per data row, with its outcome",
                    results.Cell(2, 3).GetString() == "Passed" && results.Cell(3, 3).GetString() == "Failed",
                    $"{results.Cell(2, 3).GetString()}, {results.Cell(3, 3).GetString()}");
                Check("it lists every check that was made",
                    book.Worksheet("Checks").LastRowUsed()!.RowNumber() == 1 + 2 * reloaded.Assertions.Count);
            }

            using (var document = SpreadsheetDocument.Open(resultsFile, false))
            {
                var charts = document.WorkbookPart!.WorksheetParts
                    .Where(p => p.DrawingsPart is not null)
                    .Sum(p => p.DrawingsPart!.ChartParts.Count());

                var faults = new OpenXmlValidator().Validate(document).Take(10).ToList();

                foreach (var fault in faults)
                {
                    log.AppendLine($"              invalid: {fault.Path?.XPath} - {fault.Description}");
                }

                Check("the results file has its charts", charts == 3, $"{charts} chart(s)");
                Check("the results file is a valid workbook", faults.Count == 0, $"{faults.Count} fault(s)");
            }

            log.AppendLine($"Results file: {resultsFile}");

            // ---------------------------------------------------------------
            // 9. The generated C# reflects all of it.
            // ---------------------------------------------------------------
            reloaded.DataSets[1].Values[expectedColumn] = test.DataSets[0].Values[expectedColumn]
                .Replace("saif", "ali").Replace("Manager", "Admin").Replace("Widget clamp", "Bolt M8");

            var codeFile = CodeGen.WriteForLink(store, url, new[] { reloaded });
            var code = File.ReadAllText(codeFile);

            log.AppendLine($"Code file   : {codeFile}");
            log.AppendLine();
            log.AppendLine("--- generated fixture ---");
            log.AppendLine(code);
            log.AppendLine("--- end ---");
            log.AppendLine();

            Check("two data rows generate a parameterised NUnit test",
                code.Contains("[TestCase(") && code.Contains("TestName ="));
            Check("the generated test takes the data columns as parameters",
                code.Contains($"string {parameters[0]}"));
            Check("the generated test types into the lookup key by key",
                code.Contains("PressSequentiallyAsync"));
            Check("the generated test picks the result by its wording",
                code.Contains("PickAsync("));
            Check("the generated test clicks a button with a text fallback",
                code.Contains("ClickStepAsync("));
            Check("the generated fixture is valid NUnit shape",
                code.Contains("[TestFixture]") && code.Contains(": PageTest"));

            // ---------------------------------------------------------------
            // 10. Performance metrics of a link, written to their own workbook.
            // ---------------------------------------------------------------
            var perfFile = Path.Combine(Path.GetTempPath(), "pts-selftest-performance.xlsx");
            var perf = Performance.MeasureAsync(url, headless: true).GetAwaiter().GetResult();

            log.AppendLine($"Performance : {perf.Headline().Replace("\r\n", "  ")}");

            Check("the measured report is for the page that was asked for", perf.Url == url);
            Check("it has the full load timeline", perf.Timeline.Count == 8,
                $"{perf.Timeline.Count} row(s)");
            Check("it has the paint and web-vital milestones", perf.Experience.Count == 4,
                $"{perf.Experience.Count} row(s)");
            Check("the page reached its load event", perf.Timeline.Single(m => m.Label == "Page load").Value > 0,
                $"{perf.Timeline.Single(m => m.Label == "Page load").Value} ms");

            PerfBook.Write(perf, perfFile);

            using (var book = new XLWorkbook(perfFile))
            {
                var names = book.Worksheets.Select(w => w.Name).ToList();

                Check("the performance workbook has its overview and its requests",
                    names.SequenceEqual(new[] { "Overview", "Requests" }), string.Join(", ", names));
                Check("the overview names the page it measured",
                    book.Worksheet("Overview").Cell(2, 1).GetString() == url);
            }

            using (var document = SpreadsheetDocument.Open(perfFile, false))
            {
                var faults = new OpenXmlValidator().Validate(document).Take(10).ToList();

                foreach (var fault in faults)
                {
                    log.AppendLine($"              invalid: {fault.Path?.XPath} - {fault.Description}");
                }

                Check("the performance workbook is a valid workbook", faults.Count == 0, $"{faults.Count} fault(s)");
            }

            log.AppendLine($"Perf file   : {perfFile}");

            File.Delete(perfFile);

            // ---------------------------------------------------------------
            // 11. A project is saved under its name, and can be given another.
            // ---------------------------------------------------------------
            Check("a link is not taken for a project name",
                ProjectDialogs.LooksLikeLink("https://portal.example.com/uind/indents/new") &&
                ProjectDialogs.LooksLikeLink("https_portal_example_com_uind_indents_new") &&
                ProjectDialogs.LooksLikeLink("portal.example.com/indents") &&
                !ProjectDialogs.LooksLikeLink("Portal UAT") &&
                !ProjectDialogs.LooksLikeLink("Indents v2.1"));
            Check("projects are listed by the name they were given",
                Storage.ListProjects().Contains(ProjectName), string.Join(", ", Storage.ListProjects()));

            const string renamedTo = "_selftest renamed";

            if (Storage.ProjectExists(renamedTo))
            {
                Storage.DeleteProject(renamedTo);
            }

            var renamed = Storage.RenameProject(ProjectName, renamedTo);
            var moved = renamed.LoadAll();

            Check("a renamed project is listed under its new name only",
                Storage.ListProjects().Contains(renamedTo) && !Storage.ListProjects().Contains(ProjectName),
                string.Join(", ", Storage.ListProjects()));
            Check("its test cases, screenshots and generated C# moved with it",
                moved.Count == 1 &&
                File.Exists(moved[0].CodeFile) && moved[0].CodeFile.StartsWith(renamed.CodeDir) &&
                moved[0].LastResults.All(r => File.Exists(r.ScreenshotPath)) &&
                !Directory.Exists(store.CodeDir),
                moved.Count == 1 ? moved[0].CodeFile : $"{moved.Count} test case(s)");

            // ---------------------------------------------------------------
            // Scraping: open a paginated grid and pull every page into Excel.
            // ---------------------------------------------------------------
            var scrapePage = WriteScrapeSamplePage();
            var scrapeUrl = new Uri(scrapePage).AbsoluteUri;
            var scrapeFile = Path.Combine(Path.GetTempPath(), "pts-selftest-scrape.xlsx");

            var scrape = ScrapeHeadlessAsync(scrapeUrl, log).GetAwaiter().GetResult();
            var grid = scrape.Tables.OrderByDescending(t => t.Cells).FirstOrDefault();

            log.AppendLine($"Scraped     : {scrape.Pages} page(s), {scrape.RowCount} row(s), {scrape.Tables.Count} table(s)");

            Check("the paginated grid was walked to the last page", scrape.Pages == 3, $"{scrape.Pages} page(s)");
            Check("every row across every page was collected",
                grid is not null && grid.Rows.Count == 25, $"{grid?.Rows.Count ?? 0} row(s)");
            Check("the column headings were read",
                grid is not null && grid.Headers.SequenceEqual(new[] { "Code", "Name", "Qty" }),
                grid is null ? "no grid" : string.Join(", ", grid.Headers));
            Check("the first and last rows are the real first and last, in order",
                grid is not null && grid.Rows.Count == 25 &&
                grid.Rows[0].SequenceEqual(new[] { "C1", "Item 1", "2" }) &&
                grid.Rows[24].SequenceEqual(new[] { "C25", "Item 25", "50" }),
                grid is null || grid.Rows.Count == 0 ? "no rows" : string.Join(" | ", grid.Rows[0]));
            Check("a second table on the page became a second table",
                scrape.Tables.Count == 2, $"{scrape.Tables.Count} table(s)");

            var scrapeSheets = ScrapeBook.Write(scrape, scrapeFile);
            Check("the scrape workbook has a sheet per table", scrapeSheets == 2, $"{scrapeSheets} sheet(s)");

            using (var document = SpreadsheetDocument.Open(scrapeFile, false))
            {
                var faults = new OpenXmlValidator().Validate(document).Take(10).ToList();

                foreach (var fault in faults)
                {
                    log.AppendLine($"              invalid: {fault.Path?.XPath} - {fault.Description}");
                }

                Check("the scrape workbook is a valid .xlsx", faults.Count == 0, $"{faults.Count} fault(s)");
            }

            using (var book = new XLWorkbook(scrapeFile))
            {
                var ws = book.Worksheets.First();
                var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;

                Check("the sheet holds the header plus all 25 rows", lastRow == 26, $"last row {lastRow}");
                Check("a numeric-looking code is kept as text, not a number",
                    ws.Cell(2, 1).GetString() == "C1" && ws.Cell(26, 1).GetString() == "C25",
                    $"{ws.Cell(2, 1).GetString()} .. {ws.Cell(26, 1).GetString()}");
            }

            File.Delete(scrapePage);
            File.Delete(scrapeFile);

            // Cleanup. The results file is left behind on purpose, to be looked at.
            Storage.DeleteProject(renamedTo);

            if (Directory.Exists(renamed.CodeDir))
            {
                Directory.Delete(renamed.CodeDir, recursive: true);
            }

            File.Delete(pageFile);
            File.Delete(workbook);
        }
        catch (Exception ex)
        {
            failures++;
            log.AppendLine($"EXCEPTION   : {ex}");
        }

        log.AppendLine();
        log.AppendLine(failures == 0 ? "SELF TEST PASSED" : $"SELF TEST FAILED - {failures} check(s)");

        File.WriteAllText(logFile, log.ToString());

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Starts a recording and then drives the page with Playwright, which fires
    /// exactly the events a person's mouse and keyboard would.
    /// </summary>
    private static async Task<(TestCase Test, bool StoppedByEscape)> RecordAsync(
        string url, ProjectStore store, StringBuilder log)
    {
        var session = await RecordingSession.StartAsync(url, store.VideosDir, headless: true);
        var page = session.Page;

        await page.FillAsync("#username", "saif");

        // Chosen from the keyboard, as a person would: Admin, then down to Manager.
        await page.FocusAsync("#role");
        await page.Keyboard.PressAsync("ArrowDown");

        await page.FillAsync("#purposeBox", "Office supplies");
        await page.CheckAsync("input[name=\"plan\"][value=\"pro\"]");
        await page.CheckAsync("#terms");

        // Clicks that land on the page rather than on anything in it.
        await page.ClickAsync("h1");
        await page.Mouse.ClickAsync(700, 150);

        var search = page.Locator("#itemSearch");
        await search.ClickAsync();
        await search.PressSequentiallyAsync("widget", new Microsoft.Playwright.LocatorPressSequentiallyOptions { Delay = 40 });
        await page.WaitForSelectorAsync("#itemResults li");
        await page.GetByText("Widget clamp").ClickAsync();

        // A lookup with nothing to say it is one: a plain text box whose
        // results are links the page adds as you type.
        var vendor = page.Locator("#vendor");
        await vendor.ClickAsync();
        await vendor.PressSequentiallyAsync("acm", new Microsoft.Playwright.LocatorPressSequentiallyOptions { Delay = 40 });
        await page.WaitForSelectorAsync("#vendorHits a");
        await page.GetByText("Acme Tools").ClickAsync();

        // Asks "Submit this form?" first; the answer has to be OK.
        await page.ClickAsync("#submit");

        // The page talks to the recorder through an exposed function, so give
        // the last events a moment to arrive before finishing.
        await page.WaitForTimeoutAsync(600);

        log.AppendLine($"Recorder    : {session.Count} event(s) captured before stop");

        // This is how a person ends a recording: Esc, in the page, with no
        // window of ours anywhere on screen.
        await page.Keyboard.PressAsync("Escape");

        var stopped = true;

        try
        {
            await session.StopRequested.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            stopped = false;
        }

        return (await session.StopAsync("Submit the sample form"), stopped);
    }

    /// <summary>
    /// Opens the scrape sample hidden, asks it to scrape straight away (the
    /// floating button is what a person would press), and returns what came back.
    /// </summary>
    private static async Task<ScrapeResult> ScrapeHeadlessAsync(string url, StringBuilder log)
    {
        await using var session = await ScrapeSession.StartAsync(url, headless: true);
        var result = await session.ScrapeAsync(line => log.AppendLine($"              {line}"));
        return result;
    }

    /// <summary>
    /// A page with a three-page, twenty-five row grid behind a Next button, plus
    /// a small second table, to prove pagination is followed and every table is
    /// taken.
    /// </summary>
    private static string WriteScrapeSamplePage()
    {
        var file = Path.Combine(Path.GetTempPath(), "pts-selftest-scrape.html");

        File.WriteAllText(file, """
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>Sample grid</title></head>
        <body>
          <h1>Indents</h1>
          <table id="grid">
            <thead><tr><th>Code</th><th>Name</th><th>Qty</th></tr></thead>
            <tbody id="body"></tbody>
          </table>
          <nav><button id="next" type="button">Next &rsaquo;</button></nav>

          <h2>Summary</h2>
          <table id="summary">
            <thead><tr><th>Metric</th><th>Value</th></tr></thead>
            <tbody>
              <tr><td>Total</td><td>25</td></tr>
              <tr><td>Open</td><td>9</td></tr>
            </tbody>
          </table>

          <script>
            var data = [];
            for (var i = 1; i <= 25; i++) { data.push(['C' + i, 'Item ' + i, String(i * 2)]); }

            var pageSize = 10;
            var page = 0;

            function render() {
              var body = document.getElementById('body');
              body.innerHTML = '';
              data.slice(page * pageSize, page * pageSize + pageSize).forEach(function (r) {
                var tr = document.createElement('tr');
                r.forEach(function (v) { var td = document.createElement('td'); td.textContent = v; tr.appendChild(td); });
                body.appendChild(tr);
              });
              document.getElementById('next').disabled = (page + 1) * pageSize >= data.length;
            }

            document.getElementById('next').addEventListener('click', function () {
              if ((page + 1) * pageSize < data.length) { page++; render(); }
            });

            render();
          </script>
        </body>
        </html>
        """);

        return file;
    }

    /// <summary>
    /// Does to the workbook what a tester would do in Excel: copy the recorded
    /// row, rename it, and change the values.
    /// </summary>
    private static void AddDataRow(string path, TestCase test, string expectedColumn, StringBuilder log)
    {
        using var book = new XLWorkbook(path);
        var ws = book.Worksheets.First();

        var headers = new List<string>();

        for (var c = 4; c <= 200; c++)
        {
            var name = ws.Cell(1, c).GetString().Trim();

            if (name.Length == 0)
            {
                break;
            }

            headers.Add(name);
        }

        log.AppendLine($"Data columns: {string.Join(", ", headers)}");

        // Row 2 is the recorded one; row 3 is the tester's addition, typed in
        // rather than copied, so the hidden test id column is left empty.
        ws.Cell(3, 2).Value = "Admin and a bolt";
        ws.Cell(3, 3).Value = true;

        for (var c = 0; c < headers.Count; c++)
        {
            var column = headers[c];
            var recorded = ws.Cell(2, 4 + c).GetString();

            var replacement = column == expectedColumn
                ? "Submitted: ali / Admin / pro / true / Bolt M8"
                : recorded switch
                {
                    "saif" => "ali",
                    "Manager" => "Admin",
                    "widget" => "bolt",
                    "Widget clamp" => "Bolt M8",
                    _ => recorded
                };

            ws.Cell(3, 4 + c).Value = replacement;
        }

        book.Save();
    }

    /// <summary>A small page holding one of every control the recorder handles.</summary>
    private static string WriteSamplePage()
    {
        var file = Path.Combine(Path.GetTempPath(), "pts-selftest-form.html");

        File.WriteAllText(file, """
        <!doctype html>
        <html>
        <head><meta charset="utf-8"><title>Sample form</title></head>
        <body>
          <h1>Sample form</h1>
          <form onsubmit="return false">
            <p><label for="username">User name</label>
               <input id="username" name="username" type="text" required></p>

            <div><label>Purpose<span>*</span></label>
               <textarea id="purposeBox" name="purpose"></textarea></div>

            <p><label for="role">Role</label>
               <select id="role" name="role">
                 <option value="admin">Admin</option>
                 <option value="mgr">Manager</option>
                 <option value="view">Viewer</option>
               </select></p>

            <p><label><input type="radio" name="plan" value="basic"> Basic plan</label>
               <label><input type="radio" name="plan" value="pro"> Pro plan</label></p>

            <p><label for="itemSearch">Item</label>
               <input id="itemSearch" name="itemSearch" type="text" autocomplete="off"
                      role="combobox" aria-autocomplete="list" aria-owns="itemResults">
               <ul id="itemResults" role="listbox" style="display:none;list-style:none;margin:0;padding:0"></ul></p>

            <div><label>Vendor</label>
               <input id="vendor" name="vendor" type="text" autocomplete="off">
               <div id="vendorHits"></div></div>

            <p><label for="terms">Accept terms</label>
               <input id="terms" name="terms" type="checkbox"></p>

            <p><button id="submit" type="button">Submit</button></p>
          </form>

          <div id="result" style="display:none"></div>

          <script>
            // A type-ahead that only reacts to real keystrokes and answers
            // after a delay, the way a server-backed item lookup does.
            var catalogue = ['Blue widget', 'Red widget', 'Widget clamp', 'Bolt M8', 'Bolt M10'];
            var searchBox = document.getElementById('itemSearch');
            var results = document.getElementById('itemResults');
            var chosenItem = '';
            var pending = null;

            searchBox.addEventListener('keyup', function () {
              var term = searchBox.value.trim().toLowerCase();
              chosenItem = '';
              if (pending) { clearTimeout(pending); }
              if (term.length < 2) { results.style.display = 'none'; return; }

              pending = setTimeout(function () {
                var hits = catalogue.filter(function (c) { return c.toLowerCase().indexOf(term) >= 0; });
                results.innerHTML = '';
                hits.forEach(function (hit) {
                  var li = document.createElement('li');
                  li.setAttribute('role', 'option');
                  li.textContent = hit;
                  li.addEventListener('click', function () {
                    chosenItem = hit;
                    searchBox.value = hit;
                    results.style.display = 'none';
                  });
                  results.appendChild(li);
                });
                results.style.display = hits.length ? 'block' : 'none';
              }, 400);
            });

            // A lookup nothing marks as one, with its results added as links.
            var vendors = ['Acme Tools', 'Acme Fasteners', 'Bright Steel'];
            var vendorBox = document.getElementById('vendor');
            var vendorHits = document.getElementById('vendorHits');
            var vendorPending = null;

            vendorBox.addEventListener('keyup', function () {
              var term = vendorBox.value.trim().toLowerCase();
              if (vendorPending) { clearTimeout(vendorPending); }
              vendorHits.innerHTML = '';
              if (term.length < 2) { return; }

              vendorPending = setTimeout(function () {
                vendors.filter(function (v) { return v.toLowerCase().indexOf(term) >= 0; }).forEach(function (hit) {
                  var a = document.createElement('a');
                  a.href = '#';
                  a.textContent = hit;
                  a.style.display = 'block';
                  a.addEventListener('click', function (e) {
                    e.preventDefault();
                    vendorBox.value = hit;
                    vendorHits.innerHTML = '';
                  });
                  vendorHits.appendChild(a);
                });
              }, 300);
            });

            // Pages fire events at their own fields as they load. Nobody chose
            // anything here, so it must not turn up in a recording.
            document.getElementById('role').dispatchEvent(new Event('change', { bubbles: true }));

            document.getElementById('submit').addEventListener('click', function () {
              if (!window.confirm('Submit this form?')) { return; }
              var plan = document.querySelector('input[name="plan"]:checked');
              var out = document.getElementById('result');
              out.style.display = 'block';
              out.innerText = 'Submitted: '
                + document.getElementById('username').value + ' / '
                + document.getElementById('role').selectedOptions[0].text + ' / '
                + (plan ? plan.value : 'none') + ' / '
                + document.getElementById('terms').checked + ' / '
                + (chosenItem || 'no item');
            });
          </script>
        </body>
        </html>
        """);

        return file;
    }
}
