using System.Text.Json;
using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>
/// License Switching contract 8.3, the approving machine's side: approve another machine's sign-in in a browser
/// profile here and hand the answer back sealed to that machine. Runs against a fake license tool and a fake seal.
/// </summary>
public sealed class LicenseApproverTests
{
    private const string SignInId = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private const string AccountB = "account-b@example.com";
    private const string Url = "https://claude.ai/oauth/authorize?code=true&state=s1&login_hint=account-b%40example.com";

    private readonly FakeClock _clock = new();
    private readonly FakeLicenseCommandLine _tool = new();
    private readonly FakeApprovalSeal _seal = new();

    private LicenseApprover Approver(bool enabled = true, bool withSeal = true)
    {
        var approver = new LicenseApprover(_tool, _clock.UtcNow, enabled);
        if (withSeal) approver.AttachSeal(_seal);
        return approver;
    }

    private LicenseApproveRequest Request() => new()
    {
        SignInId = SignInId,
        AccountEmail = "Account-B@Example.com",
        AuthorizeUrl = Url,
        BrowserProfile = "Profile B",
        SealToRecipient = "the-waiting-machine",
        NotAfterUtc = _clock.Now + LicenseContract.SignInNotAfter,
    };

    [Fact]
    public async Task ApproveAsync_Approved_AnswersOnlyTheSealedApproval_SealedToTheWaitingMachineForThisSignIn()
    {
        var result = await Approver().ApproveAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseApprovalOutcomes.Approved, result.Outcome);
        Assert.Null(result.ReasonCode);
        Assert.Equal(SignInId, result.SignInId);
        Assert.Equal((Url, AccountB, "Profile B"), Assert.Single(_tool.ApproveCalls));
        Assert.Equal(("the-waiting-machine", SignInId), Assert.Single(_seal.Sealed));
        // The approval itself is in no field of the answer; it opens only from the sealed one.
        Assert.DoesNotContain(FakeRemoteSignIn.ApprovalMarker, JsonSerializer.Serialize(result));
        Assert.Equal(FakeRemoteSignIn.ApprovalMarker, await _seal.OpenAsync(result.SealedApproval!, SignInId, CancellationToken.None));
    }

    [Theory]
    [InlineData(LicenseRenewalErrors.WrongAccount)]
    [InlineData(LicenseRenewalErrors.BrowserNotSignedIn)]
    [InlineData(LicenseRenewalErrors.ApprovalFailed)]
    public async Task ApproveAsync_ToolRefuses_AnswersItsReason_AndSealsNothing(string reason)
    {
        _tool.ApproveAnswer = LicenseApproveAnswer.Refused(reason);

        var result = await Approver().ApproveAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseApprovalOutcomes.Refused, result.Outcome);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal(LicenseRenewalSentences.For(reason), result.Message);
        Assert.Null(result.SealedApproval);
        Assert.Empty(_seal.Sealed);
    }

    [Fact]
    public async Task ApproveAsync_AfterItsDeadline_IsExpired_AndNoBrowserIsOpened()
    {
        var request = Request();
        _clock.Advance(LicenseContract.SignInNotAfter + TimeSpan.FromSeconds(1));

        var result = await Approver().ApproveAsync(request, CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.Expired, result.ReasonCode);
        Assert.Empty(_tool.ApproveCalls);
    }

    [Fact]
    public async Task ApproveAsync_LicenseSwitchingTurnedOff_ApprovesNothing()
    {
        var result = await Approver(enabled: false).ApproveAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseApprovalOutcomes.Refused, result.Outcome);
        Assert.Equal(LicenseRenewalErrors.NoApprover, result.ReasonCode);
        Assert.Empty(_tool.ApproveCalls);
    }

    [Theory]
    [InlineData(false, "the-waiting-machine")]
    [InlineData(true, "")]
    public async Task ApproveAsync_NothingToSealWithOrTo_IsRefusedBeforeAnyBrowserOpens(bool withSeal, string recipient)
    {
        // An approval that cannot be sealed must not be made: it would exist with nowhere safe to go.
        var request = Request();
        request.SealToRecipient = recipient;

        var result = await Approver(withSeal: withSeal).ApproveAsync(request, CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.ApprovalFailed, result.ReasonCode);
        Assert.Empty(_tool.ApproveCalls);
    }

    [Fact]
    public async Task ApproveAsync_NoBrowserProfileNamed_IsNoApprover()
    {
        var request = Request();
        request.BrowserProfile = " ";

        var result = await Approver().ApproveAsync(request, CancellationToken.None);

        Assert.Equal(LicenseRenewalErrors.NoApprover, result.ReasonCode);
        Assert.Empty(_tool.ApproveCalls);
    }

    [Fact]
    public async Task ApproveAsync_SealingFailsAfterTheApproval_IsRefused_AndTheApprovalGoesNowhere()
    {
        var approver = Approver();
        _seal.Broken = true;

        var result = await approver.ApproveAsync(Request(), CancellationToken.None);

        Assert.Equal(LicenseApprovalOutcomes.Refused, result.Outcome);
        Assert.Equal(LicenseRenewalErrors.ApprovalFailed, result.ReasonCode);
        Assert.Null(result.SealedApproval);
        Assert.DoesNotContain(FakeRemoteSignIn.ApprovalMarker, JsonSerializer.Serialize(result));
    }
}
