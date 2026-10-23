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

    /// <summary>
    /// The projects on disk, by the name they were given. The folder is only a
    /// filename-safe form of that name, so it is never what gets shown.
    /// </summary>
    public static IReadOnlyList<string> ListProjects()
    {
        Directory.CreateDirectory(ProjectsDir);

        return Directory.GetDirectories(ProjectsDir)
            .Select(NameOf)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The name saved in a project folder, falling back to the folder's own name.</summary>
    private static string NameOf(string dir)
    {
        try
        {
            var meta = JsonSerializer.Deserialize<ProjectMeta>(
                File.ReadAllText(Path.Combine(dir, "project.json")), Json);

            if (!string.IsNullOrWhiteSpace(meta?.Name))
            {
                return meta.Name.Trim();
            }
        }
        catch (Exception)
        {
            // No readable project.json: the folder name is the best there is.
        }

        return Path.GetFileName(dir) ?? "";
    }

    /// <summary>
    /// The folder a project lives in. An existing project is found by its saved
    /// name first, so a folder that does not match the name still opens.
    /// </summary>
    private static string DirFor(string name)
    {
        Directory.CreateDirectory(ProjectsDir);

        var existing = Directory.GetDirectories(ProjectsDir)
            .FirstOrDefault(d => string.Equals(NameOf(d), name, StringComparison.OrdinalIgnoreCase));

        return existing ?? Path.Combine(ProjectsDir, Slug(name));
    }

    /// <summary>True when a project with this name, or one that would share its folder, exists.</summary>
    public static bool ProjectExists(string name) => Directory.Exists(DirFor(name));

    public static ProjectStore OpenProject(string name)
    {
        var store = new ProjectStore(name, DirFor(name));
        store.EnsureFolders();
        return store;
    }

    public static void DeleteProject(string name)
    {
        var dir = DirFor(name);

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Gives a project a new name and moves everything saved under the old one:
    /// its data folder and its generated C#. Returns the project under its new
    /// name.
    /// </summary>
    public static ProjectStore RenameProject(string oldName, string newName)
    {
        var old = OpenProject(oldName);
        var created = old.CreatedUtc;
        var newDir = Path.Combine(ProjectsDir, Slug(newName));

        if (!string.Equals(old.Dir, newDir, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Move(old.Dir, newDir);
        }

        var renamed = new ProjectStore(newName, newDir);

        // The generated files carry the old name in their namespace, so they
        // are written again below rather than moved.
        if (!string.Equals(old.CodeDir, renamed.CodeDir, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(old.CodeDir))
        {
            Directory.Delete(old.CodeDir, recursive: true);
        }

        renamed.WriteMeta(created);
        renamed.EnsureFolders();

        var tests = renamed.LoadAll();

        foreach (var test in tests)
        {
            // Screenshots and the recording moved along with the folder.
            test.VideoFile = Moved(test.VideoFile, old.Dir, newDir);

            foreach (var result in test.LastResults.Append(test.LastResult).OfType<TestResult>())
            {
                result.ScreenshotPath = Moved(result.ScreenshotPath, old.Dir, newDir);
            }
        }

        foreach (var group in tests.GroupBy(t => t.Url))
        {
            var file = CodeGen.WriteForLink(renamed, group.Key, group.ToList());

            foreach (var test in group)
            {
                test.CodeFile = file;
                renamed.Save(test);
            }
        }

        return renamed;
    }

    private static string Moved(string path, string oldDir, string newDir) =>
        path.StartsWith(oldDir, StringComparison.OrdinalIgnoreCase)
            ? newDir + path[oldDir.Length..]
            : path;

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

/// <summary>What project.json holds: the name the operator gave the project.</summary>
internal sealed class ProjectMeta
{
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
}

/// <summary>Reads and writes the test cases and run log of a single project.</summary>
internal sealed class ProjectStore
{
    public string Name { get; }
    public string Dir { get; }

    public string TestsDir => Path.Combine(Dir, "tests");
    public string ScreenshotsDir => Path.Combine(Dir, "screenshots");
    public string VideosDir => Path.Combine(Dir, "videos");

    public string ResultsDir => Path.Combine(Dir, "results");

    /// <summary>Default name offered when the operator downloads the test data.</summary>
    public string WorkbookName => $"{Storage.Slug(Name)}-test-data.xlsx";

    /// <summary>A fresh results file name; one is written at the end of every run.</summary>
    public string NewResultsFile() =>
        Path.Combine(ResultsDir, $"{Storage.Slug(Name)}-results-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx");

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
        Directory.CreateDirectory(VideosDir);
        Directory.CreateDirectory(ResultsDir);
        Directory.CreateDirectory(CodeDir);

        if (!File.Exists(MetaFile))
        {
            WriteMeta(DateTime.UtcNow);
        }
    }

    public void WriteMeta(DateTime createdUtc)
    {
        Directory.CreateDirectory(Dir);

        var meta = new ProjectMeta { Name = Name, CreatedUtc = createdUtc };
        File.WriteAllText(MetaFile, JsonSerializer.Serialize(meta, Storage.Json));
    }

    public DateTime CreatedUtc
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<ProjectMeta>(File.ReadAllText(MetaFile), Storage.Json)?.CreatedUtc
                       ?? DateTime.UtcNow;
            }
            catch (Exception)
            {
                return DateTime.UtcNow;
            }
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
                    Migrate(tc);
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

    /// <summary>
    /// Folds a test case saved before ordered steps existed into the new shape:
    /// every field became a step, then every button click, which is the order
    /// those cases actually ran in.
    /// </summary>
    private static void Migrate(TestCase test)
    {
        if (test.Steps.Count > 0 || (test.Fields.Count == 0 && test.Clicks.Count == 0))
        {
            return;
        }

        foreach (var field in test.Fields)
        {
            var action = field.Kind switch
            {
                "select" => StepActions.Select,
                "radio" => StepActions.Check,
                "checkbox" => Runner.IsTruthy(field.Value) ? StepActions.Check : StepActions.Uncheck,
                "search" => StepActions.Type,
                _ => StepActions.Fill
            };

            test.Steps.Add(new TestStep
            {
                Use = field.Use,
                Action = action,
                Selector = field.Selector,
                Label = field.Label,
                Value = field.Kind is "checkbox" or "radio" ? "" : field.Value
            });

            // A search field was one row that meant "type, then pick"; that is
            // two steps now.
            if (field.Kind == "search" && field.Pick.Trim().Length > 0)
            {
                test.Steps.Add(new TestStep
                {
                    Action = StepActions.Click,
                    Selector = field.ResultsSelector.Trim().Length > 0
                        ? $"{field.ResultsSelector.Trim()} [role=option], {field.ResultsSelector.Trim()} li"
                        : "[role=option]",
                    Label = $"{field.Label} result",
                    FallbackText = field.Pick.Trim()
                });
            }
        }

        foreach (var click in test.Clicks)
        {
            test.Steps.Add(new TestStep
            {
                Action = StepActions.Click,
                Selector = click.Selector,
                Label = click.Label,
                WaitAfter = click.WaitAfter
            });
        }

        test.Fields.Clear();
        test.Clicks.Clear();
        test.Source = "scanned";
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
            DataSet = result.DataSet,
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
