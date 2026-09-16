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

        return new BrowserSession { Playwright = playwright, Context = context, Page = page };
    }

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
