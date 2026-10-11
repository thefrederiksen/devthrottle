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

    private LicenseSwitchCoordinator NewCoordinator(LicenseKeyHolderLock? holder = null) =>
        new(_tool, holder ?? _holder, new LicenseSwitchResultStore(Path.Combine(_folder.Path, "switch-results"), _clock.UtcNow), _reporter, _clock.UtcNow);

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
