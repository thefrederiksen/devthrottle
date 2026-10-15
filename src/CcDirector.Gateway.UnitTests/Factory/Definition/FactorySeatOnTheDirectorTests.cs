using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Definition;

/// <summary>
/// THE DIRECTOR STAMPS WHAT IT IS SENT AND PUSHES IT BACK (Factory Definition mission, phase 5). Two links: the create's
/// own pre-launch stamp (<see cref="SessionCommandExecutor.StampSeatStartFacts"/>, called from the create) and the push
/// mapping (<see cref="ControlEndpoints.Map"/>). Drop either and the Gateway's history row never learns the seat, so a
/// seat's child would inherit nothing - while every store test, which builds its own record, stays green.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactorySeatOnTheDirectorTests
{
    private sealed class NullBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Test";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static Session ANewSession() => new SessionManager(new AgentOptions()).RestoreEmbeddedSession(new PersistedSession
    {
        Id = Guid.NewGuid(),
        RepoPath = Path.GetTempPath(),
        WorkingDirectory = Path.GetTempPath(),
        CreatedAt = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero),
    }, new NullBackend());

    [Fact]
    public void StampSeatStartFacts_TheCreatesFieldsAreStampedAndPushed()
    {
        var session = ANewSession();

        SessionCommandExecutor.StampSeatStartFacts(session, new NewSessionRequest
        {
            FactorySeat = "outreach",
            FactoryDefinitionVersion = 4,
            FactoryKit = FactoryKits.RulesOnly,
            FactoryStartOverride = "agent Codex, chosen by the person",
            Model = "claude-sonnet-5-5",
            // Instructions to the create, not birth facts: the Director does nothing with these yet.
            FactoryWorktree = "devthrottle",
            FactoryHome = true,
        });
        var pushed = ControlEndpoints.Map(session, "dir-1");

        Assert.Equal("outreach", pushed.FactorySeat);
        Assert.Equal(4, pushed.FactoryDefinitionVersion);
        Assert.Equal(FactoryKits.RulesOnly, pushed.FactoryKit);
        Assert.Equal("agent Codex, chosen by the person", pushed.FactoryStartOverride);
        Assert.Equal("claude-sonnet-5-5", pushed.LaunchModel);
    }

    [Fact]
    public void StampSeatStartFacts_UNDEFINED_FACTORY_ACreateWithNoSeat_PushesNone()
    {
        var session = ANewSession();

        SessionCommandExecutor.StampSeatStartFacts(session, new NewSessionRequest { Factory = "website-factory" });
        var pushed = ControlEndpoints.Map(session, "dir-1");

        Assert.Null(pushed.FactorySeat);
        Assert.Null(pushed.FactoryDefinitionVersion);
        Assert.Null(pushed.FactoryKit);
        Assert.Null(pushed.FactoryStartOverride);
        Assert.Null(pushed.LaunchModel);
    }
}
