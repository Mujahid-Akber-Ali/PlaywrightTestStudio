using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace PlaywrightTestStudio;

/// <summary>
/// One table lifted off a page: a title for its sheet, the column headings, and
/// a row per record. Values are already strings - whatever the cell showed.
/// </summary>
internal sealed class ScrapedTable
{
    public string Title { get; set; } = "";
    public List<string> Headers { get; set; } = new();
    public List<List<string>> Rows { get; } = new();

    /// <summary>How much data the table holds, for picking the one worth paging.</summary>
    public int Cells => Rows.Sum(r => r.Count);

    /// <summary>
    /// What makes this table the same table after the page turns: its headings,
    /// or failing that its width. Used to keep appending to the right grid when a
    /// page carries more than one.
    /// </summary>
    public string Signature => Headers.Count > 0
        ? "H:" + string.Join("\u0001", Headers)
        : "C:" + (Rows.Count > 0 ? Rows.Max(r => r.Count) : 0);
}

/// <summary>Everything a scrape produced: the tables, and how many pages it walked.</summary>
internal sealed class ScrapeResult
{
    public string Url { get; set; } = "";
    public int Pages { get; set; }
    public List<ScrapedTable> Tables { get; } = new();

    public int RowCount => Tables.Sum(t => t.Rows.Count);
}

/// <summary>
/// Scrapes a page the operator has set up by hand. The browser opens, they apply
/// whatever filters they want and load the data, then press the "Scrape to Excel"
/// button the tool floats on the page. Everything the page shows - and every page
/// after it, if it paginates - is read into a workbook.
///
/// It is the mirror image of <see cref="RecordingSession"/>: visible, driven from
/// inside the page, ended from inside the page. The shared browser profile means
/// a site that needs a login is reached the same way a scan or a run reaches it.
/// </summary>
internal sealed class ScrapeSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowserContext _context;

    // Set from the exposed-function callback, which Playwright raises off a
    // background thread; the continuation belongs on the UI thread.
    private readonly TaskCompletionSource<string> _requested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A page that turned over no new rows is the end of the data.</summary>
    private const int MaxPages = 1000;

    public IPage Page { get; }
    public string Url { get; }

    /// <summary>True once the operator has closed the browser window themselves.</summary>
    public bool BrowserClosed { get; private set; }

    /// <summary>
    /// Completes when the operator acts in the page: "scrape" when they press the
    /// button, "cancel" on Esc, "closed" if they shut the window. There is no
    /// window of ours on screen while this runs, so those are the only ways out.
    /// </summary>
    public Task<string> Requested => _requested.Task;

    private ScrapeSession(IPlaywright playwright, IBrowserContext context, IPage page, string url)
    {
        _playwright = playwright;
        _context = context;
        Page = page;
        Url = url;
    }

    /// <summary>
    /// Opens the page with the floating button in place. Visible by default -
    /// the operator has to be able to filter the page - but the self test drives
    /// it hidden.
    /// </summary>
    public static async Task<ScrapeSession> StartAsync(string url, bool headless = false)
    {
        var playwright = await BrowserSession.CreatePlaywrightAsync();

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
        var session = new ScrapeSession(playwright, context, page, url);

        // The floating button and Esc both come back through here.
        await context.ExposeFunctionAsync<string, bool>("__ptsScrape", command =>
        {
            session._requested.TrySetResult(command);
            return true;
        });

        // An init script survives navigation, so the button is still there after
        // the operator applies a filter and the page reloads.
        await context.AddInitScriptAsync(OverlayScript);

        context.Close += (_, _) =>
        {
            session.BrowserClosed = true;
            session._requested.TrySetResult("closed");
        };

        // Leave the page's own "are you sure?" boxes for the operator to answer,
        // exactly as the recorder does; accept them when nobody can.
        void LeaveDialogsToTheOperator(IPage opened) =>
            opened.Dialog += (_, dialog) =>
            {
                if (headless)
                {
                    _ = dialog.AcceptAsync();
                }
            };

        LeaveDialogsToTheOperator(page);
        context.Page += (_, opened) => LeaveDialogsToTheOperator(opened);

        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        return session;
    }

    /// <summary>
    /// Reads the page the operator set up, then follows its pagination to the end,
    /// appending every page's rows to the grid it started on. The browser stays
    /// open throughout - the clicks that turn the pages happen in it.
    /// </summary>
    public async Task<ScrapeResult> ScrapeAsync(Action<string>? progress = null)
    {
        var result = new ScrapeResult { Url = Url };

        async Task Status(string text)
        {
            progress?.Invoke(text);

            try
            {
                await Page.EvaluateAsync(
                    "t => { window.__ptsScraping = true; window.__ptsStatus = t; " +
                    "const h = document.getElementById('__pts_status'); if (h) h.textContent = t; " +
                    "const b = document.getElementById('__pts_scrape_btn'); if (b) b.style.display = 'none'; }",
                    text);
            }
            catch (Exception)
            {
                // The status line is a courtesy; losing it does not fail a scrape.
            }
        }

        await Status("Reading page 1...");
        await SettleAsync();

        var first = await ExtractAsync();

        if (first.Count == 0)
        {
            result.Pages = 1;
            return result;
        }

        result.Tables.AddRange(first);

        // The biggest grid is the one worth turning the pages for; the rest are
        // taken once, as they stand.
        var main = first.Aggregate((a, b) => b.Cells > a.Cells ? b : a);
        var mainSignature = main.Signature;
        var previous = Serialize(main.Rows);

        var pages = 1;

        while (pages < MaxPages)
        {
            if (!await ClickNextAsync())
            {
                break;
            }

            pages++;
            await Status($"Reading page {pages} ({main.Rows.Count} row(s) so far)...");
            await SettleAsync();

            var tables = await ExtractAsync();

            var current = tables.FirstOrDefault(t => t.Signature == mainSignature)
                          ?? (tables.Count > 0 ? tables.Aggregate((a, b) => b.Cells > a.Cells ? b : a) : null);

            if (current is null)
            {
                break;
            }

            var serialized = Serialize(current.Rows);

            // "Next" was there but changed nothing - the last page, with a button
            // that never disabled itself. Stop before the rows double up.
            if (serialized == previous)
            {
                break;
            }

            previous = serialized;
            main.Rows.AddRange(current.Rows);
        }

        result.Pages = pages;
        return result;
    }

    /// <summary>Lets a filter or a page turn settle before the DOM is read.</summary>
    private async Task SettleAsync()
    {
        await Page.WaitForTimeoutAsync(250);

        try
        {
            await Page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 4_000 });
        }
        catch (TimeoutException)
        {
            // A page that never falls quiet is read as it is.
        }
    }

    /// <summary>
    /// Finds the "next page" control, and if there is an enabled one, clicks it.
    /// The find marks the element so Playwright can land a real click on it rather
    /// than a synthetic one the page might ignore.
    /// </summary>
    private async Task<bool> ClickNextAsync()
    {
        bool found;

        try
        {
            found = await Page.EvaluateAsync<bool>(FindNextScript);
        }
        catch (Exception)
        {
            return false;
        }

        if (!found)
        {
            return false;
        }

        try
        {
            await Page.ClickAsync("[data-pts-next]", new PageClickOptions { Timeout = 5_000 });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<List<ScrapedTable>> ExtractAsync()
    {
        JsonElement json;

        try
        {
            json = await Page.EvaluateAsync<JsonElement>(ScrapeScript);
        }
        catch (Exception)
        {
            return new List<ScrapedTable>();
        }

        return Parse(json);
    }

    private static List<ScrapedTable> Parse(JsonElement json)
    {
        var list = new List<ScrapedTable>();

        if (json.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in json.EnumerateArray())
        {
            var table = new ScrapedTable
            {
                Title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? ""
                    : ""
            };

            if (item.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Array)
            {
                foreach (var h in headers.EnumerateArray())
                {
                    table.Headers.Add(h.ValueKind == JsonValueKind.String ? h.GetString() ?? "" : "");
                }
            }

            if (item.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var cells = new List<string>();

                    foreach (var cell in row.EnumerateArray())
                    {
                        cells.Add(cell.ValueKind == JsonValueKind.String ? cell.GetString() ?? "" : cell.ToString());
                    }

                    table.Rows.Add(cells);
                }
            }

            if (table.Rows.Count > 0 || table.Headers.Count > 0)
            {
                list.Add(table);
            }
        }

        return list;
    }

    /// <summary>A cheap fingerprint of a page's rows, to tell when a turn did nothing.</summary>
    private static string Serialize(List<List<string>> rows)
    {
        var sb = new StringBuilder();

        foreach (var row in rows)
        {
            sb.Append(string.Join("\u0001", row)).Append('\u0002');
        }

        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _context.CloseAsync();
        }
        catch (Exception)
        {
            // The operator may already have closed the window.
        }

        _playwright.Dispose();
    }

    // -----------------------------------------------------------------------
    // The scripts that run inside the page
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads every visible table on the page into a plain array of
    /// { title, headers, rows }. A cell that holds a form control reports what
    /// the control shows, so a grid of inputs scrapes to its values rather than
    /// to blanks.
    /// </summary>
    private const string ScrapeScript = """
    () => {
      const clean = (s) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim();

      const isVisible = (el) => {
        const r = el.getBoundingClientRect();
        const st = window.getComputedStyle(el);
        return r.width > 0 && r.height > 0 && st.visibility !== 'hidden' && st.display !== 'none';
      };

      const cellText = (cell) => {
        const control = cell.querySelector('input, select, textarea');
        if (control) {
          const tag = control.tagName.toLowerCase();
          if (tag === 'select') {
            const o = control.selectedOptions && control.selectedOptions[0];
            return clean(o ? o.text : control.value);
          }
          const type = String(control.type || '').toLowerCase();
          if (type === 'checkbox' || type === 'radio') return control.checked ? 'TRUE' : 'FALSE';
          if (control.value != null && String(control.value).length) return clean(control.value);
        }
        return clean(cell.innerText || cell.textContent);
      };

      const titleFor = (tbl, n) => {
        const caption = tbl.querySelector('caption');
        if (caption && clean(caption.innerText)) return clean(caption.innerText);

        const aria = tbl.getAttribute('aria-label');
        if (clean(aria)) return clean(aria);

        // The nearest heading above the table usually names it.
        let node = tbl;
        for (let hops = 0; hops < 8 && node; hops++) {
          let sib = node.previousElementSibling;
          while (sib) {
            if (/^H[1-6]$/.test(sib.tagName) && clean(sib.innerText)) return clean(sib.innerText).slice(0, 60);
            const h = sib.querySelector ? sib.querySelector('h1,h2,h3,h4,h5,h6') : null;
            if (h && clean(h.innerText)) return clean(h.innerText).slice(0, 60);
            sib = sib.previousElementSibling;
          }
          node = node.parentElement;
        }
        return 'Table ' + n;
      };

      const rowCells = (tr) => {
        const cells = tr.querySelectorAll(':scope > th, :scope > td');
        return Array.prototype.map.call(cells, cellText);
      };

      const nonEmpty = (row) => row.some(v => v.length > 0);

      const readTable = (tbl, n) => {
        let headers = [];

        const thead = tbl.querySelector('thead');
        if (thead) {
          const hrows = thead.querySelectorAll('tr');
          const htr = hrows[hrows.length - 1];      // the lowest row of a grouped header
          if (htr) headers = rowCells(htr);
        }

        const rows = [];
        const bodies = tbl.querySelectorAll('tbody');
        const scopes = bodies.length ? bodies : [tbl];

        scopes.forEach(scope => {
          scope.querySelectorAll('tr').forEach(tr => {
            if (tr.closest('thead') || tr.closest('tfoot')) return;
            const row = rowCells(tr);
            if (row.length && nonEmpty(row)) rows.push(row);
          });
        });

        // No <thead>: a first row made only of <th> is the header.
        if (!headers.length && rows.length) {
          const first = (bodies.length ? bodies[0] : tbl).querySelector('tr');
          if (first && first.querySelector('th') && !first.querySelector('td')) {
            headers = rows.shift();
          }
        }

        return { title: titleFor(tbl, n), headers: headers.map(clean), rows };
      };

      const out = [];
      let n = 1;

      document.querySelectorAll('table').forEach(tbl => {
        if (!isVisible(tbl)) return;
        const table = readTable(tbl, n);
        if (table.rows.length || table.headers.length) { out.push(table); n++; }
      });

      // Only fall back to ARIA grids (ag-Grid and the like) when the page has no
      // real tables - otherwise a grid gets read twice.
      if (out.length === 0) {
        document.querySelectorAll('[role="grid"], [role="table"], [role="treegrid"]').forEach(grid => {
          if (!isVisible(grid)) return;

          const headers = [];
          grid.querySelectorAll('[role="columnheader"]').forEach(h => headers.push(clean(h.innerText || h.textContent)));

          const rows = [];
          grid.querySelectorAll('[role="row"]').forEach(r => {
            const cells = r.querySelectorAll('[role="gridcell"], [role="cell"]');
            if (!cells.length) return;
            const row = Array.prototype.map.call(cells, c => clean(c.innerText || c.textContent));
            if (nonEmpty(row)) rows.push(row);
          });

          if (rows.length) { out.push({ title: titleFor(grid, n), headers, rows }); n++; }
        });
      }

      return out;
    }
    """;

    /// <summary>
    /// Looks for the control that goes to the next page, and marks the enabled one
    /// it finds with data-pts-next. Covers rel=next, an aria-label or title that
    /// says "next", the class names the common grid and pagination libraries use,
    /// and a link or button whose text is Next or a forward chevron.
    /// </summary>
    private const string FindNextScript = """
    () => {
      document.querySelectorAll('[data-pts-next]').forEach(e => e.removeAttribute('data-pts-next'));

      const clean = (s) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim();

      const isVisible = (el) => {
        const r = el.getBoundingClientRect();
        const st = window.getComputedStyle(el);
        return r.width > 0 && r.height > 0 && st.visibility !== 'hidden' && st.display !== 'none';
      };

      const disabled = (el) => {
        if (!el) return false;
        if (el.disabled) return true;
        if (el.getAttribute && el.getAttribute('aria-disabled') === 'true') return true;
        const cls = String(el.className || '');
        if (/\b(disabled|k-state-disabled|k-disabled)\b/i.test(cls)) return true;
        const li = el.closest ? el.closest('li, .page-item') : null;
        if (li && (/\bdisabled\b/i.test(String(li.className || '')) || li.getAttribute('aria-disabled') === 'true')) return true;
        return false;
      };

      const candidates = [];
      const add = (el) => { if (el) candidates.push(el); };

      document.querySelectorAll('a[rel~="next"]').forEach(add);

      document.querySelectorAll('[aria-label]').forEach(e => {
        if (/next\s*page|go to (the )?next|next\b/i.test(e.getAttribute('aria-label'))) add(e);
      });

      document.querySelectorAll('[title]').forEach(e => {
        if (/next\s*page|go to (the )?next/i.test(e.getAttribute('title'))) add(e);
      });

      document.querySelectorAll(
        '.next, .pagination-next, .paginate_button.next, .page-next, .k-pager-next, [class*="next"]'
      ).forEach(add);

      document.querySelectorAll('a, button, [role="button"], li').forEach(e => {
        const t = clean(e.textContent);
        if (!t) return;
        if (/^next\b/i.test(t) && t.length <= 14) add(e);
        else if (t === '›' || t === '»' || t === '>' || t === '→' || t === 'Next ›') add(e);
      });

      for (const el of candidates) {
        const clickable = el.matches('a, button, [role="button"]')
          ? el
          : (el.querySelector('a, button, [role="button"]') || el);

        if (!isVisible(clickable)) continue;
        if (disabled(clickable) || disabled(el)) continue;

        clickable.setAttribute('data-pts-next', '1');
        return true;
      }

      return false;
    }
    """;

    /// <summary>
    /// The floating button and its hint. Shown on the top document only; inside a
    /// frame it only listens for Esc. Rebuilt on a timer so a page that redraws
    /// itself cannot wipe the button away.
    /// </summary>
    private const string OverlayScript = """
    (() => {
      if (window.__ptsScrapeInstalled) return;
      window.__ptsScrapeInstalled = true;

      const call = (command) => { try { window.__ptsScrape(command); } catch (e) {} };

      document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') call('cancel');
      }, true);

      if (window !== window.top) return;

      const build = () => {
        if (!document.body) return;

        let bar = document.getElementById('__pts_scrape_bar');

        if (!bar) {
          bar = document.createElement('div');
          bar.id = '__pts_scrape_bar';
          bar.style.cssText =
            'position:fixed;top:12px;right:12px;z-index:2147483647;display:flex;flex-direction:column;' +
            'gap:6px;align-items:flex-end;font:600 13px "Segoe UI",system-ui,sans-serif;';

          const btn = document.createElement('button');
          btn.type = 'button';
          btn.id = '__pts_scrape_btn';
          btn.textContent = '⬇ Scrape to Excel';
          btn.style.cssText =
            'cursor:pointer;background:#107c10;color:#fff;border:none;border-radius:4px;' +
            'padding:10px 16px;font:inherit;box-shadow:0 2px 8px rgba(0,0,0,.35);';
          btn.addEventListener('click', (e) => { e.preventDefault(); e.stopPropagation(); call('scrape'); });

          const hint = document.createElement('div');
          hint.id = '__pts_status';
          hint.textContent = 'Apply your filters, then click. Esc cancels.';
          hint.style.cssText =
            'background:rgba(0,0,0,.72);color:#fff;border-radius:4px;padding:6px 10px;' +
            'max-width:260px;font-weight:500;text-align:right;';

          bar.appendChild(btn);
          bar.appendChild(hint);
          document.body.appendChild(bar);
        }

        const btn = document.getElementById('__pts_scrape_btn');
        if (btn) btn.style.display = window.__ptsScraping ? 'none' : '';

        if (window.__ptsScraping && window.__ptsStatus) {
          const hint = document.getElementById('__pts_status');
          if (hint) hint.textContent = window.__ptsStatus;
        }
      };

      if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', build);
      } else {
        build();
      }

      setInterval(build, 1500);
    })();
    """;
}
