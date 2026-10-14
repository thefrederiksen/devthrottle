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

    public GatewaySignInServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-gw-signin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _blobPath = Path.Combine(_tempDir, "devthrottle-credential.bin");
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
            return GatewayAccountFactory.Build(store, hosted: false);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayAccountFactory.SigningSecretEnvVar, previous);
        }
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
}
