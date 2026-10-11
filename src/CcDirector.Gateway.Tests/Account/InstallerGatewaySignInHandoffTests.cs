using System.Runtime.Versioning;
using CcDirector.Core.Account;
using CcDirector.Gateway.Account;
using Xunit;

namespace CcDirector.Gateway.Tests.Account;

/// <summary>
/// Proves the installer -> Gateway sign-in hand-off (issue #906). The installer's forced sign-in step
/// now persists the captured token pair into the GATEWAY credential store (a
/// <see cref="WindowsProtectedTokenStore"/> rooted at the Gateway credential path, written through the
/// Core <see cref="DevThrottleAccountService"/>), instead of the Director store the Director deletes on
/// startup. These tests write a credential exactly the way the installer's persist path does, then read
/// it back exactly the way the Gateway reads it on first launch, and assert the Gateway reports
/// signed-in - so <see cref="GatewaySignInService.IsSignedIn"/> is true and no second browser sign-in is
/// prompted.
///
/// The credential store under test is the real Windows Data Protection store at a temporary path (the
/// facts no-op on a non-Windows host - the operating-system credential store is Windows-only for now).
/// The class is annotated [SupportedOSPlatform("windows")] so the platform-compatibility analyzer is
/// satisfied.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstallerGatewaySignInHandoffTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _blobPath;

    public InstallerGatewaySignInHandoffTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-installer-gw-handoff-" + Guid.NewGuid().ToString("N"));
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
    /// Writes the token pair the way the installer's persist path does: a real Windows Data Protection
    /// store at the (Gateway) credential path, driven through the Core account factory. This mirrors
    /// SignInRunner.PersistToGatewayCredentialStore without the fixed per-user path.
    /// </summary>
    private void PersistLikeInstaller(string accessToken, string refreshToken)
    {
        var installerStore = new WindowsProtectedTokenStore(_blobPath);
        var installerService = DevThrottleAccountFactory.Build(installerStore);
        installerService.StoreTokens(new DevThrottleTokens(accessToken, refreshToken));
    }

    /// <summary>
    /// Builds the Gateway credential service over the same blob path, with the signing secret set so a
    /// test-issued token validates - the exact read path the Gateway uses on first launch.
    /// </summary>
    private DevThrottleAccountService MakeGatewayService()
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

    // Acceptance criterion 2: after an installer-captured sign-in is persisted to the Gateway store, the
    // Gateway credential service reports signed-in on first launch.
    [Fact]
    public void InstallerWrittenCredential_IsReadAsSignedInByTheGateway()
    {
        if (!OnWindows) return;

        var accessToken = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        PersistLikeInstaller(accessToken, "installer-captured-refresh");

        var gatewayService = MakeGatewayService();

        Assert.True(File.Exists(_blobPath));
        Assert.True(gatewayService.IsLoggedIn());
    }

    // Acceptance criterion 2 (through the exact tray surface): GatewaySignInService.IsSignedIn(), which
    // PromptSignInIfNeeded() checks on first launch, is true - so no second browser sign-in is prompted.
    [Fact]
    public void InstallerWrittenCredential_MakesGatewaySignInServiceReportSignedIn()
    {
        if (!OnWindows) return;

        var accessToken = GatewayTestJwt.Create(DateTime.UtcNow.AddHours(1));
        PersistLikeInstaller(accessToken, "installer-captured-refresh");

        var signInService = new GatewaySignInService(MakeGatewayService());

        Assert.True(signInService.IsSignedIn());
    }

    // The other side of the bug: writing to the Director store leaves the Gateway store empty, so the
    // Gateway would report NOT signed-in and re-prompt. This guards against a regression back to the
    // pre-#906 behavior (installer writing the Director store the Gateway never reads).
    [Fact]
    public void EmptyGatewayStore_IsReadAsNotSignedInByTheGateway()
    {
        if (!OnWindows) return;

        var gatewayService = MakeGatewayService();

        Assert.False(File.Exists(_blobPath));
        Assert.False(gatewayService.IsLoggedIn());
    }
}
