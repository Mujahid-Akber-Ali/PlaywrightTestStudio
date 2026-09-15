using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlaywrightTestStudio;

/// <summary>
/// Where everything lives on disk. Test data sits under LocalAppData so it
/// survives a rebuild or a clean, while the generated C# files are written next
/// to the source tree where Visual Studio and git can see them.
/// </summary>
internal static class Storage
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PlaywrightTestStudio");

    /// <summary>One Chrome profile for every project: log in once, stay logged in.</summary>
    public static readonly string ProfileDir = Path.Combine(DataDir, "browser-profile");

    private static readonly string ProjectsDir = Path.Combine(DataDir, "projects");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// Root for the generated .cs files. Walks up from the running binary
    /// looking for the project file, so a debug run and a published copy both
    /// land somewhere sensible.
    /// </summary>
    public static string CodeRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "PlaywrightTestStudio.csproj")))
                {
                    return Path.Combine(dir.FullName, "GeneratedTests");
                }

                dir = dir.Parent;
            }

            return Path.Combine(DataDir, "GeneratedTests");
        }
    }

    public static IReadOnlyList<string> ListProjects()
    {
        Directory.CreateDirectory(ProjectsDir);

        return Directory.GetDirectories(ProjectsDir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static ProjectStore OpenProject(string name)
    {
        var store = new ProjectStore(name, Path.Combine(ProjectsDir, Slug(name)));
        store.EnsureFolders();
        return store;
    }

    public static void DeleteProject(string name)
    {
        var dir = Path.Combine(ProjectsDir, Slug(name));

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Filename-safe, human-readable form of a name. Never returns empty.</summary>
    public static string Slug(string text)
    {
        var sb = new StringBuilder();

        foreach (var ch in text.Trim())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '.' or '/' or '\\' or ':') sb.Append('_');
        }

        var slug = sb.ToString().Trim('_');

        while (slug.Contains("__"))
        {
            slug = slug.Replace("__", "_");
        }

        return slug.Length == 0 ? "unnamed" : slug;
    }

    /// <summary>A C# identifier built from arbitrary text, e.g. a URL path or a test name.</summary>
    public static string Identifier(string text)
    {
        var slug = Slug(text);

        if (!char.IsLetter(slug[0]) && slug[0] != '_')
        {
            slug = "_" + slug;
        }

        return slug;
    }
}

/// <summary>Reads and writes the test cases and run log of a single project.</summary>
internal sealed class ProjectStore
{
    public string Name { get; }
    public string Dir { get; }

    public string TestsDir => Path.Combine(Dir, "tests");
    public string ScreenshotsDir => Path.Combine(Dir, "screenshots");
    public string CodeDir => Path.Combine(Storage.CodeRoot, Storage.Slug(Name));
    private string RunLogFile => Path.Combine(Dir, "runs.json");
    private string MetaFile => Path.Combine(Dir, "project.json");

    public ProjectStore(string name, string dir)
    {
        Name = name;
        Dir = dir;
    }

    public void EnsureFolders()
    {
        Directory.CreateDirectory(TestsDir);
        Directory.CreateDirectory(ScreenshotsDir);
        Directory.CreateDirectory(CodeDir);

        if (!File.Exists(MetaFile))
        {
            var meta = new { Name, CreatedUtc = DateTime.UtcNow };
            File.WriteAllText(MetaFile, JsonSerializer.Serialize(meta, Storage.Json));
        }
    }

    /// <summary>Every saved test case, newest first. Unreadable files are skipped, not fatal.</summary>
    public List<TestCase> LoadAll()
    {
        var list = new List<TestCase>();

        foreach (var file in Directory.GetFiles(TestsDir, "*.json"))
        {
            try
            {
                var tc = JsonSerializer.Deserialize<TestCase>(File.ReadAllText(file), Storage.Json);

                if (tc is not null)
                {
                    list.Add(tc);
                }
            }
            catch (Exception)
            {
                // A hand-edited or truncated file should not stop the whole list
                // from loading; it simply does not appear.
            }
        }

        return list.OrderByDescending(t => t.CreatedUtc).ToList();
    }

    public void Save(TestCase test)
    {
        File.WriteAllText(FileFor(test), JsonSerializer.Serialize(test, Storage.Json));
    }

    public void Delete(TestCase test)
    {
        var file = FileFor(test);

        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    private string FileFor(TestCase test) => Path.Combine(TestsDir, $"{test.Id}.json");

    public List<RunLogEntry> LoadRuns()
    {
        if (!File.Exists(RunLogFile))
        {
            return new List<RunLogEntry>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<RunLogEntry>>(File.ReadAllText(RunLogFile), Storage.Json)
                   ?? new List<RunLogEntry>();
        }
        catch (Exception)
        {
            return new List<RunLogEntry>();
        }
    }

    /// <summary>Appends one run and keeps the log to a workable size.</summary>
    public void AppendRun(TestCase test, TestResult result)
    {
        var runs = LoadRuns();

        runs.Add(new RunLogEntry
        {
            TestId = test.Id,
            TestName = test.Name,
            Url = test.Url,
            Status = result.Status,
            Message = result.Message,
            RanUtc = result.RanUtc,
            Seconds = result.Seconds
        });

        if (runs.Count > 2000)
        {
            runs = runs.Skip(runs.Count - 2000).ToList();
        }

        File.WriteAllText(RunLogFile, JsonSerializer.Serialize(runs, Storage.Json));
    }
}
