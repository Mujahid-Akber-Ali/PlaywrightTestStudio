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

        // Left alone, Playwright cancels every "Are you sure?" box the instant
        // it opens, and the operator never gets to answer it. With a handler
        // attached the box stays up for them to click. Nobody is there to click
        // when the window is hidden, so then it is accepted.
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
                    if (!StepActions.All.Contains(kind))
                    {
                        return;
                    }

                    var step = new TestStep
                    {
                        Action = kind,
                        Selector = Str(item, "selector"),
                        Label = Str(item, "label"),
                        Value = Str(item, "value"),
                        FallbackText = Str(item, "text")
                    };

                    var last = _steps.Count > 0 ? _steps[^1] : null;

                    if (kind == StepActions.Click)
                    {
                        // The page flags a click on something a search produced:
                        // it sits in a results list, or it appeared after the
                        // last keystroke.
                        var flagged = Flag(item, "pick");
                        var typedJustBefore = last is not null &&
                                              last.Action is StepActions.Type or StepActions.Fill;

                        // A click straight after typing into a lookup is picking
                        // a result, not pressing a button - and it belongs to the
                        // field that was typed into.
                        var followsSearch = typedJustBefore &&
                                            (flagged || (last!.Action == StepActions.Type && !Flag(item, "button")));

                        step.Pick = flagged || followsSearch;

                        if (followsSearch)
                        {
                            // A box whose typing produced a list to pick from is
                            // a lookup, whatever it looked like, and has to be
                            // replayed key by key for the list to show up again.
                            last!.Action = StepActions.Type;

                            if (last.Label.Length > 0)
                            {
                                step.Label = $"{last.Label} result";
                            }
                        }

                        // Picking from a list never loads a page; a button might.
                        step.WaitAfter = !step.Pick;
                    }

                    if (kind == StepActions.Press && step.Value == "Enter")
                    {
                        // Enter in a box usually submits something.
                        step.WaitAfter = true;
                    }

                    // Keystroke after keystroke in one box is one step, not ten.
                    // A box already found to be a lookup stays one.
                    var typing = step.Action is StepActions.Fill or StepActions.Type;

                    if (last is not null &&
                        last.Selector == step.Selector &&
                        (last.Action == step.Action
                            ? typing || step.Action == StepActions.Select
                            : typing && last.Action is StepActions.Fill or StepActions.Type))
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

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

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
            // A column is for data somebody entered. Which key was pressed and
            // where the page went are part of the script, not of the data.
            if (!step.TakesValue || step.Action is StepActions.Goto or StepActions.Press)
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
    /// action before the page's own handlers can stop it, and - Alt+click
    /// aside - never changes what the page does.
    ///
    /// What it leaves out matters as much as what it takes: a click on empty
    /// page, a change the page made to itself, the inner workings of a select2
    /// box. Each of those replays as a step that has nothing to find.
    ///
    /// Alt+click records an assertion on the element instead of a click.
    /// </summary>
    private const string RecorderScript = """
    (() => {
      if (window.__ptsRecorderInstalled) return;
      window.__ptsRecorderInstalled = true;

      const send = (item) => {
        try { window.__ptsRecord(JSON.stringify(item)); } catch (e) {}
      };

      // Esc ends the recording wherever the keyboard happens to be. Nothing
      // else is taken from inside an iframe: a step recorded there could not be
      // found again from the page that holds the frame.
      if (window !== window.top) {
        document.addEventListener('keydown', (e) => {
          if (e.key === 'Escape') send({ kind: 'stop' });
        }, true);
        return;
      }

      const esc = (s) => (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/([^\w-])/g, '\\$1');
      const quote = (s) => String(s).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
      const clean = (s) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim().slice(0, 120);

      const unique = (sel) => {
        try { return document.querySelectorAll(sel).length === 1; } catch (e) { return false; }
      };

      // An id a framework made up will be a different one on the next visit.
      const stableId = (id) =>
        typeof id === 'string' && id.length > 0 &&
        !/\d{4,}|^(select2-|ember\d|ext-gen|yui_|gwt-|mat-|mui-|radix-|headlessui-|react-select|:r)/i.test(id) &&
        !/(?=[0-9a-f-]*\d)[0-9a-f-]{12,}/i.test(id);

      const selectorFor = (el) => {
        if (!el || el.nodeType !== 1) return '';
        if (stableId(el.id) && unique('#' + esc(el.id))) return '#' + esc(el.id);

        const tag = el.tagName.toLowerCase();

        const byAttr = (attr) => {
          const v = el.getAttribute(attr);
          if (!v || v.length > 150) return '';
          const s = tag + '[' + attr + '="' + quote(v) + '"]';
          return unique(s) ? s : '';
        };

        for (const attr of ['data-testid', 'data-test', 'data-qa', 'data-automation-id']) {
          const s = byAttr(attr);
          if (s) return s;
        }

        const name = el.getAttribute('name');
        if (name) {
          if (el.type === 'radio' && el.value) {
            const r = tag + '[name="' + quote(name) + '"][value="' + quote(el.value) + '"]';
            if (unique(r)) return r;
          }
          const s = byAttr('name');
          if (s) return s;
        }

        // Wording and destinations outlive the layout around them.
        for (const attr of (tag === 'a' ? ['aria-label', 'title', 'href'] : ['aria-label', 'placeholder', 'title'])) {
          if (attr === 'href' && /^(#|javascript:)/i.test(el.getAttribute('href') || '#')) continue;
          const s = byAttr(attr);
          if (s) return s;
        }

        const parts = [];
        let node = el;
        while (node && node.nodeType === 1 && parts.length < 10) {
          if (stableId(node.id) && unique('#' + esc(node.id))) { parts.unshift('#' + esc(node.id)); break; }
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

      // ---- what a field is called ---------------------------------------
      const CONTROLS = 'input:not([type="hidden"]), select, textarea';

      // The label's own words, without the hint or the asterisk tucked inside.
      const labelText = (node) => {
        let own = '';
        node.childNodes.forEach(c => { if (c.nodeType === 3) own += c.textContent; });
        return (clean(own) || clean(node.innerText)).replace(/\s*[*:]+\s*$/, '').trim();
      };

      // Forms that put a bare <label> above the field without tying the two
      // together: the nearest label before the field, as long as no other field
      // sits in between to claim it.
      const nearbyLabel = (el) => {
        let node = el;
        for (let depth = 0; depth < 3 && node && node.parentElement && node !== document.body; depth++) {
          let sib = node.previousElementSibling;
          while (sib) {
            if (sib.matches('label, legend')) {
              const text = labelText(sib);
              if (text) return text;
            }
            if (sib.matches(CONTROLS) || sib.querySelector(CONTROLS)) return '';
            sib = sib.previousElementSibling;
          }
          node = node.parentElement;
        }
        return '';
      };

      const labelFor = (el) => {
        if (el.labels && el.labels.length && labelText(el.labels[0])) return labelText(el.labels[0]);

        const ids = String(el.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean);
        const named = ids.map(id => document.getElementById(id)).filter(Boolean).map(n => clean(n.innerText)).join(' ');
        if (clean(named)) return clean(named);

        const aria = el.getAttribute('aria-label');
        if (clean(aria)) return clean(aria);

        const t = String(el.type || '').toLowerCase();
        if ((t === 'checkbox' || t === 'radio') && el.nextElementSibling &&
            el.nextElementSibling.matches('label') && labelText(el.nextElementSibling)) {
          return labelText(el.nextElementSibling);
        }

        const near = nearbyLabel(el);
        if (near) return near;

        if (clean(el.placeholder)) return clean(el.placeholder);
        if (clean(el.title)) return clean(el.title);

        const cell = el.closest ? el.closest('td, th') : null;
        if (cell) {
          if (cell.previousElementSibling && !cell.previousElementSibling.querySelector(CONTROLS) &&
              clean(cell.previousElementSibling.innerText)) {
            return clean(cell.previousElementSibling.innerText).replace(/\s*[*:]+\s*$/, '');
          }
          const table = cell.closest('table');
          const head = table ? table.querySelector('thead tr, tr') : null;
          const th = head ? head.children[cell.cellIndex] : null;
          if (th && th !== cell && clean(th.innerText)) return clean(th.innerText);
        }

        return clean(el.getAttribute('name') || el.id || el.tagName.toLowerCase());
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

      // ---- who did it: the operator, or the page to itself ---------------
      // Pages fire events at their own fields while they load and whenever one
      // field resets another. Those are not something the operator did, and
      // replaying them asks for values that were never chosen.
      let lastGesture = 0;
      let lastTyped = 0;
      let lastTypedIn = null;

      for (const type of ['mousedown', 'pointerdown', 'keydown', 'touchstart']) {
        document.addEventListener(type, (e) => {
          if (!e.isTrusted) return;
          lastGesture = Date.now();
          hookJQuery();
        }, true);
      }

      const justActed = () => Date.now() - lastGesture < 1500;

      // What has been added to the page since the last keystroke. A click on
      // one of those is a click on something the typing produced: a result.
      // Only kept between a keystroke and the next click - everything on a
      // page was "added" at some point while it loaded.
      let fresh = null;

      new MutationObserver((records) => {
        if (!fresh) return;
        for (const record of records) {
          record.addedNodes.forEach(n => { if (n.nodeType === 1) fresh.add(n); });
        }
      }).observe(document, { childList: true, subtree: true });

      const isFresh = (el) => {
        if (!fresh) return false;
        let node = el;
        for (let depth = 0; depth < 6 && node && node.nodeType === 1; depth++) {
          if (fresh.has(node)) return true;
          node = node.parentElement;
        }
        return false;
      };

      // ---- select2 --------------------------------------------------------
      // It hides the real <select>, draws its own box and list, and announces
      // the choice through jQuery, where no ordinary listener hears it. The
      // clicks inside it are its own business; the step worth keeping is
      // "choose X in this dropdown", against the real <select>.
      const inSelect2 = (el) => !!(el.closest && el.closest('.select2-container'));

      // Sent there and then, not once select2 has applied the choice: steps
      // have to arrive in the order they were taken.
      const select2Chosen = (optionText) => {
        const box = document.querySelector('select + .select2-container--open');
        const select = box ? box.previousElementSibling : null;
        if (!select || select.tagName !== 'SELECT' || !optionText) return;

        send({ kind: 'select', selector: selectorFor(select), label: labelFor(select), value: optionText });
      };

      document.addEventListener('mousedown', (e) => {
        if (!e.isTrusted || e.button !== 0 || !e.target || !e.target.closest) return;
        const option = e.target.closest('.select2-results__option');
        if (option && option.getAttribute('aria-disabled') !== 'true' && option.getAttribute('role') !== 'group') {
          select2Chosen(clean(option.innerText));
        }
      }, true);

      // Other dress-up dropdowns (chosen, bootstrap-select) announce the same
      // way. jQuery may load long after this script, so look for it lazily.
      let jqueryHooked = false;

      const hookJQuery = () => {
        if (jqueryHooked || !window.jQuery || !window.jQuery.fn || !window.jQuery.fn.on) return;
        jqueryHooked = true;

        window.jQuery(document).on('change', 'select', (e) => {
          const el = e.target;
          if (e.originalEvent || !justActed()) return;          // native ones are heard below
          if (el.classList.contains('select2-hidden-accessible')) return;
          const picked = el.selectedOptions && el.selectedOptions[0];
          const value = picked ? clean(picked.text) : '';
          if (!value || !el.value) return;
          send({ kind: 'select', selector: selectorFor(el), label: labelFor(el), value: value });
        });
      };

      // ---- typing -------------------------------------------------------
      document.addEventListener('input', (e) => {
        const el = e.target;
        if (!el || !el.tagName || inSelect2(el)) return;

        // A calendar writing the chosen date into its box is worth a step. A
        // lookup writing the picked item back into the box that was just typed
        // in is not: the pick already says it.
        if (!e.isTrusted && (!justActed() || el === lastTypedIn)) return;

        const tag = el.tagName.toLowerCase();
        if (tag !== 'input' && tag !== 'textarea' && !el.isContentEditable) return;

        const t = String(el.type || 'text').toLowerCase();
        if (t === 'checkbox' || t === 'radio' || t === 'hidden' || t === 'file') return;

        if (e.isTrusted) {
          fresh = new WeakSet();
          lastTyped = Date.now();
          lastTypedIn = el;
        }

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
          const value = picked ? clean(picked.text) : String(el.value);

          // A dropdown the page changed by itself is only worth a step when it
          // answers something the operator just did and actually chose
          // something. Putting a dependent dropdown back to "Please select"
          // is the page tidying up, not a choice.
          if (!e.isTrusted && (!justActed() || !value || !el.value)) return;

          send({ kind: 'select', selector: selectorFor(el), label: labelFor(el), value: value });
          return;
        }

        if (tag === 'input' && e.isTrusted) {
          const t = String(el.type || '').toLowerCase();
          if (t === 'checkbox') {
            send({ kind: el.checked ? 'check' : 'uncheck', selector: selectorFor(el), label: labelFor(el) });
          } else if (t === 'radio') {
            send({ kind: 'check', selector: selectorFor(el), label: labelFor(el) });
          }
        }
      }, true);

      // ---- clicks -------------------------------------------------------
      const CLICKABLE =
        'a, button, summary, label, [role="button"], [role="link"], [role="option"], [role="menuitem"], ' +
        '[role="tab"], [role="checkbox"], [role="radio"], [role="switch"], [role="treeitem"], [onclick], ' +
        'input[type="submit"], input[type="button"], input[type="reset"], input[type="image"]';

      const RESULTS =
        '[role="listbox"], [role="option"], .tt-menu, ul.ui-autocomplete, .autocomplete-items, ' +
        '.autocomplete-suggestions, .typeahead';

      // A click that landed on the page rather than on anything in it - to
      // close a list, to take the cursor out of a box. Replayed, it would hit
      // whatever happens to sit in the middle of that area.
      const stray = (el) =>
        el === document.body || el === document.documentElement ||
        el.matches('form, main, section, article, header, footer, nav, fieldset, table, tbody, tr, p, h1, h2, h3, h4, h5, h6') ||
        !!el.querySelector('input, select, textarea, button, a') ||
        (clean(el.innerText).length === 0 && !el.matches('img, svg, svg *, canvas, i')) ||
        String(el.innerText || '').trim().length > 80;

      document.addEventListener('click', (e) => {
        const el = e.target;
        if (!e.isTrusted || !el || !el.tagName || !el.closest) return;

        // Alt+click marks something to assert on rather than something to
        // press, so the page does not get to act on it.
        if (e.altKey) {
          e.preventDefault();
          e.stopPropagation();
          send({ kind: 'assert', selector: selectorFor(el), text: clean(el.innerText || el.value) });
          return;
        }

        if (inSelect2(el)) return;

        const tag = el.tagName.toLowerCase();
        const t = String(el.type || '').toLowerCase();

        // These report themselves through input/change; a click as well would
        // be noise.
        if (tag === 'select' || tag === 'option' || tag === 'textarea') return;
        if (tag === 'input' && t !== 'submit' && t !== 'button' && t !== 'reset' && t !== 'image') return;
        if (el.isContentEditable) return;

        let target = el.closest(CLICKABLE);

        // A label passes the click to its field, and the field reports it.
        if (target && target.tagName === 'LABEL') return;

        const inResults = !!el.closest(RESULTS) ||
                          (!!el.closest('.dropdown-menu') && Date.now() - lastTyped < 15000);
        const appeared = isFresh(el);
        fresh = null;

        if (!target) {
          const handCursor = getComputedStyle(el).cursor === 'pointer';
          if (!handCursor && !inResults && !appeared && stray(el)) return;
          target = el;
        }

        const text = clean(target.innerText || target.value);
        const isButton = target.matches(
          'button, [type="submit"], input[type="button"], input[type="reset"], [role="button"]');

        send({
          kind: 'click',
          selector: selectorFor(target),
          label: text.slice(0, 60) ||
                 clean(target.getAttribute('aria-label') || target.title || target.getAttribute('alt') ||
                       target.tagName.toLowerCase()),
          text: text,
          // Picking a suggestion is choosing data; pressing a button is not.
          pick: inResults || (appeared && !isButton),
          button: isButton
        });
      }, true);

      // ---- keys ---------------------------------------------------------
      document.addEventListener('keydown', (e) => {
        // Esc ends the recording, so it is never recorded as a keypress.
        if (e.key === 'Escape') { send({ kind: 'stop' }); return; }

        const el = e.target;
        if (!e.isTrusted || !el || !el.tagName) return;

        if (inSelect2(el)) {
          if (e.key === 'Enter') {
            const lit = document.querySelector('.select2-results__option--highlighted');
            if (lit) select2Chosen(clean(lit.innerText));
          }
          return;
        }

        // Enter submits or accepts a suggestion; the arrows walk a suggestion
        // list. Inside a textarea Enter is just part of the text.
        if (el.tagName.toLowerCase() !== 'input') return;

        const t = String(el.type || 'text').toLowerCase();
        const arrows = (e.key === 'ArrowDown' || e.key === 'ArrowUp') &&
                       (t === 'text' || t === 'search' || t === 'email' || t === 'tel' || t === 'url');

        if (e.key !== 'Enter' && !arrows) return;

        send({ kind: 'press', selector: selectorFor(el), label: labelFor(el), value: e.key });
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
