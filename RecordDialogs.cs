using System.Drawing;
using System.Windows.Forms;

namespace PlaywrightTestStudio;

/// <summary>
/// The recording flow: open the page, work through it, stop, review what was
/// captured. No selectors are typed by hand at any point.
/// </summary>
internal static class RecordDialogs
{
    /// <summary>
    /// Records a new test case into the project. Returns the saved case, or null
    /// if the operator backed out.
    /// </summary>
    public static TestCase? Record(IWin32Window owner, ProjectStore store)
    {
        var url = ProjectDialogs.AskLink(owner, purpose: "record");

        if (url is null)
        {
            return null;
        }

        RecordingSession session;

        try
        {
            session = Ui.RunWithProgress(owner, "Opening browser", $"Loading {url}",
                async _ => await RecordingSession.StartAsync(url, store.VideosDir));
        }
        catch (Exception ex)
        {
            Ui.Error(BrowserSession.Explain(ex), $"{Ui.AppTitle} - could not start recording");
            return null;
        }

        var kept = ShowRecordingWindow(owner, session);

        if (!kept)
        {
            Ui.RunWithProgress<bool>(owner, "Discarding", "Closing the browser...",
                async _ => { await session.DisposeAsync(); return true; });

            return null;
        }

        var name = ProjectDialogs.AskText("Name", "Name this test case",
            "It becomes the method name in the generated C# file and the sheet name in Excel.",
            SuggestName(store, url));

        if (name is null)
        {
            name = $"Recorded {DateTime.Now:yyyy-MM-dd HH:mm}";
        }

        TestCase test;

        try
        {
            test = Ui.RunWithProgress(owner, "Saving recording", "Closing the browser...",
                async _ => await session.StopAsync(name));
        }
        catch (Exception ex)
        {
            Ui.Error(BrowserSession.Explain(ex));
            return null;
        }

        if (test.Steps.Count == 0)
        {
            Ui.Warn("Nothing was recorded.\r\n\r\n" +
                    "Actions are captured as you type, choose and click. If the page is inside an iframe, " +
                    "it is not recorded.");
            return null;
        }

        if (!EditSteps(owner, test, "Here is what was recorded"))
        {
            return null;
        }

        var choice = TestCaseDialogs.AskAssertions(owner, test, suggestedName: test.Name);

        if (choice is null)
        {
            return null;
        }

        test.Name = choice.Name;
        test.Assertions = choice.Assertions;

        // Assertions can introduce parameters of their own, and the recorded
        // data row has to carry a column for each one.
        RecordingSession.AssignParameters(test);
        SyncRecordedRow(test);

        store.Save(test);
        TestCaseDialogs.RegenerateCode(store, test);

        if (choice.RunNow)
        {
            var results = TestCaseDialogs.RunAndSave(owner, store, test);
            TestCaseDialogs.ShowResult(owner, test, results);
        }
        else
        {
            Ui.Info($"Saved \"{test.Name}\" with {test.Steps.Count} step(s).\r\n\r\n" +
                    $"C# file:\r\n{test.CodeFile}\r\n\r\n" +
                    "Use \"Download Excel\" to add more test data rows.");
        }

        return test;
    }

    /// <summary>
    /// The small window that sits beside the browser while recording. It polls
    /// rather than marshalling every event onto the UI thread - a recording is
    /// a handful of actions a second at most.
    /// </summary>
    private static bool ShowRecordingWindow(IWin32Window owner, RecordingSession session)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Recording", 460, 280);
        form.TopMost = true;
        form.ControlBox = false;

        var dot = new Label
        {
            Text = "REC",
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Color.White,
            BackColor = Ui.Fail,
            TextAlign = ContentAlignment.MiddleCenter,
            Location = new Point(20, 18),
            Size = new Size(56, 26)
        };

        form.Controls.Add(dot);
        form.Controls.Add(Ui.Title("Recording your session", 88, 18));

        form.Controls.Add(Ui.Hint(
            "Work through the page in the browser window. Everything you type, choose, tick and click " +
            "is captured in order.", 22, 56, 410));

        form.Controls.Add(Ui.Hint(
            "Hold Alt and click anything to record a check on it instead of a click - that is how you say " +
            "\"this is what proves the test passed\".", 22, 100, 410));

        var count = new Label
        {
            Text = "0 actions captured",
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            ForeColor = Ui.Accent,
            AutoSize = true,
            Location = new Point(22, 158)
        };

        var latest = new Label
        {
            Text = "",
            Font = Ui.Body,
            ForeColor = Ui.Muted,
            AutoEllipsis = true,
            Location = new Point(22, 182),
            Size = new Size(410, 18)
        };

        var stop = Ui.Primary("Stop and save", 20, 214, 150);
        var discard = Ui.Secondary("Discard", 178, 214, 110);

        var kept = false;

        stop.Click += (_, _) =>
        {
            kept = true;
            form.DialogResult = DialogResult.OK;
        };

        discard.Click += (_, _) =>
        {
            if (session.Count == 0 || Ui.Confirm($"Throw away the {session.Count} action(s) recorded so far?"))
            {
                form.DialogResult = DialogResult.Cancel;
            }
        };

        var timer = new System.Windows.Forms.Timer { Interval = 400 };

        timer.Tick += (_, _) =>
        {
            var steps = session.Steps;
            var assertions = session.Assertions.Count;

            count.Text = $"{steps.Count} action(s) captured" +
                         (assertions > 0 ? $", {assertions} check(s)" : "");

            latest.Text = steps.Count > 0
                ? "Last: " + Runner.Describe(steps[^1], steps[^1].Value)
                : "";

            // Closing the browser is a natural way to finish.
            if (session.BrowserClosed)
            {
                timer.Stop();
                kept = session.Count > 0;
                form.DialogResult = kept ? DialogResult.OK : DialogResult.Cancel;
            }
        };

        form.Shown += (_, _) => timer.Start();
        form.FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); };

        form.Controls.AddRange(new Control[] { count, latest, stop, discard });

        form.ShowDialog(owner);

        return kept;
    }

    /// <summary>
    /// Review and adjust the steps: untick anything stray the recorder picked
    /// up, rename a data column, or correct a value.
    /// </summary>
    public static bool EditSteps(IWin32Window owner, TestCase test, string heading)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Steps", 980, 620, resizable: true);

        form.Controls.Add(Ui.Title(heading));
        form.Controls.Add(Ui.Hint(
            "Untick anything you do not want replayed. \"Data column\" is the Excel heading that supplies the " +
            "value - keep it to vary that value per row, clear it to always use what was recorded.",
            22, 46, 930));

        var grid = Ui.NewGrid(20, 92, 936, 428);

        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "Use", Width = 44 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "No", HeaderText = "#", Width = 36, ReadOnly = true });

        var actionColumn = new DataGridViewComboBoxColumn
        {
            Name = "Action",
            HeaderText = "Action",
            Width = 80,
            FlatStyle = FlatStyle.Flat
        };

        actionColumn.Items.AddRange(StepActions.All);
        grid.Columns.Add(actionColumn);

        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Label", HeaderText = "Field / button", Width = 190, ReadOnly = true });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Recorded value", Width = 170 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Parameter", HeaderText = "Data column", Width = 130 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Selector",
            HeaderText = "Target",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        grid.DataError += (_, e) => e.ThrowException = false;

        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        void Fill()
        {
            grid.Rows.Clear();

            for (var i = 0; i < test.Steps.Count; i++)
            {
                var step = test.Steps[i];

                var index = grid.Rows.Add(step.Use, i + 1, step.Action, step.Label,
                    step.Value, step.Parameter, step.Selector);

                grid.Rows[index].Cells["Selector"].Style.Font = Ui.Mono;
                grid.Rows[index].Cells["Selector"].Style.ForeColor = Ui.Muted;

                if (!step.TakesValue)
                {
                    foreach (var name in new[] { "Value", "Parameter" })
                    {
                        grid.Rows[index].Cells[name].ReadOnly = true;
                        grid.Rows[index].Cells[name].Style.BackColor = SystemColors.Control;
                    }
                }
            }
        }

        Fill();

        var up = Ui.Secondary("Move up", 20, 538, 92);
        var down = Ui.Secondary("Move down", 118, 538, 100);
        var remove = Ui.Secondary("Delete step", 224, 538, 100);
        var next = Ui.Primary("Next: assertion", 690, 538, 150);
        var cancel = Ui.Secondary("Cancel", 848, 538, 108);

        foreach (var control in new Control[] { up, down, remove })
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        }

        next.Anchor = cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;

        void Commit()
        {
            grid.EndEdit();

            for (var i = 0; i < test.Steps.Count && i < grid.Rows.Count; i++)
            {
                var row = grid.Rows[i];
                var step = test.Steps[i];

                step.Use = row.Cells["Use"].Value is bool b && b;
                step.Action = row.Cells["Action"].Value?.ToString() ?? step.Action;
                step.Value = row.Cells["Value"].Value?.ToString() ?? "";
                step.Parameter = (row.Cells["Parameter"].Value?.ToString() ?? "").Trim();
                step.Selector = row.Cells["Selector"].Value?.ToString() ?? step.Selector;
            }
        }

        void Move(int delta)
        {
            Commit();

            if (grid.CurrentRow is null)
            {
                return;
            }

            var from = grid.CurrentRow.Index;
            var to = from + delta;

            if (to < 0 || to >= test.Steps.Count)
            {
                return;
            }

            (test.Steps[from], test.Steps[to]) = (test.Steps[to], test.Steps[from]);
            Fill();

            grid.CurrentCell = grid.Rows[to].Cells["Label"];
        }

        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(+1);

        remove.Click += (_, _) =>
        {
            Commit();

            if (grid.CurrentRow is null || test.Steps.Count == 0)
            {
                return;
            }

            test.Steps.RemoveAt(grid.CurrentRow.Index);
            Fill();
        };

        next.Click += (_, _) =>
        {
            Commit();

            if (test.Steps.All(s => !s.Use) &&
                !Ui.Confirm("No step is ticked, so the test will only open the page and check it.\r\n\r\nCarry on?"))
            {
                return;
            }

            form.DialogResult = DialogResult.OK;
        };

        cancel.Click += (_, _) => form.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[] { grid, up, down, remove, next, cancel });
        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK;
    }

    /// <summary>
    /// Keeps the "Recorded" data row in step with the parameters the case now
    /// declares, so the downloaded sheet always has a filled-in example row.
    /// </summary>
    public static void SyncRecordedRow(TestCase test)
    {
        if (test.DataSets.Count == 0)
        {
            test.DataSets.Add(new DataSet { Name = "Recorded" });
        }

        var first = test.DataSets[0];

        foreach (var step in test.Steps.Where(s => s.Parameter.Length > 0))
        {
            if (!first.Values.ContainsKey(step.Parameter))
            {
                first.Values[step.Parameter] = step.Value;
            }
        }

        foreach (var assertion in test.Assertions.Where(a => a.Parameter.Length > 0))
        {
            if (!first.Values.ContainsKey(assertion.Parameter))
            {
                first.Values[assertion.Parameter] = assertion.Expected;
            }
        }
    }

    private static string SuggestName(ProjectStore store, string url)
    {
        var existing = store.LoadAll().Count(t => t.Url == url);
        var page = CodeGen.ClassNameFor(url).Replace("Tests", "");

        return $"{page} case {existing + 1}";
    }
}
