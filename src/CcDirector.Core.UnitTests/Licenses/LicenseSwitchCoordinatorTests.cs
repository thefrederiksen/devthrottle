using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>License Switching contract 3.3, steps 2 to 12, and 3.6 (a repeated SwitchId never runs twice).</summary>
public sealed class LicenseSwitchCoordinatorTests : IDisposable
{
    private const string SwitchId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40";
    private const string OtherSwitchId = "0d9e8c7b6a5f4e3d2c1b0a9f8e7d6c5b";
    private const string AccountA = "account-a@example.com";
    private const string AccountB = "account-b@example.com";

    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();
    private readonly FakeLicenseCommandLine _tool;
    private readonly LicenseKeyHolderLock _holder;
    private readonly LicenseUsageReporter _reporter;
    private readonly FakeReportSink _sink = new();

    public LicenseSwitchCoordinatorTests()
    {
        _tool = new FakeLicenseCommandLine { UtcNow = () => _clock.Now }.Hold(AccountA, active: true).Hold(AccountB);
        _holder = new LicenseKeyHolderLock(_folder.Path);
        Assert.True(_holder.TryAcquire());
        _reporter = new LicenseUsageReporter(_tool, _holder, _clock.UtcNow, "the-windows-machine", "owner");
        _reporter.AttachSink(_sink);
    }

    public void Dispose()
    {
        _holder.Dispose();
        _folder.Dispose();
    }

    private LicenseSwitchCoordinator NewCoordinator(LicenseKeyHolderLock? holder = null, bool enabled = true) =>
        new(_tool, holder ?? _holder, new LicenseSwitchResultStore(Path.Combine(_folder.Path, "switch-results"), _clock.UtcNow), _reporter, _clock.UtcNow, enabled);

    private LicenseSwitchRequest Request(string target = AccountB, string expected = AccountA, string id = SwitchId) => new()
    {
        SwitchId = id,
        TargetAccountEmail = target,
        ExpectedActiveAccountEmail = expected,
        Reason = LicenseSwitchReasons.Threshold,
        RequestedAtUtc = _clock.Now,
        NotAfterUtc = _clock.Now + LicenseContract.SwitchNotAfter,
    };

    [Fact]
    public async Task SwitchAsync_SpareHeld_ExchangesReadsBackVerifiesAndReports()
    {
        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.Switched, result.Outcome);
        Assert.Null(result.ReasonCode);
        Assert.Equal(AccountA, result.PreviousActiveAccountEmail);
        Assert.Equal(AccountB, result.ActiveAccountEmailAfter);
        Assert.True(result.VerifiedByUsageRead);
        Assert.False(result.Replayed);
        Assert.Equal(AccountB, _tool.Active);
        Assert.Equal((AccountB, AccountA), _tool.SwitchCalls.Single());

        var report = await _reporter.TickAsync(CancellationToken.None);
        Assert.Equal(LicenseReportTriggers.AfterSwitch, report!.Trigger);
        Assert.Null(report.SwitchInProgressId);
    }

    [Fact]
    public async Task SwitchAsync_SameSwitchIdAgain_AnswersStoredResultAndDoesNotRunTwice()
    {
        var first = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        var again = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);   // a restarted Director

        Assert.True(again.Replayed);
        Assert.Equal(first.Outcome, again.Outcome);
        Assert.Equal(first.ActiveAccountEmailAfter, again.ActiveAccountEmailAfter);
        Assert.Single(_tool.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsync_StoredResultOlderThanRetention_IsTreatedAsNew()
    {
        await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);
        _clock.Advance(LicenseContract.SwitchResultRetention + TimeSpan.FromMinutes(1));

        var again = await NewCoordinator().SwitchAsync(Request(target: AccountA, expected: AccountB), CancellationToken.None);

        Assert.False(again.Replayed);
        Assert.Equal(2, _tool.SwitchCalls.Count);
    }

    [Fact]
    public async Task SwitchAsync_NotTheKeyHolder_RefusesAndTouchesNothing()
    {
        using var otherFolder = new TempFolder();
        using var notHeld = new LicenseKeyHolderLock(otherFolder.Path);

        var result = await NewCoordinator(notHeld).SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.NotKeyHolder, result.ReasonCode);
        Assert.Empty(_tool.SwitchCalls);
        Assert.Equal(0, _tool.ListCalls);
    }

    [Fact]
    public async Task SwitchAsync_AnotherSwitchRunning_RefusesWithSwitchInProgress()
    {
        _tool.SwitchGate = new TaskCompletionSource();
        var coordinator = NewCoordinator();
        var first = coordinator.SwitchAsync(Request(), CancellationToken.None);
        while (_tool.SwitchCalls.Count == 0) await Task.Delay(5);

        var second = await coordinator.SwitchAsync(Request(id: OtherSwitchId), CancellationToken.None);
        _tool.SwitchGate.SetResult();
        await first;

        Assert.Equal(LicenseSwitchReasonCodes.SwitchInProgress, second.ReasonCode);
        Assert.Single(_tool.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsync_RefusedSwitchInProgressRetriedAfterTheOtherEnds_ReplaysTheRefusal()
    {
        // Review of 3868: a switch-in-progress refusal is a finished result for its SwitchId (contract 3.6). A Gateway
        // that timed out before hearing it retries the same id, and must hear the refusal again, not run the switch.
        _tool.SwitchGate = new TaskCompletionSource();
        var coordinator = NewCoordinator();
        var first = coordinator.SwitchAsync(Request(), CancellationToken.None);
        while (_tool.SwitchCalls.Count == 0) await Task.Delay(5);
        var refused = await coordinator.SwitchAsync(Request(target: AccountA, expected: AccountB, id: OtherSwitchId), CancellationToken.None);
        _tool.SwitchGate.SetResult();
        await first;

        var retry = await NewCoordinator().SwitchAsync(Request(target: AccountA, expected: AccountB, id: OtherSwitchId), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.SwitchInProgress, refused.ReasonCode);
        Assert.True(retry.Replayed);
        Assert.Equal(LicenseSwitchOutcomes.Failed, retry.Outcome);
        Assert.Equal(LicenseSwitchReasonCodes.SwitchInProgress, retry.ReasonCode);
        Assert.Single(_tool.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsync_RefusedWhileAnotherRunsThenReplayedAfterIt_CarriesTheAccountActiveAtEachMoment()
    {
        // Fix-round review of 3868, the reviewer's sequence: A is active while S1 to B runs; S2 is refused and its answer
        // is lost; S1 finishes, so B is active; the Gateway retries S2. The replay must say B - never an empty account,
        // and never the A of the moment it was refused, either of which would undo what the after-switch report said.
        _tool.SwitchGate = new TaskCompletionSource();
        var coordinator = NewCoordinator();
        var s1 = coordinator.SwitchAsync(Request(), CancellationToken.None);
        while (_tool.SwitchCalls.Count == 0) await Task.Delay(5);

        var refused = await coordinator.SwitchAsync(Request(id: OtherSwitchId), CancellationToken.None);
        _tool.SwitchGate.SetResult();
        Assert.Equal(LicenseSwitchOutcomes.Switched, (await s1).Outcome);
        var retried = await coordinator.SwitchAsync(Request(id: OtherSwitchId), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.SwitchInProgress, refused.ReasonCode);
        Assert.Equal(AccountA, refused.ActiveAccountEmailAfter);    // active when it was refused
        Assert.True(retried.Replayed);
        Assert.Equal(LicenseSwitchReasonCodes.SwitchInProgress, retried.ReasonCode);
        Assert.Equal(AccountB, retried.ActiveAccountEmailAfter);    // active when it was replayed
        Assert.Equal(AccountB, retried.PreviousActiveAccountEmail);
        Assert.Single(_tool.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsync_StoredRefusalReplayedWhenTheToolCannotList_LastReadAccountAndSaysSo()
    {
        var coordinator = NewCoordinator();
        var refused = await coordinator.SwitchAsync(Request(target: "account-c@example.com"), CancellationToken.None);
        Assert.Equal(LicenseSwitchReasonCodes.SpareNotHeld, refused.ReasonCode);

        _tool.FailWith = "cc-devthrottle is not installed";
        var replayed = await coordinator.SwitchAsync(Request(target: "account-c@example.com"), CancellationToken.None);

        Assert.True(replayed.Replayed);
        Assert.Equal(AccountA, replayed.ActiveAccountEmailAfter);
        Assert.EndsWith(LicenseSwitchCoordinator.ActiveNotReadSentence, replayed.Message);
        Assert.DoesNotContain("not installed", replayed.Message);
    }

    [Fact]
    public async Task SwitchAsync_StoredRefusalReplayedByARestartedDirectorThatCannotList_ThrowsRatherThanAnEmptyAccount()
    {
        await NewCoordinator().SwitchAsync(Request(target: "account-c@example.com"), CancellationToken.None);
        _tool.FailWith = "cc-devthrottle is not installed";

        await Assert.ThrowsAsync<LicenseCommandLineException>(() =>
            NewCoordinator().SwitchAsync(Request(target: "account-c@example.com"), CancellationToken.None));
    }

    [Fact]
    public async Task SwitchAsync_StoredSwitchReplayed_KeepsItsOwnReadBack()
    {
        // A switch that ran answers its own read-back on replay, as contract 3.6 says; only refusals are read again.
        var first = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);
        _tool.Held[0] = (AccountA, true);
        _tool.Held[1] = (AccountB, false);

        var again = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.True(again.Replayed);
        Assert.Equal(first.ActiveAccountEmailAfter, again.ActiveAccountEmailAfter);
        Assert.Equal(AccountB, again.ActiveAccountEmailAfter);
    }

    [Fact]
    public async Task SwitchAsync_NotTheKeyHolderThenRetriedOnTheHolder_Runs()
    {
        // "Not the key holder" stays unstored: the results folder is shared, and the holder must run the same id.
        using var notHeld = new LicenseKeyHolderLock(Path.Combine(_folder.Path, "elsewhere"));
        var refused = await NewCoordinator(notHeld).SwitchAsync(Request(), CancellationToken.None);

        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.NotKeyHolder, refused.ReasonCode);
        Assert.Equal(LicenseSwitchOutcomes.Switched, result.Outcome);
        Assert.False(result.Replayed);
    }

    [Fact]
    public async Task SwitchAsync_SameSwitchIdWhileRunning_WaitsAndAnswersItsResultReplayed()
    {
        _tool.SwitchGate = new TaskCompletionSource();
        var coordinator = NewCoordinator();
        var first = coordinator.SwitchAsync(Request(), CancellationToken.None);
        while (_tool.SwitchCalls.Count == 0) await Task.Delay(5);

        var second = coordinator.SwitchAsync(Request(), CancellationToken.None);
        Assert.False(second.IsCompleted);
        _tool.SwitchGate.SetResult();

        var a = await first;
        var b = await second;
        Assert.False(a.Replayed);
        Assert.True(b.Replayed);
        Assert.Equal(LicenseSwitchOutcomes.Switched, b.Outcome);
        Assert.Single(_tool.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsync_PastNotAfter_ExpiresWithoutExchanging()
    {
        var request = Request();
        _clock.Advance(LicenseContract.SwitchNotAfter + TimeSpan.FromSeconds(1));

        var result = await NewCoordinator().SwitchAsync(request, CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.Expired, result.ReasonCode);
        Assert.Empty(_tool.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsync_TargetAlreadyActive_AnswersAlreadyActive()
    {
        var result = await NewCoordinator().SwitchAsync(Request(target: AccountA, expected: AccountA), CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.AlreadyActive, result.Outcome);
        Assert.Null(result.ReasonCode);
        Assert.Empty(_tool.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsync_ActiveIsNotWhatTheGatewayExpected_RefusesActiveAccountChanged()
    {
        var result = await NewCoordinator().SwitchAsync(Request(expected: "account-c@example.com"), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.ActiveAccountChanged, result.ReasonCode);
        Assert.Equal(AccountA, result.ActiveAccountEmailAfter);
        Assert.Empty(_tool.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsync_NoSpareForTarget_RefusesSpareNotHeld()
    {
        var result = await NewCoordinator().SwitchAsync(Request(target: "account-c@example.com"), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.SpareNotHeld, result.ReasonCode);
        Assert.Empty(_tool.SwitchCalls);
    }

    [Theory]
    [InlineData(LicenseReadErrors.Unauthorized)]
    [InlineData(LicenseReadErrors.RefreshFailed)]
    public async Task SwitchAsync_SpareNeedsANewSignIn_RefusesSpareNotUsable(string readError)
    {
        _tool.ReadErrors[AccountB] = readError;
        await _reporter.TickAsync(CancellationToken.None);

        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.SpareNotUsable, result.ReasonCode);
        Assert.Empty(_tool.SwitchCalls);
    }

    [Fact]
    public async Task SwitchAsync_SpareSignInExpired_RefusesSpareNotUsable()
    {
        // Review of 3868, contract 3.3 step 9: a spare whose sign-in expires at or before now is not switched to.
        _tool.SignInExpires[AccountB] = _clock.Now;
        await _reporter.TickAsync(CancellationToken.None);

        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.Failed, result.Outcome);
        Assert.Equal(LicenseSwitchReasonCodes.SpareNotUsable, result.ReasonCode);
        Assert.Empty(_tool.SwitchCalls);
        Assert.Equal(AccountA, _tool.Active);
    }

    [Fact]
    public async Task SwitchAsync_SpareSignInExpiresLater_StillSwitches()
    {
        _tool.SignInExpires[AccountB] = _clock.Now.AddMinutes(1);
        await _reporter.TickAsync(CancellationToken.None);

        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.Switched, result.Outcome);
    }

    [Fact]
    public async Task SwitchAsync_LicenseSwitchingTurnedOff_FailsPlainlyAndTouchesNothing()
    {
        var result = await NewCoordinator(enabled: false).SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.Failed, result.Outcome);
        Assert.Equal(LicenseSwitchReasonCodes.NotKeyHolder, result.ReasonCode);
        Assert.Equal(LicenseSwitchCoordinator.TurnedOffMessage, result.Message);
        Assert.Equal(0, _tool.ListCalls);
        Assert.Empty(_tool.SwitchCalls);

        var turnedOn = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);   // not stored: runs when on
        Assert.Equal(LicenseSwitchOutcomes.Switched, turnedOn.Outcome);
    }

    [Fact]
    public async Task SwitchAsync_ToolReasonOutsideTheContract_IsNeitherReportedNorQuoted()
    {
        // Review of 3868, P1: a reason the contract has no word for could be anything the tool printed.
        var marker = FakeLicenseCommandLine.ToolTextMarker;
        _tool.SwitchAnswer = CcDevthrottleLicenseCommandLine.ReadSwitchAnswer(
            "{\"outcome\": \"failed\", \"reasonCode\": \"" + marker + "\", " +
            "\"previousActiveAccountEmail\": \"" + AccountA + "\", \"activeAccountEmailAfter\": \"" + AccountA + "\"}");

        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.ExchangeFailedUnknown, result.ReasonCode);
        Assert.DoesNotContain(marker, result.Message);
        Assert.DoesNotContain(marker, LicenseSwitchCoordinator.ExchangeFailureSentence(marker, AccountB));
    }

    [Fact]
    public async Task SwitchAsync_SpareMerelyRateLimited_StillSwitches()
    {
        _tool.ReadErrors[AccountB] = LicenseReadErrors.RateLimited;
        await _reporter.TickAsync(CancellationToken.None);
        _tool.ReadErrors.Clear();

        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.Switched, result.Outcome);
    }

    [Theory]
    [InlineData(LicenseSwitchReasonCodes.ExchangeFailedRestored)]
    [InlineData(LicenseSwitchReasonCodes.ExchangeFailedUnknown)]
    public async Task SwitchAsync_ExchangeFails_PassesTheToolsReasonOn(string reasonCode)
    {
        _tool.SwitchAnswer = new LicenseExchangeAnswer
        {
            Outcome = LicenseSwitchOutcomes.Failed,
            ReasonCode = reasonCode,
            Message = FakeLicenseCommandLine.ToolTextMarker,
            PreviousActiveAccountEmail = AccountA,
            ActiveAccountEmailAfter = AccountA,
        };

        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.Failed, result.Outcome);
        Assert.Equal(reasonCode, result.ReasonCode);
        Assert.Equal(AccountA, result.ActiveAccountEmailAfter);
        Assert.DoesNotContain(FakeLicenseCommandLine.ToolTextMarker, result.Message);   // contract rule 6.1
    }

    [Fact]
    public async Task SwitchAsync_ReadBackIsNotTheTarget_FailsVerifyFailed()
    {
        _tool.SwitchAnswer = new LicenseExchangeAnswer
        {
            Outcome = LicenseSwitchOutcomes.Switched,
            PreviousActiveAccountEmail = AccountA,
            ActiveAccountEmailAfter = AccountA,
        };

        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchReasonCodes.VerifyFailed, result.ReasonCode);
        Assert.Equal(AccountA, result.ActiveAccountEmailAfter);
    }

    [Fact]
    public async Task SwitchAsync_NewSignInDoesNotAnswerAUsageRead_StillSwitchedButNotVerified()
    {
        _tool.ReadErrors[AccountB] = LicenseReadErrors.Network;

        var result = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.Switched, result.Outcome);
        Assert.False(result.VerifiedByUsageRead);
        Assert.Equal(AccountB, _tool.Active);
    }

    [Fact]
    public async Task SwitchAsync_ToolCannotList_ThrowsStoresNothingAndStillReports()
    {
        _tool.FailWith = "cc-devthrottle is not installed";

        await Assert.ThrowsAsync<LicenseCommandLineException>(() => NewCoordinator().SwitchAsync(Request(), CancellationToken.None));

        _tool.FailWith = null;
        var again = await NewCoordinator().SwitchAsync(Request(), CancellationToken.None);
        Assert.False(again.Replayed);
        Assert.Equal(LicenseSwitchOutcomes.Switched, again.Outcome);
    }

    [Fact]
    public async Task SwitchAsync_EmailsInAnotherCase_AreComparedLowerCased()
    {
        var result = await NewCoordinator().SwitchAsync(Request(target: "Account-B@Example.com", expected: "ACCOUNT-A@example.com"), CancellationToken.None);

        Assert.Equal(LicenseSwitchOutcomes.Switched, result.Outcome);
    }
}
