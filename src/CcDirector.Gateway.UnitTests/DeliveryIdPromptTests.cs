using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Director refuses a second copy of a delivery (Voice Delivery mission, phase 1). On 25 September 2026 a spoken
/// prompt reached the agent twice: the Director typed it after 122 seconds, the Gateway had given up at 30 and the owner
/// pressed "Send anyway", and nothing refused the second copy. Driven through the real prompt core against a real
/// session, and asserted on what the terminal RECEIVED, not only on the answer.
/// </summary>
public sealed class DeliveryIdPromptTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-delivery-prompt-" + Guid.NewGuid().ToString("N"));
    private readonly DeliveryRecord _record;

    public DeliveryIdPromptTests() => _record = new DeliveryRecord(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static PromptRequest Delivery(string deliveryId, string text = "run the build and tell me what broke") => new()
    {
        Text = text,
        AppendEnter = true,
        Surface = "cockpit",
        DeliveryId = deliveryId,
    };

    /// <summary>A terminal session over a scripted terminal, for an agent the Director keeps no conversation record
    /// for: the send completes on the terminal alone, with no sixty-second wait for a conversation file this test
    /// has no agent to write.</summary>
    private static (Session Session, ScriptedTerminal Terminal) NewTerminalSession()
    {
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        session.AgentKind = Core.Agents.AgentKind.Gemini;
        return (session, terminal);
    }

    private static PromptResponse Body(DirectorCommandResult result)
    {
        Assert.True(result.Ok, result.Error);
        return JsonSerializer.Deserialize<PromptResponse>(result.BodyJson!, Json)!;
    }

    [Fact]
    public async Task SendPromptAsync_DeliveryIdAlreadyDelivered_IsRefusedAndTypesNothing()
    {
        // Proves a second copy of a delivered recording types NOTHING into the terminal - no text, no Enter - and is
        // answered as a refusal that says the words are already in.
        var (session, terminal) = NewTerminalSession();

        var first = Body(await SessionCommandExecutor.SendPromptAsync(session, Delivery("upload-1"), SendSource.Delivery, _record));
        Assert.True(first.Accepted);
        Assert.Equal(DeliveryState.Delivered, first.DeliveryState);
        var writesAfterFirst = terminal.Writes.Count;
        Assert.Single(terminal.Submitted);

        var second = Body(await SessionCommandExecutor.SendPromptAsync(session, Delivery("upload-1"), SendSource.Delivery, _record));

        Assert.False(second.Accepted);
        Assert.Equal(DeliveryState.Delivered, second.DeliveryState);
        Assert.Contains("nothing was typed", second.DeliveryStateReason);
        Assert.Equal(writesAfterFirst, terminal.Writes.Count);
        Assert.Single(terminal.Submitted);
    }

    [Fact]
    public async Task SendPromptAsync_SameDeliveryIdWhileTheFirstIsStillTyping_OnlyOneTypes()
    {
        // Proves two concurrent copies of one delivery id cannot both type: while the first send is held mid-typing,
        // the second is refused as Delivering with nothing sent, and only the first reaches the terminal.
        var backend = new HeldSendBackend();
        var manager = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);

            var first = SessionCommandExecutor.SendPromptAsync(session, Delivery("upload-2"), SendSource.Delivery, _record);
            await backend.Typing.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Bounded: a second copy that is NOT refused waits behind the held first send, and must fail here, not hang.
            var second = Body(await SessionCommandExecutor.SendPromptAsync(session, Delivery("upload-2"), SendSource.Delivery, _record)
                .WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.False(second.Accepted);
            Assert.Equal(DeliveryState.Delivering, second.DeliveryState);
            Assert.Equal(1, backend.Sends);

            backend.Release.SetResult();
            var firstBody = Body(await first.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(firstBody.Accepted);
            Assert.Equal(DeliveryState.Delivered, firstBody.DeliveryState);
            Assert.Equal(1, backend.Sends);
            Assert.Equal(DeliveryState.Delivered, _record.Read(session.Id, "upload-2").State);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public async Task SendPromptAsync_AfterASendThatThrew_TheSameDeliveryIdIsTyped()
    {
        // Proves a failed send is recorded NotDelivered with its reason, and a retry of that id is a real retry: it types.
        var backend = new HeldSendBackend { FailNext = "the composer never echoed" };
        var manager = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
            backend.Release.SetResult();

            var thrown = await Assert.ThrowsAnyAsync<Exception>(
                () => SessionCommandExecutor.SendPromptAsync(session, Delivery("upload-3"), SendSource.Delivery, _record));
            Assert.Contains("the composer never echoed", thrown.Message);
            var failed = _record.Read(session.Id, "upload-3");
            Assert.Equal(DeliveryState.NotDelivered, failed.State);
            Assert.Contains("the composer never echoed", failed.Reason);

            var retry = Body(await SessionCommandExecutor.SendPromptAsync(session, Delivery("upload-3"), SendSource.Delivery, _record));

            Assert.True(retry.Accepted);
            Assert.Equal(DeliveryState.Delivered, retry.DeliveryState);
            Assert.Equal(2, backend.Sends);
        }
        finally { manager.Dispose(); }
    }

    /// <summary>A TYPED prompt carrying a delivery id (Voice Delivery mission, phase 6): the Gateway mints ids for typed
    /// text too, and on 25 September 2026 (case 2f) a typed prompt that was refused was logged "(no delivery id)".</summary>
    private static PromptRequest Typed(string deliveryId) => new()
    {
        Text = "typed words that did not go in",
        AppendEnter = true,
        Surface = "cockpit",
        DeliveryId = deliveryId,
    };

    [Fact]
    public async Task SendPromptAsync_TypedPromptWithDeliveryIdRefused_IsRecordedNotDeliveredAgainstItsId()
    {
        // Proves a typed prompt (not a dictation) whose send is refused is recorded against its delivery id with the
        // reason, exactly as a spoken one is.
        var backend = new HeldSendBackend { FailNext = "the composer still holds text after it was cleared" };
        var manager = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);
            backend.Release.SetResult();

            var thrown = await Assert.ThrowsAnyAsync<Exception>(
                () => SessionCommandExecutor.SendPromptAsync(session, Typed("typed-1"), SendSource.UserInput, _record));

            Assert.Contains("still holds text", thrown.Message);
            var entry = _record.Read(session.Id, "typed-1");
            Assert.Equal(DeliveryState.NotDelivered, entry.State);
            Assert.Contains("still holds text", entry.Reason);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public async Task SendPromptAsync_TypedPromptWithDeliveryIdFailsAfterTheAnswer_IsRecordedNotDeliveredAgainstItsId()
    {
        // Proves a typed prompt answered "delivering" inside the Gateway's wait, whose send then fails, has that late
        // outcome recorded against its delivery id - not only logged.
        var backend = new HeldSendBackend { FailNext = "the composer never echoed the typed text" };
        var manager = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var session = manager.CreateEmbeddedSession(Path.GetTempPath(), null, backend);

            var answer = Body(await SessionCommandExecutor.SendPromptAsync(session, Typed("typed-2"), SendSource.UserInput, _record,
                answerBudget: TimeSpan.FromMilliseconds(300)));
            Assert.Equal(DeliveryState.Delivering, answer.DeliveryState);
            Assert.Equal(DeliveryState.Delivering, _record.Read(session.Id, "typed-2").State);

            backend.Release.SetResult();
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (_record.Read(session.Id, "typed-2").State == DeliveryState.Delivering && DateTime.UtcNow < until)
                await Task.Delay(50);

            var entry = _record.Read(session.Id, "typed-2");
            Assert.Equal(DeliveryState.NotDelivered, entry.State);
            Assert.Contains("never echoed", entry.Reason);
        }
        finally { manager.Dispose(); }
    }

    [Fact]
    public async Task SendPromptAsync_RecordCannotBeRead_RefusesAndTypesNothing()
    {
        // Proves an unreadable record refuses the delivery with the file named, and types nothing - never "unknown".
        var (session, terminal) = ScriptedTerminal.NewWaitingSession();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_record.FileFor(session.Id), "{ not a record\n");

        var result = await SessionCommandExecutor.SendPromptAsync(session, Delivery("upload-4"), SendSource.Delivery, _record);

        Assert.False(result.Ok);
        Assert.Contains(_record.FileFor(session.Id), result.Error);
        Assert.Empty(terminal.Writes);
    }

    [Fact]
    public async Task SendPromptAsync_NoDeliveryId_WritesNothingToTheRecord()
    {
        // Proves a prompt without a delivery id is sent exactly as before and never touches the record - while its answer
        // still says what became of the send, as every prompt sent with Enter does (the Delivery Lead's ruling).
        var (session, terminal) = NewTerminalSession();

        var response = Body(await SessionCommandExecutor.SendPromptAsync(session,
            new PromptRequest { Text = "typed words", AppendEnter = true }, SendSource.UserInput, _record));

        Assert.True(response.Accepted);
        Assert.Equal(DeliveryState.Delivered, response.DeliveryState);
        Assert.Single(terminal.Submitted);
        Assert.False(File.Exists(_record.FileFor(session.Id)));
    }

    // ===== the delivery-state verb ===========================================================================

    private static DirectorCommand Ask(Guid session, string deliveryId) => new()
    {
        CommandId = "ask-1",
        Verb = DeliveryStateRequest.Verb,
        SessionId = session.ToString(),
        PayloadJson = SessionCommandExecutor.Serialize(new DeliveryStateRequest { DeliveryId = deliveryId }),
    };

    private DeliveryStateResponse Answer(Guid session, string deliveryId)
    {
        var result = SessionReadExecutor.DeliveryStateOf(Ask(session, deliveryId), _record);
        Assert.True(result.Ok, result.Error);
        return JsonSerializer.Deserialize<DeliveryStateResponse>(result.BodyJson!, Json)!;
    }

    [Fact]
    public void DeliveryStateVerb_AnswersEachOfTheFourStates()
    {
        // Proves "what became of delivery id X?" answers unknown, delivering, delivered and not-delivered with its reason.
        var session = Guid.NewGuid();
        _record.TryBeginDelivery(session, "delivering");
        _record.TryBeginDelivery(session, "delivered");
        _record.MarkDelivered(session, "delivered");
        _record.TryBeginDelivery(session, "failed");
        _record.MarkNotDelivered(session, "failed", "the session exited");

        Assert.Equal(DeliveryState.Unknown, Answer(session, "never-seen").State);
        Assert.Equal(DeliveryState.Delivering, Answer(session, "delivering").State);
        Assert.Equal(DeliveryState.Delivered, Answer(session, "delivered").State);
        var failed = Answer(session, "failed");
        Assert.Equal(DeliveryState.NotDelivered, failed.State);
        Assert.Equal("the session exited", failed.Reason);
        Assert.Equal("failed", failed.DeliveryId);
    }

    [Fact]
    public void DeliveryStateVerb_UnreadableRecord_IsAFailureNamingTheFile()
    {
        // Proves a record that cannot be read answers a failure that names the file, never Unknown.
        var session = Guid.NewGuid();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_record.FileFor(session), "garbage\n");

        var result = SessionReadExecutor.DeliveryStateOf(Ask(session, "upload-5"), _record);

        Assert.False(result.Ok);
        Assert.Equal(DirectorCommandStatus.Error, result.Status);
        Assert.Contains(_record.FileFor(session), result.Error);
    }

    [Fact]
    public async Task DeliveryStateVerb_IsRegisteredOnTheDirectorsDispatch()
    {
        // Proves the verb is reachable through the real dispatch (not answered "unknown verb").
        var manager = new SessionManager(new Core.Configuration.AgentOptions());
        try
        {
            var result = await SessionCommandExecutor.DispatchAsync(manager, "dir-A", new DirectorCommand
            {
                CommandId = "c1",
                Verb = DeliveryStateRequest.Verb,
                SessionId = "not-a-guid",
                PayloadJson = "{}",
            });

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.DoesNotContain("unknown verb", result.Error);
        }
        finally { manager.Dispose(); }
    }

    /// <summary>A one-call-submit terminal whose send can be held mid-typing and can be made to throw, counting sends.</summary>
    private sealed class HeldSendBackend : ISessionBackend
    {
        private int _sends;
        public int Sends => Volatile.Read(ref _sends);
        public TaskCompletionSource Typing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? FailNext { get; set; }
        public int ProcessId => 0;
        public string Status => "held";
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
            Interlocked.Increment(ref _sends);
            Typing.TrySetResult();
            await Release.Task;
            if (FailNext is { } failure)
            {
                FailNext = null;
                throw new InvalidOperationException(failure);
            }
            Buffer!.Write(Encoding.UTF8.GetBytes(text + "\r"));
        }

        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
