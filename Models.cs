using System.Text.Json.Serialization;

namespace PlaywrightTestStudio;

/// <summary>
/// One interactive control found on a scanned page. The scanner emits one of
/// these per control; radio buttons are emitted individually and grouped by
/// <see cref="Name"/> when the operator is asked for data.
/// </summary>
internal sealed class ElementInfo
{
    /// <summary>text | search | select | radio | checkbox | button</summary>
    public string Kind { get; set; } = "text";

    public string Tag { get; set; } = "";
    public string InputType { get; set; } = "";

    /// <summary>
    /// For a search / type-ahead field: where its results appear, when the page
    /// says so through aria-owns, aria-controls or a datalist. Empty means the
    /// runner falls back to the usual suggestion containers.
    /// </summary>
    public string ResultsSelector { get; set; } = "";

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

internal static class StepActions
{
    public const string Goto = "goto";
    public const string Fill = "fill";

    /// <summary>Typed one key at a time, for lookups that search on key events.</summary>
    public const string Type = "type";

    public const string Select = "select";
    public const string Check = "check";
    public const string Uncheck = "uncheck";
    public const string Click = "click";
    public const string Press = "press";

    public static readonly string[] All =
    {
        Goto, Fill, Type, Select, Check, Uncheck, Click, Press
    };
}

/// <summary>
/// One recorded or authored action, in the order it happens. Replaces the old
/// "all the fields, then all the buttons" shape, which could not describe a real
/// session where typing and clicking interleave.
/// </summary>
internal sealed class TestStep
{
    public bool Use { get; set; } = true;

    /// <summary>One of <see cref="StepActions"/>.</summary>
    public string Action { get; set; } = StepActions.Fill;

    public string Selector { get; set; } = "";

    /// <summary>What the control is called on screen, for the report and the Excel sheet.</summary>
    public string Label { get; set; } = "";

    /// <summary>The literal value, used when <see cref="Parameter"/> is empty.</summary>
    public string Value { get; set; } = "";

    /// <summary>
    /// Column in the test data sheet that supplies the value. This is what makes
    /// a recorded test re-runnable against rows a person types into Excel.
    /// </summary>
    public string Parameter { get; set; } = "";

    /// <summary>
    /// Visible text of the element when it was recorded. Used to find it again
    /// if the selector misses - suggestion lists in particular are rebuilt with
    /// different ids each time they open.
    /// </summary>
    public string FallbackText { get; set; } = "";

    /// <summary>Let the page settle after this step.</summary>
    public bool WaitAfter { get; set; }

    /// <summary>
    /// A click that chooses an item from a list of results, rather than a button
    /// press. Only these get a data column - which row of the sheet you are on
    /// should change which item is picked, never which button is pressed.
    /// </summary>
    public bool Pick { get; set; }

    [JsonIgnore]
    public string Display => string.IsNullOrWhiteSpace(Label) ? Selector : Label;

    /// <summary>
    /// The value this step was recorded with. For a click that is the text of
    /// the thing clicked, so picking a different search result is just another
    /// column in the data sheet.
    /// </summary>
    [JsonIgnore]
    public string RecordedValue => Action == StepActions.Click ? FallbackText : Value;

    /// <summary>True when the step carries a value a person could vary per run.</summary>
    [JsonIgnore]
    public bool TakesValue =>
        Action is StepActions.Fill or StepActions.Type or StepActions.Select
            or StepActions.Press or StepActions.Goto ||
        (Action == StepActions.Click && Pick && FallbackText.Trim().Length > 0);
}

/// <summary>
/// One row of test data: a named set of values for the parameters a test case
/// declares. A test case runs once per enabled data set.
/// </summary>
internal sealed class DataSet
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public Dictionary<string, string> Values { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The check that decides pass or fail.</summary>
internal sealed class AssertionSpec
{
    /// <summary>One of <see cref="AssertionKinds"/>.</summary>
    public string Kind { get; set; } = AssertionKinds.UrlContains;

    public string Selector { get; set; } = "";
    public string Expected { get; set; } = "";

    /// <summary>Test data column supplying Expected, so the check varies per row.</summary>
    public string Parameter { get; set; } = "";
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

    /// <summary>Which data set produced this result.</summary>
    public string DataSet { get; set; } = "";

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
    public string Notes { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>How it was built: recorded | scanned | excel.</summary>
    public string Source { get; set; } = "recorded";

    /// <summary>The page scan, when there was one. Empty for a recorded case.</summary>
    public List<ElementInfo> Elements { get; set; } = new();

    /// <summary>The actions, in order.</summary>
    public List<TestStep> Steps { get; set; } = new();

    /// <summary>One run per enabled data set. Always at least one.</summary>
    public List<DataSet> DataSets { get; set; } = new();

    public List<AssertionSpec> Assertions { get; set; } = new();

    /// <summary>Absolute path of the generated C# file for this link.</summary>
    public string CodeFile { get; set; } = "";

    /// <summary>Screen recording of the session this case was captured from.</summary>
    public string VideoFile { get; set; } = "";

    /// <summary>Summary of the last run: Passed only when every data set passed.</summary>
    public TestResult? LastResult { get; set; }

    /// <summary>One entry per data set from the last run.</summary>
    public List<TestResult> LastResults { get; set; } = new();

    // -----------------------------------------------------------------
    // Legacy shape, kept so projects saved before steps existed still load.
    // ProjectStore.LoadAll folds these into Steps and then clears them.
    // -----------------------------------------------------------------
    public List<FieldValue> Fields { get; set; } = new();
    public List<ClickStep> Clicks { get; set; } = new();

    /// <summary>Every parameter the steps and assertions refer to, in first-use order.</summary>
    public List<string> Parameters()
    {
        var seen = new List<string>();

        void Add(string name)
        {
            if (name.Length > 0 && !seen.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                seen.Add(name);
            }
        }

        foreach (var step in Steps)
        {
            Add(step.Parameter);
        }

        foreach (var assertion in Assertions)
        {
            Add(assertion.Parameter);
        }

        return seen;
    }

    /// <summary>The data sets to run, always yielding at least the recorded one.</summary>
    public List<DataSet> RunnableDataSets()
    {
        var enabled = DataSets.Where(d => d.Enabled).ToList();

        return enabled.Count > 0
            ? enabled
            : new List<DataSet> { new() { Name = "Recorded" } };
    }
}

/// <summary>Legacy: a value entered before ordered steps existed.</summary>
internal sealed class FieldValue
{
    public bool Use { get; set; } = true;
    public string Kind { get; set; } = "text";
    public string Label { get; set; } = "";
    public string Selector { get; set; } = "";
    public string Value { get; set; } = "";
    public string Pick { get; set; } = "";
    public string ResultsSelector { get; set; } = "";
    public string Group { get; set; } = "";
}

/// <summary>Legacy: a button press before ordered steps existed.</summary>
internal sealed class ClickStep
{
    public string Label { get; set; } = "";
    public string Selector { get; set; } = "";
    public bool WaitAfter { get; set; } = true;
}

/// <summary>One entry in a project's run log.</summary>
internal sealed class RunLogEntry
{
    public string TestId { get; set; } = "";
    public string TestName { get; set; } = "";
    public string DataSet { get; set; } = "";
    public string Url { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime RanUtc { get; set; }
    public double Seconds { get; set; }
}
