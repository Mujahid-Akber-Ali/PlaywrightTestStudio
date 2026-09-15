using System.Text.Json;

namespace PlaywrightTestStudio;

/// <summary>
/// The handful of preferences that outlive a session. Kept next to the project
/// data rather than in the registry so a copied LocalAppData folder brings them
/// along.
/// </summary>
internal static class AppSettings
{
    private static readonly string File = Path.Combine(Storage.DataDir, "settings.json");

    /// <summary>
    /// Run scans and tests without a visible window. Off by default: watching
    /// the first run of a new link is usually how a bad selector gets spotted.
    /// </summary>
    public static bool Headless { get; set; }

    /// <summary>Remembered so the app can offer to reopen the last project.</summary>
    public static string LastProject { get; set; } = "";

    public static void Load()
    {
        if (!System.IO.File.Exists(File))
        {
            return;
        }

        try
        {
            var saved = JsonSerializer.Deserialize<Snapshot>(System.IO.File.ReadAllText(File), Storage.Json);

            if (saved is not null)
            {
                Headless = saved.Headless;
                LastProject = saved.LastProject;
            }
        }
        catch (Exception)
        {
            // Corrupt settings fall back to the defaults rather than blocking start-up.
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Storage.DataDir);

            var snapshot = new Snapshot { Headless = Headless, LastProject = LastProject };
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(snapshot, Storage.Json));
        }
        catch (Exception)
        {
            // Not being able to remember a preference is not worth an error popup.
        }
    }

    private sealed class Snapshot
    {
        public bool Headless { get; set; }
        public string LastProject { get; set; } = "";
    }
}
