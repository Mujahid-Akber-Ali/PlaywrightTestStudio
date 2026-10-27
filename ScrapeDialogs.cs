using System.Windows.Forms;

namespace PlaywrightTestStudio;

/// <summary>
/// The scrape-data flow: open a link, let the operator filter and load the page,
/// then on the press of the floating button read everything on it - across every
/// page it paginates to - into an Excel workbook.
/// </summary>
internal static class ScrapeDialogs
{
    /// <summary>
    /// Scrapes a page to Excel. Like recording, the browser is the only window on
    /// screen while it runs: the operator sets the page up and presses "Scrape to
    /// Excel", or Esc to back out.
    /// </summary>
    public static async Task ScrapeAsync(Form owner, ProjectStore store)
    {
        var url = ProjectDialogs.AskLink(owner, purpose: "scrape");

        if (url is null)
        {
            return;
        }

        ScrapeSession session;

        try
        {
            // Always visible - there is nothing to filter on a hidden window.
            session = Ui.RunWithProgress(owner, "Opening browser", $"Loading {url}",
                async _ => await ScrapeSession.StartAsync(url));
        }
        catch (Exception ex)
        {
            Ui.Error(BrowserSession.Explain(ex), $"{Ui.AppTitle} - could not open the page");
            return;
        }

        ScrapeResult result;

        // Out of the way entirely, so no window of ours sits over the page.
        owner.Hide();

        try
        {
            var command = await session.Requested;

            if (command != "scrape")
            {
                // Esc, or the window closed: nothing to save. The browser is shut
                // in the finally below.
                return;
            }

            result = await session.ScrapeAsync();
        }
        catch (Exception ex)
        {
            owner.Show();
            Ui.Error(BrowserSession.Explain(ex), $"{Ui.AppTitle} - scrape failed");
            return;
        }
        finally
        {
            await session.DisposeAsync();
            owner.Show();
            owner.Activate();
        }

        if (result.RowCount == 0)
        {
            Ui.Warn("No table data was found on the page.\r\n\r\n" +
                    "Load the data first - apply the filter and run the page so the grid is showing - " +
                    "then press \"Scrape to Excel\".");
            return;
        }

        using var save = new SaveFileDialog
        {
            Title = "Save scraped data",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"{Storage.Slug(store.Name)}-scrape-{DateTime.Now:yyyyMMdd-HHmmss}.xlsx",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (save.ShowDialog(owner) != DialogResult.OK)
        {
            return;
        }

        int sheets;

        try
        {
            sheets = ScrapeBook.Write(result, save.FileName);
        }
        catch (IOException ex)
        {
            Ui.Error($"Could not write the file.\r\n\r\n{ex.Message}\r\n\r\n" +
                     "If it is open in Excel, close it and try again.");
            return;
        }

        var pages = result.Pages > 1 ? $" across {result.Pages} page(s)" : "";
        var extra = sheets > 1 ? $" in {sheets} sheet(s)" : "";

        if (Ui.Confirm($"Scraped {result.RowCount} row(s){pages}{extra} to:\r\n{save.FileName}\r\n\r\nOpen it now?"))
        {
            Ui.Open(save.FileName);
        }
    }
}
