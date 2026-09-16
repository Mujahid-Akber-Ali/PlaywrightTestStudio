# Playwright Test Studio

A Windows desktop tool (C# / .NET 8 WinForms + Microsoft.Playwright) that builds
browser tests by **recording what you do**, then re-runs them against as many
rows of test data as somebody types into Excel.

Nobody picks a locator. You work through the page once; the tool captures every
action, hands you a workbook, and whoever owns the test data fills it in and
uploads it back.

## The flow

```
  Start
    |
    +-- "Which project do you want to work on?"      <- popup, pick or create
    |
  Project home
    |
    +-- Record a test case
    |     |
    |     +-- "Which page?"                <- popup: the link (+ sign in first)
    |     +-- Browser opens. You type, choose, tick and click.
    |     |     Alt+click anything = record a CHECK on it, not a click.
    |     +-- "Stop and save"
    |     +-- Review the steps             <- popup: untick strays, name columns
    |     +-- "What should this check?"    <- popup: name + assertions
    |     +-- Run  ->  PASSED / FAILED  ->  stored
    |
    +-- Download Excel  ->  hand it to a tester  ->  they add data rows
    +-- Upload Excel    ->  every row runs as its own test
    |
    +-- Show all test cases     <- every case, data rows, last status, Run
    +-- Run all test cases
    +-- Sign in to a site...
    +-- Generated C# / Data folder / Switch project
```

A **project** is a workspace: its own links, test cases, data, results, workbook
and generated code. The app always starts by asking which one you want.

## Running it

```
dotnet build
dotnet run
```

The first browser action downloads Chromium for Playwright; that happens once.

## Recording

**Record a test case** opens the link in a real browser and watches it. Typing,
dropdown choices, ticks, radio buttons, clicks and Enter are all captured, in
order, as you go. A small window beside the browser counts the actions and shows
the last one.

- Typing into one box is **one** step holding the final value, not one per key.
- A lookup box (`role="combobox"`, `aria-autocomplete`, a select2/typeahead
  wrapper, a name like `itemSearch`) is captured as a **type** step, replayed one
  key at a time so the page actually searches. The click on the suggestion that
  follows is captured as the **pick**.
- **Alt+click** records a check on whatever you clicked instead of a click -
  that is how you say "this is what proves the test passed".
- Closing the browser window finishes the recording.
- The session is also saved as a video, next to the test case.

Afterwards you get the step list to review: untick anything stray, reorder,
delete, or rename a data column. Then the assertions popup.

## The Excel round trip

**Download Excel** writes one workbook for the project:

| Sheet | What it holds |
|-------|---------------|
| How to use | Instructions, so the file explains itself to whoever gets it |
| Test Cases | One row per test case: id, name, link, enabled |
| Steps | Every action in order, with the data column each one reads |
| Assertions | The checks, with a dropdown of the allowed wording |
| Data - *name* | **One sheet per test case**, one row per data set |
| Results | How the last run went |

The data sheet is the point. After recording once you get:

| Test ID | Data Set | Enabled | user_name | role | item | item_result | expectedResult |
|---------|----------|---------|-----------|------|------|-------------|----------------|
| a1b2c3 | Recorded | TRUE | saif | Manager | widget | Widget clamp | Submitted: saif / Manager … |

A tester copies that row, renames it, changes the values, and uploads. **Upload
Excel** reads it back and offers to run everything: each enabled row runs as its
own test and passes or fails on its own.

Notes:

- The **Parameter** column on Steps is what ties a step to a data column. Clear
  it and that step always uses what was recorded.
- Put a data column name in **Parameter** on the Assertions sheet and the
  expected text comes from that column too - so row 1 can expect one result and
  row 2 another.
- Pressing a button never becomes a data column; picking an item from a
  suggestion list does.
- Uploading never deletes anything. A test case left out of the file is left
  alone; ids it has not seen are added.
- **Test ID** is how the file matches back to the project. Leave it alone.

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
| type | types one key at a time, then waits - for lookups that search as you type |
| select | chooses a dropdown option by its visible label |
| check / uncheck | ticks or unticks a checkbox, or picks a radio button |
| click | clicks, falling back to the text the element showed when recorded |
| press | presses a key (Enter, usually) |

The click fallback matters: suggestion lists are rebuilt every time they open, so
their structural selectors go stale while their wording does not.

Widgets that hide the real `<select>` and draw their own box over it (select2,
chosen) are handled: when the `<select>` is not visible the runner sets it in the
DOM and fires `input`, `change` and a jQuery `change`.

## Where things are kept

```
%LOCALAPPDATA%\PlaywrightTestStudio\
    settings.json            headless on/off, last project
    browser-profile\         the shared Chrome profile (your logins)
    projects\<project>\
        tests\<id>.json      one file per test case
        runs.json            the run log behind "Recent runs"
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
the data. It produces the same steps and the same workbook. Recording is easier
for anything with a flow to it; scanning is useful for a page you would rather
read than act on.

## Self test

```
dotnet build
bin\Debug\net8.0-windows\PlaywrightTestStudio.exe --selftest
```

Walks the whole journey with no UI: builds a sample form, records a session on
it, checks every action was captured, runs it, downloads the workbook, adds a
second data row the way a tester would, uploads it, runs both rows, confirms a
row expecting the wrong thing fails **on its own**, and reads back the generated
C#. Exit code 0 means all checks passed; the log, including the generated
fixture, is written to `%TEMP%\playwright-test-studio-selftest.log`.

## Known limits

- Controls inside an `<iframe>` are not recorded or scanned.
- Controls inside a shadow root are not recorded or scanned.
- Navigating by typing in the address bar mid-recording is not captured; clicks
  that navigate are.
- One browser at a time - runs are sequential because they share the profile.
- Generated fixtures start from a clean browser context, so a link behind a login
  needs a storage-state file or a sign-in step added to the fixture. Inside the
  studio the shared profile handles it.
