using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// License Switching contract 8.1 and 8.2: the key holder renews this machine's sign-ins once a day and at start, and
/// a renewal that failed travels in the report. Runs against a fake license tool; no sign-in exists anywhere.
/// </summary>
public sealed class LicenseRenewerTests : IDisposable
{
    private const string AccountA = "account-a@example.com";
    private const string AccountB = "account-b@example.com";

    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLicenseCommandLine _tool;
    private readonly FakeReportSink _sink = new();
    private readonly FakeApprovalRelay _relay = new();
    private readonly FakeApprovalSeal _seal = new();
    private readonly LicenseSwitching _licenses;

    public LicenseRenewerTests()
    {
        _tool = new FakeLicenseCommandLine { UtcNow = () => _clock.Now }.Hold(AccountA, active: true).Hold(AccountB);
        _licenses = new LicenseSwitching(_tool, _folder.Path, _clock.UtcNow, "the-windows-machine", "owner", enabled: true);
        _licenses.Reporter.AttachSink(_sink);
        _licenses.SignIns.AttachRelay(_relay);
        _licenses.SignIns.AttachSeal(_seal);
    }

    public void Dispose()
    {
        _licenses.Dispose();
        _folder.Dispose();
    }

    private void Rows(params (string Email, string Result, string? Error)[] rows)
    {
        _tool.RenewRows.Clear();
        foreach (var (email, result, error) in rows)
            _tool.RenewRows.Add(new LicenseRenewalRow { AccountEmail = email, IsActive = email == AccountA, Result = result, Error = error });
    }

    private async Task<LicenseHolding> ReportedAsync(string email)
    {
        var report = await _licenses.Reporter.TickAsync(CancellationToken.None);
        Assert.NotNull(report);
        return report!.Holdings.Single(h => h.AccountEmail == email);
    }

    [Fact]
    public async Task TickAsync_AtStart_TakesTheKeyHolderLockAndRuns_WithNoGatewayAttached()
    {
        // A renewal is this machine's own business: it must not wait for a tunnel.
        _licenses.Reporter.AttachSink(null);
        Rows((AccountA, LicenseRenewalResults.NotDue, null), (AccountB, LicenseRenewalResults.Due, null));
        _tool.LocalSignIns[AccountB] = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Renewed, SignInExpiresAtUtc = _clock.Now.AddDays(29) };

        var rows = await _licenses.Renewer.TickAsync(CancellationToken.None);

        Assert.True(_licenses.HolderLock.IsHeld);
        Assert.Equal(1, _tool.RenewCalls);
        Assert.Equal(_clock.Now, _licenses.Renewer.LastRunUtc);
        // Only the account that is due is signed in again; the one that is not is left alone.
        Assert.Equal(new[] { AccountB }, _tool.SignInCalls.Select(c => c.Email));
        Assert.Equal(LicenseRenewalResults.NotDue, rows!.Single(r => r.AccountEmail == AccountA).Result);
        var renewed = rows!.Single(r => r.AccountEmail == AccountB);
        Assert.Equal(LicenseRenewalResults.Renewed, renewed.Result);
        Assert.Equal(_clock.Now.AddDays(29), renewed.SignInExpiresAtUtc);
    }

    [Fact]
    public async Task TickAsync_RunsOnceADay_NotAtEveryCheck()
    {
        Rows((AccountA, LicenseRenewalResults.NotDue, null));
        await _licenses.Renewer.TickAsync(CancellationToken.None);

        _clock.Advance(LicenseRenewer.CheckInterval);
        Assert.Null(await _licenses.Renewer.TickAsync(CancellationToken.None));
        _clock.Advance(LicenseContract.RenewalRunInterval - LicenseRenewer.CheckInterval - TimeSpan.FromSeconds(1));
        Assert.Null(await _licenses.Renewer.TickAsync(CancellationToken.None));
        Assert.Equal(1, _tool.RenewCalls);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(await _licenses.Renewer.TickAsync(CancellationToken.None));
        Assert.Equal(2, _tool.RenewCalls);
    }

    [Fact]
    public async Task TickAsync_AnotherDirectorHoldsTheKey_RunsNothing()
    {
        using var holder = new LicenseKeyHolderLock(_folder.Path);
        Assert.True(holder.TryAcquire());

        Assert.Null(await _licenses.Renewer.TickAsync(CancellationToken.None));

        Assert.Equal(0, _tool.RenewCalls);
    }

    [Fact]
    public async Task TickAsync_ARenewalThatFailed_TravelsInTheReport_ForTheActiveAccountToo()
    {
        Rows((AccountA, LicenseRenewalResults.Due, null), (AccountB, LicenseRenewalResults.Due, null));
        _tool.LocalSignIns[AccountA] = LicenseSignInAnswer.Failed(LicenseRenewalErrors.BrowserNotSignedIn);
        _tool.LocalSignIns[AccountB] = LicenseSignInAnswer.Failed(LicenseRenewalErrors.WrongAccount);

        await _licenses.Renewer.TickAsync(CancellationToken.None);

        var report = await _licenses.Reporter.TickAsync(CancellationToken.None);
        var active = report!.Holdings.Single(h => h.IsActive);
        var spare = report.Holdings.Single(h => !h.IsActive);
        Assert.Equal(LicenseRenewalErrors.BrowserNotSignedIn, active.RenewalError);
        Assert.Equal(_clock.Now, active.RenewalFailedAtUtc);
        Assert.Equal(LicenseRenewalSentences.For(LicenseRenewalErrors.BrowserNotSignedIn), active.RenewalErrorDetail);
        Assert.Equal(LicenseRenewalErrors.WrongAccount, spare.RenewalError);
    }

    [Fact]
    public async Task TickAsync_AFailureThenASuccessTheNextDay_ClearsTheFailure_AndSaysChanged()
    {
        Rows((AccountB, LicenseRenewalResults.Due, null));
        _tool.LocalSignIns[AccountB] = LicenseSignInAnswer.Failed(LicenseRenewalErrors.ApprovalFailed);
        var failedRun = await _licenses.Renewer.TickAsync(CancellationToken.None);
        Assert.Equal((LicenseRenewalResults.Failed, LicenseRenewalErrors.ApprovalFailed), (failedRun!.Single().Result, failedRun!.Single().Error));
        Assert.Equal(LicenseRenewalErrors.ApprovalFailed, (await ReportedAsync(AccountB)).RenewalError);

        _clock.Advance(LicenseContract.RenewalRunInterval);
        _tool.LocalSignIns[AccountB] = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Renewed };
        await _licenses.Renewer.TickAsync(CancellationToken.None);

        var report = await _licenses.Reporter.TickAsync(CancellationToken.None);
        Assert.Equal(LicenseReportTriggers.Changed, report!.Trigger);
        var holding = report.Holdings.Single(h => h.AccountEmail == AccountB);
        Assert.Null(holding.RenewalError);
        Assert.Null(holding.RenewalFailedAtUtc);
    }

    [Fact]
    public async Task TickAsync_NothingDue_AsksForNoReport()
    {
        Rows((AccountA, LicenseRenewalResults.NotDue, null), (AccountB, LicenseRenewalResults.UnknownExpiry, null));
        await _licenses.Reporter.TickAsync(CancellationToken.None);   // the "connected" report
        _sink.Sent.Clear();

        await _licenses.Renewer.TickAsync(CancellationToken.None);

        // Nothing changed, so the next report tick inside the interval sends nothing.
        Assert.Null(await _licenses.Reporter.TickAsync(CancellationToken.None));
        Assert.Empty(_sink.Sent);
    }

    [Fact]
    public async Task TickAsync_AFailureThatWasRenewedByHand_IsClearedWhenTheAccountIsNoLongerDue()
    {
        Rows((AccountB, LicenseRenewalResults.Due, null));
        _tool.LocalSignIns[AccountB] = LicenseSignInAnswer.Failed(LicenseRenewalErrors.WrongAccount);
        await _licenses.Renewer.TickAsync(CancellationToken.None);
        Assert.Equal(LicenseRenewalErrors.WrongAccount, (await ReportedAsync(AccountB)).RenewalError);

        // The owner signs it in by hand; the next day the tool says it is not due any more.
        _clock.Advance(LicenseContract.RenewalRunInterval);
        Rows((AccountB, LicenseRenewalResults.NotDue, null));
        await _licenses.Renewer.TickAsync(CancellationToken.None);

        var report = await _licenses.Reporter.TickAsync(CancellationToken.None);
        Assert.Equal(LicenseReportTriggers.Changed, report!.Trigger);
        Assert.Null(report.Holdings.Single(h => h.AccountEmail == AccountB).RenewalError);
        Assert.Single(_tool.SignInCalls);
    }

    [Fact]
    public async Task TickAsync_AccountWithNoProfileHere_IsRenewedThroughAnotherMachine()
    {
        Rows((AccountB, LicenseRenewalResults.Due, null));
        _relay.OnRequest = request => Assert.True(_licenses.Approvals.Deliver(new LicenseApprovalResult
        {
            SignInId = request.SignInId,
            Outcome = LicenseApprovalOutcomes.Approved,
            SealedApproval = _seal.SealAsync(FakeRemoteSignIn.ApprovalMarker, "this-machine", request.SignInId, CancellationToken.None).Result,
        }));

        var rows = await _licenses.Renewer.TickAsync(CancellationToken.None);

        var asked = Assert.Single(_relay.Requests);
        Assert.Matches(LicenseContract.SignInIdShape, asked.SignInId);
        Assert.Equal(AccountB, asked.AccountEmail);
        Assert.Equal(_clock.Now + LicenseContract.SignInApprovalWait, asked.NotAfterUtc);
        Assert.Equal(FakeRemoteSignIn.ApprovalMarker, Assert.Single(_tool.RemoteSignIns).TypedIn);
        Assert.Equal((AccountB, true), Assert.Single(_tool.SignInCalls));
        Assert.Equal(LicenseRenewalResults.Renewed, rows!.Single().Result);
        Assert.Null((await ReportedAsync(AccountB)).RenewalError);
    }

    [Fact]
    public async Task TickAsync_NoProfileHereAndTheOtherMachineIsOffline_ReportsWhatTheRelaySaid()
    {
        Rows((AccountB, LicenseRenewalResults.Due, null));
        _relay.OnRequest = request => _licenses.Approvals.Deliver(new LicenseApprovalResult
        {
            SignInId = request.SignInId, Outcome = LicenseApprovalOutcomes.Refused, ReasonCode = LicenseRenewalErrors.ApproverOffline,
        });

        await _licenses.Renewer.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.ApproverOffline, (await ReportedAsync(AccountB)).RenewalError);
        Assert.True(Assert.Single(_tool.RemoteSignIns).Abandoned);
    }

    [Fact]
    public async Task TickAsync_NoProfileHereAndNoGatewayThatRelays_IsNoApprover_AndNoSignInIsStarted()
    {
        Rows((AccountB, LicenseRenewalResults.Due, null));
        _relay.CanRequest = false;

        var rows = await _licenses.Renewer.TickAsync(CancellationToken.None);

        Assert.Equal((LicenseRenewalResults.Failed, LicenseRenewalErrors.NoApprover), (rows!.Single().Result, rows!.Single().Error));
        // The check's own row said no profile is set here, so nothing more was asked and nothing was started: this
        // is every Director's state until a Gateway relays, once a day, and it must not file an error report a day.
        Assert.Empty(_tool.SignInCalls);
        Assert.Empty(_tool.BrowserQuestions);
        Assert.Empty(_tool.RemoteSignIns);
        Assert.Equal(LicenseRenewalErrors.NoApprover, (await ReportedAsync(AccountB)).RenewalError);
    }

    [Fact]
    public async Task TickAsync_ProfileHereAndNoGatewayThatRelays_IsStillRenewedHere()
    {
        Rows((AccountB, LicenseRenewalResults.Due, null));
        _tool.LocalSignIns[AccountB] = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Renewed };
        _relay.CanRequest = false;

        var rows = await _licenses.Renewer.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseRenewalResults.Renewed, rows!.Single().Result);
        Assert.Equal((AccountB, false), Assert.Single(_tool.SignInCalls));
        Assert.Empty(_tool.BrowserQuestions);
    }

    [Fact]
    public async Task TickAsync_AStoredSignInTheToolCouldNotRead_IsReportedAsItsOwnFailure()
    {
        Rows((AccountB, LicenseRenewalResults.Failed, LicenseRenewalErrors.StoreRefused));

        await _licenses.Renewer.TickAsync(CancellationToken.None);

        Assert.Empty(_tool.SignInCalls);
        Assert.Equal(LicenseRenewalErrors.StoreRefused, (await ReportedAsync(AccountB)).RenewalError);
    }

    [Fact]
    public async Task TickAsync_ToolGivesNoAnswer_IsTriedAgainAtTheNextCheck_NotADayLater()
    {
        _tool.FailWith = "cc-devthrottle license renew printed nothing.";
        Assert.Null(await _licenses.Renewer.TickAsync(CancellationToken.None));
        Assert.Null(_licenses.Renewer.LastRunUtc);

        _tool.FailWith = null;
        Rows((AccountA, LicenseRenewalResults.NotDue, null));
        _clock.Advance(LicenseRenewer.CheckInterval);

        Assert.NotNull(await _licenses.Renewer.TickAsync(CancellationToken.None));
        Assert.Equal(2, _tool.RenewCalls);
    }

    [Fact]
    public void Start_LicenseSwitchingTurnedOff_NeverSchedulesTheRenewal()
    {
        using var other = new TempFolder();
        using var off = new LicenseSwitching(_tool, other.Path, _clock.UtcNow, "the-windows-machine", "owner", enabled: false);

        off.Start();

        Assert.False(off.Renewer.IsScheduled);
        Assert.False(off.Reporter.IsScheduled);
        Assert.False(off.HolderLock.IsHeld);
    }

    [Fact]
    public async Task Reporter_KeyHolderThatHoldsNothing_StillReports_WithNoHoldings()
    {
        // Contract 2.4 as amended by 8.3: a machine that only APPROVES other machines' sign-ins holds nothing, and
        // its report is how the Gateway knows which Director to send license-approve to.
        using var other = new TempFolder();
        var nothing = new FakeLicenseCommandLine { UtcNow = () => _clock.Now };
        using var licenses = new LicenseSwitching(nothing, other.Path, _clock.UtcNow, "the-approving-machine", "owner", enabled: true);
        var sink = new FakeReportSink();
        licenses.Reporter.AttachSink(sink);

        var first = await licenses.Reporter.TickAsync(CancellationToken.None);
        _clock.Advance(LicenseContract.ReportInterval);
        var second = await licenses.Reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.Connected, first!.Trigger);
        Assert.Empty(first.Holdings);
        Assert.Equal("the-approving-machine", first.MachineName);
        Assert.Equal(LicenseReportTriggers.Interval, second!.Trigger);
        Assert.Empty(second.Holdings);
        Assert.Equal(2, sink.Sent.Count);
    }
}
