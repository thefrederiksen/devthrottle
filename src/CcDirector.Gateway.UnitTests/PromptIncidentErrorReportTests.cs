using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Input;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.UnitTests.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// ONE FAILED PROMPT IS ONE INCIDENT, AND IT NEVER CARRIES THE PROMPT'S WORDS (the Error Logging mission, issue #3675,
/// step 4b and 4e). On 9 October 2026 one phone prompt refused six times reached the error store as eighteen unlinked
/// rows. Every test here drives a failing send through the real path - the Gateway command handler, the prompt verb, the
/// session, its terminal - with the real error reporter listening to the real log, and reads the batch that would have
/// left the machine.
///
/// The reporter here hears only lines that belong to the test's own session: lines logged inside a context naming it,
/// and any line that mentions its id. A line that mentions the session but is not linked to the incident is therefore
/// caught, not filtered out.
/// </summary>
public sealed class PromptIncidentErrorReportTests : IDisposable
{
    private const string Marker = "ZQX7MARK";

    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    /// <summary>A desktop-less session's backend whose every send fails: the words are refused.</summary>
    private sealed class RefusingBackend : ISessionBackend
    {
        public int ProcessId => 0;
        public string Status => "refusing";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new(1 << 16);
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Buffer!.Write(data);
        public Task SendTextAsync(string text) => throw new InvalidOperationException("the terminal refused the text");
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>A backend whose every send fails only after <see cref="Delay"/>: a refusal that outlives the verb's answer.</summary>
    private sealed class LateRefusingBackend : ISessionBackend
    {
        public TimeSpan Delay = TimeSpan.FromMilliseconds(1500);
        public int ProcessId => 0;
        public string Status => "refusing late";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer { get; } = new(1 << 16);
#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067
        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) => Buffer!.Write(data);
        public async Task SendTextAsync(string text)
        {
            await Task.Delay(Delay);
            throw new InvalidOperationException("the terminal refused the text after a while");
        }
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-incident-" + Guid.NewGuid().ToString("N"));
    private readonly DeliveryRecord _record;
    private readonly StubHandler _handler = new();
    private readonly ErrorReporter _reporter;
    private readonly Action<string>? _previousObserver;
    private readonly SessionManager _manager = new(new AgentOptions());
    private readonly List<IDisposable> _cleanup = new();
    private string _watchedSessionId = "";

    public PromptIncidentErrorReportTests()
    {
        _record = new DeliveryRecord(Path.Combine(_dir, "records"));
        var config = new GatewayConfig { Url = "https://gateway.example", Token = "device-key" };
        _reporter = new ErrorReporter(ErrorReportLimits.Director, () => config, new HttpClient(_handler),
            machineName: "TEST-MACHINE", productVersion: "2.18.0+test");
        _previousObserver = FileLog.ErrorObserver;
        FileLog.ErrorObserver = line =>
        {
            if (_watchedSessionId.Length == 0) return;
            if (ErrorContext.Current?.SessionId == _watchedSessionId || line.Contains(_watchedSessionId, StringComparison.Ordinal))
                _reporter.OnLogLine(line);
        };
    }

    public void Dispose()
    {
        FileLog.ErrorObserver = _previousObserver;
        foreach (var d in _cleanup) d.Dispose();
        _manager.Dispose();
        _reporter.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private Session RefusingSession()
    {
        var session = _manager.CreateEmbeddedSession(Path.GetTempPath(), null, new RefusingBackend());
        _watchedSessionId = session.Id.ToString();
        return session;
    }

    private static PromptRequest Prompt(string text, string deliveryId) =>
        new() { Text = text, AppendEnter = true, DeliveryId = deliveryId, Surface = "phone" };

    /// <summary>The prompt command through the Gateway command handler, as the Gateway's call reaches it.</summary>
    private Task<DirectorCommandResult> PromptCommand(Session session, string commandId, PromptRequest request, TimeSpan? answerBudget = null) =>
        GatewayStreamClient.HandleCommandAsync(
            new DirectorCommand { CommandId = commandId, Verb = PromptPathErrorContext.PromptVerb, SessionId = session.Id.ToString() },
            upStreamHandler: null,
            commandDispatcher: _ => SessionCommandExecutor.SendPromptAsync(session, request, SendSource.UserInput, _record, answerBudget));

    /// <summary>Runs <paramref name="act"/> and waits for the verb's late outcome for <paramref name="deliveryId"/>, as written
    /// by the one place it is written.</summary>
    private static async Task<string> LateOutcomeFor(string deliveryId, Func<Task> act)
    {
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Observe(string id, string state) { if (id == deliveryId) seen.TrySetResult(state); }
        SessionCommandExecutor.LateOutcomeObserver += Observe;
        try
        {
            await act();
            var done = await Task.WhenAny(seen.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            Assert.True(done == seen.Task, "no late outcome was written within 60 seconds");
            return await seen.Task;
        }
        finally
        {
            SessionCommandExecutor.LateOutcomeObserver -= Observe;
        }
    }

    /// <summary>Every report the reporter would send, read off the fake Gateway.</summary>
    private async Task<List<ErrorReportItem>> SentAsync()
    {
        while (await _reporter.SendPendingAsync(CancellationToken.None) > 0) { }
        return _handler.Bodies.SelectMany(b => JsonSerializer.Deserialize<ErrorReportBatch>(b)!.Reports!).ToList();
    }

    [Fact]
    public async Task OneFailedPrompt_EveryRowCarriesTheCommandId_AndOnlyTheFailedDeliveryIsUserVisible()
    {
        var session = RefusingSession();
        const string commandId = "bb79ac24ae064eaeb7498d771cfea774";

        var result = await PromptCommand(session, commandId, Prompt("release it to dev and test it", "upload-1"));

        Assert.False(result.Ok);
        var rows = await SentAsync();
        Console.WriteLine($"rows reported for the one failed prompt: {rows.Count} ({string.Join(", ", rows.Select(r => r.Source))})");
        Assert.Contains(rows, r => r.Source == "PromptDeliveryFailures");
        Assert.Contains(rows, r => r.Source == "GatewayStreamClient");
        Assert.All(rows, r =>
        {
            Assert.Equal(commandId, r.CorrelationId);
            Assert.Equal(session.Id.ToString(), r.SessionId);
        });
        var shown = Assert.Single(rows, r => r.UserVisible == true);
        Assert.Equal("PromptDeliveryFailures", shown.Source);
        Assert.StartsWith("FAILED DELIVERY", shown.Message);
        Assert.Equal("phone", shown.Surface);
        Assert.Equal(PromptDeliveryFailures.SendPromptAction, shown.Action);
        // The path's own scopes carry only the ids: no other row names a screen, an action or a visibility.
        Assert.All(rows.Where(r => !ReferenceEquals(r, shown)), r =>
        {
            Assert.Null(r.UserVisible);
            Assert.Null(r.Surface);
            Assert.Null(r.Action);
        });
    }

    [Fact]
    public async Task SixRefusals_WithSixCommandIds_AreSixFailedDeliveryRows_NotOneCountedRow()
    {
        var session = RefusingSession();
        var ids = Enumerable.Range(1, 6).Select(i => $"cmd-{i}-{Guid.NewGuid():N}").ToList();

        foreach (var id in ids)
            await PromptCommand(session, id, Prompt("go on", "upload-six"));

        var failed = (await SentAsync()).Where(r => r.Source == "PromptDeliveryFailures").ToList();
        Assert.Equal(6, failed.Count);
        Assert.Equal(ids.OrderBy(i => i), failed.Select(r => r.CorrelationId).OrderBy(i => i));
        Assert.All(failed, r => Assert.Equal(1, r.RepeatCount));
    }

    [Fact]
    public async Task AFailedSendWithNoCommand_UsesItsDeliveryIdAsTheCorrelationId()
    {
        var session = RefusingSession();

        await Assert.ThrowsAnyAsync<Exception>(() =>
            SessionCommandExecutor.SendPromptAsync(session, Prompt("release it", "upload-no-command"), SendSource.UserInput, _record));

        var rows = await SentAsync();
        Assert.Contains(rows, r => r.Source == "PromptDeliveryFailures");
        Assert.All(rows, r => Assert.Equal("upload-no-command", r.CorrelationId));
    }

    // ---- 4e: how the previous send ended ----

    [Theory]
    [InlineData("none", "previous_send=none")]
    [InlineData("delivered", "previous_send=delivered, previous_correlation_id=cmd-previous")]
    [InlineData("refused", "previous_send=refused, previous_correlation_id=cmd-previous")]
    [InlineData("in-flight", "previous_send=in-flight, previous_correlation_id=cmd-previous")]
    public async Task ARefusal_SaysHowThePreviousSendToTheSessionEnded(string previous, string expected)
    {
        var session = RefusingSession();
        using (ErrorContext.Begin(correlationId: "cmd-previous"))
        {
            switch (previous)
            {
                case "delivered": _record.TryBeginDelivery(session.Id, "upload-previous"); _record.MarkDelivered(session.Id, "upload-previous"); break;
                case "refused": _record.MarkNotDelivered(session.Id, "upload-previous", "the composer still holds text after it was cleared"); break;
                case "in-flight": _record.TryBeginDelivery(session.Id, "upload-previous"); break;
            }
        }

        await PromptCommand(session, "cmd-this-one", Prompt("release it to dev", "upload-this-one"));

        var shown = Assert.Single(await SentAsync(), r => r.Source == "PromptDeliveryFailures");
        Assert.True(shown.Message!.Contains(expected, StringComparison.Ordinal), shown.Message);
        if (previous == "none") Assert.DoesNotContain("previous_correlation_id", shown.Message);
    }

    [Fact]
    public async Task TheSecondOfTwoRefusals_NamesTheFirstAsItsPreviousSend()
    {
        var session = RefusingSession();

        await PromptCommand(session, "cmd-first", Prompt("release it", "upload-first"));
        await PromptCommand(session, "cmd-second", Prompt("release it", "upload-second"));

        var second = Assert.Single(await SentAsync(), r => r.Source == "PromptDeliveryFailures" && r.CorrelationId == "cmd-second");
        Assert.Contains("previous_send=refused, previous_correlation_id=cmd-first", second.Message);
    }

    [Fact]
    public async Task ARefusal_AfterASendThatTimedOut_SaysTimedOutAndNamesIt_FromTheLineTheLateOutcomeWrote()
    {
        // A working Codex whose composer is hidden under a menu after the Enter: the verb answers "delivering", and the
        // late watch ends at its limit with nothing proven - the record's "unconfirmed" line, written by WriteLateOutcome
        // after the verb's own scopes were disposed.
        var dir = Path.Combine(_dir, "codex");
        Directory.CreateDirectory(dir);
        var transcript = Path.Combine(dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.Codex, transcript, dir) { Working = true, SwallowEnter = true, MenuAfterEnter = true };
        var working = new Session(Guid.NewGuid(), dir, dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.Codex };
        _cleanup.Add(terminal);
        _cleanup.Add(working);
        terminal.StartDrawing();
        working.ApplyTerminalActivityState(ActivityState.Working);
        working.ComposerReleaseWindowForTests = TimeSpan.FromSeconds(1);
        working.LateArrivalLimitForTests = TimeSpan.FromSeconds(2);
        _watchedSessionId = working.Id.ToString();

        var late = await LateOutcomeFor("upload-previous", () => PromptCommand(working, "cmd-previous", Prompt("check the build", "upload-previous")));

        Assert.Equal(DeliveryStates.Unconfirmed, late);
        var line = _record.LatestOtherThan(working.Id, exceptDeliveryId: null);
        Assert.Equal("upload-previous", line!.Id);
        Assert.Equal("cmd-previous", line.CorrelationId);

        // The same session, now on a terminal that refuses: the refusal names the send that timed out.
        var refusing = new Session(working.Id, Path.GetTempPath(), Path.GetTempPath(), null, new RefusingBackend(), SessionBackendType.Embedded);
        refusing.MarkRunning();
        _cleanup.Add(refusing);
        await PromptCommand(refusing, "cmd-this-one", Prompt("release it to dev", "upload-this-one"));

        var shown = Assert.Single(await SentAsync(), r => r.Source == "PromptDeliveryFailures" && r.CorrelationId == "cmd-this-one");
        Assert.True(shown.Message!.Contains("previous_send=timed-out, previous_correlation_id=cmd-previous", StringComparison.Ordinal), shown.Message);
    }

    // ---- After the verb has answered ----

    [Fact]
    public async Task ASendRefusedAfterTheVerbAnswered_EveryLateRowAndTheRecordsFinalLineCarryTheCommandId()
    {
        // The verb answers "delivering" at a 200 millisecond budget; the terminal refuses 1.5 seconds later, after the
        // command's and the verb's scopes were disposed. The late outcome is written by its own task.
        var session = _manager.CreateEmbeddedSession(Path.GetTempPath(), null, new LateRefusingBackend());
        _watchedSessionId = session.Id.ToString();

        DirectorCommandResult? answer = null;
        var late = await LateOutcomeFor("upload-late", async () =>
            answer = await PromptCommand(session, "cmd-late", Prompt("release it", "upload-late"), answerBudget: TimeSpan.FromMilliseconds(200)));

        Assert.True(answer!.Ok, answer.Error);
        Assert.Equal(DeliveryStates.NotDelivered, late);
        var line = _record.LatestOtherThan(session.Id, exceptDeliveryId: null);
        Assert.Equal("upload-late", line!.Id);
        Assert.Equal(DeliveryStates.NotDelivered, line.State);
        Assert.Equal("cmd-late", line.CorrelationId);
        var lateRows = await SentAsync();
        Console.WriteLine($"rows reported after the verb answered: {lateRows.Count} ({string.Join(", ", lateRows.Select(r => r.Source))})");
        Assert.Contains(lateRows, r => r.Source == "PromptDeliveryFailures");
        Assert.All(lateRows, r =>
        {
            Assert.Equal("cmd-late", r.CorrelationId);
            Assert.Equal(session.Id.ToString(), r.SessionId);
        });

        // The next refusal names it: the link 4e exists for survives a send that outlived its answer.
        await PromptCommand(session, "cmd-second", Prompt("release it", "upload-second"));
        var second = Assert.Single(await SentAsync(), r => r.Source == "PromptDeliveryFailures" && r.CorrelationId == "cmd-second");
        Assert.Contains("previous_send=refused, previous_correlation_id=cmd-late", second.Message);
    }

    [Fact]
    public async Task ACreatedSessionsFirstPrompt_RunAfterTheCommandsScopeIsGone_StillCarriesTheCommandId()
    {
        // The create verb answers before its first prompt is typed, and the handler disposes the command's scope; here the
        // first prompt's task is held until after that, the order a busy machine produces.
        var session = RefusingSession();
        session.AgentKind = AgentKind.Gemini;
        var release = new TaskCompletionSource();
        Task firstPrompt;
        using (ErrorContext.Begin(correlationId: "cmd-create"))
        {
            firstPrompt = SessionCommandExecutor.StartPrePrompt(session, "set up the repository", TimeSpan.FromMilliseconds(100), release.Task);
        }
        release.SetResult();
        await firstPrompt;

        var rows = await SentAsync();
        Console.WriteLine($"rows reported for the failed first prompt: {rows.Count} ({string.Join(", ", rows.Select(r => r.Source))})");
        Assert.Contains(rows, r => r.Source == "PromptDeliveryFailures");
        Assert.All(rows, r =>
        {
            Assert.Equal("cmd-create", r.CorrelationId);
            Assert.Equal(session.Id.ToString(), r.SessionId);
        });
    }

    // ---- Never the prompt's words ----

    [Fact]
    public async Task AFailingSend_NeverPutsThePromptsWordsInAnyReport()
    {
        // A terminal that never echoes: the send fails as "the composer never echoed the typed text", and that refusal
        // carries what the Director looked for and what the terminal showed - which is the prompt itself.
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        _cleanup.Add(session);
        session.AgentKind = AgentKind.Gemini;
        terminal.Echo = false;
        _watchedSessionId = session.Id.ToString();
        var text = $"ship the release {Marker} FAILED: then report back on {Marker}";

        // The echo miss takes longer than the verb's usual 20-second answer; this verb waits for the failure itself.
        var result = await PromptCommand(session, "cmd-marker", Prompt(text, "upload-marker"), answerBudget: TimeSpan.FromMinutes(3));

        // The line itself carried the words - the route this test closes is live...
        Assert.False(result.Ok);
        Assert.Contains(Marker, result.Error);
        // ...and the reports are there, and none of them carries them, in any field.
        var rows = await SentAsync();
        Console.WriteLine($"rows reported for the failing send: {rows.Count}");
        Assert.Contains(rows, r => r.Source == "PromptDeliveryFailures");
        Assert.Contains(rows, r => r.Source == "GatewayStreamClient");
        Assert.NotEmpty(_handler.Bodies);
        Assert.All(_handler.Bodies, body => Assert.DoesNotContain(Marker, body));
        Assert.Contains(rows, r => (r.Message + r.Stack).Contains("<withheld:", StringComparison.Ordinal));
        // ...and every row of it, the wait line among them, is one incident.
        Assert.All(rows, r => Assert.Equal("cmd-marker", r.CorrelationId));
    }

    [Theory]
    [InlineData("long", "fix the ERROR " + Marker + " in the build and then run the whole test suite again before you report")]
    [InlineData("short", "fix the ERROR " + Marker + " in the build now")]
    public async Task ASendStillDelivering_TheLateWatchsWaitLinesNeverCarryThePromptsWords_AndCarryTheCommandId(string shape, string text)
    {
        // A working Codex whose composer is hidden under a menu after the Enter: the send answers "still delivering" and
        // the session's own late records watch runs on after every scope of the send is disposed, writing WAITING after 5
        // seconds and WAIT ENDED at its limit - each quoting the prompt's first 60 characters. With an upper-case ERROR in
        // them, both are error lines. "long" quotes a cut label, "short" the whole prompt.
        var dir = Path.Combine(_dir, "codex-" + shape);
        Directory.CreateDirectory(dir);
        var transcript = Path.Combine(dir, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(transcript, "");
        var terminal = new ScriptedAgentTerminal(AgentKind.Codex, transcript, dir) { Working = true, SwallowEnter = true, MenuAfterEnter = true };
        var session = new Session(Guid.NewGuid(), dir, dir, null, terminal, SessionBackendType.ConPty) { AgentKind = AgentKind.Codex };
        _cleanup.Add(terminal);
        _cleanup.Add(session);
        terminal.StartDrawing();
        session.ApplyTerminalActivityState(ActivityState.Working);
        session.ComposerReleaseWindowForTests = TimeSpan.FromSeconds(1);
        session.LateArrivalLimitForTests = TimeSpan.FromSeconds(7);
        _watchedSessionId = session.Id.ToString();

        var late = await LateOutcomeFor("upload-watch-" + shape, () => PromptCommand(session, "cmd-watch", Prompt(text, "upload-watch-" + shape)));

        Assert.Equal(DeliveryStates.Unconfirmed, late);
        var rows = await SentAsync();
        Console.WriteLine($"rows reported for the still-delivering send ({shape}): {rows.Count} ({string.Join(" | ", rows.Select(r => r.Message))})");
        // The wait lines are error lines and are reported - the route this test closes is live...
        Assert.Contains(rows, r => r.Message!.StartsWith("WAIT ENDED", StringComparison.Ordinal));
        // ...none carries the prompt's words, in any field, and every row is this send's.
        Assert.All(_handler.Bodies, body => Assert.DoesNotContain(Marker, body));
        Assert.All(rows, r =>
        {
            Assert.Equal("cmd-watch", r.CorrelationId);
            Assert.Equal(session.Id.ToString(), r.SessionId);
        });
    }
}
