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

        // A test file that changed is always run, and the types of both sides of a changed file are followed.
        Assert.Contains("if ($byPath.ContainsKey($f)) { $hit[$f] = \"changed itself\"; continue }", script);
        Assert.Contains("show \"${mergeBase}:$Path\"", script);
    }
}
