using Microsoft.Playwright;

namespace PlaywrightTestStudio;

/// <summary>
/// One Chromium window driven by Playwright, using the shared persistent
/// profile so a login done once is still there on the next scan or run.
/// </summary>
internal sealed class BrowserSession : IAsyncDisposable
{
    private static bool _browsersInstalled;

    public IPlaywright Playwright { get; private init; } = null!;
    public IBrowserContext Context { get; private init; } = null!;
    public IPage Page { get; private init; } = null!;

    /// <summary>
    /// Starts a session. The first call may download Chromium, which is why the
    /// caller is expected to show a wait cursor around it.
    /// </summary>
    /// <summary>
    /// Playwright itself, with the browser download taken care of. Callers that
    /// need their own context options - the recorder, for one - start here.
    /// </summary>
    public static async Task<IPlaywright> CreatePlaywrightAsync()
    {
        EnsureBrowsersInstalled();
        Directory.CreateDirectory(Storage.ProfileDir);

        return await Microsoft.Playwright.Playwright.CreateAsync();
    }

    public static async Task<BrowserSession> StartAsync(bool headless)
    {

        var playwright = await CreatePlaywrightAsync();

        IBrowserContext context;

        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                Storage.ProfileDir,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Headless = headless,
                    ViewportSize = ViewportSize.NoViewport,
                    Args = headless ? null : new[] { "--start-maximized" }
                });
        }
        catch (Exception)
        {
            playwright.Dispose();
            throw;
        }

        context.SetDefaultTimeout(30_000);
        context.SetDefaultNavigationTimeout(60_000);

        var page = context.Pages.Count > 0 ? context.Pages[0] : await context.NewPageAsync();
        var session = new BrowserSession { Playwright = playwright, Context = context, Page = page };

        context.Close += (_, _) => session.Closed = true;

        return session;
    }

    /// <summary>True once the window is gone, whether we closed it or the operator did.</summary>
    public bool Closed { get; private set; }

    /// <summary>
    /// Downloads the Chromium build Playwright expects, once per process. Cheap
    /// after the first time - the CLI exits immediately when it is already there.
    /// </summary>
    private static void EnsureBrowsersInstalled()
    {
        if (_browsersInstalled)
        {
            return;
        }

        var exit = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });

        if (exit != 0)
        {
            throw new InvalidOperationException(
                $"Playwright could not install Chromium (exit code {exit}).\r\n" +
                "Run this once from a terminal in the project folder:\r\n" +
                "    dotnet build\r\n" +
                "    pwsh bin/Debug/net8.0-windows/playwright.ps1 install chromium");
        }

        _browsersInstalled = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Context.CloseAsync();
        }
        catch (Exception)
        {
            // The window may already be gone if the operator closed it by hand.
        }

        Playwright.Dispose();
    }

    /// <summary>
    /// Turns the exceptions Playwright throws on startup into something an
    /// operator can act on.
    /// </summary>
    public static string Explain(Exception ex)
    {
        var text = ex.Message;

        if (text.Contains("has been closed", StringComparison.OrdinalIgnoreCase))
        {
            return "The browser window was closed before the run finished.";
        }

        if (text.Contains("ProcessSingleton", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("profile", StringComparison.OrdinalIgnoreCase) && text.Contains("use", StringComparison.OrdinalIgnoreCase))
        {
            return "The shared browser profile is already open in another window.\r\n" +
                   "Close the Chromium window this tool opened, then try again.";
        }

        if (text.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase))
        {
            return "Chromium is not installed for Playwright yet.\r\n" +
                   "Close this dialog and try once more - the tool will download it.";
        }

        return text;
    }
}

/// <summary>
/// The one browser a whole run shares. Starting Chromium costs a few seconds, so
/// every test case and every data row gets a fresh tab in the same window
/// instead of a window of its own.
/// </summary>
internal sealed class RunBrowser : IAsyncDisposable
{
    private readonly bool _headless;
    private BrowserSession? _session;

    public RunBrowser(bool headless)
    {
        _headless = headless;
    }

    /// <summary>
    /// A clean tab for the next run. If the operator closed the window part way
    /// through, a new one is started rather than failing everything after it.
    /// </summary>
    public async Task<IPage> NewPageAsync()
    {
        if (_session is null || _session.Closed)
        {
            await DisposeAsync();
            _session = await BrowserSession.StartAsync(_headless);
        }

        var context = _session.Context;
        var page = await context.NewPageAsync();

        // Whatever the last run left open - its tab, any popups - goes, without
        // running their "are you sure you want to leave" handlers.
        foreach (var other in context.Pages.Where(p => p != page).ToList())
        {
            try
            {
                await other.CloseAsync();
            }
            catch (PlaywrightException)
            {
            }
        }

        return page;
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }
}
