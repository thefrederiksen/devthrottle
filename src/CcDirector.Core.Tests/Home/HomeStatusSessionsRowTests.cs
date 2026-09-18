using System;
using System.Collections.Generic;
using CcDirector.Core.Home;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Home;

/// <summary>
/// The Sessions row exists because this page once printed "All systems go - 8 of 8 tools passing" while
/// the Director's own log already held "cc-devthrottle ... FAILED to reach ...: missing or invalid token",
/// and a red banner on the Tools tab said so on the same screen. Two verdicts about one machine, in
/// opposite terms.
///
/// These tests pin the part that made that possible: the tools rows can be entirely green and correct
/// while sessions are dead, so a green tools row must never be able to carry the page to "all ready".
/// </summary>
public class HomeStatusSessionsRowTests
{
    private static readonly IReadOnlyList<AgentCliFact> HealthyClis =
        new[] { new AgentCliFact("Claude Code", Found: true, Version: "2.1.0") };

    /// <summary>Every non-session input healthy, so only the Sessions row can move the verdict.</summary>
    private static HomeStatus BuildWith(FleetToolCheck? reachability)
        => HomeStatusBuilder.Build(
            HealthyClis, toolsBuilt: 8, toolsTotal: 8, brokenTools: Array.Empty<string>(),
            toolHealth: null, basePythonBroken: false, toolsSetupInProgress: false,
            sessionReachability: reachability);

    private static FleetToolCheck Fault(string resolved, string expected) =>
        new(FleetToolVerdict.CannotReachGateway, resolved, expected, "Error: missing or invalid token");

    private static HomeCheck? SessionsRow(HomeStatus status)
    {
        foreach (var check in status.Checks)
            if (check.Title == HomeStatusBuilder.SessionsRowTitle) return check;
        return null;
    }

    // THE "ANOTHER INSTALL" DETECTION COMPARES PATHS, so these have to be real paths for the platform
    // under test. A backslash is an ordinary character in a Unix file name, not a separator, so off
    // Windows the comparison could not see two different directories and the row never named the cause.
    //
    // Note what that did to the NEGATIVE case below: it asserts the row does NOT say "another install",
    // which is trivially true when the detection cannot run at all. It was passing on macOS while
    // proving nothing, which is why both are spelled per platform rather than only the one that failed.
    private static string OurBin => OperatingSystem.IsWindows()
        ? @"C:\Users\x\AppData\Local\cc-director\instances\slot-5\bin"
        : "/Users/x/Library/Application Support/cc-director/instances/slot-5/bin";

    private static string CommandFromAnotherInstall => OperatingSystem.IsWindows()
        ? @"C:\Users\x\AppData\Local\cc-director\bin\cc-devthrottle.CMD"
        : "/Users/x/Library/Application Support/cc-director/bin/cc-devthrottle";

    private static string CommandFromOurOwnBin =>
        Path.Combine(OurBin, OperatingSystem.IsWindows() ? "cc-devthrottle.CMD" : "cc-devthrottle");

    [Fact]
    public void NoVerdictYet_AddsNoRowAtAll()
    {
        // Unjudged is not a pass and not a failure. Inventing either is the bug.
        var status = BuildWith(null);

        Assert.Null(SessionsRow(status));
    }

    [Fact]
    public void ToolsAllPassingButSessionsCannotReach_ThePageIsNotAllReady()
    {
        // The exact screenshot: healthy tools, healthy agent, sessions dead.
        var status = BuildWith(Fault(
            @"C:\Users\x\AppData\Local\cc-director\bin\cc-devthrottle.CMD",
            @"C:\Users\x\AppData\Local\cc-director\instances\slot-5\bin"));

        Assert.False(status.AllReady);
        var row = SessionsRow(status);
        Assert.NotNull(row);
        Assert.Equal(HomeCheckLevel.Bad, row!.Level);
    }

    [Fact]
    public void DifferentInstall_TheDetailNamesTheRealCauseNotAGenericFailure()
    {
        var status = BuildWith(Fault(CommandFromAnotherInstall, OurBin));

        // A user who has just been told "DevThrottle is down" has to be able to read this row and know
        // that neither the product nor the network is the problem.
        var detail = SessionsRow(status)!.Detail;
        Assert.Contains("another install", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SameInstallStillRefused_ReportsWhatWasSeenRatherThanBlamingTheInstall()
    {
        // Resolved from our OWN bin and still refused: repointing PATH would not repair this, so the row
        // must not imply it would.
        var status = BuildWith(Fault(CommandFromOurOwnBin, OurBin));

        var detail = SessionsRow(status)!.Detail;
        Assert.DoesNotContain("another install", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing or invalid token", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotOnPathAtAll_IsAFailureWithItsOwnReason()
    {
        var status = BuildWith(new FleetToolCheck(
            FleetToolVerdict.NotFound, null, @"C:\x\bin", "Nothing named cc-devthrottle is on this machine's PATH."));

        var row = SessionsRow(status);
        Assert.Equal(HomeCheckLevel.Bad, row!.Level);
        Assert.False(status.AllReady);
    }

    [Fact]
    public void Working_IsGreenAndLetsThePageBeAllReady()
    {
        var binDir = @"C:\Users\x\AppData\Local\cc-director\instances\default\bin";
        var status = BuildWith(new FleetToolCheck(
            FleetToolVerdict.Working, binDir + @"\cc-devthrottle.CMD", binDir, "reached the fleet through the Gateway"));

        Assert.Equal(HomeCheckLevel.Ok, SessionsRow(status)!.Level);
        Assert.True(status.AllReady);
    }

    [Fact]
    public void NoGateway_AddsNoRow_NeverARepairableToolFault()
    {
        // "No Gateway means no agent tooling" is the Remove-the-network-port mission's accepted
        // trade, and a local-only machine chose that configuration. The page must not carry a
        // standing fault for it, and it must never route to a tool repair - the install has
        // nothing wrong with it. Matching the page's long-standing standalone behavior: no row.
        var status = BuildWith(new FleetToolCheck(
            FleetToolVerdict.NoGateway, null, @"C:\x\bin",
            "No Gateway connection right now, so the fleet tools have nothing to reach."));

        Assert.Null(SessionsRow(status));
        Assert.True(status.AllReady);
    }

    [Fact]
    public void EveryFailingVerdict_OffersARouteToTheRepair()
    {
        // A row that states a fault and offers nowhere to go reads as broken.
        foreach (var check in new[]
                 {
                     Fault(@"C:\a\bin\cc-devthrottle.CMD", @"C:\b\bin"),
                     new FleetToolCheck(FleetToolVerdict.NotFound, null, @"C:\b\bin", "not on PATH"),
                 })
        {
            var row = SessionsRow(BuildWith(check));
            Assert.Equal(HomeCheckAction.OpenTools, row!.Action);
        }
    }

    // The Gateway refuses this Director's session keys (#2457, #2459). On 2026-08-05 this exact
    // state - every session in the fleet locked out by a Gateway older than the Directors talking
    // to it - produced NO VERDICT, and no verdict renders as no row. The Home page was blank while
    // the Director's own log named the cause every ten seconds.

    [Fact]
    public void GatewayRefusedTheKey_IsARedRow_NotSilence()
    {
        var status = BuildWith(new FleetToolCheck(
            FleetToolVerdict.GatewayRefusedKey, null, @"C:\x\bin",
            "The Gateway is connected but refuses this Director's session keys."));

        var row = SessionsRow(status);
        Assert.NotNull(row);
        Assert.Equal(HomeCheckLevel.Bad, row!.Level);
        Assert.False(status.AllReady);
    }

    [Fact]
    public void GatewayRefusedTheKey_TheRowBlamesTheGatewayAndSaysItIsEverySession()
    {
        // The user arrives here having been told by an agent that DevThrottle is broken. The row has
        // to move them off this machine - nothing on it is at fault - and "every session" is what
        // turns one odd session into a Gateway that needs deploying.
        var detail = SessionsRow(BuildWith(new FleetToolCheck(
            FleetToolVerdict.GatewayRefusedKey, null, @"C:\x\bin", "refused")))!.Detail;

        Assert.Contains("Gateway", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EVERY session", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("older than this Director", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GatewayRefusedTheKey_OffersTheCauseWithoutAssertingIt()
    {
        // The evidence underneath this row is a single false from RegisterSessionKeyAsync, which
        // returns the same value for a hub-side refusal, an unconnected tunnel and a transport
        // failure. Stating "the Gateway is out of date" as fact would be a confident diagnosis drawn
        // from evidence that cannot support it - the very failure mode this row exists to end, which
        // makes it the worst possible place to reintroduce it.
        var detail = SessionsRow(BuildWith(new FleetToolCheck(
            FleetToolVerdict.GatewayRefusedKey, null, @"C:\x\bin", "refused")))!.Detail;

        Assert.Contains("did not accept", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("most often", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is out of date", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GatewayRefusedTheKey_DoesNotRouteToAToolRepair()
    {
        // The install is fine. Offering to repair it would spend the user's attempt on a guaranteed
        // no-op and land them back on the same red row - the shape issue #1045 fixed for the
        // stale-install case, which must not be reintroduced by a new verdict.
        var row = SessionsRow(BuildWith(new FleetToolCheck(
            FleetToolVerdict.GatewayRefusedKey, null, @"C:\x\bin", "refused")));

        Assert.NotEqual(HomeCheckAction.OpenTools, row!.Action);
        Assert.NotEqual(HomeCheckAction.RepairTools, row.Action);
        Assert.Equal(HomeCheckAction.OpenSettings, row.Action);
    }

    [Fact]
    public void EveryVerdictInTheEnumHasBeenDecided_NoneFallsThroughToSilence()
    {
        // The defect this whole row exists to prevent is a real state rendering as nothing. It came
        // back anyway, through a state that had no verdict rather than no row - so pin the decision
        // itself: every verdict must have been CONSIDERED here, and the two that legitimately show
        // no row must be named rather than reached by falling off the end of the switch.
        //
        // A verdict added later fails this test until someone decides what it renders. That is the
        // point: silence must be a choice, never a default.
        var showNoRowOnPurpose = new[] { FleetToolVerdict.Unchecked, FleetToolVerdict.NoGateway };

        foreach (FleetToolVerdict verdict in Enum.GetValues<FleetToolVerdict>())
        {
            var row = SessionsRow(BuildWith(new FleetToolCheck(verdict, null, @"C:\x\bin", "detail")));
            if (Array.IndexOf(showNoRowOnPurpose, verdict) >= 0)
            {
                Assert.Null(row);
                continue;
            }

            Assert.True(row is not null,
                $"{verdict} renders no Sessions row at all. If that is intended, add it to the list "
                + "above with the reason; if it is not, this is the blank-page defect returning.");
        }
    }
}
