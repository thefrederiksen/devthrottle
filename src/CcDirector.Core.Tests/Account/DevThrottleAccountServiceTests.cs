using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

public sealed class DevThrottleAccountServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AuthEventLog _eventLog;
    private readonly string _eventLogPath;
    private readonly DateTime _now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    public DevThrottleAccountServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-dt-acct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _eventLogPath = Path.Combine(_tempDir, "auth-events.jsonl");
        _eventLog = new AuthEventLog(_eventLogPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JwtAccessTokenValidator Validator() =>
        new(TestJwt.SigningSecret, new FixedTime(_now));

    private DevThrottleAccountService MakeService(
        IProtectedTokenStore store,
        ITokenRefresher? refresher = null)
        => new(store, Validator(), _eventLog, refresher ?? new StubTokenRefresher(null));

    // Acceptance criterion: with no stored credential, the check returns false.
    [Fact]
    public void IsLoggedIn_NoStoredCredential_ReturnsFalse()
    {
        var service = MakeService(new InMemoryTokenStore());

        Assert.False(service.IsLoggedIn());
    }

    // Acceptance criterion: a valid cached credential returns true (offline - no network call;
    // the service has no network dependency on this path, and the only network seam, ITokenRefresher,
    // is provably never invoked here).
    [Fact]
    public void IsLoggedIn_ValidCachedCredential_ReturnsTrueWithoutCallingRefresher()
    {
        var store = new InMemoryTokenStore();
        var refresher = new StubTokenRefresher(null);
        var service = MakeService(store, refresher);
        service.StoreTokens(new DevThrottleTokens(TestJwt.Create(_now.AddHours(1)), "refresh-1"));

        var result = service.IsLoggedIn();

        Assert.True(result);
        Assert.False(refresher.WasCalled);
    }

    // Acceptance criterion: an expired-but-well-formed cached credential still reports logged-in
    // (offline), so the gate does not lock the user out while the background refresh has not run yet.
    [Fact]
    public void IsLoggedIn_ExpiredButWellFormedCredential_ReturnsTrueWithoutNetwork()
    {
        var store = new InMemoryTokenStore();
        var refresher = new StubTokenRefresher(null);
        var service = MakeService(store, refresher);
        service.StoreTokens(new DevThrottleTokens(TestJwt.Create(_now.AddHours(-1)), "refresh-1"));

        var result = service.IsLoggedIn();

        Assert.True(result);
        Assert.False(refresher.WasCalled);
    }

    // Acceptance criterion: a tampered/wrong-signature cached access token is treated as not logged in.
    [Fact]
    public void IsLoggedIn_TamperedCredential_ReturnsFalse()
    {
        var store = new InMemoryTokenStore();
        var service = MakeService(store);
        var tampered = TestJwt.Tamper(TestJwt.Create(_now.AddHours(1)));
        store.Save(new DevThrottleTokens(tampered, "refresh-1"));

        Assert.False(service.IsLoggedIn());
    }

    // Acceptance criterion: with a cached credential whose access token has expired but whose refresh
    // token is valid, and networking enabled, the service renews the access token in the background.
    [Fact]
    public async Task RefreshIfNeededAsync_ExpiredAccessTokenValidRefresh_RenewsAndStores()
    {
        var store = new InMemoryTokenStore();
        var freshTokens = new DevThrottleTokens(TestJwt.Create(_now.AddHours(2)), "refresh-2");
        var refresher = new StubTokenRefresher(freshTokens);
        var service = MakeService(store, refresher);
        service.StoreTokens(new DevThrottleTokens(TestJwt.Create(_now.AddHours(-1)), "refresh-1"));

        var refreshed = await service.RefreshIfNeededAsync();

        Assert.True(refreshed);
        Assert.True(refresher.WasCalled);
        Assert.Equal("refresh-1", refresher.ReceivedRefreshToken);
        var stored = store.Load();
        Assert.NotNull(stored);
        Assert.Equal(freshTokens.AccessToken, stored.AccessToken);
        Assert.Equal("refresh-2", stored.RefreshToken);
    }

    // Background refresh while offline: the exchange returns nothing, the cached credential is kept.
    [Fact]
    public async Task RefreshIfNeededAsync_OfflineExpiredAccessToken_KeepsCachedCredential()
    {
        var store = new InMemoryTokenStore();
        var refresher = new StubTokenRefresher(null);
        var service = MakeService(store, refresher);
        var expired = TestJwt.Create(_now.AddHours(-1));
        service.StoreTokens(new DevThrottleTokens(expired, "refresh-1"));

        var refreshed = await service.RefreshIfNeededAsync();

        Assert.False(refreshed);
        Assert.True(refresher.WasCalled);
        var stored = store.Load();
        Assert.NotNull(stored);
        Assert.Equal(expired, stored.AccessToken);
    }

    // A still-valid access token needs no refresh and never calls the network seam.
    [Fact]
    public async Task RefreshIfNeededAsync_ValidAccessToken_NoRefresh()
    {
        var store = new InMemoryTokenStore();
        var refresher = new StubTokenRefresher(new DevThrottleTokens("new", "new"));
        var service = MakeService(store, refresher);
        service.StoreTokens(new DevThrottleTokens(TestJwt.Create(_now.AddHours(1)), "refresh-1"));

        var refreshed = await service.RefreshIfNeededAsync();

        Assert.False(refreshed);
        Assert.False(refresher.WasCalled);
    }

    // Acceptance criterion: logout clears the store, the next check returns false, and a logout event
    // is recorded.
    [Fact]
    public void Logout_ClearsStoreNextCheckFalseAndRecordsLogoutEvent()
    {
        var store = new InMemoryTokenStore();
        var service = MakeService(store);
        service.StoreTokens(new DevThrottleTokens(TestJwt.Create(_now.AddHours(1)), "refresh-1"));
        Assert.True(service.IsLoggedIn());

        service.Logout();

        Assert.False(store.HasTokens);
        Assert.False(service.IsLoggedIn());
        var events = _eventLog.ReadAll();
        Assert.Contains(events, e => e.Kind == AuthEventLog.LoggedOut);
    }

    // Acceptance criterion (event side): a logged-in event is recorded on first store, and is not
    // re-recorded on a re-store of an already-logged-in install.
    [Fact]
    public void StoreTokens_FirstStore_RecordsLoggedInEventOnce()
    {
        var store = new InMemoryTokenStore();
        var service = MakeService(store);

        service.StoreTokens(new DevThrottleTokens(TestJwt.Create(_now.AddHours(1)), "refresh-1"));
        service.StoreTokens(new DevThrottleTokens(TestJwt.Create(_now.AddHours(2)), "refresh-2"));

        var loggedInEvents = _eventLog.ReadAll().Count(e => e.Kind == AuthEventLog.LoggedIn);
        Assert.Equal(1, loggedInEvents);
    }

    // Issue #582 AC1: the account area shows the signed-in identity (email and provider) for a stored
    // credential. GetIdentity reads them from the cached token's claims, with no network call.
    [Fact]
    public void GetIdentity_StoredCredentialWithIdentityClaims_ReturnsEmailAndProvider()
    {
        var store = new InMemoryTokenStore();
        var refresher = new StubTokenRefresher(null);
        var service = MakeService(store, refresher);
        var token = TestJwt.CreateWithIdentity(_now.AddHours(1), "user@example.com", "google");
        service.StoreTokens(new DevThrottleTokens(token, "refresh-1"));

        var identity = service.GetIdentity();

        Assert.NotNull(identity);
        Assert.Equal("user@example.com", identity.Email);
        Assert.Equal("google", identity.Provider);
        Assert.False(refresher.WasCalled);
    }

    // No stored credential -> no identity (the account area shows an explicit unavailable state).
    [Fact]
    public void GetIdentity_NoStoredCredential_ReturnsNull()
    {
        var service = MakeService(new InMemoryTokenStore());

        Assert.Null(service.GetIdentity());
    }

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTime(DateTime nowUtc) => _now = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
