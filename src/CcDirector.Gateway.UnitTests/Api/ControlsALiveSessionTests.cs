using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// WHETHER A RUN'S SESSION IS WAITING ON ITS OWN WORKERS (the Factory Failures mission, 6 November 2026), read from what
/// the account's Directors pushed: a session that controls a session that has not ended is waiting on that worker, not
/// on a person, so the waited-without-reporting rule does not count it.
/// </summary>
public sealed class ControlsALiveSessionTests : IDisposable
{
    private static readonly TenantId Account = new("acct-controls-live");
    private static readonly TenantId Other = new("acct-controls-other");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-controls-live-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private readonly PushedSessionStore _pushed = new();

    public ControlsALiveSessionTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
        var now = DateTime.UtcNow;
        _registry.RegisterFromStream("director-a", "MACHINE_A", "someone", "1.0", pid: 11, startedAt: now, tenant: Account);
        _registry.RegisterFromStream("director-b", "MACHINE_B", "someone", "1.0", pid: 12, startedAt: now, tenant: Other);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private void Push(TenantId tenant, string director, params SessionDto[] sessions)
    {
        _pushed.RegisterConnection(tenant, director, "conn-" + director);
        Assert.True(_pushed.ApplySnapshot(tenant, director, "conn-" + director, 1, sessions.ToList()));
    }

    private static SessionDto Row(string id, string? controller, string status = "Running") => new()
    {
        // An ended session is one whose activity is Exited (FleetManagerSessions.IsGone), as its Director pushes it.
        SessionId = id, ControllerSessionId = controller, Status = status, ActivityState = status == "Exited" ? "Exited" : "Working",
    };

    [Fact]
    public void ControlsALiveSession_AWorkerStillRunning_IsTrue()
    {
        Push(Account, "director-a", Row("boss-1", null), Row("worker-1", "boss-1"));

        Assert.True(GatewayEndpoints.ControlsALiveSession(_registry, _pushed, Account, "boss-1"));
    }

    [Fact]
    public void ControlsALiveSession_OnlyAWorkerThatEnded_IsFalse()
    {
        Push(Account, "director-a", Row("boss-1", null), Row("worker-1", "boss-1", status: "Exited"));

        Assert.False(GatewayEndpoints.ControlsALiveSession(_registry, _pushed, Account, "boss-1"));
    }

    [Fact]
    public void ControlsALiveSession_NoWorker_IsFalse()
    {
        Push(Account, "director-a", Row("seat-1", null), Row("someone-else", "another-boss"));

        Assert.False(GatewayEndpoints.ControlsALiveSession(_registry, _pushed, Account, "seat-1"));
    }

    [Fact]
    public void ControlsALiveSession_AWorkerInAnotherAccount_DoesNotCount()
    {
        Push(Account, "director-a", Row("boss-1", null));
        Push(Other, "director-b", Row("worker-1", "boss-1"));

        Assert.False(GatewayEndpoints.ControlsALiveSession(_registry, _pushed, Account, "boss-1"));
    }
}
