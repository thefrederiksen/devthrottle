using System.Text.Json.Nodes;
using CcDirector.Core.Configuration;
using CcDirector.Core.Onboarding;
using CcDirector.Core.Settings;
using Xunit;

namespace CcDirector.Core.Tests.Onboarding;

/// <summary>
/// Tests for the first-run onboarding model (issue #370): the auto-open trigger condition, the
/// gateway URL validation, the persistence of gateway.url and the onboarding-complete marker, and
/// the Claude Code availability check. Config-touching tests redirect CcStorage to a temp root via
/// CC_DIRECTOR_ROOT and are serialized with the other config-env tests.
/// </summary>
[Collection("ConfigEnvSerial")]
public class OnboardingModelTests
{
    private static string NewRoot() =>
        Path.Combine(Path.GetTempPath(), "cc-director-onboarding-tests", Guid.NewGuid().ToString("N"));

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

    // --- Trigger condition -----------------------------------------------------------------

    [Fact]
    public void ShouldShowOnboarding_FreshInstall_NoMarkerNoGateway_ReturnsTrue()
    {
        WithRoot(() => Assert.True(OnboardingModel.ShouldShowOnboarding()));
    }

    [Fact]
    public void ShouldShowOnboarding_AfterMarkComplete_ReturnsFalse()
    {
        WithRoot(() =>
        {
            Assert.True(OnboardingModel.ShouldShowOnboarding());

            OnboardingModel.MarkComplete();

            Assert.False(OnboardingModel.ShouldShowOnboarding());
            Assert.True(OnboardingModel.IsOnboardingComplete());
        });
    }

    [Fact]
    public void ShouldShowOnboarding_WhenGatewayUrlSet_ReturnsFalse_EvenWithoutMarker()
    {
        WithRoot(() =>
        {
            OnboardingModel.PersistGatewayUrl("http://gateway-host:7878");

            // A configured gateway alone suppresses the auto-open, even before the marker is set.
            Assert.False(OnboardingModel.IsOnboardingComplete());
            Assert.False(OnboardingModel.ShouldShowOnboarding());
        });
    }

    [Fact]
    public void ShouldShowOnboarding_WhenGatewayUrlBlank_StillShows()
    {
        WithRoot(() =>
        {
            // A blank gateway.url is local-only, not a configured gateway, so the wizard still shows.
            CcDirectorConfigService.MergePatch(new JsonObject
            {
                ["gateway"] = new JsonObject { ["url"] = "" },
            });

            Assert.True(OnboardingModel.ShouldShowOnboarding());
        });
    }

    // --- Gateway URL validation ------------------------------------------------------------

    [Theory]
    [InlineData("http://gateway-host:7878")]
    [InlineData("https://gateway.example.com")]
    [InlineData("http://10.0.0.5:7878")]
    public void ValidateGatewayUrl_WellFormedHttpUrl_IsValid(string url)
    {
        var result = OnboardingModel.ValidateGatewayUrl(url);
        Assert.True(result.IsValid);
        Assert.Equal(url, result.NormalizedUrl);
        Assert.Equal("", result.Message);
    }

    [Fact]
    public void ValidateGatewayUrl_TrimsSurroundingWhitespace()
    {
        var result = OnboardingModel.ValidateGatewayUrl("  http://gateway-host:7878  ");
        Assert.True(result.IsValid);
        Assert.Equal("http://gateway-host:7878", result.NormalizedUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gateway-host:7878")]
    [InlineData("ftp://gateway-host:7878")]
    [InlineData("not a url at all")]
    public void ValidateGatewayUrl_InvalidInput_IsRejectedWithMessage(string url)
    {
        var result = OnboardingModel.ValidateGatewayUrl(url);
        Assert.False(result.IsValid);
        Assert.Equal("", result.NormalizedUrl);
        Assert.NotEqual("", result.Message);
    }

    [Fact]
    public void ValidateGatewayUrl_Null_IsRejected()
    {
        var result = OnboardingModel.ValidateGatewayUrl(null);
        Assert.False(result.IsValid);
    }

    // --- Persistence -----------------------------------------------------------------------

    [Fact]
    public void PersistGatewayUrl_ValidUrl_WritesGatewayUrl()
    {
        WithRoot(() =>
        {
            OnboardingModel.PersistGatewayUrl("http://gateway-host:7878");

            var gateway = GatewayConfig.Load();
            Assert.Equal("http://gateway-host:7878", gateway.Url);
        });
    }

    [Fact]
    public void PersistGatewayUrl_InvalidUrl_Throws_AndPersistsNothing()
    {
        WithRoot(() =>
        {
            Assert.Throws<ArgumentException>(() => OnboardingModel.PersistGatewayUrl("not-a-url"));

            var gateway = GatewayConfig.Load();
            Assert.Equal("", gateway.Url);
        });
    }

    [Fact]
    public void MarkComplete_DoesNotClobberExistingGatewaySection()
    {
        WithRoot(() =>
        {
            OnboardingModel.PersistGatewayUrl("http://gateway-host:7878");
            OnboardingModel.MarkComplete();

            // The completion marker is merged in without dropping the gateway block (no data loss).
            Assert.True(OnboardingModel.IsOnboardingComplete());
            Assert.Equal("http://gateway-host:7878", GatewayConfig.Load().Url);
        });
    }

    // --- Agent availability ----------------------------------------------------------------

    [Fact]
    public void CheckClaudeAvailable_NullOptions_Throws()
    {
        var model = new OnboardingModel(new ToolDetectionService());
        Assert.Throws<ArgumentNullException>(() => model.CheckClaudeAvailable(null!));
    }

    [Fact]
    public void CheckClaudeAvailable_AlwaysReturnsCoherentVerdictWithNonEmptyMessage()
    {
        // Whatever the build agent's PATH looks like, the agent step must never dead-end: the result
        // always carries an actionable message, and the available/path fields agree (a resolved path
        // exactly when available). This is the contract the wizard renders against, regardless of
        // whether Claude Code happens to be installed on this machine.
        var options = new AgentOptions { ClaudePath = "definitely-not-a-real-claude-binary-xyz" };
        var model = new OnboardingModel(new ToolDetectionService());

        var result = model.CheckClaudeAvailable(options);

        Assert.NotEqual("", result.Message);
        Assert.Equal(result.IsAvailable, result.ResolvedPath.Length > 0);
    }

    [Fact]
    public void CheckClaudeAvailable_ReturnsAvailable_WhenClaudeResolvesToAnExistingFile()
    {
        // Point the configured Claude path at a real existing executable on this machine so the
        // PATH/executable resolver (the #448 work) reports it available. This proves the available
        // branch without depending on Claude actually being installed on the build agent.
        var realExe = ResolveExistingSystemExecutable();
        var options = new AgentOptions { ClaudePath = realExe };
        var model = new OnboardingModel(new ToolDetectionService());

        var result = model.CheckClaudeAvailable(options);

        Assert.True(result.IsAvailable);
        Assert.NotEqual("", result.ResolvedPath);
    }

    private static string ResolveExistingSystemExecutable()
    {
        // cmd.exe on Windows, /bin/sh elsewhere - a file guaranteed to exist and be resolvable.
        if (OperatingSystem.IsWindows())
        {
            var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            var cmd = Path.Combine(systemRoot, "System32", "cmd.exe");
            if (File.Exists(cmd)) return cmd;
        }

        foreach (var candidate in new[] { "/bin/sh", "/bin/bash" })
            if (File.Exists(candidate)) return candidate;

        throw new InvalidOperationException("No known system executable found for the test.");
    }
}
