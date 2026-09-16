using System.Text;
using ClosedXML.Excel;

namespace PlaywrightTestStudio;

/// <summary>
/// End-to-end check of the engine with no UI in the way. It walks the whole
/// journey the tool is for: record a session, download the workbook, add a row
/// of test data the way a tester would, upload it, and run every row.
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

            var parameters = test.Parameters();
            log.AppendLine($"Parameters  : {string.Join(", ", parameters)}");

            Check("every typed value became a data column", parameters.Count == 4,
                string.Join(", ", parameters));
            Check("the picked result is named after its field, not its value",
                parameters.Any(p => p.StartsWith("item", StringComparison.OrdinalIgnoreCase) && p.Contains("result", StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", parameters));
            Check("pressing a button did NOT become a data column",
                !parameters.Any(p => p.Contains("submit", StringComparison.OrdinalIgnoreCase)),
                string.Join(", ", parameters));
            Check("the recorded values became the first data row",
                test.DataSets.Count == 1 && test.DataSets[0].Values.Count == 4);

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
            // 4. Download the workbook.
            // ---------------------------------------------------------------
            ExcelBook.Export(store, new[] { test }, workbook);

            Check("the workbook was written", File.Exists(workbook));

            using (var book = new XLWorkbook(workbook))
            {
                var names = book.Worksheets.Select(w => w.Name).ToList();
                log.AppendLine($"Sheets      : {string.Join(", ", names)}");

                Check("the workbook explains itself", names.Contains("How to use"));
                Check("it has a Test Cases sheet", names.Contains("Test Cases"));
                Check("it has a Steps sheet", names.Contains("Steps"));
                Check("it has an Assertions sheet", names.Contains("Assertions"));
                Check("it has a Results sheet", names.Contains("Results"));
                Check("it has a data sheet for the test case", names.Any(n => n.StartsWith("Data")));
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

            var reloaded = store.LoadAll().Single(t => t.Id == test.Id);

            Check("the second data row came through", reloaded.DataSets.Count == 2,
                $"{reloaded.DataSets.Count} row(s)");
            Check("the steps survived the round trip", reloaded.Steps.Count == test.Steps.Count,
                $"{reloaded.Steps.Count} vs {test.Steps.Count}");
            Check("the assertions survived the round trip", reloaded.Assertions.Count == 2);
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

            // ---------------------------------------------------------------
            // 8. The generated C# reflects all of it.
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
            Check("the generated test picks the result with a text fallback",
                code.Contains("ClickStepAsync("));
            Check("the generated fixture is valid NUnit shape",
                code.Contains("[TestFixture]") && code.Contains(": PageTest"));

            // Cleanup.
            Storage.DeleteProject(ProjectName);

            if (Directory.Exists(store.CodeDir))
            {
                Directory.Delete(store.CodeDir, recursive: true);
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
        await page.SelectOptionAsync("#role", new Microsoft.Playwright.SelectOptionValue { Label = "Manager" });
        await page.CheckAsync("input[name=\"plan\"][value=\"pro\"]");
        await page.CheckAsync("#terms");

        var search = page.Locator("#itemSearch");
        await search.ClickAsync();
        await search.PressSequentiallyAsync("widget", new Microsoft.Playwright.LocatorPressSequentiallyOptions { Delay = 40 });
        await page.WaitForSelectorAsync("#itemResults li");
        await page.GetByText("Widget clamp").ClickAsync();

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
    /// Does to the workbook what a tester would do in Excel: copy the recorded
    /// row, rename it, and change the values.
    /// </summary>
    private static void AddDataRow(string path, TestCase test, string expectedColumn, StringBuilder log)
    {
        using var book = new XLWorkbook(path);
        var ws = book.Worksheets.First(w => w.Name.StartsWith("Data"));

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

        // Row 2 is the recorded one; row 3 is the tester's addition.
        ws.Cell(3, 1).Value = test.Id;
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

            <p><label for="notes">Notes</label>
               <textarea id="notes" name="notes"></textarea></p>

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

            document.getElementById('submit').addEventListener('click', function () {
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
