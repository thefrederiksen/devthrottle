using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Architecture;

/// <summary>
/// THE DEFAULT GATE RUNS THE PARKED TESTS A CHANGE CAN REACH.
///
/// On 16 October 2026 a change broke two Gateway unit test classes, merged on a green default run because that
/// suite is parked, and held main red for a day - which blocked every release behind it. The gate used to WARN
/// about a parked suite the change could reach; now <c>scripts/test-local.ps1</c> asks
/// <c>scripts/select-parked-tests.ps1</c> for the affected classes of Gateway.UnitTests and Core.Tests and runs
/// them after the default suites. These tests pin the parts that, removed, would quietly turn that back into a
/// warning. They read the scripts' text, because a gate test may not start a real process.
/// </summary>
public sealed class GateRunsTheParkedTestsAChangeReachesTests
{
    private static string Script(string name) =>
        File.ReadAllText(Path.Combine(RepositorySourceIndex.Root, "scripts", name));

    [Fact]
    public void TestLocal_DefaultRun_StartsTheSelectedParkedClassesAfterTheDefaultSuites()
    {
        var script = Script("test-local.ps1");

        var select = script.IndexOf("$parkedPending = @(& (Join-Path $PSScriptRoot \"select-parked-tests.ps1\"))", StringComparison.Ordinal);
        Assert.True(select >= 0, "test-local.ps1 no longer asks select-parked-tests.ps1 which parked classes to run.");

        // Only in a default run: a -Filter run is the caller's own choice, and -Parked already runs everything.
        var guardLine = script.LastIndexOf("if (-not $Parked -and -not $Gateway -and -not $Quarantine -and $Filter -eq \"\") {", select, StringComparison.Ordinal);
        Assert.True(guardLine >= 0 && select - guardLine < 200, "The parked selection is no longer made in exactly the default run.");

        // Started once every default suite has been waited on, so it cannot take the processor from them.
        var start = script.IndexOf("if ($ri -ge $running.Count) {", StringComparison.Ordinal);
        Assert.True(start > select, "The parked classes are no longer started after the default suites have finished.");
        // The wait loop ends only when nothing parked is still pending.
        var stop = script.IndexOf("if ($parkedPending.Count -eq 0) { break }", start, StringComparison.Ordinal);
        Assert.True(stop > start && stop - start < 200, "The wait loop no longer waits for the pending parked classes before it ends.");
        var launch = script.IndexOf("$r = Start-TestSuite $s.Project \"\" (@(\"--filter\", $selFilter) + $parkedBlame)", start, StringComparison.Ordinal);
        Assert.True(launch > start, "The selected parked classes are no longer started.");
        Assert.Contains("\"FullyQualifiedName~.$_.\"", script);
        Assert.Contains(")&$suiteFilter\" })", script);

        // No ceiling for them - their cost is what the change reaches - but hang detection instead.
        Assert.Contains("-or $r.PSObject.Properties['NoCeiling']) {", script);
        Assert.Contains("$parkedBlame = @(\"--blame-hang\"", script);
    }

    [Fact]
    public void TestLocal_ParkedSelectionThatCannotRun_FailsTheGate()
    {
        var script = Script("test-local.ps1");
        var select = script.IndexOf("$parkedPending = @(& (Join-Path $PSScriptRoot \"select-parked-tests.ps1\"))", StringComparison.Ordinal);
        Assert.True(select >= 0);

        // The old coverage warning caught a selector failure and turned it into a note. Neither call may be
        // wrapped in a try that swallows it.
        var before = script.Substring(Math.Max(0, select - 400), 400);
        Assert.DoesNotContain("try {", before);
        Assert.DoesNotContain("coverage gap unknown", script);
    }

    [Fact]
    public void SelectParkedTests_SelectsTheWholeSuite_WhenItCannotReasonAboutTheChange()
    {
        var script = Script("select-parked-tests.ps1");

        // Fails toward running more: no reasoning possible, a non-C# file no test names, or too many classes.
        Assert.Contains("if ($suites.RunAll) {\n        $whole = $true", script.Replace("\r\n", "\n"));
        Assert.Contains("changed and no test file names it, so nothing can be ruled out", script);
        Assert.Contains("if ($classes.Count -gt $MaxClasses) {\n            $whole = $true", script.Replace("\r\n", "\n"));

        // A changed test file is run AND its declared types are followed, so a changed rig reaches its users.
        var normalized = script.Replace("\r\n", "\n");
        Assert.Contains("if ($byPath.ContainsKey($f)) { $hit[$f] = \"changed itself\" }\n                $before = $types.Count", normalized);
        // A changed C# file that declares no type selects the whole suite rather than nothing.
        Assert.Contains("changed and declares no type a test could name, so nothing can be ruled out", script);
        // The before side is read without letting PowerShell 5.1 turn git's "not in that commit" into a crash.
        Assert.Contains("$old = Invoke-GitQuiet @(\"show\", \"${mergeBase}:$Path\")", script);
        Assert.Contains("$ErrorActionPreference = \"Continue\"", script);
        // Only files holding tests name classes, so a filter can never consist of helpers alone.
        // ...including a custom attribute such as [WindowsOnlyFact], which some files use alone.
        Assert.Contains("Where-Object { $byPath[$_] -match '\\[[A-Za-z]*(Fact|Theory)\\b' }", script);
        // A typeless changed file selects the whole suite on EITHER side - an assembly attribute in a test
        // project reaches every test in it.
        Assert.Contains("if ($types.Count -eq $before) {", script);
    }

    [Fact]
    public void SelectTests_SeesTheUncommittedChange_TheGateIsJudging()
    {
        var script = Script("select-tests.ps1");

        Assert.Contains("$dirty = @(& git -C $repoRoot diff --name-only HEAD 2>$null)", script);
        Assert.Contains("$untracked = @(& git -C $repoRoot ls-files --others --exclude-standard 2>$null)", script);
        Assert.Contains("$changed = @($changed + $dirty + $untracked | Where-Object { $_ } | Select-Object -Unique)", script);
    }

    [Fact]
    public void SelectTests_AGitWarningOnStandardError_DoesNotEndTheGate()
    {
        // On 18 October 2026 every gate run on a tree holding a file git would convert line endings for died on
        // git's "LF will be replaced by CRLF" warning: Windows PowerShell 5.1 turns a native command's standard-error
        // line into a terminating error under Stop, even with 2>$null. The reads of what the change touches run with
        // the preference lowered, and put it back afterwards.
        var script = Script("select-tests.ps1").Replace("\r\n", "\n");

        var lowered = script.IndexOf("$ErrorActionPreference = \"Continue\"", StringComparison.Ordinal);
        var restored = script.IndexOf("} finally { $ErrorActionPreference = $previousPreference }", StringComparison.Ordinal);
        Assert.True(lowered >= 0 && restored > lowered, "select-tests.ps1 no longer lowers the error preference around its git reads.");
        foreach (var read in new[]
                 {
                     "& git -C $repoRoot diff --name-only \"$Base...HEAD\"",
                     "& git -C $repoRoot diff --name-only HEAD",
                     "& git -C $repoRoot ls-files --others --exclude-standard",
                 })
        {
            var at = script.IndexOf(read, StringComparison.Ordinal);
            Assert.True(at > lowered && at < restored, $"'{read}' runs outside the lowered error preference, so a git warning ends the gate.");
        }

        // Lowered is not ignored: a read that fails still stops the gate, saying which.
        Assert.Contains("throw \"git diff --name-only HEAD failed (exit $LASTEXITCODE)", script);
        Assert.Contains("throw \"git ls-files --others failed (exit $LASTEXITCODE)", script);
    }
}
