using System.Collections.Concurrent;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #325: the advertised-endpoint re-verification state machine. The Gateway probes each
/// HTTP-registered Director's advertised endpoint every heartbeat cycle; a failed probe (no
/// answer, non-2xx, or an answer from the wrong process) flags the registration
/// unreachable-by-name - distinct from heartbeat loss - and the next successful probe clears it.
/// The monitor's probe is injected so the state machine is tested without HTTP.
/// </summary>
public sealed class AdvertisedEndpointMonitorTests
{
    private const string Id = "dir-325";
    private const string Endpoint = "https://machine.tailnet.example:7879";

    private static DirectorRegistry NewRegistryWithHttpDirector(string id = Id, string endpoint = Endpoint)
    {
        // No Start(): these tests need neither the filesystem watcher nor the sweeper.
        var reg = new DirectorRegistry(Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N")));
        reg.Upsert(new DirectorRegistrationRequest { DirectorId = id, TailnetEndpoint = endpoint, Pid = 1234 });
        return reg;
    }

    private static AdvertisedEndpointMonitor MonitorAnswering(DirectorRegistry reg, Func<(HealthDto? health, string? error)> answer)
        => new(reg, (_, _) => Task.FromResult(answer()));

    [Fact]
    public async Task ProbeAllAsync_EndpointStopsAnswering_FlagsUnreachableByName()
    {
        // Arrange: healthy first (answers as itself), then the advertised name goes dark.
        var reg = NewRegistryWithHttpDirector();
        var dead = false;
        using var monitor = MonitorAnswering(reg, () => dead
            ? (null, "healthz probe timed out after 2s at " + Endpoint + "/healthz")
            : (new HealthDto { DirectorId = Id }, null));

        // Act + Assert: healthy pass stamps ok.
        await monitor.ProbeAllAsync();
        var d = reg.Get(Id);
        Assert.NotNull(d);
        Assert.Equal(DirectorDto.EndpointStateOk, d.AdvertisedEndpointState);
        Assert.Null(d.AdvertisedEndpointUnreachableSince);
        Assert.Null(d.AdvertisedEndpointError);
        Assert.NotNull(d.AdvertisedEndpointCheckedAt);

        // The break: the very next failed probe flags it (well inside the 30 s budget).
        dead = true;
        await monitor.ProbeAllAsync();
        d = reg.Get(Id);
        Assert.NotNull(d);
        Assert.Equal(DirectorDto.EndpointStateUnreachableByName, d.AdvertisedEndpointState);
        Assert.NotNull(d.AdvertisedEndpointUnreachableSince);
        Assert.Contains("timed out", d.AdvertisedEndpointError);
    }

    [Fact]
    public async Task ProbeAllAsync_EndpointAnswersAgain_ClearsFlag()
    {
        // Arrange: flagged.
        var reg = NewRegistryWithHttpDirector();
        var dead = true;
        using var monitor = MonitorAnswering(reg, () => dead
            ? (null, "connection refused")
            : (new HealthDto { DirectorId = Id }, null));
        await monitor.ProbeAllAsync();
        var d = reg.Get(Id);
        Assert.NotNull(d);
        Assert.Equal(DirectorDto.EndpointStateUnreachableByName, d.AdvertisedEndpointState);

        // Act: the mapping comes back - the next successful probe auto-clears, no restarts.
        dead = false;
        await monitor.ProbeAllAsync();

        // Assert
        d = reg.Get(Id);
        Assert.NotNull(d);
        Assert.Equal(DirectorDto.EndpointStateOk, d.AdvertisedEndpointState);
        Assert.Null(d.AdvertisedEndpointUnreachableSince);
        Assert.Null(d.AdvertisedEndpointError);
    }

    [Fact]
    public async Task ProbeAllAsync_DirectorIdMismatch_CountsAsFailure()
    {
        // Arrange: the advertised URL answers /healthz, but as a DIFFERENT Director (impostor guard).
        var reg = NewRegistryWithHttpDirector();
        using var monitor = MonitorAnswering(reg, () => (new HealthDto { DirectorId = "someone-else" }, null));

        // Act
        await monitor.ProbeAllAsync();

        // Assert: an answer from the wrong process is a failure with its own reason, never a pass.
        var d = reg.Get(Id);
        Assert.NotNull(d);
        Assert.Equal(DirectorDto.EndpointStateUnreachableByName, d.AdvertisedEndpointState);
        Assert.Contains("someone-else", d.AdvertisedEndpointError);
        Assert.Contains("wrong process", d.AdvertisedEndpointError);
    }

    [Fact]
    public async Task ProbeAllAsync_AnswerWithoutDirectorId_CountsAsFailure()
    {
        // An unidentifiable answerer cannot prove the name reaches THIS Director.
        var reg = NewRegistryWithHttpDirector();
        using var monitor = MonitorAnswering(reg, () => (new HealthDto { DirectorId = null }, null));

        await monitor.ProbeAllAsync();

        var d = reg.Get(Id);
        Assert.NotNull(d);
        Assert.Equal(DirectorDto.EndpointStateUnreachableByName, d.AdvertisedEndpointState);
        Assert.Contains("no directorId", d.AdvertisedEndpointError);
    }

    [Fact]
    public async Task ProbeAllAsync_FileDiscoveredDirector_IsNotProbed()
    {
        // Arrange: an FSW-discovered same-machine Director (no advertised name to verify).
        var dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "file-dir.json"),
            "{\"directorId\":\"file-dir\",\"pid\":" + Environment.ProcessId + ",\"controlEndpoint\":\"http://127.0.0.1:55555\"}");
        using var reg = new DirectorRegistry(dir);
        reg.Start(); // synchronous LoadExisting picks the file up

        var probed = new ConcurrentBag<string>();
        using var monitor = new AdvertisedEndpointMonitor(reg, (endpoint, _) =>
        {
            probed.Add(endpoint);
            return Task.FromResult<(HealthDto?, string?)>((null, "should never be called"));
        });

        // Act
        await monitor.ProbeAllAsync();

        // Assert: never probed, state stays null (not applicable, not "ok").
        Assert.Empty(probed);
        var d = reg.Get("file-dir");
        Assert.NotNull(d);
        Assert.Null(d.AdvertisedEndpointState);
    }

    [Fact]
    public async Task ProbeAllAsync_FlaggedNoEndpointRegistration_IsNotProbed()
    {
        // Arrange: a #324 flagged registration - the Director itself declared its endpoint dead.
        var reg = new DirectorRegistry(Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N")));
        reg.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = "flagged-dir",
            TailnetEndpoint = "",
            EndpointUnreachableReason = "tailnet identity unresolvable: tailscale CLI not found",
        });

        var probed = new ConcurrentBag<string>();
        using var monitor = new AdvertisedEndpointMonitor(reg, (endpoint, _) =>
        {
            probed.Add(endpoint);
            return Task.FromResult<(HealthDto?, string?)>((null, "should never be called"));
        });

        // Act
        await monitor.ProbeAllAsync();

        // Assert
        Assert.Empty(probed);
        var d = reg.Get("flagged-dir");
        Assert.NotNull(d);
        Assert.Null(d.AdvertisedEndpointState);
    }

    [Fact]
    public void RecordEndpointProbeResult_FailureWithoutReason_Throws()
    {
        var reg = NewRegistryWithHttpDirector();
        Assert.Throws<ArgumentException>(() => reg.RecordEndpointProbeResult(Id, ok: false, error: null));
    }

    [Fact]
    public void Upsert_AfterFlagged_ResetsEndpointProbeState()
    {
        // A re-register replaces the dto, so a fresh registration re-earns its probe state.
        var reg = NewRegistryWithHttpDirector();
        reg.RecordEndpointProbeResult(Id, ok: false, error: "connection refused");
        var flagged = reg.Get(Id);
        Assert.NotNull(flagged);
        Assert.Equal(DirectorDto.EndpointStateUnreachableByName, flagged.AdvertisedEndpointState);

        reg.Upsert(new DirectorRegistrationRequest { DirectorId = Id, TailnetEndpoint = Endpoint, Pid = 1234 });

        var fresh = reg.Get(Id);
        Assert.NotNull(fresh);
        Assert.Null(fresh.AdvertisedEndpointState);
        Assert.Null(fresh.AdvertisedEndpointUnreachableSince);
    }
}
