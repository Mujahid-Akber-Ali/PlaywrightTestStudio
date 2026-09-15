# Generated tests

Playwright Test Studio writes one C# file per link here, grouped into a
folder per studio project. They are NUnit fixtures, and they are **not**
compiled by `PlaywrightTestStudio.csproj` (see the `Compile Remove` item
in that file) because this app does not reference NUnit.

To run them, create a test project once and point it at this folder:

    dotnet new nunit -n GeneratedTests.Runner
    cd GeneratedTests.Runner
    dotnet add package Microsoft.Playwright.NUnit
    dotnet build

Then add to `GeneratedTests.Runner.csproj`:

    <ItemGroup>
      <Compile Include="..\<ProjectFolder>\**\*.cs" />
    </ItemGroup>

and run:

    dotnet test

Note that these fixtures use a fresh browser context, so any link behind
a login needs a storage-state file or a sign-in step added to the
fixture. Inside the studio itself the shared browser profile handles it.