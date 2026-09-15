# Playwright Test Studio

A Windows desktop tool (C# / .NET 8 WinForms + Microsoft.Playwright) that builds
browser tests by pointing at a page instead of by writing code.

Give it a link. It opens the page, lists every dropdown, text field, radio
button, checkbox and button on it, asks you for the data to enter and for the
assertion that decides pass or fail, then runs the scenario and stores the
result. Every link also gets its own C# file you can take to Visual Studio or CI.

## The flow

```
  Start
    |
    +-- "Which project do you want to work on?"      <- popup, pick or create
    |
  Project home
    |
    +-- New test case from a link...
    |     |
    |     +-- "Which page?"            <- popup: the link (+ sign in first)
    |     +-- "Everything on this page"<- popup: dropdowns / text / radio / buttons
    |     +-- "Enter the data"         <- popup: one row per field, buttons to click
    |     +-- "What should this check?"<- popup: test case name + assertions
    |     +-- Run  ->  PASSED / FAILED ->  stored
    |
    +-- Show all test cases            <- popup: every case, last status, Run / Run all
    +-- Run all test cases
    +-- Sign in to a site...
    +-- Open generated C# files
    +-- Switch project
```

A **project** is a workspace: its own links, test cases, results and generated
code. The app always starts by asking which one you want.

## Running it

```
dotnet build
dotnet run
```

The first browser action downloads Chromium for Playwright; that happens once.

## Logins

Every scan and every run uses one shared Chrome profile, so you sign in once and
stay signed in. Use **Sign in to a site...** (or **Sign in first...** in the link
popup): a browser window opens, you log in, you close it, and the cookies stay in
the profile.

Only one browser can use the profile at a time, so finish a sign-in before
starting a scan or a run.

## What a test case holds

| Part       | What it is |
|------------|------------|
| Link       | the page the test opens |
| Elements   | the full scan, kept so the case can be edited without rescanning |
| Fields     | the values to type, select or check, one per ticked control |
| Clicks     | the buttons to press, in order, with an optional wait after each |
| Assertions | the checks that decide pass or fail - all of them run |

Assertions available: URL contains, page title contains, page text contains /
does not contain, element is visible / not visible, element text contains,
element value equals, element is enabled, element is checked.

A run records Passed, Failed or Error, the per-assertion outcome with the actual
value found, how long it took, and a full-page screenshot.

## Where things are kept

```
%LOCALAPPDATA%\PlaywrightTestStudio\
    settings.json            headless on/off, last project
    browser-profile\         the shared Chrome profile (your logins)
    projects\<project>\
        tests\<id>.json      one file per test case
        runs.json            the run log behind "Recent runs"
        screenshots\         one per run
```

Generated C# lives in the source tree instead, so it is visible to Visual Studio
and to git:

```
GeneratedTests\<project>\<host_and_path>Tests.cs
```

One file per link, one `[Test]` method per test case recorded against that link.
It is rewritten whenever a case for that link is saved or run, so hand edits are
overwritten.

These files are NUnit fixtures and are deliberately **not** compiled by
`PlaywrightTestStudio.csproj` (see the `Compile Remove` item) - this app does not
reference NUnit. `GeneratedTests\README.md` explains how to wire them into a test
project and run them with `dotnet test`.

## Self test

```
dotnet build
bin\Debug\net8.0-windows\PlaywrightTestStudio.exe --selftest
```

Builds a sample form, scans it, fills it, runs it, checks a deliberately wrong
assertion does fail, and reads back the generated C# file. Exit code 0 means all
checks passed; the log is written to
`%TEMP%\playwright-test-studio-selftest.log`.

## Known limits

- Controls inside an `<iframe>` are not scanned.
- Controls inside a shadow root are not scanned.
- One browser at a time - runs are sequential because they share the profile.
- Generated fixtures start from a clean browser context, so a link behind a
  login needs a storage-state file or a sign-in step added to the fixture. Inside
  the studio the shared profile handles it.
