using CcDirector.ControlApi;
using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The troubleshooting ladder (issue #223): rungs run in the human-diagnosis order, the
/// FIRST failing rung is the verdict (later rungs skip), and the firewall is deliberately
/// last and informational. Fully seam-driven - no tailscale CLI, no sockets.
/// </summary>
public sealed class GatewayConnectivitySelfTestTests
{
    private const int Port = 7885;
    private const string DirectorId = "11111111-2222-3333-4444-555555555555";

    private static readonly string RunningStatusJson = """{ "BackendState": "Running", "Self": { "DNSName": "machine-a.tail0123.ts.net." } }""";
    private static readonly string StoppedStatusJson = """{ "BackendState": "Stopped" }""";
    private static readonly string ServeWithMapping = $$"""{ "Web": { "machine-a.tail0123.ts.net:{{Port}}": { "Handlers": { "/": { "Proxy": "http://127.0.0.1:{{Port}}" } } } } }""";
    private static readonly string ServeEmpty = """{ "Web": {} }""";

    private static GatewayConnectivitySelfTest Make(
        Func<string, (bool, string, string)> runner,
        Func<string, CancellationToken, Task<(bool, string)>>? httpProbe = null,
        bool cliAvailable = true,
        string? endpoint = "https://machine-a.tail0123.ts.net:7885",
        string? provisionerLastError = null)
        => new(Port, DirectorId, endpoint, gatewayUrl: null, provisionerLastError)
        {
            Runner = runner,
            CliAvailable = () => cliAvailable,
            HttpProbe = httpProbe ?? ((_, _) => Task.FromResult((true, $"{{\"directorId\":\"{DirectorId}\"}}"))),
        };

    private static async Task<List<LadderRung>> RunAll(GatewayConnectivitySelfTest test)
    {
        var rungs = new List<LadderRung>();
        await foreach (var r in test.RunAsync())
            rungs.Add(r);
        return rungs;
    }

    private static (bool, string, string) FakeCli(string args) => args switch
    {
        "status --json" => (true, RunningStatusJson, ""),
        "serve status --json" => (true, ServeWithMapping, ""),
        _ => throw new InvalidOperationException($"unexpected CLI call: {args}"),
    };

    [Fact]
    public async Task EverythingHealthy_AllChecksPass_InfosLast()
    {
        var rungs = await RunAll(Make(FakeCli));

        Assert.Equal(6, rungs.Count);
        Assert.All(rungs.Take(4), r => Assert.Equal(RungStatus.Pass, r.Status));
        Assert.Equal(RungStatus.Info, rungs[4].Status); // versions
        Assert.Equal(RungStatus.Info, rungs[5].Status); // firewall, LAST by design
        Assert.Contains("Firewall", rungs[5].Title);
    }

    [Fact]
    public async Task TailscaleStopped_FailsRungOne_SkipsTheRest()
    {
        var rungs = await RunAll(Make(args => args == "status --json"
            ? (true, StoppedStatusJson, "")
            : throw new InvalidOperationException("ladder must stop at rung 1")));

        Assert.Equal(RungStatus.Fail, rungs[0].Status);
        Assert.Contains("Stopped", rungs[0].Found);
        Assert.Contains("tailscale up", rungs[0].Fix);
        Assert.Equal(RungStatus.Skipped, rungs[1].Status);
        Assert.Equal(RungStatus.Skipped, rungs[2].Status);
        Assert.Equal(RungStatus.Skipped, rungs[3].Status);
    }

    [Fact]
    public async Task CliMissing_FailsRungOne_WithInstallFix()
    {
        var rungs = await RunAll(Make(_ => throw new InvalidOperationException("no CLI calls expected"), cliAvailable: false));

        Assert.Equal(RungStatus.Fail, rungs[0].Status);
        Assert.Contains("Install Tailscale", rungs[0].Fix);
    }

    [Fact]
    public async Task ServeMappingMissing_FailsRungTwo_WithExactServeCommand_AndProvisionerError()
    {
        // The SORENLAPTOP root cause: tailnet up, nothing serves the port.
        var rungs = await RunAll(Make(
            args => args == "status --json" ? (true, RunningStatusJson, "") : (true, ServeEmpty, ""),
            provisionerLastError: "tailscale serve --https=7885 failed: access denied"));

        Assert.Equal(RungStatus.Pass, rungs[0].Status);
        Assert.Equal(RungStatus.Fail, rungs[1].Status);
        Assert.Equal($"tailscale serve --bg --https={Port} http://localhost:{Port}", rungs[1].Fix);
        Assert.Contains("SORENLAPTOP", rungs[1].Found);
        Assert.Contains("access denied", rungs[1].Found); // provisioner's recorded reason rides along
        Assert.Equal(RungStatus.Skipped, rungs[2].Status);
        Assert.Equal(RungStatus.Skipped, rungs[3].Status);
    }

    [Fact]
    public async Task LocalListenerDead_FailsRungThree_AsDirectorProblem()
    {
        var rungs = await RunAll(Make(FakeCli,
            httpProbe: (url, _) => Task.FromResult(url.Contains("127.0.0.1")
                ? (false, "connection refused")
                : (true, DirectorId))));

        Assert.Equal(RungStatus.Pass, rungs[1].Status);
        Assert.Equal(RungStatus.Fail, rungs[2].Status);
        Assert.Contains("Director problem", rungs[2].Found);
        Assert.Equal(RungStatus.Skipped, rungs[3].Status);
    }

    [Fact]
    public async Task AdvertisedUrlDead_FailsRungFour_PointsAtTheUrl()
    {
        var rungs = await RunAll(Make(FakeCli,
            httpProbe: (url, _) => Task.FromResult(url.Contains("127.0.0.1")
                ? (true, $"{{\"directorId\":\"{DirectorId}\"}}")
                : (false, "timeout after 5s"))));

        Assert.Equal(RungStatus.Pass, rungs[2].Status);
        Assert.Equal(RungStatus.Fail, rungs[3].Status);
        Assert.Contains("timeout after 5s", rungs[3].Found);
    }

    [Fact]
    public async Task AdvertisedUrlAnswersAsWrongDirector_FailsRungFour()
    {
        var rungs = await RunAll(Make(FakeCli,
            httpProbe: (url, _) => Task.FromResult(url.Contains("127.0.0.1")
                ? (true, $"{{\"directorId\":\"{DirectorId}\"}}")
                : (true, "{\"directorId\":\"a-completely-different-id\"}"))));

        Assert.Equal(RungStatus.Fail, rungs[3].Status);
        Assert.Contains("DIFFERENT Director", rungs[3].Found);
        Assert.Contains("serve --https=7885 off", rungs[3].Fix);
    }

    [Fact]
    public async Task NoAdvertisedEndpoint_FailsRungFour_ExplainsWhy()
    {
        var rungs = await RunAll(Make(FakeCli, endpoint: null));

        Assert.Equal(RungStatus.Fail, rungs[3].Status);
        Assert.Contains("no advertised tailnet endpoint", rungs[3].Found);
    }

    // ===== ParseBackendState (pure) =====

    [Theory]
    [InlineData("""{ "BackendState": "Running" }""", "Running")]
    [InlineData("""{ "BackendState": "Stopped" }""", "Stopped")]
    [InlineData("""{ "BackendState": "NeedsLogin" }""", "NeedsLogin")]
    [InlineData("""{ "Self": {} }""", null)]
    [InlineData("""{ "BackendState": "" }""", null)]
    [InlineData("""{ "BackendState": 42 }""", null)]
    public void ParseBackendState_ExtractsOrNull(string json, string? expected)
        => Assert.Equal(expected, TailscaleIdentity.ParseBackendState(json));
}
