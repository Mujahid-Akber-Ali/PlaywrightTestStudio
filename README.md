# Playwright Test Studio

A Windows desktop tool (C# / .NET 8 WinForms + Microsoft.Playwright) that builds
browser tests by **recording what you do**, then re-runs them against as many
rows of test data as somebody types into Excel, and writes a results file with
charts at the end of every run.

Nobody picks a locator. You work through the page once; the tool captures every
action, hands you a test data file, and whoever owns the test data fills it in
and uploads it back.

## The flow

```
  Start
    |
    +-- "Which project do you want to work on?"      <- popup, pick or create
    |       New project asks for a NAME (not a link)
    |
  Project home
    |
    +-- Record a test case
    |     |
    |     +-- "Which page?"                <- popup: the link (+ sign in first)
    |     +-- The app HIDES. Browser only. You type, choose, tick and click.
    |     |     Alt+click anything = record a CHECK on it, not a click.
    |     +-- Press Esc in the page to stop  (or close the browser)
    |     +-- Name it                      <- popup
    |     +-- Review the steps             <- popup: untick strays, name columns
    |     +-- "What should this check?"    <- popup: name + assertions
    |     +-- Run  ->  PASSED / FAILED  ->  stored  ->  results file written
    |
    +-- Download test data  ->  hand it to a tester  ->  they add data rows
    +-- Upload test data    ->  every row runs as its own test
    |
    +-- Scrape data to Excel    <- read a page's grid, every page of it, to Excel
    |
    +-- Show all test cases     <- every case, data rows, last status, Run
    +-- Run all test cases      ->  results file, with charts
    +-- Results file            <- the last result of everything, any time
    +-- Sign in to a site...
    +-- Generated C# / Data folder / Switch project
```

A **project** is a workspace: its own links, test cases, data, results and
generated code. The app always starts by asking which one you want.

## Projects are saved by name

**New project...** asks what the project is called, and that name is what the
project, its folder, its test data file, its results files and its generated C#
are saved under. A link is not accepted as a name - the link belongs to a test
case, and you are asked for it when you record.

- The picker lists projects by the name they were given.
- **Rename...** moves a project - test cases, data, screenshots, results and
  generated C# - to a new name.
- A project that was saved under a link by an earlier version asks for a proper
  name the first time you open it. Cancel and it opens as it is.
- A new test case is suggested a name from the project (`Portal UAT case 3`),
  not from the link.

## Running it

```
dotnet build
dotnet run
```

The first browser action downloads Chromium for Playwright; that happens once.

## Recording

**Record a test case** opens the link in a real browser and watches it. Typing,
dropdown choices, ticks, radio buttons, clicks, Enter and the arrow keys in a
lookup are all captured, in order, as you go.

**The studio hides itself while you record.** The browser is the only window on
screen - nothing of ours sits over the page or catches a stray click.

- **Press Esc in the page to stop.** Closing the browser window also stops it.
- A small red "Recording - press Esc to stop" badge appears top-right on each
  page and fades after a few seconds. It cannot be clicked through to or
  interfere with the page - `pointer-events` is off.
- Typing into one box is **one** step holding the final value, not one per key.
- A lookup box is captured as a **type** step, replayed one key at a time so the
  page actually searches, and the click on the suggestion that follows is
  captured as the **pick**. A box is a lookup when it says so (`role="combobox"`,
  `aria-autocomplete`, a typeahead wrapper, a name like `itemSearch`) **or when
  typing in it produced something you then clicked** - a plain text box whose
  results are links the page adds is recognised that way.
- **Alt+click** records a check on whatever you clicked instead of a click -
  that is how you say "this is what proves the test passed".
- An "Are you sure?" box stays on screen for you to answer.
- The session is also saved as a video, next to the test case.

### What is deliberately not recorded

A step that has nothing to find fails the replay, so these are left out:

- **A click on the page rather than on anything in it** - to close a list, to
  take the cursor out of a box. Only links, buttons, things with a hand cursor,
  suggestions, and short pieces of text are clicks.
- **A change the page makes to itself** - events it fires at its own fields
  while loading, or a dependent dropdown it puts back to "Please select" after
  you chose something else.
- **The inner workings of a select2 dropdown.** Opening it, typing in its search
  box and clicking its list become one step: *choose X in that dropdown*.
- A click on a label - the tick or the field it belongs to is recorded instead.

Fields are named from their label. That includes the common layout where a bare
`<label>` sits above the field with nothing tying the two together, so the data
column is called `budget`, not `select2-budgetId-container`.

Nothing is saved until you have named it and been through the review and
assertion popups - cancelling at any of those throws the recording away.

Afterwards you get the step list to review: untick anything stray, reorder,
delete, or rename a data column. Then the assertions popup.

## Replaying

Every test case and every data row of a run shares **one browser window**, each
in a fresh tab, instead of starting a browser apiece.

Nothing waits for a fixed length of time when it can wait for the thing it
needs:

- **A pick goes by the wording of the item**, because that is what the data row
  names and a results list is built afresh every time it opens. The run waits
  for that item to turn up, however long the search takes, and prefers an exact
  match over a partial one. Where the item sat in the list when it was recorded
  is only a last resort.
- **A button goes by its selector**, with its wording to fall back on after a
  second and a half.
- **A dropdown option is waited for** - a dropdown that depends on an earlier
  choice is filled a moment after that choice is made.
- **The checks keep looking for five seconds** before they count as failed, so
  a page that had not finished drawing does not fail a test.
- After a click the run waits for a page load only if one actually started.

A step that cannot be carried out is reported as an **Error** naming the step:

```
Step 3 of 12 (pick "IC 74HC123") failed: "IC 74HC123" was not offered to pick
within 15 seconds. Check the search text before it brings that item up.
```

`"Are you sure?"` boxes are answered **OK** during a replay.

## The test data file

**Download test data** writes one workbook for the project. It holds **test data
and nothing else**: one sheet per test case, a column for every value entered
while recording, and a row per data set. After recording once you get:

| Data Set | Enabled | user_name | role | item | item_result | expectedResult |
|----------|---------|-----------|------|------|-------------|----------------|
| Recorded | TRUE | saif | Manager | widget | Widget clamp | Submitted: saif / Manager … |

A tester copies that row, renames it, changes the values, and uploads. **Upload
test data and run** reads it back and offers to run everything: each enabled row
runs as its own test and passes or fails on its own.

Notes:

- How the test drives the page - its steps, selectors and checks - is **not** in
  the file. It stays in the project, where an edit in Excel cannot break it. Use
  **Show all test cases** > **Edit steps** / **Edit checks** to change it.
- A pressed button or key never becomes a column; a typed value, a dropdown
  choice and an item picked from a suggestion list do.
- Give a check a data column in **Edit checks** and its expected text comes from
  that column too - so row 1 can expect one result and row 2 another.
- A hidden first column holds the test id that ties a sheet to its test case. It
  travels with a copied row; a row typed in from scratch works too.
- The data columns are formatted as text, so `2026-10-01` and `007` stay as
  typed.
- Uploading replaces the data rows of the test cases in the file and changes
  nothing else. A test case with no sheet in the file is left alone.
- A test case that only clicks has no data and gets no sheet.
- A workbook downloaded from an earlier version (with Test Cases, Steps and
  Assertions sheets) can still be uploaded.

## The results file

Every run ends by writing a results workbook to the project's `results` folder -
after **Run all test cases** you are offered it straight away, and **Results
file** on the home screen writes a fresh one at any time. It covers every test
case in the project:

| Sheet | What it holds |
|-------|---------------|
| Summary | Headline numbers (test cases, data rows, passed, failed, error, not run, pass rate, time taken), three charts, and the tables the charts are drawn from |
| Results | One line per test case per data row: result, what happened, checks passed, seconds, when, the test data used, a link to the screenshot |
| Checks | One line per assertion, with what the page actually showed |

The charts are real Excel charts, so they can be restyled or copied into a
report:

1. **How the data rows did** - passed, failed, error, not run.
2. **Each test case, by outcome of its data rows** - a stacked bar per test case.
3. **Time taken by each test case**.

*Failed* means a check did not hold. *Error* means a step could not be carried
out, so the checks were never reached.

## Assertions

URL contains, page title contains, page text contains / does not contain,
element is visible / not visible, element text contains, element value equals,
element is enabled, element is checked.

All of them run. Any failure fails that data row. A run records Passed, Failed or
Error per row, the per-assertion outcome with the actual value found, how long it
took, and a full-page screenshot.

## Step actions

| Action | What it does |
|--------|--------------|
| goto | navigates to a URL |
| fill | sets a field's value |
| type | types one key at a time - for lookups that search as you type |
| select | chooses a dropdown option by its visible label |
| check / uncheck | ticks or unticks a checkbox, or picks a radio button |
| click | clicks a button, or picks an item from a list by its wording |
| press | presses a key (Enter, ArrowDown, ...) |

Widgets that hide the real `<select>` and draw their own box over it (select2,
chosen) are handled: when the `<select>` is not visible the runner sets it in the
DOM and fires `input`, `change` and a jQuery `change`. A select2 that fetches its
options as you search is driven through its own box instead.

A checkbox hidden behind a drawing of one is ticked through the page's own click
handler; a read-only date box that only its calendar writes to has its value set
directly.

## Where things are kept

```
%LOCALAPPDATA%\PlaywrightTestStudio\
    settings.json            headless on/off, last project
    browser-profile\         the shared Chrome profile (your logins)
    projects\<project>\
        project.json         the project's name
        tests\<id>.json      one file per test case
        runs.json            the run log behind "Recent runs"
        results\             one results workbook per run
        screenshots\         one per run, per data row
        videos\              the recorded sessions
```

Generated C# lives in the source tree instead, so it is visible to Visual Studio
and to git:

```
GeneratedTests\<project>\<host_and_path>Tests.cs
```

One file per link, one `[Test]` method per test case. **A test case with more
than one data row becomes a parameterised method with a `[TestCase]` attribute
per row**, which is the NUnit shape for exactly this:

```csharp
[TestCase("saif", "Manager", "widget", "Widget clamp", "Submitted: saif / …", TestName = "… - Recorded")]
[TestCase("ali",  "Admin",   "bolt",   "Bolt M8",      "Submitted: ali / …",  TestName = "… - Admin and a bolt")]
public async Task Submit_the_form(string user_name, string role, string item,
                                  string item_result, string expectedResult)
```

These files are NUnit fixtures and are deliberately **not** compiled by
`PlaywrightTestStudio.csproj` (see the `Compile Remove` item) - this app does not
reference NUnit. `GeneratedTests\README.md` explains how to wire them into a test
project and run them with `dotnet test`.

## Logins

Every recording and every run uses one shared Chrome profile, so you sign in once
and stay signed in. Use **Sign in to a site...** (or **Sign in first...** in the
link popup): a browser window opens, you log in, you close it, and the cookies
stay in the profile.

Only one browser can use the profile at a time, so finish a sign-in before
starting a recording or a run.

## Scan mode

**Scan a page instead...** is the older way in: read the page, list every
dropdown, text field, radio button and button, tick the ones you want and type
the data. It produces the same steps and the same test data file. Recording is
easier for anything with a flow to it; scanning is useful for a page you would
rather read than act on.

## Scrape mode

**Scrape data to Excel** is the other thing a project does. Where testing drives
a page, scraping reads one: paste the link, the page opens in a browser, you
apply whatever filter you want and load the data, then press the green **Scrape
to Excel** button the tool floats in the top-right corner (Esc cancels).

```
  Project home
    |
    +-- Scrape data to Excel...
          |
          +-- "Which page?"            <- popup: the link (+ sign in first)
          +-- The app HIDES. Browser only. Filter and load the grid you want.
          +-- Press "Scrape to Excel"  <- the floating button in the page
          |     Reads every visible table; follows the "Next" page to the end.
          +-- Save the workbook        <- one sheet per table, the big grid first
```

What it reads:

- Every visible `<table>` on the page, each to its own sheet. If the page has no
  real tables it falls back to ARIA grids (`role="grid"`, the kind ag-Grid and
  the like draw out of `<div>`s).
- The largest grid is **paginated to the end**: the tool finds the "Next" control
  (a `rel="next"` link, a `next`/`›`/`»` button, an aria-label or title that says
  "next", or the class names the common pagination libraries use), clicks it,
  reads the new rows, and repeats until there is no enabled next page - or a page
  turns up nothing new. Up to 1000 pages.
- A cell that holds a form control reports what the control shows (the chosen
  option, a ticked box as `TRUE`), not a blank.

Every value is written as **text**, so a code like `007` or a date stays exactly
as the page showed it. The browser is always visible for a scrape - there is
nothing to filter on a hidden window - whatever the headless setting says.

## Self test

```
dotnet build
bin\Debug\net8.0-windows\PlaywrightTestStudio.exe --selftest
```

Walks the whole journey with no UI: builds a sample form, records a session on
it, checks every action was captured - and that stray clicks and the page's own
events were **not** - and that **Esc stops the recording**, runs it, downloads
the test data, adds a second row the way a tester would, uploads it, runs both
rows, confirms a row expecting the wrong thing fails **on its own** and that a
broken step says which step it was, writes the results file and checks its
charts, reads back the generated C#, scrapes a three-page grid and confirms all
of it came through into a valid workbook, and renames the project. Exit code 0 means
all checks passed; the log, including the generated fixture, is written to
`%TEMP%\playwright-test-studio-selftest.log`, and the results file it made is
left at `%TEMP%\pts-selftest-results.xlsx` to look at.

## Known limits

- Esc ends a recording, so an Esc keypress cannot itself be recorded as a step.
  If the page needs one, add a `press` step with the value `Escape` afterwards.
- A recording made before the recorder learned to leave out stray clicks still
  has them. Untick them in **Edit steps**, or record the test case again.
- "Are you sure?" boxes are always answered OK on replay, whatever was answered
  while recording.
- Controls inside an `<iframe>` are not recorded or scanned.
- Controls inside a shadow root are not recorded or scanned.
- File upload fields are not recorded.
- Navigating by typing in the address bar mid-recording is not captured; clicks
  that navigate are.
- One browser at a time - runs are sequential because they share the profile.
- Generated fixtures start from a clean browser context, so a link behind a login
  needs a storage-state file or a sign-in step added to the fixture. Inside the
  studio the shared profile handles it.
