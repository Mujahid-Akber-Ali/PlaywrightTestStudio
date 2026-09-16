using System.Text.Json;
using Microsoft.Playwright;

namespace PlaywrightTestStudio;

/// <summary>
/// Records a session in a real browser: the operator works through the page as
/// they normally would and every action becomes a step. This is the way to build
/// a test case - nobody has to read the DOM or pick a locator by hand.
///
/// A screen recording of the session is captured alongside it.
/// </summary>
internal sealed class RecordingSession : IAsyncDisposable
{
    private readonly IPlaywright _playwright;
    private readonly IBrowserContext _context;
    private readonly List<TestStep> _steps = new();
    private readonly List<AssertionSpec> _assertions = new();
    private readonly object _gate = new();

    // Completed asynchronously: it is set from a Playwright callback thread, and
    // whatever is waiting belongs on the UI thread.
    private readonly TaskCompletionSource _stopRequested =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IPage Page { get; }
    public string Url { get; }

    /// <summary>
    /// Completes when the operator presses Esc in the page, or closes the
    /// browser window. Nothing of this application is on screen while a
    /// recording runs, so those are the two ways to finish.
    /// </summary>
    public Task StopRequested => _stopRequested.Task;

    /// <summary>True once the browser window has been closed by the operator.</summary>
    public bool BrowserClosed { get; private set; }

    private RecordingSession(IPlaywright playwright, IBrowserContext context, IPage page, string url)
    {
        _playwright = playwright;
        _context = context;
        Page = page;
        Url = url;
    }

    public IReadOnlyList<TestStep> Steps
    {
        get { lock (_gate) { return _steps.ToList(); } }
    }

    public IReadOnlyList<AssertionSpec> Assertions
    {
        get { lock (_gate) { return _assertions.ToList(); } }
    }

    public int Count
    {
        get { lock (_gate) { return _steps.Count + _assertions.Count; } }
    }

    /// <summary>
    /// Opens the page and starts capturing. Visible by default - watching the
    /// window is the point - but the self test drives it hidden.
    /// </summary>
    public static async Task<RecordingSession> StartAsync(string url, string videoDir, bool headless = false)
    {
        Directory.CreateDirectory(videoDir);

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
                    Args = headless ? null : new[] { "--start-maximized" },
                    RecordVideoDir = videoDir
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
        var session = new RecordingSession(playwright, context, page, url);

        // The page calls this for every action the operator takes.
        await context.ExposeFunctionAsync<string, bool>("__ptsRecord", json =>
        {
            session.Accept(json);
            return true;
        });

        // An init script survives navigation, so the recording keeps going when
        // a click takes the operator to the next page.
        await context.AddInitScriptAsync(RecorderScript);

        context.Close += (_, _) =>
        {
            session.BrowserClosed = true;
            session._stopRequested.TrySetResult();
        };

        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

        return session;
    }

    /// <summary>
    /// Takes one event from the page. Repeated typing into the same field
    /// collapses into a single step holding the final value.
    /// </summary>
    private void Accept(string json)
    {
        try
        {
            var item = JsonDocument.Parse(json).RootElement;
            var kind = Str(item, "kind");

            if (kind == "stop")
            {
                _stopRequested.TrySetResult();
                return;
            }

            lock (_gate)
            {
                if (kind == "assert")
                {
                    var text = Str(item, "text");

                    _assertions.Add(new AssertionSpec
                    {
                        Kind = text.Length > 0 ? AssertionKinds.ElementTextContains : AssertionKinds.ElementVisible,
                        Selector = Str(item, "selector"),
                        Expected = text
                    });
                }
                else
                {
                    var step = new TestStep
                    {
                        Action = kind,
                        Selector = Str(item, "selector"),
                        Label = Str(item, "label"),
                        Value = Str(item, "value"),
                        FallbackText = Str(item, "text"),
                        WaitAfter = kind == StepActions.Click
                    };

                    var last = _steps.Count > 0 ? _steps[^1] : null;

                    if (kind == StepActions.Click)
                    {
                        // A click straight after typing into a lookup is picking
                        // a result, not pressing a button - and it belongs to the
                        // field that was typed into.
                        var followsSearch = last is not null && last.Action == StepActions.Type;

                        step.Pick = followsSearch ||
                                    (item.TryGetProperty("pick", out var flag) && flag.ValueKind == JsonValueKind.True);

                        if (followsSearch && last!.Label.Length > 0)
                        {
                            step.Label = $"{last.Label} result";
                        }
                    }

                    // Keystroke after keystroke in one box is one step, not ten.
                    if (last is not null &&
                        last.Selector == step.Selector &&
                        last.Action == step.Action &&
                        step.Action is StepActions.Fill or StepActions.Type or StepActions.Select)
                    {
                        last.Value = step.Value;
                    }
                    else
                    {
                        _steps.Add(step);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A malformed event is not worth losing the recording over.
        }
    }

    private static string Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    /// <summary>
    /// Closes the browser and returns the finished test case. Parameter names
    /// are assigned here so the Excel sheet has a column per value the operator
    /// typed, seeded with what they actually typed.
    /// </summary>
    public async Task<TestCase> StopAsync(string name)
    {
        var video = "";
        var handle = Page.Video;

        if (handle is not null)
        {
            try
            {
                // The file is only finished when the context closes, so ask for
                // the path first and check it exists afterwards.
                video = await handle.PathAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
                // No video if the operator closed the page by hand first.
            }
        }

        await DisposeAsync();

        if (video.Length > 0 && !File.Exists(video))
        {
            video = "";
        }

        var test = new TestCase
        {
            Name = name,
            Url = Url,
            Source = "recorded",
            Steps = Steps.ToList(),
            Assertions = Assertions.ToList(),
            VideoFile = File.Exists(video) ? video : ""
        };

        AssignParameters(test);

        return test;
    }

    /// <summary>
    /// Gives every value-carrying step a data column name and builds the first
    /// data set from the recorded values - that row becomes the example a person
    /// copies when they add more test data in Excel.
    /// </summary>
    public static void AssignParameters(TestCase test)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recorded = new DataSet { Name = "Recorded" };

        foreach (var step in test.Steps)
        {
            if (!step.TakesValue || step.Action == StepActions.Goto)
            {
                continue;
            }

            if (step.Parameter.Length == 0)
            {
                step.Parameter = UniqueParameter(used, step.Label, step.Selector);
            }
            else
            {
                used.Add(step.Parameter);
            }

            recorded.Values[step.Parameter] = step.RecordedValue;
        }

        if (test.DataSets.Count == 0)
        {
            test.DataSets.Add(recorded);
        }
    }

    private static string UniqueParameter(HashSet<string> used, string label, string selector)
    {
        var source = label.Trim().Length > 0 ? label : selector;
        var name = Storage.Identifier(source).Trim('_');

        if (name.Length > 28)
        {
            name = name[..28];
        }

        if (name.Length == 0)
        {
            name = "value";
        }

        name = char.ToLowerInvariant(name[0]) + name[1..];

        var candidate = name;
        var n = 2;

        while (!used.Add(candidate))
        {
            candidate = $"{name}{n++}";
        }

        return candidate;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _context.CloseAsync();
        }
        catch (Exception)
        {
        }

        _playwright.Dispose();
    }

    /// <summary>
    /// Injected into every page. Listens in the capture phase so it sees the
    /// action before the page's own handlers can stop it, and never changes what
    /// the page does.
    ///
    /// Alt+click records an assertion on the element instead of a click.
    /// </summary>
    private const string RecorderScript = """
    (() => {
      if (window.__ptsRecorderInstalled) return;
      window.__ptsRecorderInstalled = true;

      const esc = (s) => (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/([^\w-])/g, '\\$1');

      const unique = (sel) => {
        try { return document.querySelectorAll(sel).length === 1; } catch (e) { return false; }
      };

      const selectorFor = (el) => {
        if (!el || el.nodeType !== 1) return '';
        if (el.id && unique('#' + esc(el.id))) return '#' + esc(el.id);

        for (const attr of ['data-testid', 'data-test', 'data-qa', 'data-automation-id']) {
          const v = el.getAttribute(attr);
          if (v) {
            const s = '[' + attr + '="' + v.replace(/"/g, '\\"') + '"]';
            if (unique(s)) return s;
          }
        }

        const tag = el.tagName.toLowerCase();

        if (el.name) {
          const n = String(el.name).replace(/"/g, '\\"');
          if (el.type === 'radio' && el.value) {
            const r = tag + '[name="' + n + '"][value="' + String(el.value).replace(/"/g, '\\"') + '"]';
            if (unique(r)) return r;
          }
          const s = tag + '[name="' + n + '"]';
          if (unique(s)) return s;
        }

        const parts = [];
        let node = el;
        while (node && node.nodeType === 1 && parts.length < 10) {
          if (node.id && unique('#' + esc(node.id))) { parts.unshift('#' + esc(node.id)); break; }
          let part = node.tagName.toLowerCase();
          const parent = node.parentElement;
          if (parent) {
            const sibs = Array.prototype.filter.call(parent.children, c => c.tagName === node.tagName);
            if (sibs.length > 1) part += ':nth-of-type(' + (sibs.indexOf(node) + 1) + ')';
          }
          parts.unshift(part);
          node = node.parentElement;
        }
        return parts.join(' > ');
      };

      const clean = (s) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim().slice(0, 120);

      const labelFor = (el) => {
        if (el.id) {
          const l = document.querySelector('label[for="' + esc(el.id) + '"]');
          if (l && clean(l.innerText)) return clean(l.innerText);
        }
        const wrap = el.closest ? el.closest('label') : null;
        if (wrap && clean(wrap.innerText)) return clean(wrap.innerText);

        const aria = el.getAttribute('aria-label');
        if (clean(aria)) return clean(aria);

        if (clean(el.placeholder)) return clean(el.placeholder);
        if (clean(el.title)) return clean(el.title);

        const cell = el.closest ? el.closest('td, th') : null;
        if (cell && cell.previousElementSibling && clean(cell.previousElementSibling.innerText)) {
          return clean(cell.previousElementSibling.innerText);
        }

        return clean(el.name || el.id || el.tagName.toLowerCase());
      };

      // Fields that search as you type need their keys replayed one at a time.
      const isTypeahead = (el) => {
        const t = String(el.type || 'text').toLowerCase();
        if (t === 'search') return true;
        const role = String(el.getAttribute('role') || '').toLowerCase();
        if (role === 'combobox' || role === 'searchbox') return true;
        if (el.hasAttribute('aria-autocomplete')) return true;
        if (el.hasAttribute('list')) return true;
        if (el.getAttribute('aria-owns') || el.getAttribute('aria-controls')) return true;
        const classes = String(el.className || '') + ' ' +
                        String(el.parentElement ? el.parentElement.className : '');
        if (/select2|chosen|typeahead|tt-input|autocomplete|combobox|lookup|suggest/i.test(classes)) return true;
        return /search|lookup|find|suggest|autocomplete|typeahead/i.test(String(el.name || '') + ' ' + String(el.id || ''));
      };

      const send = (item) => {
        try { window.__ptsRecord(JSON.stringify(item)); } catch (e) {}
      };

      // ---- typing -------------------------------------------------------
      document.addEventListener('input', (e) => {
        const el = e.target;
        if (!el || !el.tagName) return;
        const tag = el.tagName.toLowerCase();
        if (tag !== 'input' && tag !== 'textarea' && !el.isContentEditable) return;

        const t = String(el.type || 'text').toLowerCase();
        if (t === 'checkbox' || t === 'radio' || t === 'hidden') return;

        send({
          kind: isTypeahead(el) ? 'type' : 'fill',
          selector: selectorFor(el),
          label: labelFor(el),
          value: el.isContentEditable ? clean(el.innerText) : String(el.value == null ? '' : el.value)
        });
      }, true);

      // ---- dropdowns, checkboxes, radios --------------------------------
      document.addEventListener('change', (e) => {
        const el = e.target;
        if (!el || !el.tagName) return;
        const tag = el.tagName.toLowerCase();

        if (tag === 'select') {
          const picked = el.selectedOptions && el.selectedOptions[0];
          send({
            kind: 'select',
            selector: selectorFor(el),
            label: labelFor(el),
            value: picked ? clean(picked.text) : String(el.value)
          });
          return;
        }

        if (tag === 'input') {
          const t = String(el.type || '').toLowerCase();
          if (t === 'checkbox') {
            send({ kind: el.checked ? 'check' : 'uncheck', selector: selectorFor(el), label: labelFor(el) });
          } else if (t === 'radio') {
            send({ kind: 'check', selector: selectorFor(el), label: labelFor(el) });
          }
        }
      }, true);

      // ---- clicks -------------------------------------------------------
      document.addEventListener('click', (e) => {
        const el = e.target;
        if (!el || !el.tagName) return;

        // Alt+click marks something to assert on rather than something to press.
        if (e.altKey) {
          send({ kind: 'assert', selector: selectorFor(el), text: clean(el.innerText || el.value) });
          return;
        }

        const tag = el.tagName.toLowerCase();
        const t = String(el.type || '').toLowerCase();

        // These report themselves through input/change; a click as well would
        // be noise.
        if (tag === 'select') return;
        if (tag === 'textarea') return;
        if (tag === 'input' && t !== 'submit' && t !== 'button' && t !== 'reset' && t !== 'image') return;
        if (el.isContentEditable) return;

        // Picking a suggestion is choosing data; pressing a button is not.
        const inResults = el.closest && el.closest(
          '[role="listbox"], [role="option"], .select2-results, .tt-menu, ' +
          'ul.ui-autocomplete, .autocomplete-items, .dropdown-menu');

        send({
          kind: 'click',
          selector: selectorFor(el),
          label: clean(el.innerText || el.value || el.getAttribute('aria-label') || el.title || tag),
          text: clean(el.innerText || el.value),
          pick: !!inResults
        });
      }, true);

      // ---- keys ---------------------------------------------------------
      document.addEventListener('keydown', (e) => {
        // Esc ends the recording, so it is never recorded as a keypress.
        if (e.key === 'Escape') { send({ kind: 'stop' }); return; }

        if (e.key !== 'Enter') return;
        const el = e.target;
        if (!el || !el.tagName) return;
        const tag = el.tagName.toLowerCase();
        if (tag !== 'input' && tag !== 'textarea') return;

        send({ kind: 'press', selector: selectorFor(el), label: labelFor(el), value: 'Enter' });
      }, true);

      // ---- a word about Esc ---------------------------------------------
      // Shown on each page so it is clear the recording is still running, then
      // gone. pointer-events:none, so it can never swallow a click meant for
      // the page underneath it.
      const hint = () => {
        if (!document.body) return;

        const box = document.createElement('div');
        box.textContent = 'Recording - press Esc to stop';
        box.setAttribute('data-pts-recorder', '1');
        box.style.cssText =
          'position:fixed;top:12px;right:12px;z-index:2147483647;' +
          'background:#c42b1c;color:#fff;border-radius:4px;padding:8px 14px;' +
          'font:600 13px "Segoe UI",system-ui,sans-serif;pointer-events:none;' +
          'box-shadow:0 2px 8px rgba(0,0,0,.35);transition:opacity .6s ease;';

        document.body.appendChild(box);
        setTimeout(() => { box.style.opacity = '0'; }, 3500);
        setTimeout(() => { box.remove(); }, 4300);
      };

      if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', hint);
      } else {
        hint();
      }
    })();
    """;
}
