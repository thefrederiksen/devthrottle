using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

public sealed class SessionStateEventDropTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "state-event-drop-" + Guid.NewGuid().ToString("N"));
    private readonly SessionManager _manager;
    private readonly Session _session;

    public SessionStateEventDropTests()
    {
        Directory.CreateDirectory(_directory);
        _manager = new SessionManager(new AgentOptions());
        _session = new Session(Guid.NewGuid(), _directory, _directory, null, new StubSessionBackend(), null,
            ActivityState.WaitingForInput, Start, "state-drop-test", null)
        {
            AgentKind = AgentKind.ClaudeCode,
            Clock = new FakeTimeProvider(Start),
        };
        _manager.AdoptSession(_session);
    }

    public void Dispose()
    {
        _manager.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string Drop(SessionStateEvent evt)
    {
        var path = SessionHookFiles.StateEventPathFor(_session.Id, _session.PointerDropToken, evt.Sequence, _directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("State event path has no directory."));
        File.WriteAllText(path, JsonSerializer.Serialize(evt));
        return path;
    }

    private SessionStateEvent Event(long sequence, string eventName = "turn_started", Guid? sessionId = null,
        string agent = "claude", DateTimeOffset? occurredAtUtc = null) =>
        new(1, Guid.NewGuid(), sessionId ?? _session.Id, sequence, occurredAtUtc ?? Start,
            eventName, "agent", agent, null, "2.1.302");

    [Fact]
    public void Apply_ValidEvent_ChangesStateAndRemovesDrop()
    {
        var path = Drop(Event(1));

        Assert.True(new SessionPointerWatcher(_manager, _directory).Apply(path));
        Assert.Equal(ActivityState.Working, _session.ActivityState);
        Assert.Equal(SessionStateSources.Agent, _session.ActivityStateSource);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Apply_WrongSessionInBody_RejectsAndLeavesStateUnchanged()
    {
        var path = Drop(Event(1, sessionId: Guid.NewGuid()));

        Assert.False(new SessionPointerWatcher(_manager, _directory).Apply(path));
        Assert.Equal(ActivityState.WaitingForInput, _session.ActivityState);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Apply_WrongAgent_RejectsAndLeavesStateUnchanged()
    {
        var path = Drop(Event(1, agent: "pi"));

        Assert.False(new SessionPointerWatcher(_manager, _directory).Apply(path));
        Assert.Equal(ActivityState.WaitingForInput, _session.ActivityState);
    }

    [Fact]
    public void Apply_StaleEvent_RejectsAndLeavesStateUnchanged()
    {
        var path = Drop(Event(1, occurredAtUtc: Start.AddSeconds(-31)));

        Assert.False(new SessionPointerWatcher(_manager, _directory).Apply(path));
        Assert.Equal(ActivityState.WaitingForInput, _session.ActivityState);
    }

    [Fact]
    public void Apply_MalformedJson_RejectsAndRemovesDrop()
    {
        var path = SessionHookFiles.StateEventPathFor(_session.Id, _session.PointerDropToken, 1, _directory);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("State event path has no directory."));
        File.WriteAllText(path, "not json {");

        Assert.False(new SessionPointerWatcher(_manager, _directory).Apply(path));
        Assert.Equal(ActivityState.WaitingForInput, _session.ActivityState);
        Assert.False(File.Exists(path));
    }
}
