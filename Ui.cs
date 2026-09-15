using System.Drawing;
using System.Windows.Forms;

namespace PlaywrightTestStudio;

/// <summary>
/// Shared look and feel for every popup, so the flow reads as one tool rather
/// than a pile of dialogs.
/// </summary>
internal static class Ui
{
    public const string AppTitle = "Playwright Test Studio";

    public static readonly Font Body = new("Segoe UI", 9F);
    public static readonly Font Bold = new("Segoe UI", 9F, FontStyle.Bold);
    public static readonly Font Heading = new("Segoe UI", 13F, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9F);
    public static readonly Font Input = new("Segoe UI", 11F);

    public static readonly Color Accent = Color.FromArgb(0, 90, 158);
    public static readonly Color Pass = Color.FromArgb(16, 124, 16);
    public static readonly Color Fail = Color.FromArgb(196, 43, 28);
    public static readonly Color Muted = Color.FromArgb(96, 96, 96);

    public static Form NewForm(string title, int width, int height, bool resizable = false)
    {
        return new Form
        {
            Text = title,
            Font = Body,
            ClientSize = new Size(width, height),
            FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = resizable,
            ShowInTaskbar = false,
            BackColor = Color.White
        };
    }

    public static Label Title(string text, int x = 20, int y = 16) => new()
    {
        Text = text,
        Font = Heading,
        ForeColor = Accent,
        AutoSize = true,
        Location = new Point(x, y)
    };

    /// <summary>
    /// Grey helper text. A width makes it wrap at that width; the height still
    /// comes from AutoSize, so a long hint grows downwards instead of clipping.
    /// </summary>
    public static Label Hint(string text, int x, int y, int width = 0) => new()
    {
        Text = text,
        Font = Body,
        ForeColor = Muted,
        AutoSize = true,
        MaximumSize = width == 0 ? Size.Empty : new Size(width, 0),
        Location = new Point(x, y)
    };

    public static Button Primary(string text, int x, int y, int width = 120) => new()
    {
        Text = text,
        Font = Bold,
        Location = new Point(x, y),
        Size = new Size(width, 32),
        BackColor = Accent,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false
    };

    public static Button Secondary(string text, int x, int y, int width = 120) => new()
    {
        Text = text,
        Font = Body,
        Location = new Point(x, y),
        Size = new Size(width, 32),
        FlatStyle = FlatStyle.System
    };

    /// <summary>A grid set up the way every list in this tool wants it.</summary>
    public static DataGridView NewGrid(int x, int y, int width, int height) => new()
    {
        Location = new Point(x, y),
        Size = new Size(width, height),
        Font = Body,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle,
        EditMode = DataGridViewEditMode.EditOnEnter,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        ColumnHeadersHeight = 28,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
    };

    public static void Info(string message, string? caption = null) =>
        MessageBox.Show(message, caption ?? AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);

    public static void Warn(string message, string? caption = null) =>
        MessageBox.Show(message, caption ?? AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public static void Error(string message, string? caption = null) =>
        MessageBox.Show(message, caption ?? AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static bool Confirm(string message, string? caption = null) =>
        MessageBox.Show(message, caption ?? AppTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question)
            == DialogResult.Yes;

    /// <summary>Opens a folder or file in Explorer, ignoring the usual "not there" cases.</summary>
    public static void Reveal(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            }
            else
            {
                Warn($"Not on disk yet:\r\n{path}");
            }
        }
        catch (Exception ex)
        {
            Error(ex.Message);
        }
    }

    /// <summary>
    /// Runs async work behind a small modal so the operator sees progress and
    /// cannot start a second browser against the same profile.
    /// </summary>
    public static T RunWithProgress<T>(IWin32Window? owner, string title, string firstLine,
        Func<Action<string>, Task<T>> work)
    {
        using var form = NewForm(title, 460, 140);
        form.ControlBox = false;

        var status = new Label
        {
            Text = firstLine,
            Font = Body,
            AutoEllipsis = true,
            Location = new Point(20, 40),
            Size = new Size(420, 40)
        };

        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(20, 86),
            Size = new Size(420, 12)
        };

        form.Controls.Add(Title(title, 20, 12));
        form.Controls.Add(status);
        form.Controls.Add(bar);

        T result = default!;
        Exception? failure = null;

        form.Shown += async (_, _) =>
        {
            void Report(string line)
            {
                if (form.IsHandleCreated)
                {
                    form.BeginInvoke(() => status.Text = line);
                }
            }

            try
            {
                result = await Task.Run(() => work(Report));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                form.Close();
            }
        };

        form.ShowDialog(owner);

        if (failure is not null)
        {
            throw failure;
        }

        return result;
    }
}
