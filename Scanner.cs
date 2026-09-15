using System.Text.Json;
using Microsoft.Playwright;

namespace PlaywrightTestStudio;

/// <summary>
/// Opens a link and reports every control on it: dropdowns, text fields, radio
/// buttons, checkboxes and buttons, each with a selector stable enough to drive
/// a test.
/// </summary>
internal static class Scanner
{
    public static async Task<List<ElementInfo>> ScanAsync(string url, bool headless)
    {
        await using var session = await BrowserSession.StartAsync(headless);

        await session.Page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });

        // Give client-rendered pages a moment to paint their form before the
        // DOM is read; a quiet network is a good enough signal, and a timeout
        // waiting for it is not a failure.
        try
        {
            await session.Page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
        }

        var json = await session.Page.EvaluateAsync<JsonElement>(ScanScript);

        return Parse(json);
    }

    private static List<ElementInfo> Parse(JsonElement json)
    {
        var list = new List<ElementInfo>();

        if (json.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var item in json.EnumerateArray())
        {
            var info = new ElementInfo
            {
                Kind = Str(item, "kind"),
                Tag = Str(item, "tag"),
                InputType = Str(item, "inputType"),
                Label = Str(item, "label"),
                Selector = Str(item, "selector"),
                Name = Str(item, "name"),
                Value = Str(item, "value"),
                Visible = Bool(item, "visible"),
                Required = Bool(item, "required"),
                Options = StrList(item, "options"),
                OptionValues = StrList(item, "optionValues")
            };

            if (!string.IsNullOrWhiteSpace(info.Selector))
            {
                list.Add(info);
            }
        }

        // Fields first in document order, buttons last - that is the order the
        // operator fills them in and the order the test replays them.
        var rank = new Dictionary<string, int>
        {
            ["text"] = 0,
            ["select"] = 0,
            ["radio"] = 0,
            ["checkbox"] = 0,
            ["button"] = 1
        };

        return list
            .OrderBy(e => rank.TryGetValue(e.Kind, out var r) ? r : 0)
            .ToList();
    }

    private static string Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static bool Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string> StrList(JsonElement item, string name)
    {
        var list = new List<string>();

        if (item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in v.EnumerateArray())
            {
                list.Add(e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.ToString());
            }
        }

        return list;
    }

    /// <summary>
    /// Runs inside the page and returns a plain array of control descriptors.
    /// Every selector is checked against the document before it is handed back,
    /// so a selector that reaches the C# side matched exactly one element.
    /// </summary>
    private const string ScanScript = """
    () => {
      const esc = (s) => (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/([^\w-])/g, '\\$1');

      const unique = (sel) => {
        try { return document.querySelectorAll(sel).length === 1; } catch (e) { return false; }
      };

      const selectorFor = (el) => {
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

        const by = el.getAttribute('aria-labelledby');
        if (by) {
          const t = document.getElementById(by);
          if (t && clean(t.innerText)) return clean(t.innerText);
        }

        if (clean(el.placeholder)) return clean(el.placeholder);
        if (clean(el.title)) return clean(el.title);

        // Table-shaped forms: the label is usually the cell to the left.
        const cell = el.closest ? el.closest('td, th') : null;
        if (cell && cell.previousElementSibling && clean(cell.previousElementSibling.innerText)) {
          return clean(cell.previousElementSibling.innerText);
        }

        return clean(el.name || el.id || '');
      };

      const isVisible = (el) => {
        const r = el.getBoundingClientRect();
        const st = window.getComputedStyle(el);
        return r.width > 0 && r.height > 0 && st.visibility !== 'hidden' && st.display !== 'none' && st.opacity !== '0';
      };

      const out = [];
      const seen = new Set();

      const push = (el, kind, extra) => {
        const selector = selectorFor(el);
        if (!selector) return;

        const key = kind + '|' + selector;
        if (seen.has(key)) return;
        seen.add(key);

        const item = {
          kind: kind,
          tag: el.tagName.toLowerCase(),
          inputType: String(el.type || ''),
          selector: selector,
          name: String(el.name || ''),
          value: String(el.value == null ? '' : el.value),
          label: labelFor(el),
          visible: isVisible(el),
          required: !!el.required,
          options: [],
          optionValues: []
        };

        Object.assign(item, extra || {});
        out.push(item);
      };

      document.querySelectorAll('select').forEach(el => push(el, 'select', {
        options: Array.prototype.map.call(el.options, o => clean(o.text) || String(o.value)),
        optionValues: Array.prototype.map.call(el.options, o => String(o.value))
      }));

      document.querySelectorAll('textarea').forEach(el => push(el, 'text', {}));

      document.querySelectorAll('input').forEach(el => {
        const t = String(el.type || 'text').toLowerCase();
        if (t === 'hidden') return;
        if (t === 'radio') { push(el, 'radio', {}); return; }
        if (t === 'checkbox') { push(el, 'checkbox', {}); return; }
        if (t === 'submit' || t === 'button' || t === 'reset' || t === 'image') {
          push(el, 'button', { label: clean(el.value || el.getAttribute('aria-label') || el.title || t) });
          return;
        }
        push(el, 'text', {});
      });

      document.querySelectorAll('[contenteditable="true"]').forEach(el => push(el, 'text', {}));

      document.querySelectorAll('button, [role="button"]').forEach(el => {
        if (el.tagName === 'INPUT') return;
        push(el, 'button', { label: clean(el.innerText || el.getAttribute('aria-label') || el.title || el.value) });
      });

      return out;
    }
    """;
}
