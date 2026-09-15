using System.Windows.Forms;

namespace PlaywrightTestStudio;

internal static class Program
{
    /// <summary>
    /// Start-up is deliberately a loop: pick a project, work in it, and when the
    /// operator switches projects come straight back to the picker instead of
    /// restarting the app.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            return SelfTest.Run();
        }

        ApplicationConfiguration.Initialize();

        AppSettings.Load();

        while (true)
        {
            var name = ProjectDialogs.AskProject();

            if (name is null)
            {
                return 0;
            }

            AppSettings.LastProject = name;
            AppSettings.Save();

            var store = Storage.OpenProject(name);

            using var main = new MainForm(store);
            Application.Run(main);

            if (!main.SwitchRequested)
            {
                return 0;
            }
        }
    }
}
