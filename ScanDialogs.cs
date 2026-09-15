using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace PlaywrightTestStudio;

/// <summary>
/// What the operator sees straight after a scan: first everything the page
/// contains, then a form to fill that same list in with test data.
/// </summary>
internal static class ScanDialogs
{
    /// <summary>
    /// Lists every control found on the link. Returns true to carry on to the
    /// data step.
    /// </summary>
    public static bool ShowElements(IWin32Window owner, string url, IReadOnlyList<ElementInfo> elements)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Elements found", 940, 600, resizable: true);

        var counts = new[]
        {
            $"{elements.Count(e => e.Kind == "select")} dropdown(s)",
            $"{elements.Count(e => e.Kind == "text")} text field(s)",
            $"{elements.Count(e => e.Kind == "search")} search field(s)",
            $"{elements.Count(e => e.Kind == "radio")} radio button(s)",
            $"{elements.Count(e => e.Kind == "checkbox")} checkbox(es)",
            $"{elements.Count(e => e.Kind == "button")} button(s)"
        };

        form.Controls.Add(Ui.Title("Everything on this page"));
        form.Controls.Add(Ui.Hint(url, 22, 46, 890));
        form.Controls.Add(Ui.Hint(string.Join("    -    ", counts), 22, 68, 890));

        var grid = Ui.NewGrid(20, 96, 896, 428);
        grid.ReadOnly = true;
        grid.EditMode = DataGridViewEditMode.EditProgrammatically;

        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type", Width = 80 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "Field / label", Width = 250 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Details", HeaderText = "Options / value", Width = 230 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Selector",
            HeaderText = "Selector",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Visible", HeaderText = "Visible", Width = 60 });

        foreach (var element in elements)
        {
            var details = element.Kind switch
            {
                "select" => string.Join(" | ", element.Options.Take(6)) +
                            (element.Options.Count > 6 ? $" ... (+{element.Options.Count - 6})" : ""),
                "search" => element.ResultsSelector.Length > 0
                    ? $"type, wait, then pick from {element.ResultsSelector}"
                    : "type, wait for the search, then pick a result",
                "radio" => $"group \"{element.Name}\", value \"{element.Value}\"",
                "checkbox" => $"value \"{element.Value}\"",
                _ => element.InputType
            };

            var index = grid.Rows.Add(KindLabel(element.Kind), element.Display, details,
                element.Selector, element.Visible ? "yes" : "no");

            grid.Rows[index].Cells["Selector"].Style.Font = Ui.Mono;

            if (!element.Visible)
            {
                grid.Rows[index].DefaultCellStyle.ForeColor = Ui.Muted;
            }
        }

        var next = Ui.Primary("Next: enter data", 668, 540, 140);
        var copy = Ui.Secondary("Copy selectors", 20, 540, 130);
        var cancel = Ui.Secondary("Cancel", 816, 540, 100);

        foreach (var button in new[] { next, copy, cancel })
        {
            button.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        }

        copy.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;

        copy.Click += (_, _) =>
        {
            var sb = new StringBuilder();

            foreach (var element in elements)
            {
                sb.AppendLine($"{KindLabel(element.Kind),-10} {element.Selector,-50} {element.Display}");
            }

            Clipboard.SetText(sb.ToString());
            Ui.Info($"{elements.Count} selector(s) copied to the clipboard.");
        };

        next.Click += (_, _) => form.DialogResult = DialogResult.OK;
        cancel.Click += (_, _) => form.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[] { grid, next, copy, cancel });
        form.AcceptButton = next;
        form.CancelButton = cancel;

        if (elements.Count == 0)
        {
            form.Shown += (_, _) => Ui.Warn(
                "No interactive controls were found.\r\n\r\n" +
                "If the page needs a login, sign in from the link popup first. " +
                "Controls inside an iframe are not scanned.");
        }

        return form.ShowDialog(owner) == DialogResult.OK;
    }

    /// <summary>
    /// Asks for the data to enter. The fields are exactly the ones the scan
    /// found, with dropdowns and radio groups reduced to a pick list.
    /// </summary>
    public static (List<FieldValue> Fields, List<ClickStep> Clicks)? AskData(
        IWin32Window owner,
        string url,
        IReadOnlyList<ElementInfo> elements,
        TestCase? existing = null)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Test data", 940, 660, resizable: true);

        form.Controls.Add(Ui.Title("Enter the data for this test"));
        form.Controls.Add(Ui.Hint(
            "Tick the fields to fill, then set each value. A \"search\" field is typed into, waits for the page " +
            "to search, then picks the result you name - use the Type column to switch a field between text and " +
            "search. Below, tick the buttons to press, in order.",
            22, 46, 890));

        var fieldGrid = Ui.NewGrid(20, 84, 896, 300);
        fieldGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "Use", Width = 44 });
        fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Type", HeaderText = "Type", Width = 90 });
        fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Field", HeaderText = "Field", Width = 210, ReadOnly = true });
        fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Value / text to search for", Width = 180 });
        fieldGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Pick", HeaderText = "Then pick this result", Width = 160 });
        fieldGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Selector",
            HeaderText = "Selector",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        // A combo cell rejects a value that is not in its item list; swallowing
        // the complaint keeps a stale saved value from blocking the dialog.
        fieldGrid.DataError += (_, e) => e.ThrowException = false;

        // Commit a tick or a dropdown choice as soon as it is made, so the row
        // can react to it rather than waiting for focus to move away.
        fieldGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (fieldGrid.IsCurrentCellDirty)
            {
                fieldGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        fieldGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == fieldGrid.Columns["Type"]!.Index)
            {
                SyncSearchRow(fieldGrid.Rows[e.RowIndex]);
            }
        };

        BuildFieldRows(fieldGrid, elements, existing);

        var buttonsLabel = Ui.Hint("Buttons to press, in order", 20, 396);
        buttonsLabel.Font = Ui.Bold;
        buttonsLabel.ForeColor = SystemColors.ControlText;

        var clickGrid = Ui.NewGrid(20, 418, 896, 150);
        clickGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Use", HeaderText = "Use", Width = 44 });
        clickGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Button", HeaderText = "Button", Width = 300, ReadOnly = true });
        clickGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Wait", HeaderText = "Wait after", Width = 80 });
        clickGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Selector",
            HeaderText = "Selector",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });

        BuildClickRows(clickGrid, elements, existing);

        var up = Ui.Secondary("Move up", 20, 578, 92);
        var down = Ui.Secondary("Move down", 118, 578, 100);
        var all = Ui.Secondary("Tick all fields", 224, 578, 110);
        var none = Ui.Secondary("Untick all", 340, 578, 92);
        var next = Ui.Primary("Next: assertion", 668, 578, 140);
        var cancel = Ui.Secondary("Cancel", 816, 578, 100);

        foreach (var control in new Control[] { buttonsLabel, clickGrid, up, down, all, none })
        {
            control.Anchor = control == clickGrid
                ? AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
                : AnchorStyles.Left | AnchorStyles.Bottom;
        }

        fieldGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        next.Anchor = cancel.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;

        up.Click += (_, _) => MoveRow(clickGrid, -1);
        down.Click += (_, _) => MoveRow(clickGrid, +1);

        all.Click += (_, _) => SetAll(fieldGrid, true);
        none.Click += (_, _) => SetAll(fieldGrid, false);

        List<FieldValue>? fields = null;
        List<ClickStep>? clicks = null;

        next.Click += (_, _) =>
        {
            fieldGrid.EndEdit();
            clickGrid.EndEdit();

            fields = ReadFields(fieldGrid);
            clicks = ReadClicks(clickGrid);

            if (fields.Count == 0 && clicks.Count == 0 &&
                !Ui.Confirm("Nothing is ticked, so the test will only open the page and check it.\r\n\r\nCarry on?"))
            {
                return;
            }

            form.DialogResult = DialogResult.OK;
        };

        cancel.Click += (_, _) => form.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[]
        {
            fieldGrid, buttonsLabel, clickGrid, up, down, all, none, next, cancel
        });

        form.CancelButton = cancel;

        return form.ShowDialog(owner) == DialogResult.OK && fields is not null && clicks is not null
            ? (fields, clicks)
            : null;
    }

    /// <summary>
    /// One row per fillable control, with radio buttons that share a name
    /// folded into a single pick list.
    /// </summary>
    private static void BuildFieldRows(DataGridView grid, IReadOnlyList<ElementInfo> elements, TestCase? existing)
    {
        var handledGroups = new HashSet<string>(StringComparer.Ordinal);

        foreach (var element in elements)
        {
            if (element.Kind == "button")
            {
                continue;
            }

            if (element.Kind == "radio" && element.Name.Length > 0)
            {
                if (!handledGroups.Add(element.Name))
                {
                    continue;
                }

                var group = elements.Where(e => e.Kind == "radio" && e.Name == element.Name).ToList();
                AddRadioGroupRow(grid, element.Name, group, existing);
                continue;
            }

            AddFieldRow(grid, element, existing);
        }
    }

    private static void AddRadioGroupRow(DataGridView grid, string groupName,
        List<ElementInfo> group, TestCase? existing)
    {
        var label = group.FirstOrDefault(e => e.Label.Length > 0)?.Label ?? groupName;
        var saved = existing?.Fields.FirstOrDefault(f => f.Kind == "radio" && f.Group == groupName);

        var index = grid.Rows.Add(saved?.Use ?? false, "radio", $"{label}  ({groupName})", "", "", "");
        var row = grid.Rows[index];

        var options = group.Select(OptionLabel).ToList();
        var cell = NewComboCell(options, saved?.Value ?? options.FirstOrDefault() ?? "");

        row.Cells["Value"] = cell;
        row.Cells["Selector"].Value = saved?.Selector ?? group[0].Selector;
        row.Cells["Selector"].Style.Font = Ui.Mono;
        row.Cells["Type"].ReadOnly = true;

        row.Tag = new FieldRow
        {
            Kind = "radio",
            Label = label,
            Group = groupName,
            Selector = group[0].Selector,
            Options = group
        };
    }

    private static void AddFieldRow(DataGridView grid, ElementInfo element, TestCase? existing)
    {
        var saved = existing?.Fields.FirstOrDefault(f => f.Selector == element.Selector);

        // A saved case wins over the scan: the operator may already have
        // corrected a field the scanner read as plain text.
        var kind = saved?.Kind ?? element.Kind;

        var index = grid.Rows.Add(saved?.Use ?? element.Visible, KindLabel(kind),
            element.Display, "", saved?.Pick ?? "", element.Selector);

        var row = grid.Rows[index];
        row.Cells["Selector"].Style.Font = Ui.Mono;

        // Only a typed field is ambiguous, so only those get the choice; a
        // dropdown, radio or checkbox is what it is.
        if (kind is "text" or "search")
        {
            row.Cells["Type"] = NewComboCell(new[] { "text", "search" }, kind);
        }
        else
        {
            row.Cells["Type"].ReadOnly = true;
        }

        switch (kind)
        {
            case "select":
            {
                var options = element.Options.Count > 0 ? element.Options : new List<string> { "" };
                row.Cells["Value"] = NewComboCell(options, saved?.Value ?? options[0]);
                break;
            }

            case "checkbox":
            {
                var options = new List<string> { "true", "false" };
                row.Cells["Value"] = NewComboCell(options, saved?.Value ?? "true");
                break;
            }

            case "radio":
            {
                // A radio with no name is on its own; ticking Use checks it.
                var options = new List<string> { "check" };
                row.Cells["Value"] = NewComboCell(options, "check");
                break;
            }

            default:
                row.Cells["Value"].Value = saved?.Value ?? "";
                break;
        }

        if (element.Required)
        {
            row.Cells["Field"].Style.Font = Ui.Bold;
            row.Cells["Field"].ToolTipText = "Marked required by the page";
        }

        row.Tag = new FieldRow
        {
            Kind = kind,
            Label = element.Display,
            Selector = element.Selector,
            Group = element.Name,
            ResultsSelector = saved?.ResultsSelector is { Length: > 0 } saveds
                ? saveds
                : element.ResultsSelector
        };

        SyncSearchRow(row);
    }

    /// <summary>
    /// "Then pick" only means something for a search field, so it is greyed out
    /// and locked on every other row - including when the operator switches a
    /// row's type back to plain text.
    /// </summary>
    private static void SyncSearchRow(DataGridViewRow row)
    {
        var isSearch = (row.Cells["Type"].Value?.ToString() ?? "") == "search";
        var pick = row.Cells["Pick"];

        pick.ReadOnly = !isSearch;
        pick.Style.BackColor = isSearch ? Color.White : SystemColors.Control;
        pick.Style.ForeColor = isSearch ? SystemColors.ControlText : Ui.Muted;

        pick.ToolTipText = isSearch
            ? "The result to click once the page has searched. Leave empty to take the first result."
            : "Only used by search fields.";

        row.Cells["Value"].ToolTipText = isSearch
            ? "Typed into the field one key at a time to start the search."
            : "";
    }

    private static void BuildClickRows(DataGridView grid, IReadOnlyList<ElementInfo> elements, TestCase? existing)
    {
        var buttons = elements.Where(e => e.Kind == "button").ToList();

        // Keep a previously saved order at the top, so reopening a test case
        // shows the click sequence it actually runs.
        if (existing is not null)
        {
            foreach (var click in existing.Clicks)
            {
                var match = buttons.FirstOrDefault(b => b.Selector == click.Selector);

                if (match is not null)
                {
                    buttons.Remove(match);
                    AddClickRow(grid, match, use: true, wait: click.WaitAfter);
                }
            }
        }

        foreach (var button in buttons)
        {
            AddClickRow(grid, button, use: false, wait: true);
        }
    }

    private static void AddClickRow(DataGridView grid, ElementInfo button, bool use, bool wait)
    {
        var label = string.IsNullOrWhiteSpace(button.Label) ? button.Selector : button.Label;
        var index = grid.Rows.Add(use, label, wait, button.Selector);

        grid.Rows[index].Cells["Selector"].Style.Font = Ui.Mono;
        grid.Rows[index].Tag = button;
    }

    private static DataGridViewComboBoxCell NewComboCell(IEnumerable<string> options, string selected)
    {
        var cell = new DataGridViewComboBoxCell
        {
            FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            MaxDropDownItems = 20
        };

        foreach (var option in options.Distinct())
        {
            cell.Items.Add(option);
        }

        if (cell.Items.Contains(selected))
        {
            cell.Value = selected;
        }
        else if (cell.Items.Count > 0)
        {
            cell.Value = cell.Items[0];
        }

        return cell;
    }

    private static List<FieldValue> ReadFields(DataGridView grid)
    {
        var list = new List<FieldValue>();

        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is not FieldRow context || !IsTicked(row.Cells["Use"]))
            {
                continue;
            }

            var value = row.Cells["Value"].Value?.ToString() ?? "";
            var selector = context.Selector;

            // Only a typed field carries a choice of kind; everything else is
            // fixed, so fall back to what the scan decided.
            var kind = context.Kind is "text" or "search"
                ? row.Cells["Type"].Value?.ToString() ?? context.Kind
                : context.Kind;

            // For a radio group the picked option decides which input is checked.
            if (kind == "radio" && context.Options is not null)
            {
                var picked = context.Options.FirstOrDefault(o => OptionLabel(o) == value) ?? context.Options[0];
                selector = picked.Selector;
            }

            list.Add(new FieldValue
            {
                Use = true,
                Kind = kind,
                Label = context.Label,
                Selector = selector,
                Value = value,
                Pick = kind == "search" ? row.Cells["Pick"].Value?.ToString()?.Trim() ?? "" : "",
                ResultsSelector = kind == "search" ? context.ResultsSelector : "",
                Group = context.Group
            });
        }

        return list;
    }

    private static List<ClickStep> ReadClicks(DataGridView grid)
    {
        var list = new List<ClickStep>();

        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Tag is not ElementInfo button || !IsTicked(row.Cells["Use"]))
            {
                continue;
            }

            list.Add(new ClickStep
            {
                Label = row.Cells["Button"].Value?.ToString() ?? button.Display,
                Selector = button.Selector,
                WaitAfter = IsTicked(row.Cells["Wait"])
            });
        }

        return list;
    }

    private static bool IsTicked(DataGridViewCell cell) =>
        cell.Value is bool b && b;

    private static void SetAll(DataGridView grid, bool ticked)
    {
        grid.EndEdit();

        foreach (DataGridViewRow row in grid.Rows)
        {
            row.Cells["Use"].Value = ticked;
        }
    }

    private static void MoveRow(DataGridView grid, int delta)
    {
        grid.EndEdit();

        if (grid.CurrentRow is null)
        {
            return;
        }

        var from = grid.CurrentRow.Index;
        var to = from + delta;

        if (to < 0 || to >= grid.Rows.Count)
        {
            return;
        }

        var row = grid.Rows[from];
        grid.Rows.RemoveAt(from);
        grid.Rows.Insert(to, row);
        grid.CurrentCell = grid.Rows[to].Cells["Button"];
    }

    /// <summary>The text shown for one radio option.</summary>
    private static string OptionLabel(ElementInfo radio)
    {
        if (!string.IsNullOrWhiteSpace(radio.Label))
        {
            return radio.Label;
        }

        return string.IsNullOrWhiteSpace(radio.Value) ? radio.Selector : radio.Value;
    }

    public static string KindLabel(string kind) => kind switch
    {
        "select" => "dropdown",
        "text" => "text",
        "search" => "search",
        "radio" => "radio",
        "checkbox" => "checkbox",
        "button" => "button",
        _ => kind
    };

    /// <summary>What a grid row in the data dialog is standing for.</summary>
    private sealed class FieldRow
    {
        public string Kind { get; init; } = "text";
        public string Label { get; init; } = "";
        public string Selector { get; init; } = "";
        public string Group { get; init; } = "";

        /// <summary>Where this field's search results appear, when the page says so.</summary>
        public string ResultsSelector { get; init; } = "";

        /// <summary>Set only for a radio group: the inputs the pick list maps to.</summary>
        public List<ElementInfo>? Options { get; init; }
    }
}
