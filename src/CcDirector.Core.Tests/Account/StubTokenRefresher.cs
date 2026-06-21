using CcDirector.Core.Account;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Stub <see cref="ITokenRefresher"/> for unit tests. Returns a configured token pair to simulate a
/// successful backend exchange, or null to simulate being offline / the backend declining. Records
/// whether it was called and the refresh token it received.
/// </summary>
internal sealed class StubTokenRefresher : ITokenRefresher
{
    private readonly DevThrottleTokens? _result;

    public StubTokenRefresher(DevThrottleTokens? result)
    {
        _result = result;
    }

    public bool WasCalled { get; private set; }
    public string? ReceivedRefreshToken { get; private set; }

    public Task<DevThrottleTokens?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        WasCalled = true;
        ReceivedRefreshToken = refreshToken;
        return Task.FromResult(_result);
    }
}
