using System.Net;
using System.Runtime.Versioning;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Tests the Gateway browser loopback sign-in (issue #637, Gateway Centralization Phase 2): the flow
/// that used to run on the Director now runs ON THE GATEWAY. Verifies the capture-and-store path with
/// an injected browser opener and a real loopback listener plus a stand-in completion that posts the
/// token (the same pattern the Core FirstRunLoginCoordinatorTests use): the browser is opened at the
/// configured sign-in URL, the credential the completion hands back is captured, and it is stored
/// through the Gateway-hosted credential service (issue #636) so the Gateway then reports signed-in.
/// Also covers "already signed in -> no second prompt is needed" and "cancelled sign-in leaves the
/// Gateway un-signed-in and retryable" (no crash).
///
/// The credential store under test is the real Windows Data Protection store at a temp blob path, so
/// the store/load round-trip is exercised on disk (the facts no-op on a non-Windows host - the
/// operating-system credential store is Windows-only for now, the #636/#637 assumption). The class is
/// annotated [SupportedOSPlatform("windows")] so the platform-compatibility analyzer is satisfied.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GatewaySignInServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _blobPath;
    private readonly string _authEventsPath;

    public GatewaySignInServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-gw-signin-" + Guid.NewGuid().ToString("N"));
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
    /// path, with the signing secret set so test-issued tokens validate.
    /// </summary>
    private DevThrottleAccountService MakeAccount()
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

    // Acceptance criteria 1 + 2: with no credential the browser is opened at the sign-in URL, the
    // loopback hand-back is captured, and the token is stored so the Gateway then reports signed-in.
    [Fact]
    public async Task RunSignInAsync_OpensBrowserCapturesHandBackAndStores_GatewayThenReportsSignedIn()
    {
        if (!OnWindows) return;

        var account = MakeAccount();
        Assert.False(account.IsLoggedIn()); // no credential to start - the Gateway would prompt

        string? openedUrl = null;
        var capturedTokens = new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "gateway-refresh-1");

        using var listener = new LoopbackLoginListener();
        var service = new GatewaySignInService(
            account,
            openBrowser: url => { openedUrl = url; return Task.CompletedTask; },
            listenerFactory: () => listener);

        var run = service.RunSignInAsync();
        await PostStandInCompletionAsync(listener.CallbackUrl, capturedTokens);
        var result = await run;

        Assert.True(result.Succeeded);
        Assert.NotNull(openedUrl);
        Assert.Contains(FirstRunLoginCoordinator.DefaultSignInBaseUrl, openedUrl);

        // The token is stored through the credential service and the Gateway now reports signed-in.
        Assert.True(service.IsSignedIn());
        Assert.True(account.IsLoggedIn());
        var store = new WindowsProtectedTokenStore(_blobPath);
        var stored = store.Load();
        Assert.NotNull(stored);
        Assert.Equal(capturedTokens.AccessToken, stored.AccessToken);
        Assert.Equal("gateway-refresh-1", stored.RefreshToken);
    }

    // Acceptance criterion 2 (identity): after sign-in the Gateway reads the signed-in identity from
    // the stored token (email + provider).
    [Fact]
    public async Task RunSignInAsync_AfterSignIn_GatewayReadsIdentity()
    {
        if (!OnWindows) return;

        var account = MakeAccount();
        var token = GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "gateway-user@example.com", "github");
        using var listener = new LoopbackLoginListener();
        var service = new GatewaySignInService(
            account,
            openBrowser: _ => Task.CompletedTask,
            listenerFactory: () => listener);

        var run = service.RunSignInAsync();
        await PostStandInCompletionAsync(listener.CallbackUrl, new DevThrottleTokens(token, "refresh-1"));
        var result = await run;

        Assert.True(result.Succeeded);
        var identity = service.GetIdentity();
        Assert.NotNull(identity);
        Assert.Equal("gateway-user@example.com", identity.Email);
        Assert.Equal("github", identity.Provider);
    }

    // Acceptance criterion 3: a Gateway that already has a stored credential reports signed-in, so the
    // tray does not prompt on a subsequent launch.
    [Fact]
    public void IsSignedIn_WithStoredCredential_ReturnsTrue_SoNoPromptOnSubsequentLaunch()
    {
        if (!OnWindows) return;

        var account = MakeAccount();
        account.StoreTokens(new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-1"));

        var service = new GatewaySignInService(account, openBrowser: _ => Task.CompletedTask);

        Assert.True(service.IsSignedIn());
    }

    [Fact]
    public void IsSignedIn_NoStoredCredential_ReturnsFalse_SoTheGatewayPrompts()
    {
        if (!OnWindows) return;

        var account = MakeAccount();
        var service = new GatewaySignInService(account, openBrowser: _ => Task.CompletedTask);

        Assert.False(service.IsSignedIn());
    }

    // Acceptance criterion 4: a cancelled sign-in leaves the Gateway un-signed-in and is retryable (no
    // crash) - the call returns a user-safe failure rather than throwing.
    [Fact]
    public async Task RunSignInAsync_Cancelled_LeavesGatewayUnsignedInAndRetryable()
    {
        if (!OnWindows) return;

        var account = MakeAccount();
        using var listener = new LoopbackLoginListener();
        using var cts = new CancellationTokenSource();
        var service = new GatewaySignInService(
            account,
            openBrowser: _ => Task.CompletedTask,
            listenerFactory: () => listener);

        var run = service.RunSignInAsync(cts.Token);
        cts.Cancel();
        var result = await run;

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.False(service.IsSignedIn());
        // Retryable: the single-flight guard has been released, so a fresh sign-in can start.
        Assert.False(service.IsSignInRunning);
    }

    // Acceptance criterion 4 (browser cannot open): a failure to open the browser returns a user-safe
    // failure without storing, and the Gateway stays un-signed-in and retryable.
    [Fact]
    public async Task RunSignInAsync_BrowserCannotOpen_ReturnsFailureAndStaysUnsignedIn()
    {
        if (!OnWindows) return;

        var account = MakeAccount();
        var service = new GatewaySignInService(
            account,
            openBrowser: _ => throw new InvalidOperationException("no browser"),
            listenerFactory: () => new LoopbackLoginListener());

        var result = await service.RunSignInAsync();

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
        Assert.False(service.IsSignedIn());
        Assert.False(service.IsSignInRunning);
    }

    // Single-flight: while a sign-in is in flight, a second RunSignInAsync is a no-op that returns a
    // failure rather than starting a second browser hand-off.
    [Fact]
    public async Task RunSignInAsync_WhileOneIsRunning_SecondCallIsANoOp()
    {
        if (!OnWindows) return;

        var account = MakeAccount();
        using var listener = new LoopbackLoginListener();
        var service = new GatewaySignInService(
            account,
            openBrowser: _ => Task.CompletedTask,
            listenerFactory: () => listener);

        var first = service.RunSignInAsync();
        Assert.True(service.IsSignInRunning);

        // A second call while the first is waiting for the hand-back does not open a second browser.
        var second = await service.RunSignInAsync();
        Assert.False(second.Succeeded);
        Assert.NotNull(second.FailureReason);

        // Complete the first so the test does not leak a pending listener.
        await PostStandInCompletionAsync(listener.CallbackUrl,
            new DevThrottleTokens(GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1)), "refresh-1"));
        var firstResult = await first;
        Assert.True(firstResult.Succeeded);
    }

    // Tray surface (acceptance criterion 1): with no credential the tray prompts on launch AND shows
    // the "Sign in to DevThrottle" action; the account row reads "Not signed in".
    [Fact]
    public void TraySurface_NotSignedIn_PromptsAndShowsSignInActionAndNotSignedInRow()
    {
        if (!OnWindows) return;

        var account = MakeAccount();
        var signIn = new GatewaySignInService(account, openBrowser: _ => Task.CompletedTask);

        Assert.True(GatewaySignInTraySurface.ShouldPromptOnLaunch(signIn));
        Assert.True(GatewaySignInTraySurface.ShouldShowSignInAction(signIn));
        Assert.Equal("Not signed in", GatewaySignInTraySurface.AccountRowValue(signIn));
    }

    // Tray surface (acceptance criterion 3): once signed in, the tray does NOT prompt on a subsequent
    // launch and does NOT show the sign-in action; the account row reads the signed-in identity.
    [Fact]
    public void TraySurface_SignedIn_DoesNotPromptOrShowActionAndShowsIdentity()
    {
        if (!OnWindows) return;

        var account = MakeAccount();
        account.StoreTokens(new DevThrottleTokens(
            GatewayTestJwt.CreateWithIdentity(DateTime.UtcNow.AddHours(1), "signed-in@example.com", "google"), "refresh-1"));
        var signIn = new GatewaySignInService(account, openBrowser: _ => Task.CompletedTask);

        Assert.False(GatewaySignInTraySurface.ShouldPromptOnLaunch(signIn));
        Assert.False(GatewaySignInTraySurface.ShouldShowSignInAction(signIn));
        Assert.Equal("Signed in (signed-in@example.com)", GatewaySignInTraySurface.AccountRowValue(signIn));
    }

    // Tray surface: a host with no sign-in flow (no credential service) never prompts, never shows the
    // action, and omits the account row.
    [Fact]
    public void TraySurface_NoSignInFlow_PromptsNothingAndOmitsRow()
    {
        Assert.False(GatewaySignInTraySurface.ShouldPromptOnLaunch(null));
        Assert.False(GatewaySignInTraySurface.ShouldShowSignInAction(null));
        Assert.Null(GatewaySignInTraySurface.AccountRowValue(null));
    }

    /// <summary>
    /// Simulates the sign-in completion (the local stand-in for the backend - the same role
    /// tools/devthrottle-dev-signin plays end to end) handing the credential back to the loopback
    /// callback as the access_token and refresh_token query parameters.
    /// </summary>
    private static async Task PostStandInCompletionAsync(Uri callbackUrl, DevThrottleTokens tokens)
    {
        var builder = new UriBuilder(callbackUrl)
        {
            Query = $"access_token={Uri.EscapeDataString(tokens.AccessToken)}&refresh_token={Uri.EscapeDataString(tokens.RefreshToken)}",
        };
        using var http = new HttpClient();
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                var response = await http.GetAsync(builder.Uri);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(20);
            }
        }
        throw new InvalidOperationException("Stand-in completion could not reach the loopback callback.");
    }
}
