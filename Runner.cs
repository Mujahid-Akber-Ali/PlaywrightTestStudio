using System.Diagnostics;
using Microsoft.Playwright;

namespace PlaywrightTestStudio;

/// <summary>
/// Replays a test case with Playwright and decides pass or fail. A case runs
/// once per enabled data set, so one recorded session covers as many rows as
/// somebody types into the Excel sheet.
/// </summary>
internal static class Runner
{
    /// <summary>
    /// Runs every enabled data set and returns one result each. The summary is
    /// written onto the test case: Passed only when every row passed.
    /// </summary>
    public static async Task<List<TestResult>> RunAllDataSetsAsync(
        TestCase test,
        ProjectStore store,
        bool headless,
        Action<string>? progress = null)
    {
        var sets = test.RunnableDataSets();
        var results = new List<TestResult>();
        var watch = Stopwatch.StartNew();

        for (var i = 0; i < sets.Count; i++)
        {
            var set = sets[i];
            var position = i + 1;

            var result = await RunAsync(test, store, headless, set,
                line => progress?.Invoke(sets.Count > 1
                    ? $"[data set {position}/{sets.Count}: {set.Name}] {line}"
                    : line));

            results.Add(result);
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
        bool headless,
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

        BrowserSession? session = null;

        try
        {
            progress?.Invoke("Starting browser...");
            session = await BrowserSession.StartAsync(headless);

            var page = session.Page;

            progress?.Invoke($"Opening {test.Url}");
            await page.GotoAsync(test.Url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await SettleAsync(page);

            var step = 0;

            foreach (var action in test.Steps.Where(s => s.Use))
            {
                step++;
                var value = Resolve(action.Parameter, action.RecordedValue, data);

                progress?.Invoke($"Step {step}: {Describe(action, value)}");
                await ApplyAsync(page, action, value);

                if (action.WaitAfter)
                {
                    await SettleAsync(page);
                }
            }

            progress?.Invoke("Checking assertions...");

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
            result.Message = BrowserSession.Explain(ex);

            if (session is not null)
            {
                // A screenshot of wherever the run stopped is usually the
                // fastest way to see why a step missed.
                result.ScreenshotPath = await CaptureAsync(session.Page, store, test, data);
            }
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            watch.Stop();
            result.Seconds = Math.Round(watch.Elapsed.TotalSeconds, 2);
        }

        return result;
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
        StepActions.Click => $"click \"{step.Display}\"",
        StepActions.Press => $"press {value} in \"{step.Display}\"",
        _ => $"{step.Action} {step.Display}"
    };

    private static async Task ApplyAsync(IPage page, TestStep step, string value)
    {
        switch (step.Action)
        {
            case StepActions.Goto:
                await page.GotoAsync(value, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
                break;

            case StepActions.Type:
                await TypeAsync(page, step, value);
                break;

            case StepActions.Select:
                await SelectOptionAsync(page, step, value);
                break;

            case StepActions.Check:
                await page.Locator(step.Selector).First.CheckAsync();
                break;

            case StepActions.Uncheck:
                await page.Locator(step.Selector).First.UncheckAsync();
                break;

            case StepActions.Click:
                await ClickAsync(page, step, value);
                break;

            case StepActions.Press:
                await page.Locator(step.Selector).First.PressAsync(value.Length > 0 ? value : "Enter");
                break;

            default:
                await page.Locator(step.Selector).First.FillAsync(value);
                break;
        }
    }

    /// <summary>
    /// Clicks by selector, falling back to the text the element showed when it
    /// was recorded. Suggestion lists are rebuilt each time they open, so their
    /// structural selectors go stale while their wording does not.
    /// </summary>
    private static async Task ClickAsync(IPage page, TestStep step, string fallbackText)
    {
        try
        {
            await page.Locator(step.Selector).First.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
            return;
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            if (fallbackText.Trim().Length == 0)
            {
                throw;
            }
        }

        await page.GetByText(fallbackText.Trim()).First
            .ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
    }

    /// <summary>
    /// Types into a lookup one key at a time, because those widgets search on
    /// key events and ignore a value that is simply assigned.
    /// </summary>
    private static async Task TypeAsync(IPage page, TestStep step, string value)
    {
        var input = page.Locator(step.Selector).First;

        await input.ClickAsync();

        try
        {
            await input.FillAsync("");
        }
        catch (PlaywrightException)
        {
            // Some widgets refuse an empty fill; typing over the top is fine.
        }

        await input.PressSequentiallyAsync(value, new LocatorPressSequentiallyOptions { Delay = 60 });

        // Give the search a moment to answer before the next step tries to click
        // whatever it produced.
        await page.WaitForTimeoutAsync(600);
    }

    private static async Task SelectOptionAsync(IPage page, TestStep step, string value)
    {
        var locator = page.Locator(step.Selector).First;

        // Widgets like select2 and chosen hide the real <select> and draw their
        // own box over it. Playwright cannot click what is hidden, so set the
        // value in the DOM and raise the events the page is listening for.
        if (!await locator.IsVisibleAsync())
        {
            await SetHiddenSelectAsync(locator, value);
            return;
        }

        try
        {
            await locator.SelectOptionAsync(new SelectOptionValue { Label = value });
        }
        catch (PlaywrightException)
        {
            await locator.SelectOptionAsync(new SelectOptionValue { Value = value });
        }
    }

    private static async Task SetHiddenSelectAsync(ILocator select, string wanted)
    {
        await select.EvaluateAsync("""
        (el, wanted) => {
          const text = String(wanted).trim().toLowerCase();
          const options = Array.prototype.slice.call(el.options);

          const match = options.find(o => o.text.trim().toLowerCase() === text)
                     || options.find(o => String(o.value).toLowerCase() === text)
                     || options.find(o => o.text.trim().toLowerCase().indexOf(text) >= 0);

          if (!match) {
            throw new Error('No option matching "' + wanted + '". Options: ' +
              options.map(o => o.text.trim()).join(' | '));
          }

          el.value = match.value;
          el.dispatchEvent(new Event('input', { bubbles: true }));
          el.dispatchEvent(new Event('change', { bubbles: true }));

          // Enhanced widgets usually redraw on a jQuery change.
          if (window.jQuery) { window.jQuery(el).trigger('change'); }
        }
        """, wanted);
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

    private static async Task SettleAsync(IPage page)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                new PageWaitForLoadStateOptions { Timeout = 5_000 });
        }
        catch (TimeoutException)
        {
            // A page that keeps polling never goes idle. Carrying on is correct.
        }
    }

    public static bool IsTruthy(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "checked" or "on";

    private static async Task<AssertionResult> CheckAsync(IPage page, AssertionSpec spec, string expected)
    {
        var outcome = new AssertionResult { Description = Describe(spec, expected) };

        try
        {
            switch (spec.Kind)
            {
                case AssertionKinds.UrlContains:
                    outcome.Actual = page.Url;
                    outcome.Passed = Contains(page.Url, expected);
                    break;

                case AssertionKinds.TitleContains:
                    outcome.Actual = await page.TitleAsync();
                    outcome.Passed = Contains(outcome.Actual, expected);
                    break;

                case AssertionKinds.PageTextContains:
                case AssertionKinds.PageTextNotContains:
                {
                    var text = await page.EvaluateAsync<string>(
                        "() => document.body ? document.body.innerText : ''") ?? "";

                    outcome.Actual = Excerpt(text, expected);
                    var hit = Contains(text, expected);
                    outcome.Passed = spec.Kind == AssertionKinds.PageTextContains ? hit : !hit;
                    break;
                }

                case AssertionKinds.ElementVisible:
                case AssertionKinds.ElementNotVisible:
                {
                    // Waiting rather than sampling: a control that appears a
                    // moment after the click should still count as visible.
                    var visible = true;

                    try
                    {
                        await page.Locator(spec.Selector).First.WaitForAsync(new LocatorWaitForOptions
                        {
                            State = WaitForSelectorState.Visible,
                            Timeout = 5_000
                        });
                    }
                    catch (TimeoutException)
                    {
                        visible = false;
                    }

                    outcome.Actual = visible ? "visible" : "not visible";
                    outcome.Passed = spec.Kind == AssertionKinds.ElementVisible ? visible : !visible;
                    break;
                }

                case AssertionKinds.ElementTextContains:
                    outcome.Actual = (await page.Locator(spec.Selector).First.InnerTextAsync()) ?? "";
                    outcome.Passed = Contains(outcome.Actual, expected);
                    break;

                case AssertionKinds.ElementValueEquals:
                    outcome.Actual = (await page.Locator(spec.Selector).First.InputValueAsync()) ?? "";
                    outcome.Passed = string.Equals(outcome.Actual.Trim(), expected.Trim(),
                        StringComparison.OrdinalIgnoreCase);
                    break;

                case AssertionKinds.ElementEnabled:
                    outcome.Passed = await page.Locator(spec.Selector).First.IsEnabledAsync();
                    outcome.Actual = outcome.Passed ? "enabled" : "disabled";
                    break;

                case AssertionKinds.ElementChecked:
                    outcome.Passed = await page.Locator(spec.Selector).First.IsCheckedAsync();
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
                outcome.Actual = ex.Message.Split('\n')[0];
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
