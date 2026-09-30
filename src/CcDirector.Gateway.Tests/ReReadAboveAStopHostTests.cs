using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// A PARENT IS READ AGAIN WHEN THE LAST SESSION UNDER IT STOPS (issue 3499), on a REAL booted hosted Gateway. What only a
/// booted host proves is THE WIRING: the host's turn-end fan-out, and the watcher's exit and removal callbacks, each start
/// the re-read. Every test drives the host's own turn-end watcher - the object the Director stream feeds - so deleting
/// one of those call sites in GatewayHost turns a test here red.
///
/// The parent carries a calm reading the model made while its worker ran, stored straight into the host's own store.
/// The judge switch is left at its default (off): the re-read asks no judge, so it runs anyway, and the Director is
/// not connected, so the screen is unreadable - the owned-session steps decide without it.
///
/// PARKED SUITE. Gateway.Tests serializes machine-wide and does not run in the default gate.
/// </summary>
[Collection("DirectorRoot")]
public sealed class ReReadAboveAStopHostTests : IAsyncLifetime
{
    private const string SharedToken = "reread-above-a-stop-host-token";
    private const string DirectorId = "director-reread";

    private readonly ITestOutputHelper _out;
    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-reread-root-" + Guid.NewGuid().ToString("N"));
    private readonly string _instancesDir = Path.Combine(Path.GetTempPath(), "cc-reread-" + Guid.NewGuid().ToString("N"));
    private string? _priorHosted;
    private string? _priorRoot;
    private bool _priorSweep;

    private GatewayHost _gateway = null!;
    private TenantId _tenant;
    private long _pushSequence;

    private readonly string _parentId = Guid.NewGuid().ToString();
    private readonly string _workerId = Guid.NewGuid().ToString();
    private readonly List<SessionDto> _fleet = new();

    public ReReadAboveAStopHostTests(ITestOutputHelper output) => _out = output;

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        _priorRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        _priorSweep = FleetManagerEventSweep.Enabled;
        FleetManagerEventSweep.Enabled = false;

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: SharedToken, authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        var director = HostedTestEnrollment.Enroll(_gateway, $"sub-reread-{_runId}", $"reread-{_runId}@example.com",
            $"dev-reread-dir-{_runId}", "MRRD");
        _tenant = director.Tenant;

        var now = DateTime.UtcNow;
        _fleet.Add(new SessionDto { SessionId = _parentId, Name = "Manager", ActivityState = "WaitingForInput",
                                    CreatedAt = now.AddHours(-1), LastActivityAt = now, DirectorId = DirectorId });
        _fleet.Add(new SessionDto { SessionId = _workerId, Name = "Worker", ActivityState = "Working",
                                    IsControlled = true, ControllerSessionId = _parentId,
                                    CreatedAt = now.AddMinutes(-5), LastActivityAt = now, DirectorId = DirectorId });
        _gateway.Registry.RegisterFromStream(DirectorId, "MACHINE-" + DirectorId, "someone", "1.0", pid: 4321,
            startedAt: DateTime.UtcNow, tenant: _tenant);
        _gateway.PushedSessions.RegisterConnection(_tenant, DirectorId, "conn-1");
        Assert.True(_gateway.PushedSessions.ApplySnapshot(_tenant, DirectorId, "conn-1", ++_pushSequence, _fleet.ToList()));

        // The model read the parent's stop as carrying on while the worker ran.
        _gateway.TurnVerdicts.Store(_tenant, _parentId, new TurnVerdictDto
        {
            VerdictId = Guid.NewGuid().ToString("N"), JudgedAtUtc = now, TurnEndObservedAtUtc = now,
            ScreenHash = "a-screen", ContractVersion = TurnVerdictContract.Version, Model = "a-model",
            Verdict = "continues-alone", DecidedBy = CallACodeSteps.ModelStep,
        });
        Observe(_workerId, "Working");
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        FleetManagerEventSweep.Enabled = _priorSweep;
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _priorRoot);
        foreach (var dir in new[] { _instancesDir, _root })
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* best effort */ }
    }

    private void Observe(string sid, string state) => _gateway.TurnEndWatcherForTest!.Observe(_tenant, sid, state, DirectorId);

    private void SetState(string sid, string state)
    {
        var row = _fleet.Single(s => s.SessionId == sid);
        row.ActivityState = state;
        row.LastActivityAt = DateTime.UtcNow;
        Assert.True(_gateway.PushedSessions.ApplyDelta(_tenant, DirectorId, "conn-1", ++_pushSequence, row));
    }

    /// <summary>The parent's latest reading once it is no longer the model's, or the model's after the deadline.</summary>
    private async Task<TurnVerdictDto?> ParentReadingWhenReReadAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var latest = _gateway.TurnVerdicts.Latest(_tenant, _parentId);
            if (latest is not null && latest.DecidedBy != CallACodeSteps.ModelStep) return latest;
            await Task.Delay(100);
        }
        var last = _gateway.TurnVerdicts.Latest(_tenant, _parentId);
        _out.WriteLine($"parent reading after the deadline: decidedBy={last?.DecidedBy} verdict={last?.Verdict}");
        return last;
    }

    [Fact]
    public async Task TurnEnd_OfTheLastSessionUnderIt_ReadsTheParentAgain_AndItGoesRed()
    {
        SetState(_workerId, "WaitingForInput");
        Observe(_workerId, "WaitingForInput");

        var reading = await ParentReadingWhenReReadAsync();

        Assert.Equal(CallACodeSteps.AllUnderItStoppedStep, reading?.DecidedBy);
        Assert.Equal("needed-you", reading!.Verdict);
    }

    [Fact]
    public async Task Exit_OfTheLastSessionUnderIt_ReadsTheParentAgain_AndItGoesRed()
    {
        SetState(_workerId, "Exited");
        Observe(_workerId, "Exited");

        var reading = await ParentReadingWhenReReadAsync();

        Assert.Equal(CallACodeSteps.AllUnderItStoppedStep, reading?.DecidedBy);
    }

    [Fact]
    public async Task Removal_OfTheLastSessionUnderIt_ReadsTheParentAgain_AndItGoesRed()
    {
        Assert.True(_gateway.PushedSessions.ApplyRemove(_tenant, DirectorId, "conn-1", ++_pushSequence, _workerId));
        _gateway.TurnEndWatcherForTest!.ObserveRemoval(_tenant, _workerId, DirectorId);

        var reading = await ParentReadingWhenReReadAsync();

        Assert.Equal(CallACodeSteps.NothingUnderItStep, reading?.DecidedBy);
    }
}
