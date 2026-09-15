using System.Diagnostics;
using Microsoft.Playwright;

namespace PlaywrightTestStudio;

/// <summary>
/// Replays a saved test case with Playwright and decides pass or fail. This is
/// the engine behind the Run buttons; the generated .cs file is the same
/// scenario expressed for NUnit, kept in step but not executed from here.
/// </summary>
internal static class Runner
{
    public static async Task<TestResult> RunAsync(
        TestCase test,
        ProjectStore store,
        bool headless,
        Action<string>? progress = null)
    {
        var watch = Stopwatch.StartNew();

        var result = new TestResult
        {
            RanUtc = DateTime.UtcNow,
            Status = "Error"
        };

        BrowserSession? session = null;

        try
        {
            progress?.Invoke("Starting browser...");
            session = await BrowserSession.StartAsync(headless);

            progress?.Invoke($"Opening {test.Url}");
            await session.Page.GotoAsync(test.Url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded
            });

            await SettleAsync(session.Page);

            foreach (var field in test.Fields.Where(f => f.Use))
            {
                progress?.Invoke($"Entering \"{field.Label}\"");
                await ApplyFieldAsync(session.Page, field);
            }

            foreach (var click in test.Clicks)
            {
                progress?.Invoke($"Clicking \"{click.Label}\"");
                await session.Page.ClickAsync(click.Selector);

                if (click.WaitAfter)
                {
                    await SettleAsync(session.Page);
                }
            }

            progress?.Invoke("Checking assertions...");

            foreach (var assertion in test.Assertions)
            {
                result.Assertions.Add(await CheckAsync(session.Page, assertion));
            }

            result.ScreenshotPath = await CaptureAsync(session.Page, store, test);

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
                // fastest way to see why a selector missed.
                result.ScreenshotPath = await CaptureAsync(session.Page, store, test);
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

    private static async Task ApplyFieldAsync(IPage page, FieldValue field)
    {
        switch (field.Kind)
        {
            case "select":
                await SelectOptionAsync(page, field);
                break;

            case "search":
                await SearchAndPickAsync(page, field);
                break;

            case "radio":
                await page.CheckAsync(field.Selector);
                break;

            case "checkbox":
                await page.SetCheckedAsync(field.Selector, IsTruthy(field.Value));
                break;

            default:
                await page.FillAsync(field.Selector, field.Value);
                break;
        }
    }

    /// <summary>
    /// Selects by visible label first, because that is what the operator picked
    /// in the data popup; falls back to the option's value attribute.
    /// </summary>
    private static async Task SelectOptionAsync(IPage page, FieldValue field)
    {
        var locator = page.Locator(field.Selector).First;

        // Widgets like select2 and chosen hide the real <select> and draw their
        // own box over it. Playwright cannot click what is hidden, so set the
        // value in the DOM and raise the events the page is listening for.
        if (!await locator.IsVisibleAsync())
        {
            await SetHiddenSelectAsync(locator, field.Value);
            return;
        }

        try
        {
            await page.SelectOptionAsync(field.Selector, new SelectOptionValue { Label = field.Value });
        }
        catch (PlaywrightException)
        {
            await page.SelectOptionAsync(field.Selector, new SelectOptionValue { Value = field.Value });
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
    /// Drives a type-ahead the way a person does: type into it, wait for the
    /// search to come back, then click the result. Keystrokes are sent one at a
    /// time because most lookup widgets listen for key events and ignore a
    /// value that is simply assigned.
    /// </summary>
    private static async Task SearchAndPickAsync(IPage page, FieldValue field)
    {
        var input = page.Locator(field.Selector).First;

        await input.ClickAsync();

        try
        {
            await input.FillAsync("");
        }
        catch (PlaywrightException)
        {
            // Some widgets refuse an empty fill; typing over the top is fine.
        }

        await input.PressSequentiallyAsync(field.Value, new LocatorPressSequentiallyOptions { Delay = 60 });

        var wanted = field.Pick.Trim().Length > 0 ? field.Pick.Trim() : "";

        foreach (var (candidate, timeout) in ResultCandidates(page, field, wanted))
        {
            try
            {
                await candidate.First.ClickAsync(new LocatorClickOptions { Timeout = timeout });
                return;
            }
            catch (TimeoutException)
            {
                // Try the next shape of suggestion list.
            }
            catch (PlaywrightException)
            {
            }
        }

        if (wanted.Length == 0)
        {
            // No specific item was asked for and nothing clickable was found:
            // most lookups still answer to the keyboard.
            await input.PressAsync("ArrowDown");
            await input.PressAsync("Enter");
            return;
        }

        throw new InvalidOperationException(
            $"Typed \"{field.Value}\" into \"{field.Label}\" but no search result matching " +
            $"\"{wanted}\" appeared.\r\n" +
            "Check the wording of the item, or give the field longer to search.");
    }

    /// <summary>
    /// The suggestion-list shapes worth trying, most specific first. The first
    /// one gets the long timeout because that is the wait for the search itself;
    /// by the time it gives up, the results are either there or never coming.
    /// </summary>
    private static IEnumerable<(ILocator Locator, float Timeout)> ResultCandidates(
        IPage page, FieldValue field, string wanted)
    {
        var filter = wanted.Length > 0 ? new LocatorFilterOptions { HasTextString = wanted } : null;

        ILocator Narrow(ILocator locator) => filter is null ? locator : locator.Filter(filter);

        var container = field.ResultsSelector.Trim();
        var first = true;

        float Next()
        {
            var timeout = first ? 10_000f : 1_500f;
            first = false;
            return timeout;
        }

        if (container.Length > 0)
        {
            yield return (Narrow(page.Locator($"{container} [role=option], {container} li, {container} option")), Next());
            yield return (Narrow(page.Locator($"{container} tr, {container} a, {container} div")), Next());
        }

        if (wanted.Length > 0)
        {
            yield return (page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = wanted }), Next());
        }
        else
        {
            yield return (page.GetByRole(AriaRole.Option), Next());
        }

        // The containers the common lookup widgets render into.
        yield return (Narrow(page.Locator(
            "[role=listbox] [role=option], " +
            "ul.ui-autocomplete li, " +
            ".select2-results__option, " +
            ".tt-suggestion, " +
            ".typeahead__item, " +
            ".autocomplete-suggestion, " +
            ".autocomplete-items div, " +
            ".dropdown-menu li, " +
            ".dropdown-menu a")), Next());

        if (wanted.Length > 0)
        {
            // Last resort: anything visible on the page saying what we want.
            yield return (page.GetByText(wanted), Next());
        }
    }

    public static bool IsTruthy(string value) =>
        value.Trim().ToLowerInvariant() is "true" or "yes" or "1" or "checked" or "on";

    private static async Task<AssertionResult> CheckAsync(IPage page, AssertionSpec spec)
    {
        var outcome = new AssertionResult { Description = Describe(spec) };

        try
        {
            switch (spec.Kind)
            {
                case AssertionKinds.UrlContains:
                    outcome.Actual = page.Url;
                    outcome.Passed = Contains(page.Url, spec.Expected);
                    break;

                case AssertionKinds.TitleContains:
                    outcome.Actual = await page.TitleAsync();
                    outcome.Passed = Contains(outcome.Actual, spec.Expected);
                    break;

                case AssertionKinds.PageTextContains:
                case AssertionKinds.PageTextNotContains:
                {
                    var text = await page.EvaluateAsync<string>(
                        "() => document.body ? document.body.innerText : ''") ?? "";

                    outcome.Actual = Excerpt(text, spec.Expected);
                    var hit = Contains(text, spec.Expected);
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
                    outcome.Passed = Contains(outcome.Actual, spec.Expected);
                    break;

                case AssertionKinds.ElementValueEquals:
                    outcome.Actual = (await page.Locator(spec.Selector).First.InputValueAsync()) ?? "";
                    outcome.Passed = string.Equals(outcome.Actual.Trim(), spec.Expected.Trim(),
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

    public static string Describe(AssertionSpec spec)
    {
        var target = AssertionKinds.NeedsSelector(spec.Kind) ? $" [{spec.Selector}]" : "";
        var expected = AssertionKinds.NeedsExpected(spec.Kind) ? $" \"{spec.Expected}\"" : "";

        return $"{spec.Kind}{target}{expected}";
    }

    private static bool Contains(string haystack, string needle) =>
        !string.IsNullOrEmpty(needle) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps the reported "actual" readable when the page text is huge.</summary>
    private static string Excerpt(string text, string needle)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        var at = flat.IndexOf(needle, StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            return flat.Length <= 200 ? flat : flat[..200] + "...";
        }

        var start = Math.Max(0, at - 60);
        var length = Math.Min(flat.Length - start, needle.Length + 120);

        return "..." + flat.Substring(start, length) + "...";
    }

    private static async Task<string> CaptureAsync(IPage page, ProjectStore store, TestCase test)
    {
        try
        {
            Directory.CreateDirectory(store.ScreenshotsDir);

            var file = Path.Combine(store.ScreenshotsDir,
                $"{Storage.Slug(test.Name)}_{DateTime.Now:yyyyMMdd_HHmmss}.png");

            await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, FullPage = true });

            return file;
        }
        catch (Exception)
        {
            return "";
        }
    }
}
