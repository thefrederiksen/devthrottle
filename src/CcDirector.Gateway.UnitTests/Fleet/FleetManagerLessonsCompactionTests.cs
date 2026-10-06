using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// Issue #3559, the compaction path: the marked Fleet Manager's session-start context carries the confirmed lessons, so
/// a compaction or a clear does not lose them.
///
/// The first test is the chain with every real link and nothing hand-set on the session: the real preference store,
/// the real lessons block, the real observer, the real <c>set-fleet-manager-lessons</c> verb on a real session in a real
/// session manager, and the real preamble file the session-start hook prints. The rest pin the observer's send rules.
/// </summary>
public sealed class FleetManagerLessonsCompactionTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-lessons-compact");
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private const string Correction =
        "Never queue a message to every session when none is stuck. Check first, and if nothing is stuck, do nothing.";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-lessons-compact-" + Guid.NewGuid().ToString("N"));

    public FleetManagerLessonsCompactionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _harness.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort temp cleanup */ }
    }

    private sealed class BufferBackend : Core.Backends.ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "Buffer-only";
        public bool IsRunning => true;
        public bool HasExited => false;
        public Core.Memory.CircularTerminalBuffer? Buffer { get; } = new(65536);
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Buffer?.Write(data);
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private static string AdditionalContext(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("hookSpecificOutput").GetProperty("additionalContext").GetString()!;
    }

    [Fact]
    public async Task TheMarkedFleetManagersSessionStartContext_CarriesTheConfirmedLessons_ThroughEveryRealLink()
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var fm = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferBackend());
        var preferences = new FleetPreferenceStore(_harness.Open());
        preferences.AddLesson(Tenant, Correction, "sent carry on now to all 36 sessions", "owner", confirmed: true, Now);
        preferences.AddLesson(Tenant, "UNCONFIRMED by a session", null, "fm", confirmed: false, Now);

        // The Director end of the tunnel: the real verb against the real session manager.
        var context = new SessionCommandContext(manager, "director-under-test", Services: null, SendSource.Framework);
        var observer = new FleetManagerLessonsObserver(
            markedSessionId: _ => fm.Id.ToString(),
            lessonsBlock: t => FleetManagerLessons.Build(preferences.ConfirmedLessons(t)),
            directorOf: (_, _) => "director-under-test",
            sendCommand: (_, command, _) => Task.FromResult<DirectorCommandResult?>(FleetManagerLessonsExecutor.SetLessons(context, command)));

        var before = AdditionalContext(SessionPreambleFile.Render(fm, "TEST-MACHINE", user: null, InjectedTextStore.AlwaysOurs(_dir)));
        Assert.DoesNotContain(Correction, before);

        await observer.Observe(Tenant, "director-under-test", fm.Id.ToString());

        // What the session-start hook prints on "compact": the lessons first, then the fleet preamble.
        var after = AdditionalContext(SessionPreambleFile.Render(fm, "TEST-MACHINE", user: null, InjectedTextStore.AlwaysOurs(_dir)));
        Assert.StartsWith(FleetManagerLessons.Heading, after);
        Assert.Contains("<<<" + Correction + ">>>", after);
        Assert.DoesNotContain("UNCONFIRMED", after);
        Assert.Contains("cc-devthrottle", after);

        // A lesson kept later reaches it too, as soon as the lessons change - before any compaction happens.
        preferences.AddLesson(Tenant, "a second lesson", null, "owner", confirmed: true, Now.AddMinutes(1));
        await observer.LessonsChanged(Tenant);
        var later = AdditionalContext(SessionPreambleFile.Render(fm, "TEST-MACHINE", user: null, InjectedTextStore.AlwaysOurs(_dir)));
        Assert.Contains("<<<a second lesson>>>", later);
    }

    [Theory]
    [InlineData("   ")]  // the user's own text, set to nothing: our prose is off, the owner's lessons are not
    [InlineData(null)]   // "yours is live" with no text: unreadable, and still not a reason to drop the lessons
    public void ASessionWhosePreambleIsEmptyOrUnreadable_StillGetsTheLessons(string? yours)
    {
        using var manager = new SessionManager(new Core.Configuration.AgentOptions());
        var fm = manager.CreateEmbeddedSession(Path.GetTempPath(), null, new BufferBackend());
        fm.SetFleetManagerLessons("[Fleet Manager lessons] 1 lesson\n<<<x>>>\n");
        var store = new InjectedTextStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json"));
        store.WriteCache(new InjectedTextCacheEntry(UseYours: true, Yours: yours, CachedAtUtc: DateTime.UtcNow));

        var text = AdditionalContext(SessionPreambleFile.Render(fm, "TEST-MACHINE", user: null, store));

        Assert.Equal("[Fleet Manager lessons] 1 lesson\n<<<x>>>\n", text);
    }

    // ---- the observer's send rules ------------------------------------------------------------------------

    private sealed class Wire
    {
        public string? Marked = "fm-1";
        public string? Block = "[Fleet Manager lessons] one";
        public string? Director = "dir-1";
        public DirectorCommandStatus? Answer = DirectorCommandStatus.Ok;
        public DateTime Clock = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
        public int MarkedReads;
        public Func<TenantId, string, string, bool>? IsSessionOf;
        public readonly List<(string DirectorId, string SessionId, string? Lessons)> Sent = new();

        public FleetManagerLessonsObserver Observer() => new(
            _ => { MarkedReads++; return Marked; }, _ => Block, (_, _) => Director,
            (directorId, command, _) =>
            {
                var request = JsonSerializer.Deserialize<SetFleetManagerLessonsRequest>(command.PayloadJson!,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
                Sent.Add((directorId, command.SessionId!, request.Lessons));
                return Task.FromResult(Answer is null ? null
                    : Answer == DirectorCommandStatus.Ok ? DirectorCommandResult.Success()
                    : DirectorCommandResult.Fail(Answer.Value, "unknown verb"));
            }, () => Clock, IsSessionOf);
    }

    [Fact]
    public async Task PushesReuseTheMarkedId_ForAFewSeconds_AndARefreshReadsTheMarkAndTheBlockAgain()
    {
        // Review of part 2, findings 3 and 4: not a database read on every push, and a backstop that heals a stale cache.
        var wire = new Wire();
        var observer = wire.Observer();
        for (var i = 0; i < 50; i++)
            await observer.Observe(Tenant, "dir-1", "worker-" + i);
        Assert.Equal(1, wire.MarkedReads);

        wire.Clock += FleetManagerLessonsObserver.MarkedIdReuse;
        await observer.Observe(Tenant, "dir-1", "fm-1");
        Assert.Equal(2, wire.MarkedReads);

        wire.Block = "[Fleet Manager lessons] changed behind the cache";
        await observer.Refresh(Tenant);
        Assert.Equal(3, wire.MarkedReads);
        Assert.Equal("[Fleet Manager lessons] changed behind the cache", wire.Sent[^1].Lessons);
    }

    [Fact]
    public async Task AFailedReadOfTheMark_NeverFailsThePush()
    {
        var observer = new FleetManagerLessonsObserver(
            _ => throw new InvalidOperationException("the database is gone"), _ => "block", (_, _) => "dir-1",
            (_, _, _) => Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success()));

        await observer.Observe(Tenant, "dir-1", "fm-1");
        await observer.DirectorReconnected(Tenant, "dir-1");
        await observer.LessonsChanged(Tenant);
    }

    [Fact]
    public async Task AFailedReadOfTheBlock_NeverFaultsTheTaskACallerDiscards()
    {
        // Third review of part 2: the block is read inside the asynchronous stamp; a throw there must be caught and
        // logged too, not left as a faulted task nobody observes.
        var observer = new FleetManagerLessonsObserver(
            _ => "fm-1", _ => throw new InvalidOperationException("the database is gone"), (_, _) => "dir-1",
            (_, _, _) => Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success()));

        await observer.Observe(Tenant, "dir-1", "fm-1");
        await observer.DirectorReconnected(Tenant, "dir-1");
        await observer.LessonsChanged(Tenant);
    }

    [Fact]
    public async Task OnlyTheFirstSnapshotOfANewConnection_StampsAgain_NeverTheTenSecondRePush()
    {
        // Second review of part 2: the re-push on a healthy connection must not wipe and resend the lessons every ten
        // seconds. Wired exactly as GatewayHost wires it, against the real store's new-connection signal.
        var wire = new Wire();
        var observer = wire.Observer();
        var store = new Streaming.PushedSessionStore();
        var reconnects = new List<Task>();
        store.SessionsArrivedOnNewConnection += (tenant, directorId) => reconnects.Add(observer.DirectorReconnected(tenant, directorId));
        var fm = new SessionDto { SessionId = "fm-1", DirectorId = "dir-1" };

        store.RegisterConnection(Tenant, "dir-1", "conn-1");
        for (var seq = 1; seq <= 4; seq++)
            Assert.True(store.ApplySnapshot(Tenant, "dir-1", "conn-1", seq, new[] { fm }));
        await Task.WhenAll(reconnects);
        Assert.Single(wire.Sent);

        store.UnregisterConnection(Tenant, "dir-1", "conn-1");
        store.RegisterConnection(Tenant, "dir-1", "conn-2");
        Assert.True(store.ApplySnapshot(Tenant, "dir-1", "conn-2", 1, new[] { fm }));
        Assert.True(store.ApplySnapshot(Tenant, "dir-1", "conn-2", 2, new[] { fm }));
        await Task.WhenAll(reconnects);
        Assert.Equal(2, wire.Sent.Count);
        Assert.Equal(("dir-1", "fm-1", (string?)"[Fleet Manager lessons] one"), wire.Sent[1]);
    }

    // ---- devthrottle_internal#2311: only the marked session's OWN Director, never one that merely lists its id ---------

    [Fact]
    public async Task Observe_APushFromADirectorTheRuleSaysIsNotTheMarkedSessionsOwn_IsNeverStamped_AndTheOwnersPushIs()
    {
        var wire = new Wire { IsSessionOf = (_, directorId, sessionId) => directorId == "dir-1" && sessionId == "fm-1" };
        var observer = wire.Observer();

        await observer.Observe(Tenant, "dir-colleague", "fm-1");
        Assert.Empty(wire.Sent);

        await observer.Observe(Tenant, "dir-1", "fm-1");
        Assert.Equal(new[] { ("dir-1", "fm-1", (string?)"[Fleet Manager lessons] one") }, wire.Sent);
    }

    [Fact]
    public async Task Observe_WithNoRuleWired_StampsThePushingDirector_AsBefore()
    {
        var wire = new Wire();
        await wire.Observer().Observe(Tenant, "dir-any", "fm-1");
        Assert.Equal(new[] { ("dir-any", "fm-1", (string?)"[Fleet Manager lessons] one") }, wire.Sent);
    }

    [Fact]
    public void OwnDirectorOf_TheRosterNamesAColleague_ReturnsTheDirectorTheRuleNames()
    {
        var own = FleetManagerLessonsObserver.OwnDirectorOf("dir-colleague", new[] { "dir-colleague", "dir-owner" },
            keyed: null, isOwn: d => d == "dir-owner");
        Assert.Equal("dir-owner", own);
    }

    [Fact]
    public void OwnDirectorOf_OnlyTheKeyRowNamesTheOwner_ReturnsTheKeyRowsDirector()
    {
        var own = FleetManagerLessonsObserver.OwnDirectorOf("dir-colleague", new[] { "dir-colleague" },
            keyed: "dir-owner", isOwn: d => d == "dir-owner");
        Assert.Equal("dir-owner", own);
    }

    [Fact]
    public void OwnDirectorOf_NoCandidateIsTheSessionsOwn_ReturnsNull()
    {
        var own = FleetManagerLessonsObserver.OwnDirectorOf("dir-colleague", new[] { "dir-colleague", "dir-other" },
            keyed: null, isOwn: _ => false);
        Assert.Null(own);
    }

    [Fact]
    public void OwnDirectorOf_TheRostersOwnAnswerIsTheSessionsOwn_ReturnsIt_First()
    {
        var own = FleetManagerLessonsObserver.OwnDirectorOf("dir-owner", new[] { "dir-other", "dir-owner" },
            keyed: "dir-other", isOwn: _ => true);
        Assert.Equal("dir-owner", own);
    }

    [Fact]
    public async Task OnlyTheMarkedSession_IsStamped_AndAnUnchangedBlockIsNotSentAgain()
    {
        var wire = new Wire();
        var observer = wire.Observer();

        await observer.Observe(Tenant, "dir-1", "worker-1");
        await observer.Observe(Tenant, "dir-1", "fm-1");
        await observer.Observe(Tenant, "dir-1", "fm-1");

        Assert.Equal(new[] { ("dir-1", "fm-1", (string?)"[Fleet Manager lessons] one") }, wire.Sent);
    }

    [Fact]
    public async Task AChangeOfLessons_AndAReconnect_StampAgain()
    {
        var wire = new Wire();
        var observer = wire.Observer();
        await observer.Observe(Tenant, "dir-1", "fm-1");

        wire.Block = "[Fleet Manager lessons] two";
        await observer.Observe(Tenant, "dir-1", "fm-1"); // cached until a lesson changes
        Assert.Single(wire.Sent);
        await observer.LessonsChanged(Tenant);
        Assert.Equal("[Fleet Manager lessons] two", wire.Sent[^1].Lessons);

        // A new connection: the Director dropped what it held when it opened.
        await observer.DirectorReconnected(Tenant, "dir-1");
        Assert.Equal(3, wire.Sent.Count);
        Assert.Equal("[Fleet Manager lessons] two", wire.Sent[^1].Lessons);
    }

    [Fact]
    public async Task WhenTheMarkMoves_TheFormerFleetManagerIsCleared_AndTheNewOneStamped()
    {
        var wire = new Wire();
        var observer = wire.Observer();
        await observer.Observe(Tenant, "dir-1", "fm-1");

        wire.Marked = "fm-2";
        wire.Director = "dir-2";
        await observer.Refresh(Tenant);

        Assert.Equal(("dir-1", "fm-1", (string?)null), wire.Sent[1]);
        Assert.Equal(("dir-2", "fm-2", (string?)"[Fleet Manager lessons] one"), wire.Sent[2]);
    }

    [Fact]
    public async Task ADirectorWithNoStream_IsAskedAgain_AndOneThatRefuses_IsNotAskedOnEveryPush()
    {
        var wire = new Wire { Answer = null };
        var observer = wire.Observer();
        await observer.Observe(Tenant, "dir-1", "fm-1");
        await observer.Observe(Tenant, "dir-1", "fm-1");
        Assert.Equal(2, wire.Sent.Count);

        wire.Answer = DirectorCommandStatus.BadRequest; // a Director older than the verb
        await observer.Observe(Tenant, "dir-1", "fm-1");
        await observer.Observe(Tenant, "dir-1", "fm-1");
        Assert.Equal(3, wire.Sent.Count);
    }

    [Fact]
    public async Task AnAccountWithNoLessons_SendsNothing()
    {
        var wire = new Wire { Block = null };
        await wire.Observer().Observe(Tenant, "dir-1", "fm-1");
        Assert.Empty(wire.Sent);
    }
}
