using System.Drawing;
using System.Windows.Forms;

namespace PlaywrightTestStudio;

/// <summary>
/// The two popups that start the flow: which project am I working on, and
/// which link am I testing.
/// </summary>
internal static class ProjectDialogs
{
    /// <summary>
    /// First thing the app shows. Returns the chosen project name, or null if
    /// the operator quits.
    /// </summary>
    public static string? AskProject()
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Choose project", 520, 400);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.ShowInTaskbar = true;

        form.Controls.Add(Ui.Title("Which project do you want to work on?"));
        form.Controls.Add(Ui.Hint(
            "A project keeps its own links, test cases and results. " +
            "Pick one to continue, or create a new one.", 22, 46, 470));

        var list = new ListBox
        {
            Font = Ui.Input,
            Location = new Point(20, 92),
            Size = new Size(478, 200),
            IntegralHeight = false
        };

        foreach (var name in Storage.ListProjects())
        {
            list.Items.Add(name);
        }

        var empty = Ui.Hint("No projects yet - create your first one.", 30, 100);
        empty.Visible = list.Items.Count == 0;

        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        string? chosen = null;

        var open = Ui.Primary("Open", 20, 308, 130);
        var create = Ui.Secondary("New project...", 160, 308, 130);
        var remove = Ui.Secondary("Delete", 300, 308, 90);
        var quit = Ui.Secondary("Quit", 400, 308, 98);

        open.Click += (_, _) =>
        {
            if (list.SelectedItem is string name)
            {
                chosen = name;
                form.DialogResult = DialogResult.OK;
            }
            else
            {
                Ui.Warn("Select a project first, or create a new one.");
            }
        };

        list.DoubleClick += (_, _) => open.PerformClick();

        create.Click += (_, _) =>
        {
            var name = AskText("New project", "Name this project", "What are you testing? For example: Portal UAT");

            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (Storage.ListProjects().Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
            {
                Ui.Warn($"A project called \"{name}\" already exists.");
                return;
            }

            Storage.OpenProject(name);
            chosen = name;
            form.DialogResult = DialogResult.OK;
        };

        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not string name)
            {
                return;
            }

            if (!Ui.Confirm($"Delete project \"{name}\" and every test case and result in it?\r\n\r\n" +
                            "The generated C# files are left on disk."))
            {
                return;
            }

            Storage.DeleteProject(name);
            list.Items.Remove(name);
            empty.Visible = list.Items.Count == 0;

            if (list.Items.Count > 0)
            {
                list.SelectedIndex = 0;
            }
        };

        quit.Click += (_, _) => form.DialogResult = DialogResult.Cancel;

        form.Controls.Add(list);
        form.Controls.Add(empty);
        empty.BringToFront();
        form.Controls.AddRange(new Control[] { open, create, remove, quit });
        form.AcceptButton = open;
        form.CancelButton = quit;

        return form.ShowDialog() == DialogResult.OK ? chosen : null;
    }

    /// <summary>
    /// Asks for the link to scan. Also offers a sign-in trip, because the
    /// scanner sees whatever the shared browser profile is allowed to see.
    /// </summary>
    public static string? AskLink(IWin32Window owner, string? suggested = null)
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - Link", 560, 276);

        form.Controls.Add(Ui.Title("Which page do you want to test?"));
        form.Controls.Add(Ui.Hint(
            "Paste the full link. The tool opens it and lists every dropdown, " +
            "text field, radio button and button it finds.", 22, 46, 500));

        var input = new TextBox
        {
            Font = Ui.Input,
            Location = new Point(20, 96),
            Size = new Size(516, 30),
            Text = suggested ?? "https://"
        };

        var loginHint = Ui.Hint(
            "Behind a login? Sign in once - the browser profile is shared, so the " +
            "scan and every later run stay signed in.", 22, 136, 500);

        var login = Ui.Secondary("Sign in first...", 20, 186, 130);
        var scan = Ui.Primary("Scan page", 296, 186, 120);
        var cancel = Ui.Secondary("Cancel", 424, 186, 112);

        login.Click += (_, _) =>
        {
            var url = Normalise(input.Text);

            if (url is null)
            {
                Ui.Warn("Enter the link first, then sign in.");
                return;
            }

            try
            {
                Ui.RunWithProgress<bool>(form, "Sign in",
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

        scan.Click += (_, _) =>
        {
            if (Normalise(input.Text) is null)
            {
                Ui.Warn("That does not look like a link. It should start with http:// or https://");
                return;
            }

            form.DialogResult = DialogResult.OK;
        };

        cancel.Click += (_, _) => form.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[] { input, loginHint, login, scan, cancel });
        form.AcceptButton = scan;
        form.CancelButton = cancel;

        input.Select(input.Text.Length, 0);

        return form.ShowDialog(owner) == DialogResult.OK ? Normalise(input.Text) : null;
    }

    /// <summary>Returns the link in absolute form, or null if it is not usable.</summary>
    public static string? Normalise(string text)
    {
        var raw = text.Trim();

        if (raw.Length == 0 || raw == "https://" || raw == "http://")
        {
            return null;
        }

        if (!raw.Contains("://"))
        {
            raw = "https://" + raw;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? raw
            : null;
    }

    /// <summary>A one-line prompt, used for project and test case names.</summary>
    public static string? AskText(string title, string heading, string hint, string initial = "")
    {
        using var form = Ui.NewForm($"{Ui.AppTitle} - {title}", 460, 200);

        form.Controls.Add(Ui.Title(heading));
        form.Controls.Add(Ui.Hint(hint, 22, 46, 410));

        var input = new TextBox
        {
            Font = Ui.Input,
            Location = new Point(20, 88),
            Size = new Size(416, 30),
            Text = initial
        };

        var ok = Ui.Primary("OK", 216, 136, 104);
        var cancel = Ui.Secondary("Cancel", 332, 136, 104);

        ok.Click += (_, _) =>
        {
            if (input.Text.Trim().Length == 0)
            {
                Ui.Warn("Type a name first.");
                return;
            }

            form.DialogResult = DialogResult.OK;
        };

        cancel.Click += (_, _) => form.DialogResult = DialogResult.Cancel;

        form.Controls.AddRange(new Control[] { input, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        input.Select(input.Text.Length, 0);

        return form.ShowDialog() == DialogResult.OK ? input.Text.Trim() : null;
    }
}
