using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Core.Activity;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// RULING R20 ON THE REAL ROUTE: the desktop compose box, the real <see cref="MainWindow"/>, the real Send.
///
/// The fix-round inspector failed the previous proof because it never entered this route: it built a
/// ComposerProvenance by hand and never executed MainWindow's send, so swapping the origin in the real send
/// for DesktopTyped left it green. Every test here constructs the real window headless, adds a real Session
/// over a null backend, selects it through the real SelectSession, puts text in the real PromptInput - typed
/// through the box's own hook, dictated through the same insert the Speak dialog's Insert button calls - and
/// then runs the real SendPromptCoreAsync. What is read back is the origin the Session stamped on the turn,
/// off its own OnTurnSubmitted event. Nothing about the classification is constructed here.
///
/// The box's text-changed hook is posted by the toolkit, not raised inline, so each step runs the dispatcher's
/// jobs before the next - exactly what happens between two of the user's actions in the running app.
/// </summary>
public sealed class ComposerSendRouteTests
{
    private const string Words = "deploy the gateway and tell me when it is up";

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

    /// <summary>The real window with one or more real sessions in its rail, the first selected.</summary>
    private sealed class Rig : IDisposable
    {
        public MainWindow Window { get; } = new();
        public List<Session> Sessions { get; } = new();
        public List<(Session Session, InputOrigin? Origin, SubmissionEvidence Evidence)> Submitted { get; } = new();
        public TextBox Box => Window.PromptInput;
        // The REAL activity producer over a real outbox, so what the door said is read off the ledger row it
        // wrote (source logging, 2026-09-05), not off an event a test could subscribe to differently.
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));
        private readonly ActivityEventOutbox _outbox;
        private readonly ActivityEventProducer _producer;

        public Rig(int sessions = 1, Func<ISessionBackend>? backend = null)
        {
            Directory.CreateDirectory(_dir);
            _outbox = new ActivityEventOutbox(Path.Combine(_dir, "outbox.jsonl"));
            _producer = new ActivityEventProducer(new SessionManager(new AgentOptions()) { DirectorId = "dir-test" }, _outbox);
            for (var i = 0; i < sessions; i++)
            {
                var session = new Session(Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null, backend?.Invoke() ?? new NullBackend(), null,
                    ActivityState.Idle, DateTimeOffset.UtcNow, null, null);
                session.OnTurnSubmitted += (_, origin, evidence) => Submitted.Add((session, origin, evidence));
                _producer.Wire(session);
                Sessions.Add(session);
                Window._sessions.Add(new SessionViewModel(session));
            }
            Select(0);
        }

        /// <summary>The one turn-submitted row the real producer wrote for the last send.</summary>
        public ActivityEventRecord LedgerRow() =>
            Assert.Single(_outbox.PendingBatch(100).Where(e => e.EventType == ActivityEventTypes.TurnSubmitted).TakeLast(1));

        public void Dispose()
        {
            _producer.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch { /* scratch dir; best effort */ }
        }

        public void Select(int index)
        {
            Window.SelectSession(Window._sessions[index]);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Typed into the box at its caret, as keystrokes reach the real control's text.</summary>
        public void Type(string text)
        {
            var caret = Box.CaretIndex;
            var current = Box.Text ?? "";
            Box.Text = current.Insert(caret, text);
            Box.CaretIndex = caret + text.Length;
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The Speak dialog's Insert: the transcript dropped at the caret and recorded as spoken.</summary>
        public void Dictate(string transcript)
        {
            Window.InsertTranscriptIntoPromptInputAt(transcript, Box.CaretIndex);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>A selection deleted, the caret left where the selection began - what Delete does.</summary>
        public void Delete(int start, int length)
        {
            Box.Text = (Box.Text ?? "").Remove(start, length);
            Box.CaretIndex = start;
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The ordinary Send: the same method the Send button and Ctrl+Enter run.</summary>
        public async Task<InputOrigin> SendAsync()
        {
            var before = Submitted.Count;
            await Window.SendPromptCoreAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(before + 1, Submitted.Count);
            var origin = Submitted[^1].Origin;
            Assert.NotNull(origin);
            Assert.Equal(InputSurface.Desktop, origin!.Value.Surface);
            return origin.Value;
        }
    }

    [AvaloniaFact]
    public async Task AnUntouchedInsertedDictation_SentWithTheOrdinarySend_IsStampedDesktopVoice()
    {
        // The owner's case: he speaks, the words land in the box, he presses Send. Under the "typed by
        // construction" comment this was DesktopTyped.
        using var rig = new Rig();
        rig.Dictate(Words);
        Assert.Equal(Words, rig.Box.Text);

        var origin = await rig.SendAsync();

        Assert.Equal(InputOrigin.DesktopVoice, origin);
        Assert.Equal("", rig.Box.Text);
        Assert.Equal("", rig.Sessions[0].PendingPromptText);
    }

    [AvaloniaFact]
    public async Task TypedWordsSentWithTheOrdinarySend_AreStampedDesktopTyped()
    {
        using var rig = new Rig();
        rig.Type("please deploy the gateway");

        Assert.Equal(InputOrigin.DesktopTyped, await rig.SendAsync());
    }

    /// <summary>THE SAME MIXTURES THE PHONE AND THE BACKGROUND SEND ARE FED, through the compose box and the
    /// real MainWindow send. SpokenTurnRule.Examples is one table for all three routes.</summary>
    [AvaloniaFact]
    public async Task EveryExampleMixture_ComposedInTheRealBoxAndSent_LandsOnTheSharedRulesModality()
    {
        Assert.True(SpokenTurnRule.Examples.Count >= 6, "the shared table is too short to be a contract");
        foreach (var example in SpokenTurnRule.Examples)
        {
            using var rig = new Rig();
            rig.Type(example.Before);
            if (example.Prefix.Length > 0) rig.Dictate(example.Prefix);
            rig.Dictate(example.Transcript);
            rig.Type(example.After);
            Assert.Contains(example.Transcript, rig.Box.Text);

            var origin = await rig.SendAsync();

            Assert.True(example.Expected == origin.Modality,
                $"'{example.Name}': the compose box sent {origin.Modality}, the shared rule says {example.Expected}");
        }
    }

    [AvaloniaFact]
    public async Task EditingInsideTheDictation_MakesTheTurnTyped()
    {
        using var rig = new Rig();
        rig.Dictate(Words);
        rig.Box.CaretIndex = Words.IndexOf("gateway", StringComparison.Ordinal) + "gateway".Length;
        rig.Type("s");

        Assert.Equal(InputOrigin.DesktopTyped, await rig.SendAsync());
    }

    [AvaloniaFact]
    public async Task TheSameWordsTypedThenDictated_AreToldApartByWhichCharactersWereSpoken()
    {
        // The fix-round inspector's case against a record that knew the transcript's text and not its place:
        // typed once, dictated once, the spoken copy deleted - the record still saw the words and said voice.
        using var rig = new Rig();
        rig.Type(Words);
        rig.Dictate(Words);
        Assert.Equal(Words + " " + Words, rig.Box.Text);
        // Delete the SPOKEN copy - the second one, and the space before it.
        rig.Delete(Words.Length, Words.Length + 1);
        Assert.Equal(Words, rig.Box.Text);
        Assert.Equal(InputOrigin.DesktopTyped, await rig.SendAsync());

        // The mirror: the TYPED copy deleted, the spoken one kept. Same surviving text, opposite answer.
        using var mirror = new Rig();
        mirror.Type(Words);
        mirror.Dictate(Words);
        mirror.Delete(0, Words.Length + 1);
        Assert.Equal(Words, mirror.Box.Text);
        Assert.Equal(InputOrigin.DesktopVoice, await mirror.SendAsync());
    }

    [AvaloniaFact]
    public async Task ADictationLeftInTheBox_SurvivesASessionSwitch_AsDictation()
    {
        // The unfinished case the previous round admitted: switched away from and back to, the text came
        // back and its provenance did not, so the dictation was sent as typing.
        using var rig = new Rig(sessions: 2);
        rig.Dictate(Words);

        rig.Select(1);
        Assert.Equal("", rig.Box.Text);
        Assert.Equal(Words, rig.Sessions[0].PendingPromptText);
        Assert.Equal(new SpokenTurnRule.SpokenSpan(0, Words.Length), Assert.Single(rig.Sessions[0].PendingPromptSpokenSpans));

        rig.Select(0);
        Assert.Equal(Words, rig.Box.Text);
        var origin = await rig.SendAsync();

        Assert.Equal(InputOrigin.DesktopVoice, origin);
        Assert.Same(rig.Sessions[0], rig.Submitted[^1].Session);
    }

    [AvaloniaFact]
    public async Task ADictationSwitchedAwayFrom_ThenTypedAround_IsTyped()
    {
        using var rig = new Rig(sessions: 2);
        rig.Dictate(Words);
        rig.Select(1);
        rig.Select(0);
        rig.Type(" now");

        Assert.Equal(InputOrigin.DesktopTyped, await rig.SendAsync());
    }

    [AvaloniaFact]
    public async Task TypedTextSwitchedAwayFromAndBack_IsStillTyped()
    {
        using var rig = new Rig(sessions: 2);
        rig.Type(Words);
        rig.Select(1);
        rig.Select(0);

        Assert.Equal(InputOrigin.DesktopTyped, await rig.SendAsync());
        Assert.Empty(rig.Sessions[0].PendingPromptSpokenSpans);
    }

    [AvaloniaFact]
    public async Task ASessionRestoredWithADictatedPendingPrompt_SendsItAsDictation()
    {
        // What a Director restart hands the window: the pending text and its spoken spans on the Session,
        // as SessionManager.RestoreEmbeddedSession sets them from the persisted state.
        using var rig = new Rig(sessions: 2);
        rig.Sessions[1].PendingPromptText = Words;
        rig.Sessions[1].PendingPromptSpokenSpans = new[] { new SpokenTurnRule.SpokenSpan(0, Words.Length) };

        rig.Select(1);
        Assert.Equal(Words, rig.Box.Text);

        Assert.Equal(InputOrigin.DesktopVoice, await rig.SendAsync());
    }

    // ---- source logging (owner's ruling, 2026-09-05): the compose box door writes what it knew -------------

    [AvaloniaFact]
    public async Task ADictationTypedAround_LeavesTheDoorsWholeRecordOnTheLedgerRow()
    {
        using var rig = new Rig();
        rig.Type("please ");
        rig.Dictate(Words);
        rig.Type(" now");
        var sent = "please " + Words + " now";

        await rig.SendAsync();

        var row = rig.LedgerRow();
        Assert.Equal(SubmissionRoutes.DesktopComposer, row.Route);
        Assert.Equal(SubmissionIdentityKinds.LocalUser, row.IdentityKind);
        Assert.Null(row.TranscriptId);
        // WHICH characters were spoken, over the text as sent.
        Assert.Equal("7+" + Words.Length, row.SpokenSpans);
        Assert.Equal(SubmissionEvidence.Sha256Of(sent), row.ContentSha256);
        Assert.Equal(sent.Length, row.ContentLength);
        Assert.Equal("typed/desktop", row.InputOrigin);
    }

    [AvaloniaFact]
    public async Task ADictationBehindABlankLine_IsSentTrimmed_AndItsSpanFollowsTheTrim()
    {
        // The box holds a leading Windows line ending and trailing spaces the wire never gets; the span on the
        // row must land on the spoken words in the text actually sent.
        using var rig = new Rig();
        rig.Type("\r\n");
        rig.Dictate(Words);
        rig.Type("   ");

        await rig.SendAsync();

        var row = rig.LedgerRow();
        Assert.Equal("0+" + Words.Length, row.SpokenSpans);
        Assert.Equal(SubmissionEvidence.Sha256Of(Words), row.ContentSha256);
        Assert.Equal(Words.Length, row.ContentLength);
        Assert.Equal("voice/desktop", row.InputOrigin);
    }

    [AvaloniaFact]
    public async Task TypedWords_LeaveNoSpokenCharactersOnTheRow()
    {
        using var rig = new Rig();
        rig.Type("git status");
        await rig.SendAsync();
        var row = rig.LedgerRow();
        Assert.Equal(SubmissionRoutes.DesktopComposer, row.Route);
        Assert.Null(row.SpokenSpans);
        Assert.Equal(SubmissionEvidence.Sha256Of("git status"), row.ContentSha256);
    }

    /// <summary>A backend whose every send is refused the way session 121's were (issue 3481).</summary>
    private sealed class RefusingBackend : ISessionBackend
    {
        public const string Refusal = "[ClaudeCode] ResolveRetainedComposer: the composer cannot be read after it was cleared, so nothing was typed";
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
        public Task SendTextAsync(string text) => Task.FromException(new CcDirector.Core.Drivers.ComposerNotAcceptingInputException(Refusal));
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }

    /// <summary>
    /// A REFUSED SEND ENDS IN A MESSAGE, NEVER AN UNHANDLED EXCEPTION (issue 3481). On 29 September 2026 the desktop's own
    /// Send let session 121's refusal out: "UNHANDLED UI-THREAD EXCEPTION", then "UNOBSERVED TASK" from the finalizer, and
    /// the words were gone from the box that had been emptied before the send. Through the real window: the send does not
    /// throw, the words go back in the box, the refusal is shown, and no task is left unobserved.
    /// </summary>
    [AvaloniaFact]
    public async Task SendPromptCoreAsync_SessionRefusesTheSend_ShowsTheRefusalAndPutsTheWordsBack()
    {
        // Arrange
        using var rig = new Rig(backend: () => new RefusingBackend());
        var shown = new List<(string Title, string Message)>();
        rig.Window.SendRefusedShownForTests = (title, message) => shown.Add((title, message));
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, _) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += onUnobserved;
        try
        {
            rig.Type(Words);

            // Act
            await rig.Window.SendPromptCoreAsync();
            Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Assert
            var (title, message) = Assert.Single(shown);
            Assert.Equal("Prompt not sent", title);
            Assert.Contains(RefusingBackend.Refusal, message);
            Assert.Equal(Words, rig.Box.Text);
            Assert.Empty(rig.Submitted);
            Assert.Equal(0, unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= onUnobserved;
        }
    }

    /// <summary>
    /// THE OWNER'S RULE: no prompt-send path ever passes the prompt's words into a report (issue #3675). A refused send
    /// is reported through the display helper, and its FAILED line is what reaches the Gateway. Read off the real log
    /// on the real route: the line is there (so the absence below is about a line that was written), it names what
    /// failed and carries the refusal exception, and the words - typed or dictated - are in no error line at all.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendPromptCoreAsync_Refused_NoErrorLineCarriesTheWords(bool dictated)
    {
        // Arrange
        using var rig = new Rig(backend: () => new RefusingBackend());
        rig.Window.SendRefusedShownForTests = (_, _) => { };
        var lines = new List<string>();
        Action<string> capture = line => { lock (lines) lines.Add(line); };
        using var log = FileLog.RedirectForTests(); // the log writes nothing until started, and this owns it for the test
        if (dictated) rig.Dictate(Words); else rig.Type(Words);
        FileLog.ErrorObserver += capture;
        try
        {
            // Act
            await rig.Window.SendPromptCoreAsync();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            FileLog.ErrorObserver -= capture;
        }

        // Assert
        string[] seen;
        lock (lines) seen = lines.ToArray();
        var reported = Assert.Single(seen, l => l.Contains($"could not send a prompt to session {rig.Sessions[0].Id}"));
        Assert.Contains(RefusingBackend.Refusal, reported); // the exception rides on the line: type, message, stack
        Assert.All(seen, l => Assert.DoesNotContain(Words, l));
        Assert.All(seen, l => Assert.DoesNotContain("deploy the gateway", l));
    }

    [AvaloniaFact]
    public async Task SendHandoverCommandAsync_SessionRefusesTheSend_ShowsTheRefusalAndDoesNotThrow()
    {
        using var rig = new Rig(backend: () => new RefusingBackend());
        var shown = new List<(string Title, string Message)>();
        rig.Window.SendRefusedShownForTests = (title, message) => shown.Add((title, message));

        await rig.Window.SendHandoverCommandAsync();
        Dispatcher.UIThread.RunJobs();

        var (title, message) = Assert.Single(shown);
        Assert.Equal("Handover not sent", title);
        Assert.Contains(RefusingBackend.Refusal, message);
    }

    [AvaloniaFact]
    public async Task InjectHandoverPromptAsync_SessionRefusesTheSend_ShowsTheRefusalAndDoesNotThrow()
    {
        using var rig = new Rig(backend: () => new RefusingBackend());
        var shown = new List<(string Title, string Message)>();
        rig.Window.SendRefusedShownForTests = (title, message) => shown.Add((title, message));

        await rig.Window.InjectHandoverPromptAsync(rig.Sessions[0], @"C:\handovers\h.md", TimeSpan.Zero);
        Dispatcher.UIThread.RunJobs();

        var (title, message) = Assert.Single(shown);
        Assert.Equal("Handover not sent", title);
        Assert.Contains(@"C:\handovers\h.md", message);
    }

    [AvaloniaFact]
    public async Task SendPromptCoreAsync_RefusedDictation_IsShownAsADictationAndPutBackAsSpoken()
    {
        // Arrange: one untouched dictation in the box, refused.
        using var rig = new Rig(backend: () => new RefusingBackend());
        var shown = new List<(string Title, string Message)>();
        rig.Window.SendRefusedShownForTests = (title, message) => shown.Add((title, message));
        rig.Dictate(Words);

        // Act
        await rig.Window.SendPromptCoreAsync();
        Dispatcher.UIThread.RunJobs();

        // Assert: "Dictation not sent", and the words back in the box still count as the dictation they were.
        Assert.Equal("Dictation not sent", Assert.Single(shown).Title);
        Assert.Equal(Words, rig.Box.Text);
        Assert.Equal(InputModality.Voice, rig.Window._composerProvenance.OriginFor(rig.Box.Text ?? "").Modality);
    }
}
