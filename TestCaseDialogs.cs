using System.Drawing;
using System.Windows.Forms;

namespace PlaywrightTestStudio;

/// <summary>
/// The back half of the flow: what makes a test case pass, running it against
/// every row of test data, and looking back over what has been recorded.
/// </summary>
internal static class TestCaseDialogs
{
    /// <summary>What the assertion popup hands back.</summary>
    internal sealed class AssertionChoice
    {
        public string Name { get; init; } = "";
        public List<AssertionSpec> Assertions { get; init; } = new();
        public bool RunNow { get; init; }
    }

    /// <summary>
    /// Asks what makes this test case pass. At least one assertion is required -
    /// a test that checks nothing cannot fail, which is worse than no test.
    /// </summary>
    public static AssertionChoice? AskAssertions(
        IWin32Window owner,
        TestCase test,
        string? suggestedName = null)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Assertion", 980, 560, resizable: true);

        form.Controls.Add(Ui.Title("What should this test check?"));
        form.Controls.Add(Ui.Hint(
            "Name the test case, then add the checks that decide pass or fail. They all run, and any failure " +
            "fails the test. Put a data column name in \"Data column\" to have the expected text come from the " +
            "Excel sheet instead of being fixed.", 22, 46, 930));

        var nameLabel = Ui.Hint("Test case name", 20, 96);
        nameLabel.Font = Ui.Bold;
        nameLabel.ForeColor = SystemColors.ControlText;

        var nameInput = new TextBox
        {
            Font = Ui.Input,
            Location = new Point(20, 116),
            Size = new Size(936, 30),
            Text = test.Name.Length > 0 ? test.Name : suggestedName ?? "",
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var grid = Ui.NewGrid(20, 162, 936, 264);

        var kindColumn = new DataGridViewComboBoxColumn
        {
            Name = "Kind",
            HeaderText = "Check",
            Width = 190,
            FlatStyle = FlatStyle.Flat
        };

        kindColumn.Items.AddRange(AssertionKinds.All);

        grid.Columns.Add(kindColumn);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Selector", HeaderText = "Element (element checks only)", Width = 260 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Expected",
            HeaderText = "Expected text",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Parameter", HeaderText = "Data column", Width = 130 });

        grid.DataError += (_, e) => e.ThrowException = false;

        if (test.Assertions.Count > 0)
        {
            foreach (var spec in test.Assertions)
            {
                grid.Rows.Add(spec.Kind, spec.Selector, spec.Expected, spec.Parameter);
            }
        }
        else
        {
            grid.Rows.Add(AssertionKinds.PageTextContains, "", "", "");
        }

        var add = Ui.Secondary("Add check", 20, 442, 100);
        var remove = Ui.Secondary("Remove", 128, 442, 90);
        var pick = Ui.Secondary("Pick element...", 226, 442, 120);
        var save = Ui.Primary("Save", 606, 442, 110);
        var saveRun = Ui.Primary("Save and run", 724, 442, 124);
        var cancel = Ui.Secondary("Cancel", 856, 442, 100);

        foreach (var control in new Control[] { add, remove, pick })
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        }

        foreach (var control in new Control[] { save, saveRun, cancel })
        {
            control.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        }

        add.Click += (_, _) => grid.Rows.Add(AssertionKinds.PageTextContains, "", "", "");

        remove.Click += (_, _) =>
        {
            if (grid.CurrentRow is not null && grid.Rows.Count > 1)
            {
                grid.Rows.RemoveAt(grid.CurrentRow.Index);
            }
        };

        pick.Click += (_, _) =>
        {
            if (grid.CurrentRow is null)
            {
                return;
            }

            var selector = PickElement(form, test);

            if (selector is not null)
            {
                grid.CurrentRow.Cells["Selector"].Value = selector;
            }
        };

        AssertionChoice? choice = null;

        AssertionChoice? Collect(bool runNow)
        {
            grid.EndEdit();

            var name = nameInput.Text.Trim();

            if (name.Length == 0)
            {
                Ui.Warn("Give the test case a name - it becomes the method name in the generated C# file.");
                return null;
            }

            var specs = new List<AssertionSpec>();

            foreach (DataGridViewRow row in grid.Rows)
            {
                var kind = row.Cells["Kind"].Value?.ToString() ?? "";

                if (kind.Length == 0)
                {
                    continue;
                }

                var spec = new AssertionSpec
                {
                    Kind = kind,
                    Selector = row.Cells["Selector"].Value?.ToString()?.Trim() ?? "",
                    Expected = row.Cells["Expected"].Value?.ToString()?.Trim() ?? "",
                    Parameter = row.Cells["Parameter"].Value?.ToString()?.Trim() ?? ""
                };

                if (AssertionKinds.NeedsSelector(kind) && spec.Selector.Length == 0)
                {
                    Ui.Warn($"\"{kind}\" needs an element.\r\nUse \"Pick element...\" to choose one from the recording.");
                    return null;
                }

                if (AssertionKinds.NeedsExpected(kind) && spec.Expected.Length == 0 && spec.Parameter.Length == 0)
                {
                    Ui.Warn($"\"{kind}\" needs the text to look for, or a data column to take it from.");
                    return null;
                }

                specs.Add(spec);
            }

            if (specs.Count == 0)
            {
                Ui.Warn("Add at least one check, otherwise the test can never fail.");
                return null;
            }

            return new AssertionChoice { Name = name, Assertions = specs, RunNow = runNow };
        }

        save.Click += (_, _) =>
        {
            choice = Collect(runNow: false);

            if (choice is not null)
            {
                form.DialogResult = DialogResult.OK;
            }
        };

        saveRun.Click += (_, _) =>
        {
            choice = Collect(runNow: true);

            if (choice is not null)
            {
                form.DialogResult = DialogResult.OK;
            }
        };

        cancel.Click += (_, _) => form.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[]
        {
            nameLabel, nameInput, grid, add, remove, pick, save, saveRun, cancel
        });

        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK ? choice : null;
    }

    /// <summary>
    /// The elements this test case already touches, so an assertion can point at
    /// one without anybody typing a selector.
    /// </summary>
    private static string? PickElement(IWin32Window owner, TestCase test)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Pick element", 760, 460, resizable: true);

        form.Controls.Add(Ui.Title("Which element?"));
        form.Controls.Add(Ui.Hint("Everything this test case touches, plus anything found by a page scan.",
            22, 46, 700));

        var grid = Ui.NewGrid(20, 76, 716, 300);
        grid.ReadOnly = true;
        grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "From", HeaderText = "From", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "Field / label", Width = 240 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Selector",
            HeaderText = "Selector",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddRow(string from, string label, string selector)
        {
            if (selector.Length == 0 || !seen.Add(selector))
            {
                return;
            }

            var index = grid.Rows.Add(from, label, selector);
            grid.Rows[index].Cells["Selector"].Style.Font = Ui.Mono;
        }

        foreach (var step in test.Steps)
        {
            AddRow(step.Action, step.Display, step.Selector);
        }

        foreach (var element in test.Elements)
        {
            AddRow(ScanDialogs.KindLabel(element.Kind), element.Display, element.Selector);
        }

        var use = Ui.Primary("Use this", 508, 392, 110);
        var cancel = Ui.Secondary("Cancel", 626, 392, 110);
        use.Anchor = cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;

        use.Click += (_, _) => form.DialogResult = DialogResult.OK;
        grid.CellDoubleClick += (_, _) => form.DialogResult = DialogResult.OK;
        cancel.Click += (_, _) => form.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[] { grid, use, cancel });
        form.AcceptButton = use;
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK && grid.CurrentRow is not null
            ? grid.CurrentRow.Cells["Selector"].Value?.ToString()
            : null;
    }

    /// <summary>
    /// Runs one test case against every enabled data row, stores each result,
    /// and refreshes the generated C# file.
    /// </summary>
    public static List<TestResult> RunAndSave(IWin32Window owner, ProjectStore store, TestCase test)
    {
        var results = Ui.RunWithProgress(owner, $"Running: {test.Name}", "Starting...",
            report => Runner.RunAllDataSetsAsync(test, store, AppSettings.Headless, report));

        store.Save(test);

        foreach (var result in results)
        {
            store.AppendRun(test, result);
        }

        RegenerateCode(store, test);

        return results;
    }

    /// <summary>Runs one test case, writes the results file, and shows how it went.</summary>
    public static void RunAndShow(IWin32Window owner, ProjectStore store, TestCase test)
    {
        var results = RunAndSave(owner, store, test);

        ShowResult(owner, test, results, WriteResultsFile(store));
    }

    /// <summary>
    /// Writes the results file for the project as it stands: every test case,
    /// with how each of its data rows last did. Returns the path, or null if
    /// the file could not be written.
    /// </summary>
    public static string? WriteResultsFile(ProjectStore store)
    {
        try
        {
            return ResultsBook.Write(store, store.LoadAll());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error($"The run finished, but its results file could not be written.\r\n\r\n{ex.Message}");
            return null;
        }
    }

    /// <summary>Writes a fresh results file from the last run of every test case and opens it.</summary>
    public static void OpenResultsFile(ProjectStore store)
    {
        if (store.LoadAll().All(t => t.LastResult is null))
        {
            Ui.Info("Nothing has been run in this project yet, so there are no results to show.");
            return;
        }

        if (WriteResultsFile(store) is { } file)
        {
            Ui.Open(file);
        }
    }

    /// <summary>Rewrites the file that covers this test case's link.</summary>
    public static void RegenerateCode(ProjectStore store, TestCase test)
    {
        var sameLink = store.LoadAll().Where(t => t.Url == test.Url).ToList();

        if (sameLink.All(t => t.Id != test.Id))
        {
            sameLink.Add(test);
        }

        test.CodeFile = CodeGen.WriteForLink(store, test.Url, sameLink);
        store.Save(test);
    }

    /// <summary>
    /// Runs every test case in the project, each against all its data rows.
    /// Sequential on purpose: they share one browser profile.
    /// </summary>
    public static void RunAll(IWin32Window owner, ProjectStore store, bool ask = true)
    {
        var tests = store.LoadAll().Where(t => t.Enabled).ToList();

        if (tests.Count == 0)
        {
            Ui.Info("There are no enabled test cases in this project yet.");
            return;
        }

        var rows = tests.Sum(t => t.RunnableDataSets().Count);

        if (ask && !Ui.Confirm($"Run {tests.Count} test case(s) over {rows} data row(s) now?"))
        {
            return;
        }

        var all = Ui.RunWithProgress(owner, "Running all test cases", "Starting...",
            async report =>
            {
                var log = new List<(TestCase Test, TestResult Result)>();

                // One window for the whole run: starting a browser per test
                // case would cost more time than most test cases take.
                await using var browser = new RunBrowser(AppSettings.Headless);

                for (var i = 0; i < tests.Count; i++)
                {
                    var test = tests[i];
                    var position = i + 1;

                    var results = await Runner.RunAllDataSetsAsync(test, store, AppSettings.Headless,
                        line => report($"[{position}/{tests.Count}] {test.Name} - {line}"), browser);

                    store.Save(test);

                    foreach (var result in results)
                    {
                        store.AppendRun(test, result);
                        log.Add((test, result));
                    }
                }

                return log;
            });

        foreach (var test in all.Select(r => r.Test).Distinct())
        {
            RegenerateCode(store, test);
        }

        var passed = all.Count(r => r.Result.IsPass);
        var file = WriteResultsFile(store);

        var lines = all.Select(r =>
            $"{(r.Result.IsPass ? "PASS" : "FAIL")}  {r.Test.Name}" +
            (r.Result.DataSet.Length > 0 ? $"  [{r.Result.DataSet}]" : "")).ToList();

        var summary = $"{passed} of {all.Count} run(s) passed.\r\n\r\n" +
                      string.Join("\r\n", lines.Take(20)) +
                      (lines.Count > 20 ? $"\r\n... and {lines.Count - 20} more." : "");

        if (file is null)
        {
            Ui.Info(summary, $"{Ui.AppTitle} - run finished");
            return;
        }

        if (Ui.Confirm($"{summary}\r\n\r\nThe results file, with charts, is at:\r\n{file}\r\n\r\nOpen it now?",
                $"{Ui.AppTitle} - run finished"))
        {
            Ui.Open(file);
        }
    }

    /// <summary>The pass/fail popup shown straight after a run.</summary>
    public static void ShowResult(IWin32Window owner, TestCase test, List<TestResult> results,
        string? resultsFile = null)
    {
        var overall = results.Count > 0 && results.All(r => r.IsPass);

        using var form = Ui.NewForm($"{Ui.AppTitle} - {(overall ? "Passed" : "Failed")}", 820, 520, resizable: true);

        var banner = new Label
        {
            Text = overall ? "PASSED" : "FAILED",
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = overall ? Ui.Pass : Ui.Fail,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
            Location = new Point(0, 0),
            Size = new Size(820, 52),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        form.Controls.Add(banner);
        form.Controls.Add(Ui.Hint($"{test.Name}    -    {test.Url}", 20, 64, 770));

        var passed = results.Count(r => r.IsPass);
        form.Controls.Add(Ui.Hint($"{passed} of {results.Count} data row(s) passed.", 20, 88, 770));

        var grid = Ui.NewGrid(20, 128, 776, 300);
        grid.ReadOnly = true;
        grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Result", HeaderText = "Result", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DataSet", HeaderText = "Data row", Width = 120 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Check", HeaderText = "Check", Width = 300 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Actual",
            HeaderText = "Actual",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        foreach (var result in results)
        {
            if (result.Assertions.Count == 0)
            {
                var only = grid.Rows.Add(result.Status, result.DataSet, result.Message, "");
                grid.Rows[only].Cells["Result"].Style.ForeColor = Ui.Fail;
                grid.Rows[only].Tag = result;
                continue;
            }

            foreach (var assertion in result.Assertions)
            {
                var index = grid.Rows.Add(assertion.Passed ? "pass" : "FAIL", result.DataSet,
                    assertion.Description, assertion.Actual);

                grid.Rows[index].Cells["Result"].Style.ForeColor = assertion.Passed ? Ui.Pass : Ui.Fail;
                grid.Rows[index].Cells["Result"].Style.Font = Ui.Bold;
                grid.Rows[index].Tag = result;
            }
        }

        var shot = Ui.Secondary("Open screenshot", 20, 444, 124);
        var code = Ui.Secondary("Open C# file", 150, 444, 110);
        var video = Ui.Secondary("Open recording", 266, 444, 124);
        var file = Ui.Secondary("Open results file", 396, 444, 134);
        var close = Ui.Primary("Close", 686, 444, 110);

        foreach (var control in new Control[] { shot, code, video, file })
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        }

        file.Visible = resultsFile is not null;
        file.Click += (_, _) => Ui.Open(resultsFile ?? "");

        close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        code.Enabled = test.CodeFile.Length > 0;
        video.Enabled = test.VideoFile.Length > 0 && File.Exists(test.VideoFile);

        shot.Click += (_, _) =>
        {
            var chosen = (grid.CurrentRow?.Tag as TestResult) ?? results.FirstOrDefault();
            Ui.Reveal(chosen?.ScreenshotPath ?? "");
        };

        code.Click += (_, _) => Ui.Reveal(test.CodeFile);
        video.Click += (_, _) => Ui.Reveal(test.VideoFile);
        close.Click += (_, _) => form.DialogResult = DialogResult.OK;

        form.Controls.AddRange(new Control[] { grid, shot, code, video, file, close });
        form.AcceptButton = close;
        form.CancelButton = close;

        form.ShowDialog(owner);
    }

    /// <summary>
    /// The "all test cases" popup: every scenario in the project, how many data
    /// rows it carries, and how it last did.
    /// </summary>
    public static void ShowAll(IWin32Window owner, ProjectStore store)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - {store.Name} - all test cases", 1040, 620, resizable: true);

        form.Controls.Add(Ui.Title("All test cases"));

        var summary = Ui.Hint("", 22, 46, 990);

        var grid = Ui.NewGrid(20, 76, 996, 452);
        grid.ReadOnly = true;
        grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", Width = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Test case", Width = 220 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Url",
            HeaderText = "Link",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Data", HeaderText = "Data rows", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Steps", HeaderText = "Steps", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Checks", HeaderText = "Checks", Width = 60 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LastRun", HeaderText = "Last run", Width = 130 });

        var tests = new List<TestCase>();

        void Reload()
        {
            grid.Rows.Clear();
            tests = store.LoadAll();

            foreach (var test in tests)
            {
                var status = test.LastResult?.Status ?? "Not run";
                var lastRun = test.LastResult is null
                    ? "-"
                    : test.LastResult.RanUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

                var rows = test.DataSets.Count(d => d.Enabled);

                var index = grid.Rows.Add(
                    status,
                    test.Name,
                    test.Url,
                    rows == 0 ? "1" : rows.ToString(),
                    test.Steps.Count(s => s.Use).ToString(),
                    test.Assertions.Count.ToString(),
                    lastRun);

                grid.Rows[index].Tag = test;
                grid.Rows[index].Cells["Status"].Style.Font = Ui.Bold;
                grid.Rows[index].Cells["Status"].Style.ForeColor = status switch
                {
                    "Passed" => Ui.Pass,
                    "Failed" or "Error" => Ui.Fail,
                    _ => Ui.Muted
                };

                if (!test.Enabled)
                {
                    grid.Rows[index].DefaultCellStyle.ForeColor = Ui.Muted;
                }
            }

            var passed = tests.Count(t => t.LastResult?.IsPass == true);
            var failed = tests.Count(t => t.LastResult is not null && !t.LastResult.IsPass);
            var never = tests.Count - passed - failed;
            var dataRows = tests.Sum(t => t.RunnableDataSets().Count);

            summary.Text = $"{tests.Count} test case(s) across {tests.Select(t => t.Url).Distinct().Count()} link(s), " +
                           $"{dataRows} data row(s)    -    {passed} passed, {failed} failed, {never} never run";
        }

        Reload();

        TestCase? Selected() => grid.CurrentRow?.Tag as TestCase;

        var run = Ui.Primary("Run selected", 20, 544, 110);
        var runAll = Ui.Primary("Run all", 136, 544, 80);
        var details = Ui.Secondary("Last result", 222, 544, 92);
        var resultsFile = Ui.Secondary("Results file", 320, 544, 92);
        var steps = Ui.Secondary("Edit steps", 418, 544, 90);
        var checks = Ui.Secondary("Edit checks", 514, 544, 94);
        var openCode = Ui.Secondary("Open C# file", 614, 544, 104);
        var delete = Ui.Secondary("Delete", 724, 544, 76);
        var close = Ui.Secondary("Close", 916, 544, 100);

        resultsFile.Click += (_, _) => OpenResultsFile(store);

        foreach (var control in new Control[] { run, runAll, details, resultsFile, steps, checks, openCode, delete })
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        }

        close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;

        run.Click += (_, _) =>
        {
            if (Selected() is not { } test)
            {
                return;
            }

            var results = RunAndSave(form, store, test);
            Reload();
            ShowResult(form, test, results, WriteResultsFile(store));
        };

        runAll.Click += (_, _) =>
        {
            RunAll(form, store);
            Reload();
        };

        details.Click += (_, _) =>
        {
            if (Selected() is not { } test)
            {
                return;
            }

            if (test.LastResults.Count == 0 && test.LastResult is null)
            {
                Ui.Info("This test case has not been run yet.");
                return;
            }

            var results = test.LastResults.Count > 0
                ? test.LastResults
                : new List<TestResult> { test.LastResult! };

            ShowResult(form, test, results);
        };

        steps.Click += (_, _) =>
        {
            if (Selected() is not { } test)
            {
                return;
            }

            if (!RecordDialogs.EditSteps(form, test, $"Steps: {test.Name}"))
            {
                return;
            }

            RecordDialogs.SyncRecordedRow(test);
            store.Save(test);
            RegenerateCode(store, test);
            Reload();
        };

        checks.Click += (_, _) =>
        {
            if (Selected() is not { } test)
            {
                return;
            }

            var choice = AskAssertions(form, test);

            if (choice is null)
            {
                return;
            }

            test.Name = choice.Name;
            test.Assertions = choice.Assertions;

            RecordDialogs.SyncRecordedRow(test);
            store.Save(test);
            RegenerateCode(store, test);
            Reload();

            if (choice.RunNow)
            {
                var results = RunAndSave(form, store, test);
                Reload();
                ShowResult(form, test, results, WriteResultsFile(store));
            }
        };

        openCode.Click += (_, _) =>
        {
            if (Selected() is { } test)
            {
                Ui.Reveal(test.CodeFile.Length > 0 ? test.CodeFile : store.CodeDir);
            }
        };

        delete.Click += (_, _) =>
        {
            if (Selected() is not { } test)
            {
                return;
            }

            if (!Ui.Confirm($"Delete test case \"{test.Name}\"?"))
            {
                return;
            }

            store.Delete(test);

            // The link's file still has to describe the cases that are left.
            var remaining = store.LoadAll().Where(t => t.Url == test.Url).ToList();
            CodeGen.WriteForLink(store, test.Url, remaining);

            Reload();
        };

        close.Click += (_, _) => form.DialogResult = DialogResult.OK;

        grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
            {
                details.PerformClick();
            }
        };

        form.Controls.Add(summary);
        form.Controls.AddRange(new Control[]
        {
            grid, run, runAll, details, resultsFile, steps, checks, openCode, delete, close
        });

        form.CancelButton = close;

        form.ShowDialog(owner);
    }
}
