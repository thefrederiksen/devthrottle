using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Tests for the startup account gate policy (issue #580). The policy consumes the credential
/// service's offline logged-in check and decides Block versus Start, and owns the background
/// validation that must not block the main window.
/// </summary>
public sealed class AccountGatePolicyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AuthEventLog _eventLog;
    private readonly DateTime _now = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

    public AccountGatePolicyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-dt-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _eventLog = new AuthEventLog(Path.Combine(_tempDir, "auth-events.jsonl"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private DevThrottleAccountService MakeService(
        IProtectedTokenStore store,
        ITokenRefresher? refresher = null)
        => new(store, new JwtAccessTokenValidator(TestJwt.SigningSecret, new FixedTimeProvider(_now)), _eventLog,
            refresher ?? new StubTokenRefresher(null));

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTime nowUtc) => _now = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    // Acceptance criterion: a clean profile with no stored credential blocks (the gate screen path).
    [Fact]
    public void Decide_NoStoredCredential_ReturnsBlock()
    {
        var policy = new AccountGatePolicy(MakeService(new InMemoryTokenStore()));

        Assert.Equal(GateDecision.Block, policy.Decide());
    }

    // Acceptance criterion: a valid cached credential starts to the main window (online or offline).
    [Fact]
    public void Decide_ValidCachedCredential_ReturnsStart()
    {
        var store = new InMemoryTokenStore();
        store.Save(new DevThrottleTokens(TestJwt.Create(_now.AddHours(1)), "refresh"));
        var policy = new AccountGatePolicy(MakeService(store));

        Assert.Equal(GateDecision.Start, policy.Decide());
    }

    // Acceptance criterion: an expired-but-well-formed cached credential still starts (offline runs
    // on the cached credential; revocation takes effect on the next successful online validation).
    [Fact]
    public void Decide_ExpiredButWellFormedCredential_ReturnsStart()
    {
        var store = new InMemoryTokenStore();
        store.Save(new DevThrottleTokens(TestJwt.Create(_now.AddHours(-1)), "refresh"));
        var policy = new AccountGatePolicy(MakeService(store));

        Assert.Equal(GateDecision.Start, policy.Decide());
    }

    // A tampered token is treated as not logged in -> Block (no usable credential).
    [Fact]
    public void Decide_TamperedCredential_ReturnsBlock()
    {
        var store = new InMemoryTokenStore();
        store.Save(new DevThrottleTokens(TestJwt.Tamper(TestJwt.Create(_now.AddHours(1))), "refresh"));
        var policy = new AccountGatePolicy(MakeService(store));

        Assert.Equal(GateDecision.Block, policy.Decide());
    }

    // The background validation refreshes an expired credential when a refresher returns a renewed
    // pair (the online path), without the caller having to wait on the window.
    [Fact]
    public async Task StartBackgroundValidation_ExpiredCredentialOnline_RefreshesToken()
    {
        var store = new InMemoryTokenStore();
        store.Save(new DevThrottleTokens(TestJwt.Create(_now.AddHours(-1)), "refresh"));
        var renewed = new DevThrottleTokens(TestJwt.Create(_now.AddHours(1)), "refresh2");
        var policy = new AccountGatePolicy(MakeService(store, new StubTokenRefresher(renewed)));

        await policy.StartBackgroundValidation();

        Assert.Equal(2, store.SaveCount); // initial save + refreshed save
    }

    // Offline (the refresher reports unavailable): the background validation is a no-op and the
    // credential is kept, so the Director keeps running on the cached credential.
    [Fact]
    public async Task StartBackgroundValidation_ExpiredCredentialOffline_KeepsCachedCredential()
    {
        var store = new InMemoryTokenStore();
        store.Save(new DevThrottleTokens(TestJwt.Create(_now.AddHours(-1)), "refresh"));
        var policy = new AccountGatePolicy(MakeService(store, new StubTokenRefresher(null)));

        await policy.StartBackgroundValidation();

        Assert.Equal(1, store.SaveCount); // only the initial save; nothing refreshed
        Assert.NotNull(store.Load());
    }

    // The production refresher (no backend wired yet) reports the refresh unavailable, matching the
    // offline behavior, so the Director keeps running on the cached credential.
    [Fact]
    public async Task BackendUnavailableTokenRefresher_ReturnsNull()
    {
        var refresher = new BackendUnavailableTokenRefresher();

        var result = await refresher.RefreshAsync("any-refresh-token");

        Assert.Null(result);
    }

    [Fact]
    public void Constructor_NullService_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AccountGatePolicy(null!));
    }
}
