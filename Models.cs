using System.Text.Json.Serialization;

namespace PlaywrightTestStudio;

/// <summary>
/// One interactive control found on a scanned page. The scanner emits one of
/// these per control; radio buttons are emitted individually and grouped by
/// <see cref="Name"/> when the operator is asked for data.
/// </summary>
internal sealed class ElementInfo
{
    /// <summary>text | select | radio | checkbox | button</summary>
    public string Kind { get; set; } = "text";

    public string Tag { get; set; } = "";
    public string InputType { get; set; } = "";

    /// <summary>Human label: &lt;label for&gt;, wrapping label, aria-label, placeholder or name.</summary>
    public string Label { get; set; } = "";

    /// <summary>CSS selector that resolves to exactly this element.</summary>
    public string Selector { get; set; } = "";

    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    public List<string> Options { get; set; } = new();
    public List<string> OptionValues { get; set; } = new();

    public bool Visible { get; set; }
    public bool Required { get; set; }

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Label) ? Selector : Label;
}

/// <summary>A value the test types, selects or checks before the buttons are pressed.</summary>
internal sealed class FieldValue
{
    public bool Use { get; set; } = true;

    /// <summary>text | select | radio | checkbox</summary>
    public string Kind { get; set; } = "text";

    public string Label { get; set; } = "";
    public string Selector { get; set; } = "";

    /// <summary>Typed text, option label, or "true"/"false" for a checkbox.</summary>
    public string Value { get; set; } = "";

    /// <summary>Radio group name, so the report can say which group was answered.</summary>
    public string Group { get; set; } = "";
}

/// <summary>A button press, executed in list order after every field is filled.</summary>
internal sealed class ClickStep
{
    public string Label { get; set; } = "";
    public string Selector { get; set; } = "";

    /// <summary>Wait for a navigation / network to settle after the click.</summary>
    public bool WaitAfter { get; set; } = true;
}

/// <summary>The check that decides pass or fail.</summary>
internal sealed class AssertionSpec
{
    /// <summary>One of <see cref="AssertionKinds"/>.</summary>
    public string Kind { get; set; } = AssertionKinds.UrlContains;

    public string Selector { get; set; } = "";
    public string Expected { get; set; } = "";
}

internal static class AssertionKinds
{
    public const string UrlContains = "URL contains";
    public const string TitleContains = "Page title contains";
    public const string PageTextContains = "Page text contains";
    public const string PageTextNotContains = "Page text does NOT contain";
    public const string ElementVisible = "Element is visible";
    public const string ElementNotVisible = "Element is NOT visible";
    public const string ElementTextContains = "Element text contains";
    public const string ElementValueEquals = "Element value equals";
    public const string ElementEnabled = "Element is enabled";
    public const string ElementChecked = "Element is checked";

    public static readonly string[] All =
    {
        UrlContains, TitleContains, PageTextContains, PageTextNotContains,
        ElementVisible, ElementNotVisible, ElementTextContains,
        ElementValueEquals, ElementEnabled, ElementChecked
    };

    /// <summary>True when the kind targets a specific element rather than the whole page.</summary>
    public static bool NeedsSelector(string kind) => kind is
        ElementVisible or ElementNotVisible or ElementTextContains or
        ElementValueEquals or ElementEnabled or ElementChecked;

    /// <summary>True when the kind compares against text the operator supplies.</summary>
    public static bool NeedsExpected(string kind) => kind is
        UrlContains or TitleContains or PageTextContains or
        PageTextNotContains or ElementTextContains or ElementValueEquals;
}

internal sealed class AssertionResult
{
    public string Description { get; set; } = "";
    public bool Passed { get; set; }
    public string Actual { get; set; } = "";
}

internal sealed class TestResult
{
    /// <summary>Passed | Failed | Error</summary>
    public string Status { get; set; } = "Not run";

    public string Message { get; set; } = "";
    public DateTime RanUtc { get; set; }
    public double Seconds { get; set; }
    public string ScreenshotPath { get; set; } = "";
    public List<AssertionResult> Assertions { get; set; } = new();

    [JsonIgnore]
    public bool IsPass => Status == "Passed";
}

/// <summary>Everything needed to replay one scenario against one link.</summary>
internal sealed class TestCase
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The raw scan, kept so the .cs file can be regenerated without rescanning.</summary>
    public List<ElementInfo> Elements { get; set; } = new();

    public List<FieldValue> Fields { get; set; } = new();
    public List<ClickStep> Clicks { get; set; } = new();
    public List<AssertionSpec> Assertions { get; set; } = new();

    /// <summary>Absolute path of the generated C# file for this link.</summary>
    public string CodeFile { get; set; } = "";

    public TestResult? LastResult { get; set; }
}

/// <summary>One entry in a project's run log.</summary>
internal sealed class RunLogEntry
{
    public string TestId { get; set; } = "";
    public string TestName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime RanUtc { get; set; }
    public double Seconds { get; set; }
}
