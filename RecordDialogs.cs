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
    ///
    /// While the recording runs, this application shows nothing at all - the
    /// browser is the only window on screen. Esc in the page, or closing the
    /// browser, ends it.
    /// </summary>
    public static async Task<TestCase?> RecordAsync(Form owner, ProjectStore store)
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

        TestCase test;

        // Out of the way entirely: no window of ours over the page being
        // recorded, and no risk of a stray click landing on us.
        owner.Hide();

        try
        {
            await session.StopRequested;

            test = await session.StopAsync($"Recorded {DateTime.Now:yyyy-MM-dd HH:mm}");
        }
        catch (Exception ex)
        {
            owner.Show();
            Ui.Error(BrowserSession.Explain(ex));
            return null;
        }
        finally
        {
            owner.Show();
            owner.Activate();
        }

        if (test.Steps.Count == 0)
        {
            Ui.Warn("Nothing was recorded.\r\n\r\n" +
                    "Actions are captured as you type, choose and click. If the page is inside an iframe, " +
                    "it is not recorded.");
            return null;
        }

        var name = ProjectDialogs.AskText("Name", "Name this test case",
            $"{test.Steps.Count} action(s) were captured. " +
            "The name becomes the method in the generated C# file and the sheet name in Excel.",
            SuggestName(store));

        if (name is null)
        {
            return null;
        }

        test.Name = name;

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
            TestCaseDialogs.RunAndShow(owner, store, test);
        }
        else
        {
            Ui.Info($"Saved \"{test.Name}\" with {test.Steps.Count} step(s).\r\n\r\n" +
                    $"C# file:\r\n{test.CodeFile}\r\n\r\n" +
                    "Use \"Download test data\" to add more test data rows.");
        }

        return test;
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

    /// <summary>A starting name built from the project, not from the link the case was recorded on.</summary>
    public static string SuggestName(ProjectStore store)
    {
        var taken = store.LoadAll().Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = taken.Count + 1;

        while (taken.Contains($"{store.Name} case {n}"))
        {
            n++;
        }

        return $"{store.Name} case {n}";
    }
}
