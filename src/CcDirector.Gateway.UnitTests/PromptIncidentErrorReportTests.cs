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
        Assert.All(rows.Where(r => !ReferenceEquals(r, shown)), r => Assert.False(r.UserVisible));
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
    [InlineData("timed-out", "previous_send=timed-out, previous_correlation_id=cmd-previous")]
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
                case "timed-out": _record.TryBeginDelivery(session.Id, "upload-previous"); _record.MarkUnconfirmed(session.Id, "upload-previous", DeliveryRecord.NeverInAgentRecordsReason(TimeSpan.FromMinutes(15))); break;
                case "refused": _record.MarkNotDelivered(session.Id, "upload-previous", "the composer still holds text after it was cleared"); break;
                case "in-flight": _record.TryBeginDelivery(session.Id, "upload-previous"); break;
            }
        }

        await PromptCommand(session, "cmd-this-one", Prompt("release it to dev", "upload-this-one"));

        var shown = Assert.Single(await SentAsync(), r => r.Source == "PromptDeliveryFailures");
        Assert.Contains(expected, shown.Message);
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
    }
}
