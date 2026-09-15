using System.Drawing;
using System.Windows.Forms;

namespace PlaywrightTestStudio;

/// <summary>
/// The home screen for one project. Everything else is a popup launched from
/// here, which keeps the flow - link, elements, data, assertion, result - the
/// same every time.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly ProjectStore _store;
    private readonly ListBox _recent;
    private readonly Label _summary;

    /// <summary>Set when the operator asks for a different project.</summary>
    public bool SwitchRequested { get; private set; }

    public MainForm(ProjectStore store)
    {
        _store = store;

        Text = $"{Ui.AppTitle} - {store.Name}";
        Font = Ui.Body;
        ClientSize = new Size(720, 540);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        BackColor = Color.White;

        Controls.Add(Ui.Title($"Project: {store.Name}"));

        _summary = Ui.Hint("", 22, 46, 670);
        Controls.Add(_summary);

        var newTest = Ui.Primary("New test case from a link...", 20, 84, 330);
        newTest.Height = 44;
        newTest.Font = new Font("Segoe UI", 10F, FontStyle.Bold);

        var showAll = Ui.Primary("Show all test cases", 360, 84, 330);
        showAll.Height = 44;
        showAll.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        showAll.BackColor = Color.FromArgb(60, 60, 60);

        var runAll = Ui.Secondary("Run all test cases", 20, 142, 216);
        var signIn = Ui.Secondary("Sign in to a site...", 244, 142, 216);
        var openCode = Ui.Secondary("Open generated C# files", 472, 142, 218);

        var headless = new CheckBox
        {
            Text = "Run the browser hidden (headless)",
            Font = Ui.Body,
            AutoSize = true,
            Checked = AppSettings.Headless,
            Location = new Point(22, 188)
        };

        var recentLabel = Ui.Hint("Recent runs", 20, 218);
        recentLabel.Font = Ui.Bold;
        recentLabel.ForeColor = SystemColors.ControlText;

        _recent = new ListBox
        {
            Font = Ui.Mono,
            Location = new Point(20, 240),
            Size = new Size(670, 216),
            IntegralHeight = false
        };

        var switchProject = Ui.Secondary("Switch project", 20, 470, 150);
        var dataFolder = Ui.Secondary("Open data folder", 178, 470, 150);
        var quit = Ui.Secondary("Quit", 590, 470, 100);

        newTest.Click += (_, _) => NewTestCase();
        showAll.Click += (_, _) => { TestCaseDialogs.ShowAll(this, _store); RefreshView(); };
        runAll.Click += (_, _) => { TestCaseDialogs.RunAll(this, _store); RefreshView(); };
        openCode.Click += (_, _) => Ui.Reveal(_store.CodeDir);
        dataFolder.Click += (_, _) => Ui.Reveal(_store.Dir);

        signIn.Click += (_, _) =>
        {
            var url = ProjectDialogs.AskLink(this);

            if (url is null)
            {
                return;
            }

            try
            {
                Ui.RunWithProgress<bool>(this, "Sign in",
                    "A browser window is open. Sign in, then close it to come back.",
                    async _ =>
                    {
                        await Runner.OpenForLoginAsync(url);
                        return true;
                    });
            }
            catch (Exception ex)
            {
                Ui.Error(BrowserSession.Explain(ex));
            }
        };

        headless.CheckedChanged += (_, _) =>
        {
            AppSettings.Headless = headless.Checked;
            AppSettings.Save();
        };

        switchProject.Click += (_, _) =>
        {
            SwitchRequested = true;
            Close();
        };

        quit.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            newTest, showAll, runAll, signIn, openCode, headless,
            recentLabel, _recent, switchProject, dataFolder, quit
        });

        Shown += (_, _) => RefreshView();
    }

    /// <summary>Reloads the counts and the recent-run list from disk.</summary>
    private void RefreshView()
    {
        var tests = _store.LoadAll();
        var passed = tests.Count(t => t.LastResult?.IsPass == true);
        var failed = tests.Count(t => t.LastResult is not null && !t.LastResult.IsPass);

        _summary.Text = $"{tests.Count} test case(s) across {tests.Select(t => t.Url).Distinct().Count()} link(s)" +
                        $"    -    {passed} passed, {failed} failed";

        _recent.Items.Clear();

        foreach (var run in _store.LoadRuns().AsEnumerable().Reverse().Take(60))
        {
            _recent.Items.Add(
                $"{run.RanUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {(run.Status == "Passed" ? "PASS" : "FAIL"),-5} " +
                $"{Trim(run.TestName, 28),-28}  {Trim(run.Message, 60)}");
        }

        if (_recent.Items.Count == 0)
        {
            _recent.Items.Add("Nothing has been run in this project yet.");
        }
    }

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";

    /// <summary>
    /// The whole flow for a new scenario: link, elements, data, assertion, and
    /// optionally the first run.
    /// </summary>
    private void NewTestCase()
    {
        var url = ProjectDialogs.AskLink(this);

        if (url is null)
        {
            return;
        }

        List<ElementInfo> elements;

        try
        {
            elements = Ui.RunWithProgress(this, "Scanning page", $"Opening {url}",
                async report =>
                {
                    report("Starting browser...");
                    var found = await Scanner.ScanAsync(url, AppSettings.Headless);
                    report($"{found.Count} control(s) found");
                    return found;
                });
        }
        catch (Exception ex)
        {
            Ui.Error(BrowserSession.Explain(ex), $"{Ui.AppTitle} - scan failed");
            return;
        }

        if (!ScanDialogs.ShowElements(this, url, elements))
        {
            return;
        }

        var data = ScanDialogs.AskData(this, url, elements);

        if (data is null)
        {
            return;
        }

        var existingForLink = _store.LoadAll().Count(t => t.Url == url);
        var suggested = $"{CodeGen.ClassNameFor(url).Replace("Tests", "")} case {existingForLink + 1}";

        var choice = TestCaseDialogs.AskAssertions(this, url, elements, existing: null, suggestedName: suggested);

        if (choice is null)
        {
            return;
        }

        var test = new TestCase
        {
            Name = choice.Name,
            Url = url,
            Elements = elements,
            Fields = data.Value.Fields,
            Clicks = data.Value.Clicks,
            Assertions = choice.Assertions
        };

        _store.Save(test);
        TestCaseDialogs.RegenerateCode(_store, test);

        if (choice.RunNow)
        {
            var result = TestCaseDialogs.RunAndSave(this, _store, test);
            RefreshView();
            TestCaseDialogs.ShowResult(this, test, result);
            return;
        }

        RefreshView();

        Ui.Info($"Saved \"{test.Name}\".\r\n\r\nC# file:\r\n{test.CodeFile}\r\n\r\n" +
                "Use \"Show all test cases\" to run it.");
    }
}
