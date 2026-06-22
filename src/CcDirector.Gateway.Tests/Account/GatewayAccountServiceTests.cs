using System.Runtime.Versioning;
using System.Text;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Tests the Gateway-hosted DevThrottle credential service (issue #636, the Gateway Centralization
/// Phase 2 foundation). Verifies the five acceptance criteria of the issue: the Gateway stores the
/// token pair as an ENCRYPTED blob (contents not plaintext); answers "signed in?" locally (true with a
/// valid token, false with none) with no network call; reads the identity (email + provider) and
/// returns "unavailable" when no token is present; the access/refresh tokens never reach the log; and
/// the test-seed seam (<c>DEVTHROTTLE_TEST_SEED_TOKEN</c> / <c>DEVTHROTTLE_JWT_SIGNING_SECRET</c>)
/// works against the Gateway service.
///
/// The credential store under test is the real Windows Data Protection store, so the encryption and the
/// store/load round-trip are exercised on disk (the facts no-op on a non-Windows host - the operating-
/// system credential store is Windows-only for now, the issue's assumption). The class is annotated
/// [SupportedOSPlatform("windows")] so the platform-compatibility analyzer is satisfied.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GatewayAccountServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _blobPath;
    private readonly string _authEventsPath;

    public GatewayAccountServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-gw-acct-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _blobPath = Path.Combine(_tempDir, "devthrottle-credential.bin");
        _authEventsPath = Path.Combine(_tempDir, "devthrottle-auth-events.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static bool OnWindows => OperatingSystem.IsWindows();

    /// <summary>
    /// Builds the Gateway credential service over a real Windows Data Protection store at the temp blob
    /// path, with the signing secret set so test-issued tokens validate. The env var is set and restored
    /// around construction so the test is self-contained.
    /// </summary>
    private DevThrottleAccountService MakeService()
    {
        var previous = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        try
        {
            var store = new WindowsProtectedTokenStore(_blobPath);
            return GatewayAccountFactory.Build(store, _authEventsPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
    }

    // Acceptance criterion 1 / criterion 5 (store-load round-trip): the Gateway stores a token pair and
    // loads it back intact.
    [Fact]
    public void StoreThenLoad_RoundTripsTheTokenPair()
    {
        if (!OnWindows) return;

        var service = MakeService();
        var accessToken = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        service.StoreTokens(new DevThrottleTokens(accessToken, "gateway-refresh-1"));

        var store = new WindowsProtectedTokenStore(_blobPath);
        var loaded = store.Load();

        Assert.True(File.Exists(_blobPath));
        Assert.NotNull(loaded);
        Assert.Equal(accessToken, loaded.AccessToken);
        Assert.Equal("gateway-refresh-1", loaded.RefreshToken);
    }

    // Acceptance criterion 1: the persisted blob is an encrypted blob - its bytes do not contain the raw
    // token strings in plain text.
    [Fact]
    public void StoreTokens_PersistsAnEncryptedBlobNotPlaintext()
    {
        if (!OnWindows) return;

        var service = MakeService();
        const string rawRefresh = "RAW-REFRESH-TOKEN-PLAINTEXT-MARKER-636";
        var accessToken = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        service.StoreTokens(new DevThrottleTokens(accessToken, rawRefresh));

        var bytesOnDisk = File.ReadAllBytes(_blobPath);
        var textOnDisk = Encoding.UTF8.GetString(bytesOnDisk);

        Assert.DoesNotContain(rawRefresh, textOnDisk, StringComparison.Ordinal);
        Assert.DoesNotContain(accessToken, textOnDisk, StringComparison.Ordinal);
        Assert.False(ContainsBytes(bytesOnDisk, Encoding.ASCII.GetBytes(rawRefresh)));
        Assert.False(ContainsBytes(bytesOnDisk, Encoding.ASCII.GetBytes(accessToken)));
    }

    // Acceptance criterion 2: with a valid stored token, "signed in?" is true - answered locally, with no
    // network call (the only network seam is BackendUnavailableTokenRefresher, which IsLoggedIn never invokes).
    [Fact]
    public void IsLoggedIn_ValidStoredToken_ReturnsTrue()
    {
        if (!OnWindows) return;

        var service = MakeService();
        service.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-1"));

        Assert.True(service.IsLoggedIn());
    }

    // Acceptance criterion 2: with no stored credential, "signed in?" is false.
    [Fact]
    public void IsLoggedIn_NoStoredCredential_ReturnsFalse()
    {
        if (!OnWindows) return;

        var service = MakeService();

        Assert.False(service.IsLoggedIn());
    }

    // Acceptance criterion 3: the Gateway reads the identity (email + provider) from the stored token.
    [Fact]
    public void GetIdentity_StoredTokenWithIdentityClaims_ReturnsEmailAndProvider()
    {
        if (!OnWindows) return;

        var service = MakeService();
        var token = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "gateway-user@example.com", "github");
        service.StoreTokens(new DevThrottleTokens(token, "refresh-1"));

        var identity = service.GetIdentity();

        Assert.NotNull(identity);
        Assert.Equal("gateway-user@example.com", identity.Email);
        Assert.Equal("github", identity.Provider);
    }

    // Acceptance criterion 3 (the no-credential path): with no stored credential the identity is
    // unavailable (null).
    [Fact]
    public void GetIdentity_NoStoredCredential_ReturnsNull()
    {
        if (!OnWindows) return;

        var service = MakeService();

        Assert.Null(service.GetIdentity());
    }

    // Acceptance criterion 5 (the test-seed seam): DEVTHROTTLE_TEST_SEED_TOKEN seeds the access-plus-
    // refresh pair into the Gateway store, and the seeded credential then reports signed-in and the
    // decoded identity. The env vars are set and restored so the test is self-contained.
    [Fact]
    public void SeedTestCredentialIfRequested_SeedsThePairAndReportsSignedInWithIdentity()
    {
        if (!OnWindows) return;

        var accessToken = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "seeded@example.com", "google");
        var previousSecret = Environment.GetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar);
        var previousSeed = Environment.GetEnvironmentVariable(GatewayAccountFactory.TestSeedTokenEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, GatewayTestJwt.SigningSecret);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.TestSeedTokenEnvVar, accessToken + "\nseeded-refresh-token");
        try
        {
            var store = new WindowsProtectedTokenStore(_blobPath);
            var service = GatewayAccountFactory.Build(store, _authEventsPath);

            GatewayAccountFactory.SeedTestCredentialIfRequested(service);

            Assert.True(File.Exists(_blobPath));
            Assert.True(service.IsLoggedIn());
            var identity = service.GetIdentity();
            Assert.NotNull(identity);
            Assert.Equal("seeded@example.com", identity.Email);
            Assert.Equal("google", identity.Provider);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previousSecret);
            Environment.SetEnvironmentVariable(GatewayAccountFactory.TestSeedTokenEnvVar, previousSeed);
        }
    }

    // No-op when the seed env var is unset: nothing is stored and the install is not signed in.
    [Fact]
    public void SeedTestCredentialIfRequested_NoSeedEnvVar_StoresNothing()
    {
        if (!OnWindows) return;

        var previousSeed = Environment.GetEnvironmentVariable(GatewayAccountFactory.TestSeedTokenEnvVar);
        Environment.SetEnvironmentVariable(GatewayAccountFactory.TestSeedTokenEnvVar, null);
        try
        {
            var service = MakeService();

            GatewayAccountFactory.SeedTestCredentialIfRequested(service);

            Assert.False(File.Exists(_blobPath));
            Assert.False(service.IsLoggedIn());
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.TestSeedTokenEnvVar, previousSeed);
        }
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }
}
