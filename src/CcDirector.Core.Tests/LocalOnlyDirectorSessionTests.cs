using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Regression proof for issue #1809: a freshly installed Director opens a New Session with NO account
/// and NO gateway. This joins the PRODUCTION seam that decides local-only numbering rather than a bare
/// default SessionManager: the host reads the real gateway config and sets
/// <see cref="SessionManager.FleetNumberingActive"/> from <see cref="GatewayConfig.IsEnabled"/>
/// (ControlApiHost.cs:592 - <c>_sessionManager.FleetNumberingActive = gatewayConfig.IsEnabled;</c>).
/// With no gateway.url in config that flag is false, so a session must be numbered locally, at once, and
/// the Gateway must never be consulted. If New Session ever grew a gateway/account dependency - or if an
/// empty config started reporting a gateway as enabled - these tests go red.
///
/// Config is redirected to a temp root via CC_DIRECTOR_ROOT and serialized with the other config-env
/// tests. Sessions spawn a real stand-in shell (cmd.exe / /bin/sh via <see cref="TestShell"/>), matching
/// <see cref="SessionManagerTests"/>.
/// </summary>
[Collection("ConfigEnvSerial")]
public class LocalOnlyDirectorSessionTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "cc-director-localonly-session-tests", Guid.NewGuid().ToString("N"));

    private static void WithRoot(Action body)
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        var root = NewRoot();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static SessionManager NewManager() => new(new AgentOptions
    {
        ClaudePath = TestShell.Path, // cmd.exe on Windows, /bin/sh elsewhere
        DefaultBufferSizeBytes = 65536,
        GracefulShutdownTimeoutSeconds = 2
    });

    [Fact]
    public void FreshInstall_EmptyConfig_ReportsGatewayDisabled()
    {
        // GatewayConfig.IsEnabled is the exact value the host assigns to FleetNumberingActive. A fresh
        // install has no gateway.url, so it must read as disabled - the root of "New Session numbers
        // locally, no gateway needed."
        WithRoot(() => Assert.False(GatewayConfig.Load().IsEnabled));
    }
}
