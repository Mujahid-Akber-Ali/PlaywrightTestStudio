using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace PlaywrightTestStudio;

/// <summary>
/// Replays a test case with Playwright and decides pass or fail. A case runs
/// once per enabled data set, so one recorded session covers as many rows as
/// somebody types into the Excel sheet.
///
/// Nothing here waits for a fixed length of time when it can wait for the thing
/// it needs instead: the next field, the suggestion that was asked for, the
/// option a dropdown is still loading.
/// </summary>
internal static class Runner
{
    /// <summary>How long a step may look for its element before the run gives up on it.</summary>
    private const float StepTimeout = 15_000;

    /// <summary>How long a check keeps looking before it counts as failed.</summary>
    private const float CheckTimeout = 5_000;

    /// <summary>A step that could not be carried out, said in words an operator can act on.</summary>
    private sealed class StepException : Exception
    {
        public StepException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Runs every enabled data set and returns one result each. The summary is
    /// written onto the test case: Passed only when every row passed.
    ///
    /// Pass a <paramref name="browser"/> to share one window across several
    /// test cases; without one, the rows of this case still share a window.
    /// </summary>
    public static async Task<List<TestResult>> RunAllDataSetsAsync(
        TestCase test,
        ProjectStore store,
        bool headless,
        Action<string>? progress = null,
        RunBrowser? browser = null)
    {
        var sets = test.RunnableDataSets();
        var results = new List<TestResult>();
        var watch = Stopwatch.StartNew();
        var ownBrowser = browser is null;

        browser ??= new RunBrowser(headless);

        try
        {
            for (var i = 0; i < sets.Count; i++)
            {
                var set = sets[i];
                var position = i + 1;

                var result = await RunAsync(test, store, browser, set,
                    line => progress?.Invoke(sets.Count > 1
                        ? $"[data set {position}/{sets.Count}: {set.Name}] {line}"
                        : line));

                results.Add(result);
            }
        }
        finally
        {
            if (ownBrowser)
            {
                await browser.DisposeAsync();
            }
        }

        watch.Stop();

        var failed = results.Count(r => !r.IsPass);

        test.LastResults = results;
        test.LastResult = new TestResult
        {
            Status = failed == 0 ? "Passed" : results.Any(r => r.Status == "Error") ? "Error" : "Failed",
            DataSet = sets.Count == 1 ? sets[0].Name : $"{sets.Count} data sets",
            RanUtc = DateTime.UtcNow,
            Seconds = Math.Round(watch.Elapsed.TotalSeconds, 2),
            ScreenshotPath = results.FirstOrDefault(r => !r.IsPass)?.ScreenshotPath
                             ?? results.FirstOrDefault()?.ScreenshotPath ?? "",
            Message = failed == 0
                ? $"All {results.Count} data set(s) passed."
                : $"{failed} of {results.Count} data set(s) failed: " +
                  string.Join("; ", results.Where(r => !r.IsPass).Select(r => $"{r.DataSet} - {r.Message}")),
            Assertions = results.SelectMany(r => r.Assertions).ToList()
        };

        return results;
    }

    public static async Task<TestResult> RunAsync(
        TestCase test,
        ProjectStore store,
        RunBrowser browser,
        DataSet data,
        Action<string>? progress = null)
    {
        var watch = Stopwatch.StartNew();

        var result = new TestResult
        {
            RanUtc = DateTime.UtcNow,
            DataSet = data.Name,
            Status = "Error"
        };

        IPage? page = null;

        // Kept up to date so a failure can say where the run had got to.
        var doing = "Starting the browser";

        try
        {
            progress?.Invoke("Starting browser...");
            page = await browser.NewPageAsync();

            // An "Are you sure?" box is answered OK, as it was when recording.
            // Left to itself Playwright cancels it, and the action it guards
            // never happens.
            page.Dialog += (_, dialog) => _ = dialog.AcceptAsync();

            doing = $"Opening {test.Url}";
            progress?.Invoke(doing);
            await page.GotoAsync(test.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            var steps = test.Steps.Where(s => s.Use).ToList();

            for (var i = 0; i < steps.Count; i++)
            {
                var action = steps[i];
                var value = Resolve(action.Parameter, action.RecordedValue, data);

                doing = $"Step {i + 1} of {steps.Count} ({Describe(action, value)})";
                progress?.Invoke($"Step {i + 1}/{steps.Count}: {Describe(action, value)}");

                await ApplyAsync(page, action, value, i + 1 < steps.Count ? steps[i + 1] : null);

                if (action.WaitAfter)
                {
                    await AfterActionAsync(page);
                }
            }

            doing = "Checking the result";
            progress?.Invoke("Checking assertions...");
            await SettleAsync(page);

            foreach (var assertion in test.Assertions)
            {
                var expected = Resolve(assertion.Parameter, assertion.Expected, data);
                result.Assertions.Add(await CheckAsync(page, assertion, expected));
            }

            result.ScreenshotPath = await CaptureAsync(page, store, test, data);

            var failed = result.Assertions.Count(a => !a.Passed);

            if (test.Assertions.Count == 0)
            {
                result.Status = "Failed";
                result.Message = "No assertions defined, so there is nothing to prove.";
            }
            else if (failed == 0)
            {
                result.Status = "Passed";
                result.Message = $"All {result.Assertions.Count} assertion(s) passed.";
            }
            else
            {
                result.Status = "Failed";
                result.Message = $"{failed} of {result.Assertions.Count} assertion(s) failed: " +
                                 string.Join("; ", result.Assertions.Where(a => !a.Passed).Select(a => a.Description));
            }
        }
        catch (Exception ex)
        {
            result.Status = "Error";
            result.Message = $"{doing} failed: {Reason(ex)}";

            if (page is not null && !page.IsClosed)
            {
                // A screenshot of wherever the run stopped is usually the
                // fastest way to see why a step missed.
                result.ScreenshotPath = await CaptureAsync(page, store, test, data);
            }
        }
        finally
        {
            watch.Stop();
            result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
        }

        return result;
    }

    /// <summary>The first line of an error - Playwright follows it with a call log nobody needs in a report.</summary>
    private static string Reason(Exception ex)
    {
        var text = ex is StepException ? ex.Message : BrowserSession.Explain(ex);
        var cut = text.IndexOf("Call log:", StringComparison.OrdinalIgnoreCase);

        return (cut > 0 ? text[..cut] : text).Trim();
    }

    /// <summary>
    /// The value a step or assertion uses this run: the data set's column when
    /// the step names one and the row has it, otherwise what was recorded.
    /// </summary>
    public static string Resolve(string parameter, string recorded, DataSet data)
    {
        if (parameter.Length > 0 && data.Values.TryGetValue(parameter, out var supplied))
        {
            return supplied;
        }

        return recorded;
    }

    public static string Describe(TestStep step, string value) => step.Action switch
    {
        StepActions.Goto => $"go to {value}",
        StepActions.Fill => $"fill \"{step.Display}\" with \"{value}\"",
        StepActions.Type => $"search \"{step.Display}\" for \"{value}\"",
        StepActions.Select => $"choose \"{value}\" in \"{step.Display}\"",
        StepActions.Check => $"tick \"{step.Display}\"",
        StepActions.Uncheck => $"untick \"{step.Display}\"",
        StepActions.Click when step.Pick && value.Trim().Length > 0 => $"pick \"{value}\"",
        StepActions.Click => $"click \"{step.Display}\"",
        StepActions.Press => $"press {value} in \"{step.Display}\"",
        _ => $"{step.Action} {step.Display}"
    };

    private static async Task ApplyAsync(IPage page, TestStep step, string value, TestStep? next)
    {
        switch (step.Action)
        {
            case StepActions.Goto:
                await page.GotoAsync(value, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                break;

            case StepActions.Type:
                await TypeAsync(page, step, value, next);
                break;

            case StepActions.Select:
                await SelectOptionAsync(page, step, value);
                break;

            case StepActions.Check:
                await SetCheckedAsync(page, step, wanted: true);
                break;

            case StepActions.Uncheck:
                await SetCheckedAsync(page, step, wanted: false);
                break;

            case StepActions.Click:
                await ClickAsync(page, step, value);
                break;

            case StepActions.Press:
                await (await FindAsync(page, step)).PressAsync(value.Length > 0 ? value : "Enter",
                    new LocatorPressOptions { Timeout = StepTimeout });
                break;

            default:
                await FillAsync(page, step, value);
                break;
        }
    }

    /// <summary>
    /// The element a step acts on, once it is on the page. Says which field was
    /// missing rather than leaving a bare timeout to be decoded.
    /// </summary>
    private static async Task<ILocator> FindAsync(IPage page, TestStep step, bool visible = true)
    {
        var locator = page.Locator(step.Selector).First;

        try
        {
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = visible ? WaitForSelectorState.Visible : WaitForSelectorState.Attached,
                Timeout = StepTimeout
            });
        }
        catch (TimeoutException)
        {
            throw new StepException(
                $"\"{step.Display}\" did not show up on the page within {StepTimeout / 1000:0} seconds " +
                $"(looked for {step.Selector}). The page may have changed since this was recorded, " +
                "or an earlier step did not have the effect it had then.");
        }

        return locator;
    }

    private static async Task FillAsync(IPage page, TestStep step, string value)
    {
        var input = await FindAsync(page, step);

        // A date box that only its own calendar writes to cannot be typed in.
        // Its value is set directly, with the events the page listens for.
        if (!await input.IsEditableAsync())
        {
            await input.EvaluateAsync("""
            (el, value) => {
              const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
              const setter = Object.getOwnPropertyDescriptor(proto, 'value');
              if (setter && setter.set) { setter.set.call(el, value); } else { el.value = value; }
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
              if (window.jQuery) { window.jQuery(el).trigger('change'); }
            }
            """, value);
            return;
        }

        await input.FillAsync(value, new LocatorFillOptions { Timeout = StepTimeout });
    }

    /// <summary>
    /// Ticks or unticks a box. Styled checkboxes often hide the real input
    /// behind a drawing of one; that cannot be clicked, but it can be asked to
    /// click itself, which runs the page's handlers all the same.
    /// </summary>
    private static async Task SetCheckedAsync(IPage page, TestStep step, bool wanted)
    {
        var box = await FindAsync(page, step, visible: false);

        if (await box.IsVisibleAsync())
        {
            try
            {
                await box.SetCheckedAsync(wanted, new LocatorSetCheckedOptions { Timeout = 5_000 });
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                // Covered by its own label, most likely. Fall through.
            }
        }

        await box.EvaluateAsync("(el, wanted) => { if (el.checked !== wanted) { el.click(); } }", wanted);
    }

    /// <summary>
    /// Finds what a click step is aiming at.
    ///
    /// A pick - choosing an item from a list of results - goes by the wording
    /// of the item, because that is what the data row names and the list is
    /// built afresh every time it opens. The recorded position in the list is
    /// only a last resort, and only for the item that was recorded.
    ///
    /// A button goes by its selector, with its wording to fall back on.
    /// </summary>
    private static async Task ClickAsync(IPage page, TestStep step, string text)
    {
        var wanted = text.Trim();
        var bySelector = step.Selector.Trim().Length > 0 ? page.Locator(step.Selector) : null;
        ILocator? exactly = null;
        ILocator? roughly = null;

        if (wanted.Length > 0)
        {
            var words = wanted.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);

            exactly = page.GetByText(new Regex($"^\\s*{string.Join("\\s+", words)}\\s*$", RegexOptions.IgnoreCase));
            roughly = page.GetByText(wanted);
        }

        var recordedItem = string.Equals(wanted, step.FallbackText.Trim(), StringComparison.OrdinalIgnoreCase);
        var watch = Stopwatch.StartNew();
        ILocator? target = null;

        while (target is null)
        {
            var waited = watch.ElapsedMilliseconds;

            if (step.Pick && wanted.Length > 0)
            {
                // The exact wording gets a head start: "Bolt M8" must not
                // settle for "Bolt M80" while the list is still filling.
                target = await VisibleAsync(exactly)
                         ?? (waited > 1_000 ? await VisibleAsync(roughly) : null)
                         ?? (waited > 1_000 && recordedItem ? await VisibleAsync(bySelector) : null);
            }
            else
            {
                target = await VisibleAsync(bySelector)
                         ?? (waited > 1_500 ? await VisibleAsync(exactly) ?? await VisibleAsync(roughly) : null);
            }

            if (target is null)
            {
                if (waited > StepTimeout)
                {
                    throw new StepException(step.Pick && wanted.Length > 0
                        ? $"\"{wanted}\" was not offered to pick within {StepTimeout / 1000:0} seconds. " +
                          "Check the search text before it brings that item up."
                        : $"\"{step.Display}\" did not show up on the page within {StepTimeout / 1000:0} seconds " +
                          $"(looked for {step.Selector}).");
                }

                await Task.Delay(100);
            }
        }

        await target.ClickAsync(new LocatorClickOptions { Timeout = StepTimeout });
    }

    /// <summary>The first visible match, or null. A selector the page cannot parse counts as no match.</summary>
    private static async Task<ILocator?> VisibleAsync(ILocator? locator)
    {
        if (locator is null)
        {
            return null;
        }

        try
        {
            var shown = locator.Filter(new LocatorFilterOptions { Visible = true }).First;

            return await shown.CountAsync() > 0 ? shown : null;
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    /// <summary>
    /// Types into a lookup one key at a time, because those widgets search on
    /// key events and ignore a value that is simply assigned.
    /// </summary>
    private static async Task TypeAsync(IPage page, TestStep step, string value, TestStep? next)
    {
        var input = await FindAsync(page, step);

        await input.ClickAsync(new LocatorClickOptions { Timeout = StepTimeout });

        try
        {
            await input.FillAsync("", new LocatorFillOptions { Timeout = 5_000 });
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            // Some widgets refuse an empty fill; typing over the top is fine.
        }

        await input.PressSequentiallyAsync(value, new LocatorPressSequentiallyOptions { Delay = 40 });

        // A pick waits for its own item to turn up, however long the search
        // takes. Anything else gets a moment for the search to answer.
        if (next is not { Action: StepActions.Click, Pick: true })
        {
            await page.WaitForTimeoutAsync(500);
        }
    }

    /// <summary>What the page says about a dropdown and the option being asked for.</summary>
    private sealed class OptionProbe
    {
        public bool IsSelect { get; set; }
        public int Index { get; set; } = -1;
        public bool Select2 { get; set; }
        public List<string> Options { get; set; } = new();
    }

    /// <summary>
    /// Chooses an option by its wording. Waits for the option to exist, because
    /// a dropdown that depends on an earlier choice is filled a moment after
    /// that choice is made.
    /// </summary>
    private static async Task SelectOptionAsync(IPage page, TestStep step, string wanted)
    {
        // Attached, not visible: select2 and its kind hide the real <select>.
        var select = await FindAsync(page, step, visible: false);
        var watch = Stopwatch.StartNew();

        while (true)
        {
            var probe = JsonSerializer.Deserialize<OptionProbe>(
                await select.EvaluateAsync<string>(ProbeOptionScript, wanted), Storage.Json) ?? new OptionProbe();

            if (!probe.IsSelect)
            {
                throw new StepException($"\"{step.Display}\" is not a dropdown any more ({step.Selector}).");
            }

            if (probe.Index >= 0)
            {
                if (await select.IsVisibleAsync())
                {
                    await select.SelectOptionAsync(new SelectOptionValue { Index = probe.Index },
                        new LocatorSelectOptionOptions { Timeout = StepTimeout });
                }
                else
                {
                    await select.EvaluateAsync(SetOptionScript, probe.Index);
                }

                return;
            }

            // Nothing was chosen when this was recorded, and there is no blank
            // option to go back to: there is nothing to do.
            if (wanted.Trim().Length == 0)
            {
                return;
            }

            // A select2 that fetches its options only has them once it has
            // been opened and searched, so go through its own box. One with no
            // options at all is certainly that kind; one with some may just be
            // waiting on an earlier choice, and gets a moment first.
            if (probe.Select2 && (probe.Options.Count == 0 || watch.ElapsedMilliseconds > 1_500))
            {
                await PickInSelect2Async(page, select, step, wanted);
                return;
            }

            if (watch.ElapsedMilliseconds > StepTimeout)
            {
                var offered = probe.Options.Count == 0
                    ? "It has no options at all."
                    : "It offers: " + string.Join(" | ", probe.Options.Take(30)) +
                      (probe.Options.Count > 30 ? " ..." : "");

                throw new StepException($"\"{wanted}\" is not a choice in \"{step.Display}\". {offered}");
            }

            await Task.Delay(150);
        }
    }

    private const string ProbeOptionScript = """
    (el, wanted) => {
      if (el.tagName !== 'SELECT') return JSON.stringify({ isSelect: false });

      const tidy = (s) => String(s == null ? '' : s).replace(/\s+/g, ' ').trim().toLowerCase();
      const text = tidy(wanted);
      const options = Array.prototype.slice.call(el.options);

      let index = options.findIndex(o => tidy(o.text) === text);
      if (index < 0) index = options.findIndex(o => tidy(o.value) === text);
      if (index < 0 && text.length > 0) index = options.findIndex(o => tidy(o.text).indexOf(text) >= 0);

      return JSON.stringify({
        isSelect: true,
        index: index,
        select2: el.classList.contains('select2-hidden-accessible'),
        options: options.map(o => String(o.text).replace(/\s+/g, ' ').trim()).filter(t => t.length > 0)
      });
    }
    """;

    private const string SetOptionScript = """
    (el, index) => {
      if (el.multiple) { el.options[index].selected = true; } else { el.selectedIndex = index; }

      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));

      // Enhanced widgets redraw on a jQuery change, which the two above are not.
      if (window.jQuery) { window.jQuery(el).trigger('change'); }
    }
    """;

    /// <summary>Opens a select2 box, searches it if it has a search field, and clicks the option.</summary>
    private static async Task PickInSelect2Async(IPage page, ILocator select, TestStep step, string wanted)
    {
        var box = select.Locator("xpath=following-sibling::*[contains(@class,'select2-container')][1]");

        await box.Locator(".select2-selection").First.ClickAsync(new LocatorClickOptions { Timeout = StepTimeout });

        var search = page.Locator(".select2-container--open .select2-search__field").Last;

        if (await search.IsVisibleAsync())
        {
            await search.PressSequentiallyAsync(wanted, new LocatorPressSequentiallyOptions { Delay = 40 });
        }

        var option = page.Locator(".select2-results__option").Filter(new LocatorFilterOptions { HasText = wanted }).First;

        try
        {
            await option.ClickAsync(new LocatorClickOptions { Timeout = StepTimeout });
        }
        catch (TimeoutException)
        {
            throw new StepException($"\"{wanted}\" is not a choice in \"{step.Display}\".");
        }
    }

    /// <summary>
    /// Opens the link in a visible window and returns when the operator closes
    /// it. Used to sign in once; the cookies stay in the shared profile.
    /// </summary>
    public static async Task OpenForLoginAsync(string url)
    {
        await using var session = await BrowserSession.StartAsync(headless: false);

        var closed = new TaskCompletionSource();
        session.Context.Close += (_, _) => closed.TrySetResult();

        try
        {
            await session.Page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            });
        }
        catch (Exception)
        {
            // Even if the link fails to load, leave the window open so the
            // operator can navigate to the real sign-in page by hand.
        }

        await closed.Task;
    }

    /// <summary>
    /// After a click that may have gone somewhere: long enough for a page load
    /// to begin, then until the new page is there. A click that went nowhere
    /// costs a fraction of a second, not a wait for the network to fall quiet.
    /// </summary>
    private static async Task AfterActionAsync(IPage page)
    {
        await page.WaitForTimeoutAsync(150);

        try
        {
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded,
                new PageWaitForLoadStateOptions { Timeout = StepTimeout });
        }
        catch (TimeoutException)
        {
            // The next step waits for its own element anyway.
        }
    }

    /// <summary>Once per run, before the checks: let whatever the last step set off finish.</summary>
    private static async Task SettleAsync(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.Load,
                new PageWaitForLoadStateOptions { Timeout = StepTimeout });

            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 3_000 });
        }
        catch (TimeoutException)
        {
            // A page that keeps polling never goes idle. Carrying on is correct.
        }
    }

    public static bool IsTruthy(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "checked" or "on";

    /// <summary>
    /// Keeps asking until the answer is yes or time runs out, so a check does
    /// not fail just because the page had not finished drawing.
    /// </summary>
    private static async Task<bool> EventuallyAsync(Func<Task<bool>> check)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            if (await check())
            {
                return true;
            }

            if (watch.ElapsedMilliseconds >= CheckTimeout)
            {
                return false;
            }

            await Task.Delay(200);
        }
    }

    private static async Task<AssertionResult> CheckAsync(IPage page, AssertionSpec spec, string expected)
    {
        var outcome = new AssertionResult { Description = Describe(spec, expected) };

        try
        {
            var element = AssertionKinds.NeedsSelector(spec.Kind) ? page.Locator(spec.Selector).First : null;
            var wait = new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = CheckTimeout };

            switch (spec.Kind)
            {
                case AssertionKinds.UrlContains:
                    outcome.Passed = await EventuallyAsync(() => Task.FromResult(Contains(page.Url, expected)));
                    outcome.Actual = page.Url;
                    break;

                case AssertionKinds.TitleContains:
                    outcome.Passed = await EventuallyAsync(async () =>
                        Contains(outcome.Actual = await page.TitleAsync(), expected));
                    break;

                case AssertionKinds.PageTextContains:
                case AssertionKinds.PageTextNotContains:
                {
                    var text = "";

                    async Task<bool> Present() => Contains(
                        text = await page.EvaluateAsync<string>("() => document.body ? document.body.innerText : ''") ?? "",
                        expected);

                    // Waiting can show that text arrived; it cannot show that
                    // text never will, so absence is judged on the settled page.
                    var hit = spec.Kind == AssertionKinds.PageTextContains
                        ? await EventuallyAsync(Present)
                        : await Present();

                    outcome.Actual = Excerpt(text, expected);
                    outcome.Passed = spec.Kind == AssertionKinds.PageTextContains ? hit : !hit;
                    break;
                }

                case AssertionKinds.ElementVisible:
                case AssertionKinds.ElementNotVisible:
                {
                    // Waiting rather than sampling: a control that appears a
                    // moment after the click should still count as visible,
                    // and one on its way out as gone.
                    var wantVisible = spec.Kind == AssertionKinds.ElementVisible;
                    var reached = true;

                    try
                    {
                        await element!.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = wantVisible ? WaitForSelectorState.Visible : WaitForSelectorState.Hidden,
                            Timeout = CheckTimeout
                        });
                    }
                    catch (TimeoutException)
                    {
                        reached = false;
                    }

                    outcome.Actual = reached == wantVisible ? "visible" : "not visible";
                    outcome.Passed = reached;
                    break;
                }

                case AssertionKinds.ElementTextContains:
                    await element!.WaitForAsync(wait);
                    outcome.Passed = await EventuallyAsync(async () =>
                        Contains(outcome.Actual = await element.InnerTextAsync() ?? "", expected));
                    break;

                case AssertionKinds.ElementValueEquals:
                    await element!.WaitForAsync(wait);
                    outcome.Passed = await EventuallyAsync(async () => string.Equals(
                        (outcome.Actual = await element.InputValueAsync() ?? "").Trim(), expected.Trim(),
                        StringComparison.OrdinalIgnoreCase));
                    break;

                case AssertionKinds.ElementEnabled:
                    await element!.WaitForAsync(wait);
                    outcome.Passed = await EventuallyAsync(() => element.IsEnabledAsync());
                    outcome.Actual = outcome.Passed ? "enabled" : "disabled";
                    break;

                case AssertionKinds.ElementChecked:
                    await element!.WaitForAsync(wait);
                    outcome.Passed = await EventuallyAsync(() => element.IsCheckedAsync());
                    outcome.Actual = outcome.Passed ? "checked" : "not checked";
                    break;

                default:
                    outcome.Passed = false;
                    outcome.Actual = $"Unknown assertion kind \"{spec.Kind}\"";
                    break;
            }
        }
        catch (Exception ex)
        {
            // A missing element is a failed assertion, not a broken run - except
            // for "is NOT visible", where not finding it is the point.
            if (spec.Kind == AssertionKinds.ElementNotVisible)
            {
                outcome.Passed = true;
                outcome.Actual = "not present";
            }
            else
            {
                outcome.Passed = false;
                outcome.Actual = ex is TimeoutException
                    ? $"not found on the page ({spec.Selector})"
                    : ex.Message.Split('\n')[0];
            }
        }

        return outcome;
    }

    public static string Describe(AssertionSpec spec, string? expected = null)
    {
        var target = AssertionKinds.NeedsSelector(spec.Kind) ? $" [{spec.Selector}]" : "";
        var wanted = expected ?? spec.Expected;
        var value = AssertionKinds.NeedsExpected(spec.Kind) ? $" \"{wanted}\"" : "";

        return $"{spec.Kind}{target}{value}";
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(needle) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps the reported "actual" readable when the page text is huge.</summary>
    private static string Excerpt(string text, string needle)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        var at = needle.Length > 0 ? flat.IndexOf(needle, StringComparison.OrdinalIgnoreCase) : -1;

        if (at < 0)
        {
            return flat.Length <= 200 ? flat : flat[..200] + "...";
        }

        var start = Math.Max(0, at - 60);
        var length = Math.Min(flat.Length - start, needle.Length + 120);

        return "..." + flat.Substring(start, length) + "...";
    }

    private static async Task<string> CaptureAsync(IPage page, ProjectStore store, TestCase test, DataSet data)
    {
        try
        {
            Directory.CreateDirectory(store.ScreenshotsDir);

            var suffix = data.Name.Length > 0 ? "_" + Storage.Slug(data.Name) : "";

            var file = Path.Combine(store.ScreenshotsDir,
                $"{Storage.Slug(test.Name)}{suffix}_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, FullPage = true });

            return file;
        }
        catch (Exception)
        {
            return "";
        }
    }
}
