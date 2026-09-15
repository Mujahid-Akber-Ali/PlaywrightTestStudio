using System.Text;

namespace PlaywrightTestStudio;

/// <summary>
/// End-to-end check of the engine with no UI in the way: build a page, scan it,
/// fill it, run it, and read back the generated C# file. Run with
/// <c>PlaywrightTestStudio.exe --selftest</c>; the log lands in %TEMP%.
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

        try
        {
            var pageFile = WriteSamplePage();
            var url = new Uri(pageFile).AbsoluteUri;

            log.AppendLine($"Sample page : {pageFile}");

            var elements = Scanner.ScanAsync(url, headless: true).GetAwaiter().GetResult();

            log.AppendLine($"Scanned     : {elements.Count} control(s)");

            foreach (var element in elements)
            {
                log.AppendLine($"              {element.Kind,-9} {element.Selector,-34} {element.Display}");
            }

            Check("one dropdown found", elements.Count(e => e.Kind == "select") == 1);
            Check("two text fields found", elements.Count(e => e.Kind == "text") == 2,
                $"got {elements.Count(e => e.Kind == "text")}");

            var search = elements.FirstOrDefault(e => e.Kind == "search");
            Check("the item lookup is read as a search field, not a text box or dropdown",
                search is not null && search.Selector == "#itemSearch",
                search?.Selector ?? "none found");
            Check("the search field knows where its results appear",
                search?.ResultsSelector == "#itemResults", search?.ResultsSelector ?? "");
            Check("two radio buttons found", elements.Count(e => e.Kind == "radio") == 2,
                $"got {elements.Count(e => e.Kind == "radio")}");
            Check("one checkbox found", elements.Count(e => e.Kind == "checkbox") == 1);
            Check("one button found", elements.Count(e => e.Kind == "button") == 1,
                $"got {elements.Count(e => e.Kind == "button")}");

            var role = elements.First(e => e.Kind == "select");
            Check("dropdown options read", role.Options.Contains("Manager"),
                string.Join("|", role.Options));

            var pro = elements.First(e => e.Kind == "radio" && e.Value == "pro");
            Check("radio label read from wrapping label", pro.Label.Contains("Pro"), pro.Label);

            var store = Storage.OpenProject(ProjectName);

            var test = new TestCase
            {
                Name = "Submit the sample form",
                Url = url,
                Elements = elements,
                Fields = new List<FieldValue>
                {
                    new() { Kind = "text", Selector = "#username", Value = "saif", Label = "User name" },
                    new() { Kind = "select", Selector = "#role", Value = "Manager", Label = "Role" },
                    new() { Kind = "radio", Selector = pro.Selector, Value = pro.Label, Group = "plan", Label = "Plan" },
                    new() { Kind = "checkbox", Selector = "#terms", Value = "true", Label = "Terms" },
                    new()
                    {
                        Kind = "search",
                        Selector = "#itemSearch",
                        Label = "Item",
                        Value = "widget",
                        Pick = "Widget clamp",
                        ResultsSelector = search?.ResultsSelector ?? ""
                    }
                },
                Clicks = new List<ClickStep>
                {
                    new() { Label = "Submit", Selector = "#submit", WaitAfter = false }
                },
                Assertions = new List<AssertionSpec>
                {
                    new() { Kind = AssertionKinds.PageTextContains, Expected = "Submitted: saif / Manager / pro / true / Widget clamp" },
                    new() { Kind = AssertionKinds.ElementValueEquals, Selector = "#itemSearch", Expected = "Widget clamp" },
                    new() { Kind = AssertionKinds.ElementVisible, Selector = "#result" },
                    new() { Kind = AssertionKinds.ElementValueEquals, Selector = "#username", Expected = "saif" }
                }
            };

            var result = Runner.RunAsync(test, store, headless: true).GetAwaiter().GetResult();

            log.AppendLine($"Run status  : {result.Status} in {result.Seconds}s - {result.Message}");

            foreach (var assertion in result.Assertions)
            {
                log.AppendLine($"              {(assertion.Passed ? "ok  " : "FAIL")} {assertion.Description}  actual: {assertion.Actual}");
            }

            Check("the happy path run passes, type-ahead included", result.IsPass, result.Message);
            Check("a screenshot was captured", File.Exists(result.ScreenshotPath));

            // Now break one assertion and confirm the run reports Failed rather
            // than quietly passing.
            test.Assertions.Add(new AssertionSpec
            {
                Kind = AssertionKinds.PageTextContains,
                Expected = "this text is definitely not on the page"
            });

            var negative = Runner.RunAsync(test, store, headless: true).GetAwaiter().GetResult();

            Check("a wrong assertion fails the run", negative.Status == "Failed", negative.Status);

            test.Assertions.RemoveAt(test.Assertions.Count - 1);

            var codeFile = CodeGen.WriteForLink(store, url, new[] { test });
            var code = File.ReadAllText(codeFile);

            log.AppendLine($"Code file   : {codeFile}");
            log.AppendLine();
            log.AppendLine("--- generated fixture ---");
            log.AppendLine(code);
            log.AppendLine("--- end ---");
            log.AppendLine();

            Check("generated file fills the text field", code.Contains("Page.FillAsync(\"#username\", \"saif\")"));
            Check("generated file selects by label", code.Contains("SelectOptionAsync(\"#role\""));
            Check("generated file checks the radio", code.Contains("Page.CheckAsync("));
            Check("generated file sets the checkbox", code.Contains("SetCheckedAsync(\"#terms\", true)"));
            Check("generated file clicks the button", code.Contains("Page.ClickAsync(\"#submit\")"));
            Check("generated file types into the search field key by key",
                code.Contains("PressSequentiallyAsync(\"widget\""));
            Check("generated file picks the search result",
                code.Contains("Widget clamp") && code.Contains("ClickAsync(new LocatorClickOptions"));
            Check("generated file asserts", code.Contains("Assert.That(await Page.InnerTextAsync(\"body\")"));
            Check("generated file is a fixture", code.Contains("[TestFixture]") && code.Contains("[Test]"));

            // Leave nothing behind: the throwaway project, its generated folder
            // and the sample page all go.
            Storage.DeleteProject(ProjectName);

            if (Directory.Exists(store.CodeDir))
            {
                Directory.Delete(store.CodeDir, recursive: true);
            }

            File.Delete(pageFile);
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

    /// <summary>A small page holding one of every control the scanner handles.</summary>
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
