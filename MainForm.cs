using System.Drawing;
using System.Windows.Forms;

namespace PlaywrightTestStudio;

/// <summary>
/// The home screen for one project. Recording is the way in; everything else -
/// the Excel round trip, running, results - hangs off it.
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
        ClientSize = new Size(760, 600);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        BackColor = Color.White;

        Controls.Add(Ui.Title($"Project: {store.Name}"));

        _summary = Ui.Hint("", 22, 46, 710);
        Controls.Add(_summary);

        var record = Ui.Primary("Record a test case", 20, 84, 350);
        record.Height = 46;
        record.Font = new Font("Segoe UI", 10F, FontStyle.Bold);

        var showAll = Ui.Primary("Show all test cases", 380, 84, 350);
        showAll.Height = 46;
        showAll.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
        showAll.BackColor = Color.FromArgb(60, 60, 60);

        var download = Ui.Secondary("Download Excel (test cases + data)", 20, 144, 350);
        var upload = Ui.Secondary("Upload Excel and run", 380, 144, 350);

        var runAll = Ui.Secondary("Run all test cases", 20, 182, 230);
        var scan = Ui.Secondary("Scan a page instead...", 258, 182, 230);
        var signIn = Ui.Secondary("Sign in to a site...", 496, 182, 234);

        var headless = new CheckBox
        {
            Text = "Run the browser hidden (headless). Recording is always visible.",
            Font = Ui.Body,
            AutoSize = true,
            Checked = AppSettings.Headless,
            Location = new Point(22, 226)
        };

        var recentLabel = Ui.Hint("Recent runs", 20, 256);
        recentLabel.Font = Ui.Bold;
        recentLabel.ForeColor = SystemColors.ControlText;

        _recent = new ListBox
        {
            Font = Ui.Mono,
            Location = new Point(20, 278),
            Size = new Size(710, 236),
            IntegralHeight = false
        };

        var switchProject = Ui.Secondary("Switch project", 20, 528, 150);
        var codeFolder = Ui.Secondary("Generated C#", 178, 528, 150);
        var dataFolder = Ui.Secondary("Data folder", 336, 528, 130);
        var quit = Ui.Secondary("Quit", 630, 528, 100);

        record.Click += async (_, _) =>
        {
            // Async so the window can hide and the message loop keeps running
            // while the recording is in progress.
            await RecordDialogs.RecordAsync(this, _store);
            RefreshView();
        };
        showAll.Click += (_, _) => { TestCaseDialogs.ShowAll(this, _store); RefreshView(); };
        runAll.Click += (_, _) => { TestCaseDialogs.RunAll(this, _store); RefreshView(); };
        scan.Click += (_, _) => { ScanNewTestCase(); RefreshView(); };
        download.Click += (_, _) => DownloadExcel();
        upload.Click += (_, _) => { UploadExcel(); RefreshView(); };
        codeFolder.Click += (_, _) => Ui.Reveal(_store.CodeDir);
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
            record, showAll, download, upload, runAll, scan, signIn, headless,
            recentLabel, _recent, switchProject, codeFolder, dataFolder, quit
        });

        Shown += (_, _) => RefreshView();
    }

    /// <summary>Reloads the counts and the recent-run list from disk.</summary>
    private void RefreshView()
    {
        var tests = _store.LoadAll();
        var passed = tests.Count(t => t.LastResult?.IsPass == true);
        var failed = tests.Count(t => t.LastResult is not null && !t.LastResult.IsPass);
        var rows = tests.Sum(t => t.RunnableDataSets().Count);

        _summary.Text = $"{tests.Count} test case(s) across {tests.Select(t => t.Url).Distinct().Count()} link(s), " +
                        $"{rows} data row(s)    -    {passed} passed, {failed} failed";

        _recent.Items.Clear();

        foreach (var run in _store.LoadRuns().AsEnumerable().Reverse().Take(60))
        {
            var row = run.DataSet.Length > 0 ? $" [{Trim(run.DataSet, 14)}]" : "";

            _recent.Items.Add(
                $"{run.RanUtc.ToLocalTime():yyyy-MM-dd HH:mm}  {(run.Status == "Passed" ? "PASS" : "FAIL"),-5} " +
                $"{Trim(run.TestName, 24) + row,-40}  {Trim(run.Message, 46)}");
        }

        if (_recent.Items.Count == 0)
        {
            _recent.Items.Add("Nothing has been run in this project yet.");
        }
    }

    private static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";

    /// <summary>
    /// Writes the project's workbook wherever the operator wants it. This is the
    /// template they hand to whoever is going to type the test data.
    /// </summary>
    private void DownloadExcel()
    {
        var tests = _store.LoadAll();

        if (tests.Count == 0)
        {
            Ui.Info("Record a test case first - there is nothing to put in the workbook yet.");
            return;
        }

        using var save = new SaveFileDialog
        {
            Title = "Download test cases and test data",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = _store.WorkbookName,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (save.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            ExcelBook.Export(_store, tests, save.FileName);
        }
        catch (IOException ex)
        {
            Ui.Error($"Could not write the workbook.\r\n\r\n{ex.Message}\r\n\r\n" +
                     "If it is open in Excel, close it and try again.");
            return;
        }

        var rows = tests.Sum(t => t.DataSets.Count);

        if (Ui.Confirm($"Saved {tests.Count} test case(s) and {rows} data row(s) to:\r\n{save.FileName}\r\n\r\n" +
                       "Open it now?"))
        {
            Ui.Open(save.FileName);
        }
    }

    /// <summary>
    /// Reads a filled-in workbook back and offers to run everything in it -
    /// which is the whole point of handing the file out.
    /// </summary>
    private void UploadExcel()
    {
        using var open = new OpenFileDialog
        {
            Title = "Upload test cases and test data",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (open.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        ExcelBook.ImportReport report;

        try
        {
            report = ExcelBook.Import(_store, open.FileName);
        }
        catch (Exception ex)
        {
            Ui.Error($"Could not read that workbook.\r\n\r\n{ex.Message}");
            return;
        }

        foreach (var test in _store.LoadAll())
        {
            TestCaseDialogs.RegenerateCode(_store, test);
        }

        var message = report.Summary;

        if (report.Problems.Count > 0)
        {
            message += "\r\n\r\nProblems:\r\n" + string.Join("\r\n", report.Problems.Take(12));

            if (report.Problems.Count > 12)
            {
                message += $"\r\n... and {report.Problems.Count - 12} more.";
            }
        }

        if (report.TestCases == 0)
        {
            Ui.Warn(message);
            return;
        }

        RefreshView();

        if (Ui.Confirm(message + "\r\n\r\nRun everything now?"))
        {
            TestCaseDialogs.RunAll(this, _store);
        }
    }

    /// <summary>
    /// The older way in, kept for a page you would rather read than act on:
    /// scan it, tick the fields, type the data.
    /// </summary>
    private void ScanNewTestCase()
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

        var steps = ScanDialogs.AskData(this, url, elements);

        if (steps is null)
        {
            return;
        }

        var existingForLink = _store.LoadAll().Count(t => t.Url == url);

        var test = new TestCase
        {
            Name = $"{CodeGen.ClassNameFor(url).Replace("Tests", "")} case {existingForLink + 1}",
            Url = url,
            Source = "scanned",
            Elements = elements,
            Steps = steps
        };

        RecordingSession.AssignParameters(test);

        var choice = TestCaseDialogs.AskAssertions(this, test, suggestedName: test.Name);

        if (choice is null)
        {
            return;
        }

        test.Name = choice.Name;
        test.Assertions = choice.Assertions;

        RecordingSession.AssignParameters(test);
        RecordDialogs.SyncRecordedRow(test);

        _store.Save(test);
        TestCaseDialogs.RegenerateCode(_store, test);

        if (choice.RunNow)
        {
            var results = TestCaseDialogs.RunAndSave(this, _store, test);
            RefreshView();
            TestCaseDialogs.ShowResult(this, test, results);
            return;
        }

        RefreshView();

        Ui.Info($"Saved \"{test.Name}\".\r\n\r\nC# file:\r\n{test.CodeFile}\r\n\r\n" +
                "Use \"Show all test cases\" to run it.");
    }
}
