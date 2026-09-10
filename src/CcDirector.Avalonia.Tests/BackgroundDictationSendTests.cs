using CcDirector.Avalonia.Voice;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The fire-and-forget Send's disk safety net (issue #1130): the recorded WAV is saved BEFORE the
/// single transcription attempt and deleted the moment the user's words are safe in another form.
/// The regression these tests pin down: transcription failure used to lose the spoken words entirely
/// (the WAV lived only in memory) - now the file survives and the failure report names it. Driven
/// through the fake-microphone and fake-transcriber seams; no real mic, no network, no user interface.
/// </summary>
public sealed class BackgroundDictationSendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch dir; best effort */ }
    }

    private sealed class FakeMic : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public string Description => "Fake Test Microphone";
        public void Start() { }
        public void Stop() { }
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
        public Task StopAsync(TimeSpan drainTimeout) => Task.CompletedTask;
    }

    private sealed class FakeTranscriber : IDictationTranscriber
    {
        public Exception? Throws { get; init; }
        public string Text { get; init; } = "hello world";
        public Task<DictationTranscript> TranscribeAsync(byte[] wav, CancellationToken ct = default)
        {
            if (Throws is not null) throw Throws;
            return Task.FromResult(new DictationTranscript(Text, Text, 0));
        }
    }

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

    private static Session NewSession() => new(
        Guid.NewGuid(),
        repoPath: @"C:\test\repo",
        workingDirectory: @"C:\test\repo",
        claudeArgs: null,
        backend: new NullBackend(),
        claudeSessionId: "claude-test",
        activityState: ActivityState.Working,
        createdAt: DateTimeOffset.UtcNow,
        customName: null,
        customColor: null);

    private static async Task<BatchDictationRecorder> NewRecordingRecorderAsync(byte[] audio)
    {
        var fake = new FakeMic();
        var recorder = new BatchDictationRecorder(new AgentOptions(), _ => fake,
            (_, _, _) => throw new InvalidOperationException("the send path must use the injected transcriber, not the recorder's"));
        await recorder.StartAsync();
        fake.Emit(audio);
        return recorder;
    }

    private string[] SavedRecordings() =>
        Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.wav") : Array.Empty<string>();

    [Fact]
    public async Task TranscriptionFails_RecordingIsKeptOnDisk_AndTheFailureReportNamesIt()
    {
        var recorder = await NewRecordingRecorderAsync(new byte[] { 1, 2, 3, 4 });
        string? failedError = null;
        string? failedComposed = "sentinel";

        await BackgroundDictationSend.RunAsync(
            recorder, prefix: "", NewSession(),
            new FakeTranscriber { Throws = new InvalidOperationException("provider down") },
            submit: (_, _, _) => throw new InvalidOperationException("must not submit without a transcript"),
            onFailed: (err, composed) => { failedError = err; failedComposed = composed; },
            recordingsDirectory: _dir);

        var kept = SavedRecordings();
        Assert.Single(kept); // the WAV survived the failed transcription - the words are recoverable
        Assert.NotNull(failedError);
        Assert.Contains("provider down", failedError);
        Assert.Contains(kept[0], failedError); // the modal names the file so the user can find it
        Assert.Null(failedComposed); // no transcript existed
    }

    [Fact]
    public async Task Delivered_RecordingIsDeleted()
    {
        var recorder = await NewRecordingRecorderAsync(new byte[] { 1, 2, 3, 4 });
        string? submitted = null;

        await BackgroundDictationSend.RunAsync(
            recorder, prefix: "", NewSession(),
            new FakeTranscriber { Text = "the words" },
            submit: (text, _, _) => { submitted = text; return Task.CompletedTask; },
            onFailed: (_, _) => throw new InvalidOperationException("delivery must not report failure"),
            recordingsDirectory: _dir);

        Assert.Equal("the words", submitted);
        Assert.Empty(SavedRecordings()); // words delivered - the safety-net file is gone
    }

    [Fact]
    public async Task SubmitFails_TextIsRestored_AndRecordingIsDeleted()
    {
        var recorder = await NewRecordingRecorderAsync(new byte[] { 1, 2, 3, 4 });
        string? failedComposed = null;

        await BackgroundDictationSend.RunAsync(
            recorder, prefix: "", NewSession(),
            new FakeTranscriber { Text = "the words" },
            submit: (_, _, _) => throw new InvalidOperationException("composer refused"),
            onFailed: (_, composed) => failedComposed = composed,
            recordingsDirectory: _dir);

        Assert.Equal("the words", failedComposed); // the words survive as restorable text...
        Assert.Empty(SavedRecordings()); // ...so the audio copy is not needed and does not pile up
    }

    [Fact]
    public async Task SubmitFails_WithNoFailureCallback_RecordingIsKept()
    {
        // Without an onFailed callback nobody restores the words as text, so the saved WAV is the
        // only copy of what was said - deleting it here would lose the speech outright. The single
        // production caller always passes onFailed; this pins the guard for any future caller.
        var recorder = await NewRecordingRecorderAsync(new byte[] { 1, 2, 3, 4 });

        await BackgroundDictationSend.RunAsync(
            recorder, prefix: "", NewSession(),
            new FakeTranscriber { Text = "the words" },
            submit: (_, _, _) => throw new InvalidOperationException("composer refused"),
            onFailed: null,
            recordingsDirectory: _dir);

        Assert.Single(SavedRecordings());
    }

    // ---- ruling R20: the desktop classifies a mixture exactly as the phone does ------------------------

    /// <summary>
    /// THE SAME MIXTURES THE PHONE IS FED. SpokenTurnRule.Examples is one table; the phone's durable
    /// dictation route test feeds every row through the real Gateway and reads the ledger, this feeds
    /// every row through the real background Send and reads the origin it stamps, and ComposerSendRouteTests
    /// feeds every row through the real compose box and the real MainWindow send. No surface can classify a
    /// row differently from another without one of the three tests going red.
    /// </summary>
    [Fact]
    public async Task TheBackgroundSend_StampsEveryExampleMixture_ExactlyAsTheSharedRuleSays()
    {
        Assert.True(SpokenTurnRule.Examples.Count >= 6, "the shared table is too short to be a contract");
        Assert.Contains(SpokenTurnRule.Examples, e => e.Expected == InputModality.Voice);
        Assert.Contains(SpokenTurnRule.Examples, e => e.Expected == InputModality.Typed);
        foreach (var example in SpokenTurnRule.Examples)
        {
            var recorder = await NewRecordingRecorderAsync(new byte[] { 1, 2, 3, 4 });
            InputOrigin? stamped = null;
            string? submitted = null;
            SubmissionProvenance? provenance = null;
            await BackgroundDictationSend.RunAsync(
                recorder, prefix: example.Prefix, NewSession(),
                new FakeTranscriber { Text = example.Transcript },
                submit: (text, origin, door) => { submitted = text; stamped = origin; provenance = door; return Task.CompletedTask; },
                before: example.Before,
                after: example.After,
                onFailed: (_, _) => throw new InvalidOperationException("delivery must not report failure"),
                recordingsDirectory: _dir);
            Assert.NotNull(stamped);
            Assert.Equal(InputSurface.Desktop, stamped!.Value.Surface);
            Assert.True(example.Expected == stamped.Value.Modality,
                $"'{example.Name}': the desktop background Send stamped {stamped.Value.Modality}, the shared rule says {example.Expected}");
            Assert.Contains(example.Transcript, submitted!);
            // WHAT THE DOOR KNEW (source logging, 2026-09-05): the dictation door, the person at the desk, and the
            // spoken characters - the earlier segment and this transcript - where they stand in the text handed on.
            Assert.NotNull(provenance);
            Assert.Equal(SubmissionRoutes.DesktopDictation, provenance!.Route);
            Assert.Equal(SubmissionIdentityKinds.LocalUser, provenance.IdentityKind);
            var spoken = provenance.SpokenSpans.Select(s => submitted!.Substring(s.Start, s.Length)).ToArray();
            var expectedSpoken = new[] { example.Prefix.Trim(), example.Transcript }.Where(x => x.Length > 0).ToArray();
            Assert.Equal(expectedSpoken, spoken);
        }
    }

    [Fact]
    public async Task CorpusOn_DeliveredDictationKeepsTheClipAndItsTranscript()
    {
        // The seam: the safety-net file is still deleted on delivery, and the corpus pair is written
        // somewhere else. Keeping the clip must not resurrect the safety net's "something went wrong"
        // meaning, so the two directories are checked separately.
        var corpusDir = Path.Combine(_dir, "corpus");
        var recorder = await NewRecordingRecorderAsync(new byte[] { 1, 2, 3, 4 });

        await BackgroundDictationSend.RunAsync(
            recorder, prefix: "", NewSession(),
            new FakeTranscriber { Text = "the words" },
            submit: (_, _, _) => Task.CompletedTask,
            onFailed: (_, _) => throw new InvalidOperationException("delivery must not report failure"),
            recordingsDirectory: _dir,
            corpusConfig: new DictationCorpusConfig(Enabled: true, MaxClips: 100, MaxAgeDays: 90),
            corpusDirectory: corpusDir);

        Assert.Empty(SavedRecordings());   // the safety net still cleans up after itself
        Assert.Single(Directory.GetFiles(corpusDir, "*.wav"));
        Assert.Single(Directory.GetFiles(corpusDir, "*.json"));
        Assert.Contains("the words", File.ReadAllText(Directory.GetFiles(corpusDir, "*.json")[0]));
    }

    [Fact]
    public async Task CorpusOff_DeliveredDictationKeepsNothing()
    {
        var corpusDir = Path.Combine(_dir, "corpus");
        var recorder = await NewRecordingRecorderAsync(new byte[] { 1, 2, 3, 4 });

        await BackgroundDictationSend.RunAsync(
            recorder, prefix: "", NewSession(),
            new FakeTranscriber { Text = "the words" },
            submit: (_, _, _) => Task.CompletedTask,
            onFailed: (_, _) => throw new InvalidOperationException("delivery must not report failure"),
            recordingsDirectory: _dir,
            corpusConfig: new DictationCorpusConfig(Enabled: false, MaxClips: 100, MaxAgeDays: 90),
            corpusDirectory: corpusDir);

        Assert.Empty(SavedRecordings());
        Assert.False(Directory.Exists(corpusDir), "the corpus must write nothing while it is off");
    }
}
