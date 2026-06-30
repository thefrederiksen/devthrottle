using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the Director-side sidebar ACCOUNT box state mapping (issue #852). The presenter is the single,
/// pure decision point that turns a <see cref="GatewayAccountStatus"/> into one of three visual states:
/// signed-in (green, email), not-signed-in (amber nudge), or unavailable (muted). The load-bearing rule
/// these tests pin is that a not-configured / unreachable Gateway maps to the MUTED unavailable state and
/// NEVER a false "signed out", because an absent or unreachable Gateway tells us nothing about the
/// credential. This box is purely informational and never a sign-in gate (#651/#664).
/// </summary>
public sealed class AccountIndicatorPresenterTests
{
    [Fact]
    public void Describe_SignedIn_ReturnsSignedInStateWithEmail()
    {
        // Arrange
        var status = new GatewayAccountStatus(
            GatewayConfigured: true, Reachable: true, SignedIn: true,
            Email: "person@example.com", Provider: "google", Error: null);

        // Act
        var content = AccountIndicatorPresenter.Describe(status);

        // Assert
        Assert.Equal(AccountIndicatorState.SignedIn, content.State);
        Assert.Equal("SIGNED IN", content.Label);
        Assert.Equal("person@example.com", content.Sub);
    }

    [Fact]
    public void Describe_SignedInWithoutEmail_StillSignedIn_NeverEmptyLine()
    {
        // Arrange: the rare signed-in-but-identity-unavailable case.
        var status = new GatewayAccountStatus(
            GatewayConfigured: true, Reachable: true, SignedIn: true,
            Email: null, Provider: null, Error: null);

        // Act
        var content = AccountIndicatorPresenter.Describe(status);

        // Assert
        Assert.Equal(AccountIndicatorState.SignedIn, content.State);
        Assert.False(string.IsNullOrWhiteSpace(content.Sub));
    }

    [Fact]
    public void Describe_ReachableAndSignedOut_ReturnsNotSignedInNudge()
    {
        // Arrange
        var status = new GatewayAccountStatus(
            GatewayConfigured: true, Reachable: true, SignedIn: false,
            Email: null, Provider: null, Error: null);

        // Act
        var content = AccountIndicatorPresenter.Describe(status);

        // Assert
        Assert.Equal(AccountIndicatorState.NotSignedIn, content.State);
        Assert.Equal("NOT SIGNED IN", content.Label);
        Assert.Contains("sign in", content.Sub);
    }

    [Fact]
    public void Describe_NoGatewayConfigured_ReturnsUnavailable_NotAFalseSignedOut()
    {
        // Arrange: the not-configured sentinel.
        var status = GatewayAccountStatus.NotConfigured();

        // Act
        var content = AccountIndicatorPresenter.Describe(status);

        // Assert: muted unavailable, and never the not-signed-in (signed-out) state.
        Assert.Equal(AccountIndicatorState.Unavailable, content.State);
        Assert.NotEqual(AccountIndicatorState.NotSignedIn, content.State);
        Assert.DoesNotContain("NOT SIGNED IN", content.Label);
    }

    [Fact]
    public void Describe_ConfiguredButUnreachable_ReturnsUnavailable_NotAFalseSignedOut()
    {
        // Arrange: a configured Gateway that did not answer. The credential state is unknown.
        var status = new GatewayAccountStatus(
            GatewayConfigured: true, Reachable: false, SignedIn: false,
            Email: null, Provider: null, Error: "Could not reach the Gateway at http://127.0.0.1:7878.");

        // Act
        var content = AccountIndicatorPresenter.Describe(status);

        // Assert: the muted unavailable state - explicitly NOT the signed-out nudge (issue #852).
        Assert.Equal(AccountIndicatorState.Unavailable, content.State);
        Assert.NotEqual(AccountIndicatorState.NotSignedIn, content.State);
    }

    [Fact]
    public void Describe_NullStatus_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => AccountIndicatorPresenter.Describe(null!));
    }
}
