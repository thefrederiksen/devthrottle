using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// The fold that makes auto-update legible (issue #1030).
///
/// The defect these tests exist for is not a wrong answer, it is one answer where there should have
/// been six. Auto-update worked - it carried the owner's machine from 1.8.0 to 1.8.6 - and up to date,
/// has not checked yet, downloading, downloaded and waiting, a check that failed, and a release whose
/// downloads had not been attached yet all rendered as the same unchanged version number. So the
/// central test here does not check any single string: it checks that the situations are TOLD APART.
/// </summary>
public class UpdateStatusFoldTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private static UpdateStatusFacts Facts(
        UpdaterState state,
        UpdateProgress? live = null,
        int sessions = 0,
        bool launcherRunning = true,
        bool enabled = true,
        string current = "1.9.0")
        => new(current, enabled, state, live, sessions, launcherRunning, Now);

    // ---- The defect itself ------------------------------------------------

    [Fact]
    public void SixSituationsThatUsedToLookIdentical_AllRenderDifferently()
    {
        var situations = new (string Name, UpdateStatusFacts Facts)[]
        {
            ("up to date",
                Facts(new UpdaterState { LastCheckedAt = Now.AddMinutes(-4), LastCheckOutcome = "UpToDate" })),
            ("has not checked yet",
                Facts(new UpdaterState())),
            ("downloading",
                Facts(new UpdaterState(), live: new UpdateProgress(UpdatePhase.Downloading, "1.9.1", 50, 100))),
            ("downloaded and waiting",
                Facts(new UpdaterState { StagedVersion = "1.9.1", LastCheckedAt = Now.AddMinutes(-4) })),
            ("the check failed",
                Facts(new UpdaterState
                {
                    LastCheckedAt = Now.AddMinutes(-4),
                    LastCheckOutcome = "Failed",
                    LastCheckError = "the network is unreachable",
                })),
            ("the release has no downloads attached yet",
                Facts(new UpdaterState
                {
                    LastCheckedAt = Now.AddMinutes(-4),
                    LastCheckOutcome = "ReleaseNotReady",
                    LastCheckLatestVersion = "1.9.1",
                })),
        };

        var rendered = situations
            .Select(s => (s.Name, View: UpdateStatusFold.Fold(s.Facts)))
            .ToList();

        // Every one says something, in its own words, in its own state.
        foreach (var (name, view) in rendered)
        {
            Assert.False(string.IsNullOrWhiteSpace(view.Headline), $"{name} has no headline");
            Assert.False(string.IsNullOrWhiteSpace(view.Detail), $"{name} has no detail");
            Assert.False(string.IsNullOrWhiteSpace(view.Tooltip), $"{name} has no tooltip");
        }

        Assert.Equal(rendered.Count, rendered.Select(r => r.View.State).Distinct().Count());
        Assert.Equal(rendered.Count, rendered.Select(r => $"{r.View.Headline}|{r.View.Detail}").Distinct().Count());
    }

    // ---- Individual situations --------------------------------------------

    [Fact]
    public void UpToDate_SaysTheVersionAndWhenItWasChecked()
    {
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            LastCheckedAt = Now.AddMinutes(-4),
            LastCheckOutcome = "UpToDate",
        }));

        Assert.Equal("UpToDate", view.State);
        Assert.Contains("1.9.0", view.Detail);
        // "Up to date" with no time attached is a claim about the past dressed as a claim about now.
        Assert.Contains("4 minutes ago", view.Detail);
        Assert.True(view.CanCheckNow);
    }

    [Fact]
    public void NeverChecked_DoesNotClaimToBeUpToDate()
    {
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState()));

        Assert.Equal("NotCheckedYet", view.State);
        Assert.DoesNotContain("UP TO DATE", view.Headline);
        Assert.True(view.CanCheckNow);
    }

    [Fact]
    public void ReleaseNotReady_IsNeitherUpToDateNorAFailure()
    {
        // Issue #1079: a release is "latest" the instant it is published and its downloads attach about
        // five and a half minutes later. Reporting that window as "up to date" is how it stayed hidden
        // for the entire life of the product.
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            LastCheckedAt = Now.AddMinutes(-1),
            LastCheckOutcome = "ReleaseNotReady",
            LastCheckLatestVersion = "1.9.1",
        }));

        Assert.Equal("ReleaseNotReady", view.State);
        Assert.Contains("1.9.1", view.Detail);
        Assert.DoesNotContain("UP TO DATE", view.Headline);
        Assert.DoesNotContain("FAILED", view.Headline);
    }

    [Fact]
    public void CheckFailed_CarriesTheReasonAndSaysTheMachineDoesNotKnow()
    {
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            LastCheckedAt = Now.AddMinutes(-10),
            LastCheckOutcome = "Failed",
            LastCheckError = "the network is unreachable",
        }));

        Assert.Equal("CheckFailed", view.State);
        Assert.Contains("the network is unreachable", view.Tooltip);
        Assert.Contains("does not know", view.Tooltip);
        Assert.True(view.CanCheckNow);
    }

    // ---- A staged build, and what the launcher decided about it -----------

    [Fact]
    public void Staged_WithSessionsRunning_SaysItIsWaitingAndOffersNothingThatWouldInterruptThem()
    {
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState { StagedVersion = "1.9.1" },
            sessions: 3));

        Assert.Equal("StagedWaitingForSessions", view.State);
        Assert.Contains("3 sessions", view.Detail);
        // The whole point of the hold is that live work is not interrupted, so an install action here
        // would offer to destroy exactly what the hold protects.
        Assert.False(view.CanInstallNow);
        Assert.Null(view.InstallNowLabel);
    }

    [Fact]
    public void Staged_WithNoSessionsAndALauncher_OffersTheImmediateInstall()
    {
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState { StagedVersion = "1.9.1" },
            sessions: 0, launcherRunning: true));

        Assert.Equal("StagedReady", view.State);
        Assert.True(view.CanInstallNow);
        Assert.False(string.IsNullOrWhiteSpace(view.InstallNowLabel));
    }

    [Fact]
    public void Staged_WithNoLauncher_DoesNotOfferAnInstallItCannotCarryOut()
    {
        // No launcher means nothing on the machine can stop, swap and start the Director, so the button
        // would do nothing. Offering it anyway is the exact defect rule 7 was written for.
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState { StagedVersion = "1.9.1" },
            sessions: 0, launcherRunning: false));

        Assert.False(view.CanInstallNow);
        Assert.Contains("restart", view.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LauncherHeldBecauseItCouldNotSeeTheDirector_IsNotShownAsAnOrdinaryWait()
    {
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            StagedVersion = "1.9.1",
            LastApplyVersion = "1.9.1",
            LastApplyDecision = "HeldBecauseUnknown",
            LastApplyDecisionAt = Now.AddMinutes(-5),
        }));

        Assert.Equal("StagedLauncherCannotSee", view.State);
        Assert.Contains("could not reach", view.Detail);
    }

    [Fact]
    public void RolledBack_IsSaidOutLoud()
    {
        // A person had no way to learn this at all before: the version number goes back to what it was,
        // which is indistinguishable from never having updated.
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            LastApplyDecision = "RolledBack",
            LastApplyVersion = "1.9.1",
            LastApplyDecisionAt = Now.AddMinutes(-30),
            LastApplyDetail = "the new build never answered.",
            LastCheckOutcome = "UpToDate",
            LastCheckedAt = Now.AddMinutes(-2),
        }));

        Assert.Equal("RolledBack", view.State);
        Assert.Contains("1.9.1", view.Detail);
        Assert.Contains("1.9.0", view.Detail);
    }

    [Fact]
    public void RolledBack_KeepsBeingSaid_ForAsLongAsTheMachineIsStuckOnTheRestoredBuild()
    {
        // A clock must not silence this. A machine still sitting on the build that was restored has
        // nothing new to report, and that is exactly why it must keep reporting: going quiet after a
        // day would hide the one case the message exists for.
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            LastApplyDecision = "RolledBack",
            LastApplyVersion = "1.9.1",
            LastApplyDecisionAt = Now.AddDays(-40),
            LastCheckOutcome = "UpToDate",
            LastCheckedAt = Now.AddMinutes(-2),
        }));

        Assert.Equal("RolledBack", view.State);
    }

    [Fact]
    public void RolledBack_StopsBeingSaid_OnceTheMachineHasMovedPastTheBuildThatFailed()
    {
        // The owner's Mac, 2026-09-08. Version 2.0.5 was rolled back five days earlier because the
        // screen was locked at 1:57 in the morning and no build could start; 2.0.7 then installed and
        // proved healthy. The panel still showed UPDATE ROLLED BACK on a machine that was fine, and
        // told him "v2.0.7 was put back" - which never happened. 2.0.4 was what was put back.
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState
            {
                LastApplyDecision = "RolledBack",
                LastApplyVersion = "2.0.5",
                LastApplyDecisionAt = Now.AddDays(-5),
                LastCheckOutcome = "UpToDate",
                LastCheckLatestVersion = "2.0.7",
                LastCheckedAt = Now.AddMinutes(-1),
            },
            current: "2.0.7"));

        Assert.Equal("UpToDate", view.State);
        Assert.DoesNotContain("was put back", view.Detail);

        // History, not silence: the version that failed is pinned and will not be offered again, so it
        // stays reachable in the tooltip.
        Assert.Contains("2.0.5", view.Tooltip);
        Assert.Contains("rolled back", view.Tooltip);
    }

    [Fact]
    public void AnInstallThatFailed_StopsBeingSaid_OnceTheMachineHasMovedPastThatVersion()
    {
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState
            {
                LastApplyDecision = "Failed",
                LastApplyVersion = "2.0.5",
                LastApplyDecisionAt = Now.AddDays(-5),
                LastCheckOutcome = "UpToDate",
                LastCheckedAt = Now.AddMinutes(-1),
            },
            current: "2.0.7"));

        Assert.Equal("UpToDate", view.State);
        Assert.Contains("2.0.5", view.Tooltip);
        Assert.Contains("could not be installed", view.Tooltip);
    }

    [Fact]
    public void ARolledBackVersionThatCannotBeRead_KeepsBeingSaid()
    {
        // An unreadable version means "it is not known whether this machine has moved on", and that is
        // a reason to keep saying the failure happened - never a reason to go quiet about it.
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState
            {
                LastApplyDecision = "RolledBack",
                LastApplyVersion = "2.0.5-preview",
                LastApplyDecisionAt = Now.AddDays(-5),
                LastCheckOutcome = "UpToDate",
                LastCheckedAt = Now.AddMinutes(-1),
            },
            current: "2.0.7"));

        Assert.Equal("RolledBack", view.State);
    }

    [Fact]
    public void AnUpdateHeldBecauseNoDisplayIsAwake_SaysThat_AndOffersNothingThatWouldFail()
    {
        // The Mac case. The launcher will not start a Director against a sleeping screen, because the
        // build would die before its first window and be blamed for it. That is a wait, not a fault, and
        // the person reading it can end the wait by touching the keyboard - so it has to be said.
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            StagedVersion = "2.0.7",
            LastApplyVersion = "2.0.7",
            LastApplyDecision = "HeldBecauseNoDisplay",
            LastApplyDecisionAt = Now.AddMinutes(-10),
        }));

        Assert.Equal("StagedWaitingForDisplay", view.State);
        Assert.Contains("display", view.Detail, StringComparison.OrdinalIgnoreCase);

        // Offering it would offer the very thing that is being waited out. A dumb client must never be
        // handed a button whose only outcome is the failure the hold exists to avoid.
        Assert.False(view.CanInstallNow);
        Assert.Null(view.InstallNowLabel);
    }

    [Fact]
    public void RunningSessionsStillOutrankASleepingDisplay()
    {
        // Both are true at once on a busy machine at night. The sessions message is the one the person
        // can act on and the one that promises no work is interrupted, so it stays on top.
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState
            {
                StagedVersion = "2.0.7",
                LastApplyVersion = "2.0.7",
                LastApplyDecision = "HeldBecauseNoDisplay",
                LastApplyDecisionAt = Now.AddMinutes(-10),
            },
            sessions: 2));

        Assert.Equal("StagedWaitingForSessions", view.State);
    }

    [Fact]
    public void ALauncherDecisionAboutADifferentVersion_IsNotReadAsBeingAboutThisDownload()
    {
        // A "held because busy" left over from an earlier download would otherwise describe a build that
        // has since been replaced - a true record making a false statement about the present.
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState
            {
                StagedVersion = "1.9.2",
                LastApplyVersion = "1.9.1",
                LastApplyDecision = "HeldBecauseUnknown",
                LastApplyDecisionAt = Now.AddHours(-3),
            },
            sessions: 0));

        Assert.Equal("StagedReady", view.State);
    }

    // ---- Builds that do not update at all ---------------------------------

    [Fact]
    public void ABuildWithNoUpdater_SaysSo_RatherThanClaimingToBeUpToDate()
    {
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState(), enabled: false));

        Assert.Equal("AutomaticUpdatesOff", view.State);
        Assert.False(view.CanCheckNow);   // a check would do nothing, so none is offered
        Assert.False(view.CanInstallNow);
    }

    // ---- Work in flight ---------------------------------------------------

    [Theory]
    [InlineData(UpdatePhase.Checking, "Checking")]
    [InlineData(UpdatePhase.Downloading, "Downloading")]
    [InlineData(UpdatePhase.Verifying, "Verifying")]
    public void SomethingHappeningNow_OutranksWhateverTheLastCheckConcluded(UpdatePhase phase, string expected)
    {
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState { LastCheckOutcome = "UpToDate", LastCheckedAt = Now.AddHours(-1) },
            live: new UpdateProgress(phase, "1.9.1", 25, 100)));

        Assert.Equal(expected, view.State);
        Assert.True(view.Busy);
        Assert.False(view.CanCheckNow);   // a check is already running; a second would be a no-op
    }

    [Fact]
    public void Downloading_ReportsThePercentageWhenTheSizeIsKnown()
    {
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState(),
            live: new UpdateProgress(UpdatePhase.Downloading, "1.9.1", 25, 100)));

        Assert.Equal(25, view.PercentComplete);
        Assert.Contains("25%", view.Detail);
    }

    // ---- Properties that must hold for every possible answer ---------------

    [Fact]
    public void EveryAnswerIsComplete_AndNeverOffersTwoActionsAtOnce()
    {
        var states = new[]
        {
            new UpdaterState(),
            new UpdaterState { LastCheckOutcome = "UpToDate", LastCheckedAt = Now },
            new UpdaterState { LastCheckOutcome = "Failed", LastCheckError = "boom", LastCheckedAt = Now },
            new UpdaterState { LastCheckOutcome = "ReleaseNotReady", LastCheckLatestVersion = "1.9.1" },
            new UpdaterState { StagedVersion = "1.9.1" },
            new UpdaterState { LastApplyDecision = "RolledBack", LastApplyVersion = "1.9.1" },
            new UpdaterState { LastApplyDecision = "Failed", LastApplyVersion = "1.9.1" },
            new UpdaterState { LastCheckOutcome = "something a newer build wrote" },
        };

        foreach (var state in states)
            foreach (var sessions in new[] { 0, 2 })
                foreach (var launcherRunning in new[] { true, false })
                    foreach (var enabled in new[] { true, false })
                    {
                        var view = UpdateStatusFold.Fold(Facts(state, sessions: sessions, launcherRunning: launcherRunning, enabled: enabled));

                        Assert.False(string.IsNullOrWhiteSpace(view.State));
                        Assert.False(string.IsNullOrWhiteSpace(view.Headline));
                        Assert.False(string.IsNullOrWhiteSpace(view.Detail));
                        Assert.False(string.IsNullOrWhiteSpace(view.Tooltip));
                        Assert.StartsWith("#", view.Accent);
                        Assert.StartsWith("#", view.Background);
                        Assert.StartsWith("#", view.Border);

                        // One panel, one click: the surfaces render a single action, so the fold must
                        // never hand them two to choose between.
                        Assert.False(view.CanCheckNow && view.CanInstallNow);

                        // A label without its permission, or a permission without its label, is how a
                        // dead button ends up on a screen.
                        Assert.Equal(view.CanCheckNow, view.CheckNowLabel is not null);
                        Assert.Equal(view.CanInstallNow, view.InstallNowLabel is not null);
                    }
    }

    [Fact]
    public void AnUnrecognisedOutcomeFromANewerBuild_FallsToNotCheckedYet_NotToUpToDate()
    {
        // An older Director reading a state file a newer one wrote must not turn a word it does not know
        // into the most reassuring answer available.
        var view = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            LastCheckOutcome = "SomethingInventedLater",
            LastCheckedAt = Now.AddMinutes(-3),
        }));

        Assert.Equal("NotCheckedYet", view.State);
    }

    // ---- The wording of times ---------------------------------------------

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(240, "4 minutes ago")]
    [InlineData(3600, "an hour ago")]
    [InlineData(3600 * 5, "5 hours ago")]
    [InlineData(3600 * 30, "yesterday")]
    [InlineData(3600 * 24 * 4, "4 days ago")]
    public void Describe_PutsAMomentInWordsAPersonReadsAtAGlance(int secondsAgo, string expected)
        => Assert.Equal(expected, UpdateStatusFold.Describe(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void Describe_NeverClaimsTheFuture_WhenTheClockHasMoved()
        => Assert.Equal("just now", UpdateStatusFold.Describe(Now.AddMinutes(5), Now));

    [Fact]
    public void Describe_SaysNever_RatherThanNothing()
        => Assert.Equal("never", UpdateStatusFold.Describe(null, Now));

    // ---- The rule the display and the launcher share ----------------------

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, false)]
    public void ShouldApply_IsTheOneRuleBothTheLauncherAndTheOfferedActionUse(bool staged, int sessions, bool expected)
        => Assert.Equal(expected, UpdateApplyRule.ShouldApply(staged, sessions));

    [Fact]
    public void AStaleHeldBecauseBusy_IsNotReadAsStillWaitingForZeroSessions()
    {
        // The launcher held this an hour ago because sessions were running. They have since finished.
        // The record is still true about what happened and false about the machine now, and reading it
        // as current would announce that the update is waiting for zero sessions to end.
        var view = UpdateStatusFold.Fold(Facts(
            new UpdaterState
            {
                StagedVersion = "1.9.1",
                LastApplyVersion = "1.9.1",
                LastApplyDecision = "HeldBecauseBusy",
                LastApplyDecisionAt = Now.AddHours(-1),
            },
            sessions: 0));

        Assert.Equal("StagedReady", view.State);
        Assert.DoesNotContain("0 session", view.Detail);
        Assert.True(view.CanInstallNow);
    }

    // ---- A release that is finished and simply has nothing for this machine ------------

    [Fact]
    public void NoBuildForThisPlatform_IsNotShownAsAPublishWindow()
    {
        // These two shared one line in the checking code and reported "up to date" together, but they
        // are opposites: one gets better by itself, the other never does. Showing this one as "still
        // being built" would promise a wait that has no end.
        var notReady = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            LastCheckOutcome = "ReleaseNotReady",
            LastCheckLatestVersion = "1.9.1",
            LastCheckedAt = Now.AddMinutes(-1),
        }));
        var noBuild = UpdateStatusFold.Fold(Facts(new UpdaterState
        {
            LastCheckOutcome = "NoBuildForThisPlatform",
            LastCheckLatestVersion = "1.9.1",
            LastCheckError = "the release has no cc-director-win-x64.exe",
            LastCheckedAt = Now.AddMinutes(-1),
        }));

        Assert.Equal("NoBuildForThisPlatform", noBuild.State);
        Assert.NotEqual(notReady.State, noBuild.State);
        Assert.NotEqual(notReady.Headline, noBuild.Headline);
        Assert.Contains("cc-director-win-x64.exe", noBuild.Tooltip);
        // It is a report, not a wait - so it does not promise that looking again will help.
        Assert.DoesNotContain("still being built", noBuild.Detail);
    }
}
