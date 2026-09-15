using System.Drawing;
using System.Windows.Forms;

namespace PlaywrightTestStudio;

/// <summary>
/// The back half of the flow: naming the test case and its assertions, running
/// it, and looking back over everything that has been recorded.
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
        string url,
        IReadOnlyList<ElementInfo> elements,
        TestCase? existing = null,
        string? suggestedName = null)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Assertion", 940, 520, resizable: true);

        form.Controls.Add(Ui.Title("What should this test check?"));
        form.Controls.Add(Ui.Hint(
            "Name the test case, then add the checks that decide pass or fail. " +
            "They all run, and any failure fails the test.", 22, 46, 890));

        var nameLabel = Ui.Hint("Test case name", 20, 82);
        nameLabel.Font = Ui.Bold;
        nameLabel.ForeColor = SystemColors.ControlText;

        var nameInput = new TextBox
        {
            Font = Ui.Input,
            Location = new Point(20, 102),
            Size = new Size(896, 30),
            Text = existing?.Name ?? suggestedName ?? "",
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var grid = Ui.NewGrid(20, 148, 896, 258);

        var kindColumn = new DataGridViewComboBoxColumn
        {
            Name = "Kind",
            HeaderText = "Check",
            Width = 200,
            FlatStyle = FlatStyle.Flat
        };

        kindColumn.Items.AddRange(AssertionKinds.All);

        grid.Columns.Add(kindColumn);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Selector", HeaderText = "Element selector (only for element checks)", Width = 300 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Expected",
            HeaderText = "Expected text",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        grid.DataError += (_, e) => e.ThrowException = false;

        if (existing is not null && existing.Assertions.Count > 0)
        {
            foreach (var spec in existing.Assertions)
            {
                grid.Rows.Add(spec.Kind, spec.Selector, spec.Expected);
            }
        }
        else
        {
            grid.Rows.Add(AssertionKinds.PageTextContains, "", "");
        }

        var add = Ui.Secondary("Add check", 20, 422, 100);
        var remove = Ui.Secondary("Remove", 128, 422, 90);
        var pick = Ui.Secondary("Pick element...", 226, 422, 120);
        var save = Ui.Primary("Save", 566, 422, 110);
        var saveRun = Ui.Primary("Save and run", 684, 422, 124);
        var cancel = Ui.Secondary("Cancel", 816, 422, 100);

        foreach (var control in new Control[] { add, remove, pick })
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        }

        foreach (var control in new Control[] { save, saveRun, cancel })
        {
            control.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        }

        add.Click += (_, _) => grid.Rows.Add(AssertionKinds.PageTextContains, "", "");

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

            var selector = PickElement(form, elements);

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
                    Expected = row.Cells["Expected"].Value?.ToString()?.Trim() ?? ""
                };

                if (AssertionKinds.NeedsSelector(kind) && spec.Selector.Length == 0)
                {
                    Ui.Warn($"\"{kind}\" needs an element selector.\r\nUse \"Pick element...\" to choose one.");
                    return null;
                }

                if (AssertionKinds.NeedsExpected(kind) && spec.Expected.Length == 0)
                {
                    Ui.Warn($"\"{kind}\" needs the text to look for.");
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

    /// <summary>A short list of the scanned controls, for filling in a selector.</summary>
    private static string? PickElement(IWin32Window owner, IReadOnlyList<ElementInfo> elements)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Pick element", 720, 440, resizable: true);

        form.Controls.Add(Ui.Title("Which element?"));

        var grid = Ui.NewGrid(20, 56, 676, 300);
        grid.ReadOnly = true;
        grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "Field / label", Width = 240 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Selector",
            HeaderText = "Selector",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        foreach (var element in elements)
        {
            var index = grid.Rows.Add(ScanDialogs.KindLabel(element.Kind), element.Display, element.Selector);
            grid.Rows[index].Cells["Selector"].Style.Font = Ui.Mono;
        }

        var use = Ui.Primary("Use this", 468, 372, 110);
        var cancel = Ui.Secondary("Cancel", 586, 372, 110);
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
    /// Runs one test case, stores the result on the case and in the project run
    /// log, and refreshes the generated C# file so its comment header matches.
    /// </summary>
    public static TestResult RunAndSave(IWin32Window owner, ProjectStore store, TestCase test)
    {
        var result = Ui.RunWithProgress(owner, $"Running: {test.Name}", "Starting...",
            report => Runner.RunAsync(test, store, AppSettings.Headless, report));

        test.LastResult = result;
        store.Save(test);
        store.AppendRun(test, result);

        RegenerateCode(store, test);

        return result;
    }

    /// <summary>
    /// Runs every test case in the project, one after another, and reports the
    /// tally. Sequential on purpose: they share one browser profile.
    /// </summary>
    public static void RunAll(IWin32Window owner, ProjectStore store)
    {
        var tests = store.LoadAll();

        if (tests.Count == 0)
        {
            Ui.Info("There are no test cases in this project yet.");
            return;
        }

        if (!Ui.Confirm($"Run all {tests.Count} test case(s) now?"))
        {
            return;
        }

        var results = Ui.RunWithProgress(owner, "Running all test cases", "Starting...",
            async report =>
            {
                var log = new List<(TestCase Test, TestResult Result)>();

                for (var i = 0; i < tests.Count; i++)
                {
                    var test = tests[i];
                    var position = i + 1;

                    var result = await Runner.RunAsync(test, store, AppSettings.Headless,
                        line => report($"[{position}/{tests.Count}] {test.Name} - {line}"));

                    test.LastResult = result;
                    store.Save(test);
                    store.AppendRun(test, result);
                    log.Add((test, result));
                }

                return log;
            });

        foreach (var (test, _) in results)
        {
            RegenerateCode(store, test);
        }

        var passed = results.Count(r => r.Result.IsPass);

        Ui.Info($"{passed} of {results.Count} test case(s) passed.\r\n\r\n" +
                string.Join("\r\n", results.Select(r => $"{(r.Result.IsPass ? "PASS" : "FAIL")}  {r.Test.Name}")),
            $"{Ui.AppTitle} - run finished");
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

    /// <summary>The pass/fail popup shown straight after a run.</summary>
    public static void ShowResult(IWin32Window owner, TestCase test, TestResult result)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - {result.Status}", 760, 480, resizable: true);

        var banner = new Label
        {
            Text = result.IsPass ? "PASSED" : result.Status.ToUpperInvariant(),
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = result.IsPass ? Ui.Pass : Ui.Fail,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(16, 0, 0, 0),
            Location = new Point(0, 0),
            Size = new Size(760, 52),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        form.Controls.Add(banner);
        form.Controls.Add(Ui.Hint($"{test.Name}    -    {test.Url}", 20, 64, 710));
        form.Controls.Add(Ui.Hint($"{result.Message}    ({result.Seconds}s)", 20, 88, 710));

        var grid = Ui.NewGrid(20, 128, 716, 260);
        grid.ReadOnly = true;
        grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Result", HeaderText = "Result", Width = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Check", HeaderText = "Check", Width = 320 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Actual",
            HeaderText = "Actual",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        foreach (var assertion in result.Assertions)
        {
            var index = grid.Rows.Add(assertion.Passed ? "pass" : "FAIL", assertion.Description, assertion.Actual);
            grid.Rows[index].Cells["Result"].Style.ForeColor = assertion.Passed ? Ui.Pass : Ui.Fail;
            grid.Rows[index].Cells["Result"].Style.Font = Ui.Bold;
        }

        var shot = Ui.Secondary("Open screenshot", 20, 404, 130);
        var code = Ui.Secondary("Open C# file", 148, 404, 120);
        var close = Ui.Primary("Close", 626, 404, 110);

        shot.Anchor = code.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        shot.Enabled = result.ScreenshotPath.Length > 0;
        code.Enabled = test.CodeFile.Length > 0;

        shot.Click += (_, _) => Ui.Reveal(result.ScreenshotPath);
        code.Click += (_, _) => Ui.Reveal(test.CodeFile);
        close.Click += (_, _) => form.DialogResult = DialogResult.OK;

        form.Controls.AddRange(new Control[] { grid, shot, code, close });
        form.AcceptButton = close;
        form.CancelButton = close;

        form.ShowDialog(owner);
    }

    /// <summary>
    /// The "all test cases" popup: every scenario in the project with how it
    /// last did, and the buttons to run them again.
    /// </summary>
    public static void ShowAll(IWin32Window owner, ProjectStore store)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - {store.Name} - all test cases", 1000, 600, resizable: true);

        form.Controls.Add(Ui.Title("All test cases"));

        var summary = Ui.Hint("", 22, 46, 950);

        var grid = Ui.NewGrid(20, 76, 956, 442);
        grid.ReadOnly = true;
        grid.EditMode = DataGridViewEditMode.EditProgrammatically;
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Status", Width = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "Test case", Width = 230 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Url",
            HeaderText = "Link",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Steps", HeaderText = "Fields / clicks / checks", Width = 150 });
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

                var index = grid.Rows.Add(
                    status,
                    test.Name,
                    test.Url,
                    $"{test.Fields.Count} / {test.Clicks.Count} / {test.Assertions.Count}",
                    lastRun);

                grid.Rows[index].Tag = test;
                grid.Rows[index].Cells["Status"].Style.Font = Ui.Bold;
                grid.Rows[index].Cells["Status"].Style.ForeColor = status switch
                {
                    "Passed" => Ui.Pass,
                    "Failed" or "Error" => Ui.Fail,
                    _ => Ui.Muted
                };
            }

            var passed = tests.Count(t => t.LastResult?.IsPass == true);
            var failed = tests.Count(t => t.LastResult is not null && !t.LastResult.IsPass);
            var never = tests.Count - passed - failed;

            summary.Text = $"{tests.Count} test case(s) across {tests.Select(t => t.Url).Distinct().Count()} link(s)" +
                           $"    -    {passed} passed, {failed} failed, {never} never run";
        }

        Reload();

        TestCase? Selected() => grid.CurrentRow?.Tag as TestCase;

        var run = Ui.Primary("Run selected", 20, 534, 120);
        var runAll = Ui.Primary("Run all", 148, 534, 100);
        var details = Ui.Secondary("Last result", 256, 534, 110);
        var edit = Ui.Secondary("Edit data", 374, 534, 100);
        var openCode = Ui.Secondary("Open C# file", 482, 534, 110);
        var delete = Ui.Secondary("Delete", 600, 534, 90);
        var close = Ui.Secondary("Close", 876, 534, 100);

        foreach (var control in new Control[] { run, runAll, details, edit, openCode, delete })
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

            var result = RunAndSave(form, store, test);
            Reload();
            ShowResult(form, test, result);
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

            if (test.LastResult is null)
            {
                Ui.Info("This test case has not been run yet.");
                return;
            }

            ShowResult(form, test, test.LastResult);
        };

        edit.Click += (_, _) =>
        {
            if (Selected() is not { } test)
            {
                return;
            }

            if (test.Elements.Count == 0)
            {
                Ui.Warn("This test case has no saved scan, so there is nothing to edit.\r\n" +
                        "Scan the link again to rebuild it.");
                return;
            }

            var data = ScanDialogs.AskData(form, test.Url, test.Elements, test);

            if (data is null)
            {
                return;
            }

            var choice = AskAssertions(form, test.Url, test.Elements, test);

            if (choice is null)
            {
                return;
            }

            test.Name = choice.Name;
            test.Fields = data.Value.Fields;
            test.Clicks = data.Value.Clicks;
            test.Assertions = choice.Assertions;

            store.Save(test);
            RegenerateCode(store, test);
            Reload();

            if (choice.RunNow)
            {
                var result = RunAndSave(form, store, test);
                Reload();
                ShowResult(form, test, result);
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
        form.Controls.AddRange(new Control[] { grid, run, runAll, details, edit, openCode, delete, close });
        form.CancelButton = close;

        form.ShowDialog(owner);
    }
}
