using System.Text.Json;
using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// License Switching contract 8.3 and 8.4, the target machine's side: a sign-in the Gateway asks for, approved in a
/// browser profile here or - when there is none - on another machine through the Gateway. Runs against a fake license
/// tool, a fake relay and a fake seal; no sign-in exists anywhere.
/// </summary>
public sealed class LicenseSignInCoordinatorTests : IDisposable
{
    private const string SignInId = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string AccountB = "account-b@example.com";
    private const string Recipient = "this-machine";

    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLicenseCommandLine _tool;
    private readonly FakeReportSink _sink = new();
    private readonly FakeApprovalRelay _relay = new();
    private readonly FakeApprovalSeal _seal = new();
    private readonly LicenseSwitching _licenses;

    /// <summary>Completed by a test to let the deadline pass; the wait itself never touches a real clock.</summary>
    private readonly TaskCompletionSource _deadline = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private TimeSpan? _waitedFor;

    public LicenseSignInCoordinatorTests()
    {
        _tool = new FakeLicenseCommandLine { UtcNow = () => _clock.Now }.Hold("account-a@example.com", active: true).Hold(AccountB);
        _licenses = Build(enabled: true);
        Assert.True(_licenses.HolderLock.TryAcquire());
    }

    private LicenseSwitching Build(bool enabled)
    {
        var licenses = new LicenseSwitching(_tool, _folder.Path, _clock.UtcNow, "the-windows-machine", "owner", enabled,
            signInDelay: (wait, ct) => { _waitedFor = wait; return _deadline.Task.WaitAsync(ct); });
        licenses.Reporter.AttachSink(_sink);
        licenses.SignIns.AttachRelay(_relay);
        licenses.SignIns.AttachSeal(_seal);
        return licenses;
    }

    public void Dispose()
    {
        _licenses.Dispose();
        _folder.Dispose();
    }

    private LicenseSignInRequest Request(string signInId = SignInId, string email = AccountB) => new()
    {
        SignInId = signInId,
        AccountEmail = email,
        Reason = LicenseSignInReasons.Manual,
        NotAfterUtc = _clock.Now + LicenseContract.SignInNotAfter,
    };

    /// <summary>Answers a request the way the Gateway would: later, as the license-approval-result command.</summary>
    private void AnswerWith(Func<LicenseApprovalRequest, LicenseApprovalResult> answer) =>
        _relay.OnRequest = request => Assert.True(_licenses.Approvals.Deliver(answer(request)));

    private LicenseApprovalResult Approved(LicenseApprovalRequest request) => new()
    {
        SignInId = request.SignInId,
        Outcome = LicenseApprovalOutcomes.Approved,
        SealedApproval = _seal.SealAsync(FakeRemoteSignIn.ApprovalMarker, Recipient, request.SignInId, CancellationToken.None).Result,
    };

    [Fact]
    public async Task SignInAsync_BrowserProfileOnThisMachine_SignsInHereAndAsksNoOtherMachine()
    {
        var ends = _clock.Now.AddDays(29);
        _tool.LocalSignIns[AccountB] = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Renewed, SignedInAtUtc = _clock.Now, SignInExpiresAtUtc = ends };

        var result = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSignInOutcomes.Renewed, result.Outcome);
        Assert.Null(result.ReasonCode);
        Assert.Equal(ends, result.SignInExpiresAtUtc);
        Assert.Empty(_relay.Requests);
        Assert.Empty(_tool.RemoteSignIns);
    }

    [Fact]
    public async Task SignInAsync_NoProfileHere_IsApprovedOnAnotherMachine_AndTheOpenedApprovalIsTypedIn()
    {
        _tool.RemoteAnswer = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Added, SignedInAtUtc = _clock.Now };
        AnswerWith(Approved);

        var result = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSignInOutcomes.Added, result.Outcome);
        var asked = Assert.Single(_relay.Requests);
        var waiting = Assert.Single(_tool.RemoteSignIns);
        Assert.Equal(SignInId, asked.SignInId);
        Assert.Equal(AccountB, asked.AccountEmail);
        Assert.Equal(waiting.AuthorizeUrl, asked.AuthorizeUrl);
        Assert.Equal(_clock.Now + LicenseContract.SignInNotAfter, asked.NotAfterUtc);
        Assert.Equal(FakeRemoteSignIn.ApprovalMarker, waiting.TypedIn);
        Assert.False(waiting.Abandoned);
        Assert.Equal(LicenseContract.SignInNotAfter, _waitedFor);
        // The approval reached the waiting login and nothing else: not the result the Gateway gets.
        Assert.DoesNotContain(FakeRemoteSignIn.ApprovalMarker, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task SignInAsync_NoProfileHereAndNoGatewayThatRelays_IsNoApprover_AndNoLoginIsStarted()
    {
        _relay.CanRequest = false;

        var result = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSignInOutcomes.Failed, result.Outcome);
        Assert.Equal(LicenseRenewalErrors.NoApprover, result.ReasonCode);
        Assert.Empty(_tool.RemoteSignIns);
    }

    [Theory]
    [InlineData(LicenseRenewalErrors.ApproverOffline, LicenseRenewalErrors.ApproverOffline)]
    [InlineData(LicenseRenewalErrors.WrongAccount, LicenseRenewalErrors.WrongAccount)]
    [InlineData(FakeLicenseCommandLine.ToolTextMarker, LicenseRenewalErrors.ApprovalFailed)]
    [InlineData(null, LicenseRenewalErrors.ApprovalFailed)]
    public async Task SignInAsync_ApprovalRefused_EndsTheLoginWithNothingTypedIn_AndCarriesOnlyAContractWord(string? refusedWith, string expected)
    {
        AnswerWith(request => new LicenseApprovalResult
        {
            SignInId = request.SignInId, Outcome = LicenseApprovalOutcomes.Refused, ReasonCode = refusedWith, Message = FakeLicenseCommandLine.ToolTextMarker,
        });

        var result = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSignInOutcomes.Failed, result.Outcome);
        Assert.Equal(expected, result.ReasonCode);
        var waiting = Assert.Single(_tool.RemoteSignIns);
        Assert.Null(waiting.TypedIn);
        Assert.True(waiting.Abandoned);
        // The other machine's sentence never travels on: the Director writes its own (rule 6.1).
        Assert.DoesNotContain(FakeLicenseCommandLine.ToolTextMarker, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task SignInAsync_NoApprovalByTheDeadline_IsExpired_TheLoginIsEnded_AndALateAnswerIsDropped()
    {
        LicenseApprovalRequest? asked = null;
        _relay.OnRequest = request => { asked = request; _deadline.SetResult(); };

        var result = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.Expired, result.ReasonCode);
        var waiting = Assert.Single(_tool.RemoteSignIns);
        Assert.True(waiting.Abandoned);
        Assert.Null(waiting.TypedIn);
        // The answer arrives after the sign-in gave up: nobody is waiting, so it is not delivered.
        Assert.False(_licenses.Approvals.Deliver(Approved(asked!)));
        Assert.Null(waiting.TypedIn);
    }

    [Fact]
    public async Task SignInAsync_SealedApprovalThatDoesNotOpenHere_IsApprovalFailed_AndNothingIsTypedIn()
    {
        // Sealed for ANOTHER sign-in: it must not complete this one.
        AnswerWith(request => new LicenseApprovalResult
        {
            SignInId = request.SignInId,
            Outcome = LicenseApprovalOutcomes.Approved,
            SealedApproval = _seal.SealAsync(FakeRemoteSignIn.ApprovalMarker, Recipient, new string('f', 32), CancellationToken.None).Result,
        });

        var result = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.ApprovalFailed, result.ReasonCode);
        Assert.Null(Assert.Single(_tool.RemoteSignIns).TypedIn);
        Assert.True(_tool.RemoteSignIns[0].Abandoned);
    }

    [Fact]
    public async Task SignInAsync_RequestThatDoesNotReachTheGateway_IsApprovalFailed_AndTheLoginIsEnded()
    {
        _relay.FailWith = "the Gateway stream is not connected";

        var result = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.ApprovalFailed, result.ReasonCode);
        Assert.True(Assert.Single(_tool.RemoteSignIns).Abandoned);
    }

    [Fact]
    public async Task SignInAsync_ToolEndsBeforeItPrintsAUrl_CarriesItsReason_AndAsksNobody()
    {
        _tool.RemoteStartFailure = LicenseSignInAnswer.Failed(LicenseRenewalErrors.StoreRefused);

        var result = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.StoreRefused, result.ReasonCode);
        Assert.Empty(_relay.Requests);
    }

    [Fact]
    public async Task SignInAsync_SameSignInIdAgain_IsReplayed_AndNeverRunsTwice()
    {
        _tool.LocalSignIns[AccountB] = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Added };
        var first = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        var again = await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(again.Replayed);
        Assert.Equal(LicenseSignInOutcomes.Added, again.Outcome);
        Assert.Single(_tool.SignInCalls);
        // ... and after a Director restart: the result is on disk, not only in memory.
        Assert.True(new LicenseSignInResultStore(Path.Combine(_folder.Path, "sign-in-results"), _clock.UtcNow).Find(SignInId)!.Outcome == LicenseSignInOutcomes.Added);
    }

    [Fact]
    public async Task SignInAsync_AfterItsDeadline_IsExpired_AndNothingIsStarted()
    {
        var request = Request();
        _clock.Advance(LicenseContract.SignInNotAfter + TimeSpan.FromSeconds(1));

        var result = await _licenses.SignIns.SignInAsync(request, CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.Expired, result.ReasonCode);
        Assert.Empty(_tool.SignInCalls);
    }

    [Fact]
    public async Task SignInAsync_NotTheKeyHolder_IsRefusedAndNotStored()
    {
        using var other = new TempFolder();
        var notHolder = new LicenseSwitching(_tool, other.Path, _clock.UtcNow, "the-windows-machine", "owner", enabled: true);

        var result = await notHolder.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.NotKeyHolder, result.ReasonCode);
        Assert.Empty(_tool.SignInCalls);
        Assert.Null(notHolder.SignInResults.Find(SignInId));
        notHolder.Dispose();
    }

    [Fact]
    public async Task SignInAsync_LicenseSwitchingTurnedOff_ChangesNothing()
    {
        using var other = new TempFolder();
        var off = new LicenseSwitching(_tool, other.Path, _clock.UtcNow, "the-windows-machine", "owner", enabled: false);

        var result = await off.SignIns.SignInAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSignInOutcomes.Failed, result.Outcome);
        Assert.Equal(LicenseSwitchReasonCodes.NotKeyHolder, result.ReasonCode);
        Assert.Equal(LicenseSignInCoordinator.TurnedOffMessage, result.Message);
        Assert.Empty(_tool.SignInCalls);
        off.Dispose();
    }

    [Fact]
    public async Task SignInAsync_Failure_TravelsInTheNextReport_AndASuccessClearsIt()
    {
        _relay.CanRequest = false;
        await _licenses.SignIns.SignInAsync(Request(), CancellationToken.None);

        var failed = await _licenses.Reporter.TickAsync(CancellationToken.None);

        var holding = failed!.Holdings.Single(h => h.AccountEmail == AccountB);
        Assert.Equal(LicenseRenewalErrors.NoApprover, holding.RenewalError);
        Assert.Equal(_clock.Now, holding.RenewalFailedAtUtc);
        Assert.Equal(LicenseRenewalSentences.For(LicenseRenewalErrors.NoApprover), holding.RenewalErrorDetail);
        Assert.Null(failed.Holdings.Single(h => h.IsActive).RenewalError);

        _tool.LocalSignIns[AccountB] = new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Renewed };
        await _licenses.SignIns.SignInAsync(Request(new string('1', 32)), CancellationToken.None);
        var renewed = await _licenses.Reporter.TickAsync(CancellationToken.None);

        Assert.Equal(LicenseReportTriggers.Changed, renewed!.Trigger);
        holding = renewed.Holdings.Single(h => h.AccountEmail == AccountB);
        Assert.Null(holding.RenewalError);
        Assert.Null(holding.RenewalFailedAtUtc);
        Assert.Null(holding.RenewalErrorDetail);
    }
}
