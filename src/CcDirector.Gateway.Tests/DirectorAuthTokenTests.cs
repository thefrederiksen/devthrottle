using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #457: in LAN mode the Director accepts the SHARED fleet token (gateway.token) so the
/// Gateway can authenticate to it across machines. Standalone (no fleet token) falls back to
/// this machine's own persisted token.
/// </summary>
public sealed class DirectorAuthTokenTests
{
    [Fact]
    public void ResolveAcceptedToken_UsesFleetToken_WhenPresent()
        => Assert.Equal("fleet-secret-123", DirectorAuth.ResolveAcceptedToken("  fleet-secret-123  "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveAcceptedToken_FallsBackToLocal_WhenNoFleetToken(string? fleet)
    {
        // No fleet token -> the local persisted token (LoadOrCreateToken). We only assert it is a
        // non-empty value distinct from the fleet input (the local token is a generated secret).
        var resolved = DirectorAuth.ResolveAcceptedToken(fleet);
        Assert.False(string.IsNullOrWhiteSpace(resolved));
    }
}
