using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Director-side serve self-provisioning (issue #197): the Director owns the tailscale
/// serve mapping for its OWN port. All CLI calls go through injectable seams so these
/// tests run on machines (CI) without Tailscale installed. The test process runs with
/// CC_GATEWAY_NO_TAILSCALE=1 (see TestEnvironment), so lifecycle tests opt back in with
/// <c>Enabled = true</c> - safe because every CLI call here is faked.
/// </summary>
public sealed class TailscaleServeSelfProvisionerTests
{
    private const int Port = 7891;

    private static string StatusJsonWithMapping(int port, string proxy) => $$"""
        {
          "Web": {
            "machine-a.tail0123.ts.net:{{port}}": {
              "Handlers": { "/": { "Proxy": "{{proxy}}" } }
            }
          }
        }
        """;

    // ===== StatusHasMapping (pure) =====

    [Fact]
    public void StatusHasMapping_PresentLoopbackSamePort_True()
    {
        var json = StatusJsonWithMapping(Port, $"http://127.0.0.1:{Port}");
        Assert.True(TailscaleServeSelfProvisioner.StatusHasMapping(json, Port));
    }

    [Fact]
    public void StatusHasMapping_LocalhostForm_True()
    {
        var json = StatusJsonWithMapping(Port, $"http://localhost:{Port}");
        Assert.True(TailscaleServeSelfProvisioner.StatusHasMapping(json, Port));
    }

    [Fact]
    public void StatusHasMapping_WrongBackendPort_False()
    {
        // 7891 mapped, but proxying to a DIFFERENT local port: not our provisioner shape.
        var json = StatusJsonWithMapping(Port, "http://127.0.0.1:7878");
        Assert.False(TailscaleServeSelfProvisioner.StatusHasMapping(json, Port));
    }

    [Fact]
    public void StatusHasMapping_NonLoopbackBackend_False()
    {
        var json = StatusJsonWithMapping(Port, $"http://10.0.0.5:{Port}");
        Assert.False(TailscaleServeSelfProvisioner.StatusHasMapping(json, Port));
    }

    [Fact]
    public void StatusHasMapping_OtherPortOnly_False()
    {
        var json = StatusJsonWithMapping(7880, "http://127.0.0.1:7880");
        Assert.False(TailscaleServeSelfProvisioner.StatusHasMapping(json, Port));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{ "Web": {} }""")]
    public void StatusHasMapping_EmptyOrNoWeb_False(string json)
        => Assert.False(TailscaleServeSelfProvisioner.StatusHasMapping(json, Port));

    // ===== EnsureMappingAsync lifecycle (via seams) =====

    [Fact]
    public async Task EnsureMapping_AlreadyPresent_DoesNotMutate()
    {
        var mutations = new List<string>();
        using var p = new TailscaleServeSelfProvisioner(Port)
        {
            Enabled = true,
            CliAvailable = () => true,
            Runner = _ => (true, StatusJsonWithMapping(Port, $"http://127.0.0.1:{Port}"), ""),
            MutatingRunner = args => { mutations.Add(args); return (true, "", ""); },
        };

        var (ok, error) = await p.EnsureMappingAsync();

        Assert.True(ok);
        Assert.Null(error);
        Assert.Empty(mutations); // steady state costs zero serve-table writes
        Assert.NotNull(p.LastAssertedAt);
        Assert.Null(p.LastError);
    }

    [Fact]
    public async Task EnsureMapping_Missing_AssertsOwnPort()
    {
        var mutations = new List<string>();
        using var p = new TailscaleServeSelfProvisioner(Port)
        {
            Enabled = true,
            CliAvailable = () => true,
            Runner = _ => (true, "{}", ""),
            MutatingRunner = args => { mutations.Add(args); return (true, "", ""); },
        };

        var (ok, error) = await p.EnsureMappingAsync();

        Assert.True(ok);
        Assert.Null(error);
        var cmd = Assert.Single(mutations);
        Assert.Equal($"serve --bg --https={Port} http://localhost:{Port}", cmd);
    }

    [Fact]
    public async Task EnsureMapping_CliFails_ReturnsExactReason_NoFallback()
    {
        using var p = new TailscaleServeSelfProvisioner(Port)
        {
            Enabled = true,
            CliAvailable = () => true,
            Runner = _ => (true, "{}", ""),
            MutatingRunner = _ => (false, "", "error: HTTPS is not enabled on the tailnet"),
        };

        var (ok, error) = await p.EnsureMappingAsync();

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("HTTPS is not enabled on the tailnet", error);
        Assert.Equal(error, p.LastError); // surfaced for the Director-side diagnostics
    }

    [Fact]
    public async Task EnsureMapping_CliMissing_ReportsInstallInstruction()
    {
        using var p = new TailscaleServeSelfProvisioner(Port)
        {
            Enabled = true,
            CliAvailable = () => false,
        };

        var (ok, error) = await p.EnsureMappingAsync();

        Assert.False(ok);
        Assert.Contains("tailscale CLI not found", error);
    }

    [Fact]
    public async Task EnsureMapping_UnreadableStatus_StillAsserts()
    {
        // A broken status read must not block the assert: asserting is idempotent and the
        // mapping is what makes the Director reachable at all.
        var mutations = new List<string>();
        using var p = new TailscaleServeSelfProvisioner(Port)
        {
            Enabled = true,
            CliAvailable = () => true,
            Runner = _ => (false, "", "context deadline exceeded"),
            MutatingRunner = args => { mutations.Add(args); return (true, "", ""); },
        };

        var (ok, _) = await p.EnsureMappingAsync();

        Assert.True(ok);
        Assert.Single(mutations);
    }

    [Fact]
    public void RemoveOwnMapping_TurnsServeOff()
    {
        var mutations = new List<string>();
        using var p = new TailscaleServeSelfProvisioner(Port)
        {
            Enabled = true,
            CliAvailable = () => true,
            MutatingRunner = args => { mutations.Add(args); return (true, "", ""); },
        };

        p.RemoveOwnMapping();

        var cmd = Assert.Single(mutations);
        Assert.Equal($"serve --https={Port} off", cmd);
    }

    [Fact]
    public void Ctor_NonPositivePort_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TailscaleServeSelfProvisioner(0));

    [Fact]
    public async Task KillSwitchSet_DefaultInstance_NeverMutates()
    {
        // The test process runs with CC_GATEWAY_NO_TAILSCALE=1 (TestEnvironment), so a
        // provisioner WITHOUT the explicit test opt-in must report success-with-note and
        // never touch the CLI - this is the property that keeps any test-spawned host
        // from rewriting the machine's real serve table (issues #179/#200).
        var mutations = new List<string>();
        using var p = new TailscaleServeSelfProvisioner(Port)
        {
            CliAvailable = () => true,
            MutatingRunner = args => { mutations.Add(args); return (true, "", ""); },
        };

        var (ok, error) = await p.EnsureMappingAsync();
        p.RemoveOwnMapping();

        Assert.True(ok);
        Assert.Contains("CC_GATEWAY_NO_TAILSCALE", error);
        Assert.Empty(mutations);
    }
}
