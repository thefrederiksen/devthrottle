using System.Text.Json;
using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// License Switching phase 6 (Track C): the key holder's daily approver check. Each approver record that names this
/// machine is checked with its account, each of this Director's own browser profiles is checked with none when the
/// machine offers them, and all of it goes up in one report. Runs against a fake license tool, a fake tunnel and a
/// fake profile list; no browser and no sign-in exists anywhere.
/// </summary>
public sealed class LicenseApproverCheckTests : IDisposable
{
    private const string AccountA = "account-a@example.com";
    private const string AccountB = "account-b@example.com";

    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLicenseCommandLine _tool = new();
    private readonly FakeApproverChannel _channel = new();
    private readonly ControlledDelay _delay = new();
    private readonly FakeBrowserProfiles _profiles = new FakeBrowserProfiles().Add("work-chrome", "Work Chrome").Add("home-edge", "Home Edge");
    private LicenseSwitching? _licenses;

    public void Dispose()
    {
        _licenses?.Dispose();
        _folder.Dispose();
    }

    private LicenseApproverCheck Check(bool offers = false, bool enabled = true, bool holdTheKey = true)
    {
        _licenses = new LicenseSwitching(_tool, _folder.Path, _clock.UtcNow, "machine-a", "owner", enabled,
            signInDelay: _delay.Wait, offersApprovers: offers, browserProfiles: _profiles);
        if (holdTheKey) Assert.True(_licenses.HolderLock.TryAcquire());
        _licenses.ApproverCheck.AttachChannel(_channel);
        return _licenses.ApproverCheck;
    }

    private void Answers(string profile, string? asked, string state, string? account = null, string? shown = null) =>
        _tool.ApproverChecks[(profile, asked)] = new LicenseApproverToolAnswer
        {
            State = state, AccountEmail = account ?? asked, ShownAccount = shown, ToolAnswered = true,
        };

    [Fact]
    public async Task TickAsync_AssignedRecords_AreCheckedWithTheirAccount_AndGoUpInOneReport()
    {
        _channel.Assign(AccountA, "work-chrome").Assign(AccountB, "home-edge", LicenseApproverSources.Registered);
        Answers("work-chrome", AccountA, LicenseApproverStates.CanApprove, shown: AccountA);
        Answers("home-edge", AccountB, LicenseApproverStates.NeedsFreshSignIn, shown: AccountB);

        var report = await Check().TickAsync(CancellationToken.None);

        Assert.Equal(new[] { ("work-chrome", (string?)AccountA), ("home-edge", (string?)AccountB) }, _tool.ApproverCheckCalls);
        var sent = Assert.Single(_channel.Reports);
        Assert.Same(report, sent);
        Assert.False(sent.OffersApprovers);
        Assert.Equal(
            new[] { ("work-chrome", "Work Chrome", AccountA, LicenseApproverStates.CanApprove), ("home-edge", "Home Edge", AccountB, LicenseApproverStates.NeedsFreshSignIn) },
            sent.Rows.Select(r => (r.BrowserProfile, r.BrowserProfileName!, r.AccountEmail!, r.State)));
        Assert.All(sent.Rows, r => Assert.Equal(_clock.Now, r.CheckedAtUtc));
    }

    [Fact]
    public async Task TickAsync_OfferOff_OwnProfilesAreNotChecked()
    {
        _channel.Assign(AccountA, "work-chrome");

        await Check(offers: false).TickAsync(CancellationToken.None);

        Assert.Equal(new[] { ("work-chrome", (string?)AccountA) }, _tool.ApproverCheckCalls);
        Assert.Equal(0, _tool.ApproverCheckCalls.Count(c => c.Email is null));
    }

    [Fact]
    public async Task TickAsync_OfferOn_EveryOwnProfileIsCheckedWithNoAccount_AndTheReportSaysItOffers()
    {
        _channel.Assign(AccountA, "work-chrome");
        Answers("work-chrome", AccountA, LicenseApproverStates.CanApprove, shown: AccountA);
        Answers("work-chrome", null, LicenseApproverStates.CanApprove, account: AccountA, shown: AccountA);
        Answers("home-edge", null, LicenseApproverStates.SignedOut, account: null);

        var report = await Check(offers: true).TickAsync(CancellationToken.None);

        Assert.Equal(new[] { ("work-chrome", (string?)AccountA), ("work-chrome", null), ("home-edge", null) }, _tool.ApproverCheckCalls);
        Assert.True(report!.OffersApprovers);
        var signedOut = report.Rows.Single(r => r.BrowserProfile == "home-edge");
        Assert.Equal((null, LicenseApproverStates.SignedOut, "Home Edge"), (signedOut.AccountEmail, signedOut.State, signedOut.BrowserProfileName));
    }

    [Fact]
    public async Task TickAsync_LicenseSwitchingOff_ChecksNothing_AndAsksTheGatewayNothing()
    {
        _channel.Assign(AccountA, "work-chrome");

        var report = await Check(offers: true, enabled: false, holdTheKey: false).TickAsync(CancellationToken.None);

        Assert.Null(report);
        Assert.Empty(_tool.ApproverCheckCalls);
        Assert.Equal(0, _channel.AssignmentCalls);
        Assert.Empty(_channel.Reports);
    }

    [Fact]
    public async Task TickAsync_NotTheKeyHolder_ChecksNothing_AndAsksTheGatewayNothing()
    {
        // Another Director on this machine holds the sign-ins; it is the one that checks.
        var check = Check(offers: true, holdTheKey: false);
        using var holder = new LicenseKeyHolderLock(_folder.Path);
        Assert.True(holder.TryAcquire());
        Assert.False(_licenses!.HolderLock.TryAcquire());
        _channel.Assign(AccountA, "work-chrome");

        var report = await check.TickAsync(CancellationToken.None);

        Assert.Null(report);
        Assert.Empty(_tool.ApproverCheckCalls);
        Assert.Equal(0, _channel.AssignmentCalls);
    }

    [Fact]
    public async Task TickAsync_ToolGaveNoAnswer_IsReportedCheckFailed_NeverLeftOut()
    {
        _channel.Assign(AccountA, "work-chrome").Assign(AccountB, "home-edge");
        Answers("work-chrome", AccountA, LicenseApproverStates.CanApprove, shown: AccountA);
        // home-edge has no scripted answer: the tool gave nothing readable.

        var report = await Check().TickAsync(CancellationToken.None);

        var failed = report!.Rows.Single(r => r.BrowserProfile == "home-edge");
        Assert.Equal((AccountB, LicenseApproverStates.CheckFailed), (failed.AccountEmail, failed.State));
        Assert.Equal("The license tool gave no readable answer to the check on this machine.", failed.Detail);
        Assert.Equal(2, report.Rows.Count);
    }

    [Fact]
    public async Task TickAsync_EveryRowFailed_TheReportIsStillSent()
    {
        _channel.Assign(AccountA, "work-chrome").Assign(AccountB, "home-edge");

        await Check(offers: true).TickAsync(CancellationToken.None);

        var sent = Assert.Single(_channel.Reports);
        Assert.Equal(4, sent.Rows.Count);
        Assert.All(sent.Rows, r => Assert.Equal(LicenseApproverStates.CheckFailed, r.State));
    }

    [Fact]
    public async Task TickAsync_NothingAssignedAndNothingOffered_SendsAnEmptyReport()
    {
        // The Gateway learns this machine was checked and holds no approver, rather than hearing nothing.
        var report = await Check(offers: false).TickAsync(CancellationToken.None);

        Assert.Empty(Assert.Single(_channel.Reports).Rows);
        Assert.NotNull(report);
    }

    [Fact]
    public async Task TickAsync_RunsOnceADay()
    {
        _channel.Assign(AccountA, "work-chrome");
        var check = Check();

        Assert.NotNull(await check.TickAsync(CancellationToken.None));
        _clock.Advance(TimeSpan.FromHours(23));
        Assert.Null(await check.TickAsync(CancellationToken.None));
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(await check.TickAsync(CancellationToken.None));

        Assert.Equal(2, _channel.Reports.Count);
        Assert.Equal(2, _tool.ApproverCheckCalls.Count);
    }

    [Fact]
    public async Task TickAsync_GatewayKeepsNoApproverRecords_NoCheckRuns_AndOneRunsOnceItDoes()
    {
        _channel.Assign(AccountA, "work-chrome");
        _channel.CanReach = false;
        var check = Check();

        Assert.Null(await check.TickAsync(CancellationToken.None));
        Assert.Empty(_tool.ApproverCheckCalls);

        _channel.CanReach = true;
        Assert.NotNull(await check.TickAsync(CancellationToken.None));
        Assert.Single(_tool.ApproverCheckCalls);
    }

    [Fact]
    public async Task TickAsync_AssignmentsCannotBeAsked_NoCheckRuns_AndItIsTriedAgain()
    {
        _channel.Assign(AccountA, "work-chrome");
        _channel.AssignmentsFailWith = "the Gateway stream is not connected";
        var check = Check();

        Assert.Null(await check.TickAsync(CancellationToken.None));
        Assert.Empty(_tool.ApproverCheckCalls);

        _channel.AssignmentsFailWith = null;
        Assert.NotNull(await check.TickAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TickAsync_ReportNotTaken_IsSentAgainAtTheNextLook_WithoutCheckingAgain()
    {
        _channel.Assign(AccountA, "work-chrome");
        Answers("work-chrome", AccountA, LicenseApproverStates.SignedOut);
        _channel.ReportFailWith = "the Gateway stream is not connected";
        var check = Check();

        Assert.Null(await check.TickAsync(CancellationToken.None));
        _channel.ReportFailWith = null;
        _clock.Advance(LicenseApproverCheck.CheckInterval);
        var report = await check.TickAsync(CancellationToken.None);

        Assert.Single(_tool.ApproverCheckCalls);
        Assert.Equal(LicenseApproverStates.SignedOut, Assert.Single(Assert.Single(_channel.Reports).Rows).State);
        Assert.NotNull(report);
    }

    [Fact]
    public async Task TickAsync_ReportUnsentForADay_IsCheckedAgain_NotSentStale()
    {
        // The Gateway refuses a row older than its not-checked window, so a day-old report is never sent.
        _channel.Assign(AccountA, "work-chrome");
        _channel.ReportFailWith = "down";
        var check = Check();
        await check.TickAsync(CancellationToken.None);

        _channel.ReportFailWith = null;
        _clock.Advance(LicenseApproverCheck.RunInterval);
        await check.TickAsync(CancellationToken.None);

        Assert.Equal(2, _tool.ApproverCheckCalls.Count);
        Assert.Equal(_clock.Now, Assert.Single(Assert.Single(_channel.Reports).Rows).CheckedAtUtc);
    }

    [Fact]
    public async Task TickAsync_DetailIsTheDirectorsOwnWords_NeverTheTools()
    {
        _channel.Assign(AccountA, "work-chrome").Assign(AccountB, "home-edge");
        Answers("work-chrome", AccountA, LicenseApproverStates.WrongAccount, shown: "account-c@example.com");
        Answers("home-edge", AccountB, LicenseApproverStates.CheckFailed);

        var report = await Check().TickAsync(CancellationToken.None);

        Assert.Equal(
            new[] { "The browser profile is signed in to claude.ai as another account.", "The check could not open or read the approval page in this browser profile." },
            report!.Rows.Select(r => r.Detail));
        Assert.All(report.Rows, r => Assert.True(r.Detail!.Length <= LicenseApproverContract.MaxDetailLength));
        Assert.DoesNotContain(FakeLicenseCommandLine.ToolTextMarker, JsonSerializer.Serialize(report));
    }

    [Fact]
    public async Task TickAsync_ProfileNameOfAShapeTheGatewayRefuses_IsNotSent_TheRowStillIs()
    {
        _profiles.Profiles.Clear();
        _profiles.Add("odd", "Work (old)");
        _channel.Assign(AccountA, "odd").Assign(AccountB, "not-on-this-machine");

        var report = await Check().TickAsync(CancellationToken.None);

        Assert.Equal(new[] { ("odd", (string?)null), ("not-on-this-machine", (string?)null) },
            report!.Rows.Select(r => (r.BrowserProfile, r.BrowserProfileName)));
    }

    [Fact]
    public async Task TickAsync_MoreThanAReportTakes_StopsAtThirtyTwoRows()
    {
        for (var i = 0; i < 40; i++)
            _channel.Assign($"account-{i}@example.com", "work-chrome");

        var report = await Check().TickAsync(CancellationToken.None);

        Assert.Equal(LicenseApproverContract.MaxCheckRows, report!.Rows.Count);
        Assert.Equal(LicenseApproverContract.MaxCheckRows, _tool.ApproverCheckCalls.Count);
    }

    [Fact]
    public async Task TickAsync_ASignInIsRunning_TheCheckWaitsForIt_AndRunsAfter()
    {
        _channel.Assign(AccountA, "work-chrome");
        var check = Check();
        var signIn = await _licenses!.BrowserLane.EnterWorkAsync("a test sign-in", TimeSpan.FromMinutes(10), CancellationToken.None);

        // Everything before the lane completes at once, so the tick is waiting in the lane when the call returns.
        var tick = check.TickAsync(CancellationToken.None);
        Assert.False(tick.IsCompleted);
        Assert.Empty(_tool.ApproverCheckCalls);

        signIn!.Dispose();
        Assert.NotNull(await tick);
        Assert.Single(_tool.ApproverCheckCalls);
    }

    [Fact]
    public async Task TickAsync_AnApprovalArrivesDuringACheck_WaitsForTheCheckToEnd()
    {
        _channel.Assign(AccountA, "work-chrome");
        _tool.ApproverCheckGate = new TaskCompletionSource();
        var check = Check();
        var tick = check.TickAsync(CancellationToken.None);
        Assert.True(_tool.ApproverCheckRunning, "the check is held running inside the lane");

        var approval = _licenses!.BrowserLane.EnterWorkAsync("a test approval", TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.False(approval.IsCompleted);

        _tool.ApproverCheckGate.SetResult();
        await tick;
        (await approval)!.Dispose();
    }

    [Fact]
    public async Task TickAsync_AnApprovalArrivesDuringARunOfTwoChecks_GoesInBetweenThem_NotAfterBoth()
    {
        _channel.Assign(AccountA, "work-chrome").Assign(AccountB, "home-edge");
        var firstRow = new TaskCompletionSource();
        _tool.ApproverCheckGate = firstRow;
        var check = Check();
        var tick = check.TickAsync(CancellationToken.None);
        var approval = _licenses!.BrowserLane.EnterWorkAsync("a test approval", TimeSpan.FromMinutes(10), CancellationToken.None);

        // The second check is not held: were it to take the lane back first, it would run to the end before the
        // approval got in.
        _tool.ApproverCheckGate = null;
        firstRow.SetResult();
        var inLane = await approval;

        Assert.Single(_tool.ApproverCheckCalls);
        Assert.False(tick.IsCompleted);
        inLane!.Dispose();
        Assert.Equal(2, (await tick)!.Rows.Count);
    }

    // ----- an approval or sign-in that meets a running check never acts after it is too late (review of 4123) -----

    private const string SignIn = "abcdefabcdefabcdefabcdefabcdef12";

    private LicenseApproveRequest Approve() => new()
    {
        SignInId = SignIn,
        AccountEmail = AccountA,
        AuthorizeUrl = "https://claude.example/oauth/authorize?login_hint=account-a",
        BrowserProfile = "work-chrome",
        SealToRecipient = "the-waiting-machine",
        NotAfterUtc = _clock.Now.AddMinutes(3),
    };

    /// <summary>A run of one check, held running in the lane until the test lets it go.</summary>
    private async Task<(Task<LicenseApproverCheckReport?> Tick, TaskCompletionSource Release)> ACheckIsRunningAsync()
    {
        _channel.Assign(AccountA, "work-chrome");
        var release = new TaskCompletionSource();
        _tool.ApproverCheckGate = release;
        var check = Check();
        _licenses!.Approver.AttachSeal(new FakeApprovalSeal());
        var tick = check.TickAsync(CancellationToken.None);
        Assert.True(_tool.ApproverCheckRunning, "the check is held running inside the lane");
        await Task.CompletedTask;
        return (tick, release);
    }

    [Fact]
    public async Task Approve_DuringACheck_ThatOutlastsTheTimeToApproveInTime_IsRefusedExpired_AndPressesNothing()
    {
        var (tick, release) = await ACheckIsRunningAsync();

        var approval = _licenses!.Approver.ApproveAsync(Approve(), CancellationToken.None);
        // It waits no longer than the Gateway's wait for it leaves room for the tool to run.
        Assert.Equal(new[] { LicenseApprover.ToolStartAllowance }, _delay.Asked);
        Assert.Equal(TimeSpan.FromSeconds(30), LicenseApprover.ToolStartAllowance);
        Assert.False(approval.IsCompleted);

        _clock.Advance(TimeSpan.FromSeconds(31));
        _delay.PassAll();
        var answer = await approval;

        Assert.Equal((LicenseApprovalOutcomes.Refused, LicenseRenewalErrors.Expired), (answer.Outcome, answer.ReasonCode));
        Assert.Equal(LicenseApprover.TooLateMessage, answer.Message);
        Assert.Empty(_tool.ApproveCalls);
        release.SetResult();
        await tick;
    }

    [Fact]
    public async Task Approve_DuringACheck_ThatEndsOnlyAfterTheTimeRanOut_IsRefusedExpired_AndPressesNothing()
    {
        // The check ends before the wait's timer fires, but the time to approve in has already gone: the time is
        // looked at again once the approval is in the lane.
        var (tick, release) = await ACheckIsRunningAsync();
        var approval = _licenses!.Approver.ApproveAsync(Approve(), CancellationToken.None);

        _clock.Advance(TimeSpan.FromSeconds(31));
        release.SetResult();
        var answer = await approval;

        Assert.Equal((LicenseApprovalOutcomes.Refused, LicenseRenewalErrors.Expired), (answer.Outcome, answer.ReasonCode));
        Assert.Empty(_tool.ApproveCalls);
        await tick;
    }

    [Fact]
    public async Task Approve_DuringACheck_ThatEndsInTime_Approves()
    {
        var (tick, release) = await ACheckIsRunningAsync();
        var approval = _licenses!.Approver.ApproveAsync(Approve(), CancellationToken.None);

        _clock.Advance(TimeSpan.FromSeconds(29));
        release.SetResult();
        var answer = await approval;

        Assert.Equal(LicenseApprovalOutcomes.Approved, answer.Outcome);
        Assert.Single(_tool.ApproveCalls);
        await tick;
    }

    [Fact]
    public async Task SignIn_TheGatewayAskedFor_DuringACheck_ThatOutlastsItsTime_FailsExpired_AndStartsNoLogin()
    {
        var (tick, release) = await ACheckIsRunningAsync();

        var signIn = _licenses!.SignIns.SignInAsync(new LicenseSignInRequest
        {
            SignInId = SignIn, AccountEmail = AccountA, Reason = LicenseSignInReasons.Renewal, NotAfterUtc = _clock.Now.AddMinutes(10),
        }, CancellationToken.None);
        Assert.Equal(new[] { LicenseSignInCoordinator.StartAllowance }, _delay.Asked);
        Assert.Equal(TimeSpan.FromSeconds(30), LicenseSignInCoordinator.StartAllowance);

        _clock.Advance(TimeSpan.FromSeconds(31));
        _delay.PassAll();
        var result = await signIn;

        Assert.Equal((LicenseSignInOutcomes.Failed, LicenseRenewalErrors.Expired), (result.Outcome, result.ReasonCode));
        // Its own sentence: no approval was asked for, so "no approval came back" would send the owner to the wrong
        // machine.
        Assert.Equal(LicenseSignInCoordinator.TooLateMessage, result.Message);
        Assert.Empty(_tool.SignInCalls);
        Assert.Empty(_tool.BrowserQuestions);
        release.SetResult();
        await tick;
    }

    [Fact]
    public async Task TickAsync_AReportTakenLate_TheNextChecksRunADayAfterTheyStarted_NotADayAfterTheReport()
    {
        // The Gateway reads a record as not checked 26 hours after its check, so the next checks cannot wait for a
        // day after a report that was taken late.
        _channel.Assign(AccountA, "work-chrome");
        _channel.ReportFailWith = "the Gateway stream is not connected";
        var check = Check();
        Assert.Null(await check.TickAsync(CancellationToken.None));

        _channel.ReportFailWith = null;
        _clock.Advance(TimeSpan.FromHours(23));
        Assert.NotNull(await check.TickAsync(CancellationToken.None));
        Assert.Single(_tool.ApproverCheckCalls);

        _clock.Advance(TimeSpan.FromHours(1));
        Assert.NotNull(await check.TickAsync(CancellationToken.None));
        Assert.Equal(2, _tool.ApproverCheckCalls.Count);
    }

    [Fact]
    public async Task SignIn_TheGatewayAskedFor_ThatWaitedLongForTheOneAtATimeLock_WithNoCheckRunning_StillRuns()
    {
        // Second review of 4123, finding 1: the allowance limits only the wait for a running check. A sign-in that
        // waited for the daily renewal to finish runs as it did before the lane existed.
        Check();
        _tool.LocalSignIns[AccountA] = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Renewed };
        _tool.LocalSignIns[AccountB] = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Added };
        var renewalRuns = new TaskCompletionSource();
        _tool.SignInGate = renewalRuns;
        var renewal = _licenses!.SignIns.RenewAsync(AccountA, hasBrowserProfileHere: true, CancellationToken.None);
        Assert.Single(_tool.SignInCalls);

        var signIn = _licenses.SignIns.SignInAsync(new LicenseSignInRequest
        {
            SignInId = SignIn, AccountEmail = AccountB, Reason = LicenseSignInReasons.Manual, NotAfterUtc = _clock.Now.AddMinutes(10),
        }, CancellationToken.None);
        Assert.False(signIn.IsCompleted);
        _clock.Advance(TimeSpan.FromSeconds(31));
        _tool.SignInGate = null;
        renewalRuns.SetResult();

        Assert.Equal(LicenseSignInOutcomes.Renewed, (await renewal).Outcome);
        var result = await signIn;
        Assert.Equal((LicenseSignInOutcomes.Added, (string?)null), (result.Outcome, result.ReasonCode));
        Assert.Equal(new[] { AccountA, AccountB }, _tool.SignInCalls.Select(c => c.Email));
        Assert.Empty(_delay.Asked);
    }
}
