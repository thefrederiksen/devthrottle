using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using CcDirector.Core.HostedAi;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Wingman;
using Xunit;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The "voice mode" yellow window (issue #531): while the wingman is actively producing a
/// session's spoken summary, <see cref="WingmanVoiceService.IsGenerating"/> is true, which the
/// gateway folds into the existing "Briefing" yellow so the session goes red -> yellow -> red.
/// </summary>
public sealed class WingmanVoiceServiceTests : IDisposable
{
    private readonly GatewayDbTestHarness _settingsData = new();
    private TenantSettingsResolver? _settings;

    private TenantSettingsResolver Settings =>
        _settings ??= new TenantSettingsResolver(new TenantSettingsStore(_settingsData.Open()));

    public void Dispose() => _settingsData.Dispose();

    /// <summary>
    /// A persist path inside a DIRECTORY unique to this call - never a unique filename in the shared machine
    /// temp directory.
    ///
    /// This distinction is the whole isolation guarantee, and it is worth spelling out because the harness
    /// that preceded it was not careless - it was correct, and then quietly stopped being correct without a
    /// line of it changing. It randomized the persist FILENAME, which was sufficient while the file was the
    /// deepest thing the service derived anything from. Then the voice state was partitioned by tenant, and
    /// the service began deriving its per-tenant root from that file's PARENT directory. Every instance
    /// pointed at a bare temp filename now resolves to ONE shared tenants/local partition under the machine
    /// temp path, so a clip written for "sid-1" by one test is loaded by the next test's fresh service (the
    /// constructor loads every partition), and tests start passing or failing on each other's leftovers.
    ///
    /// The rule to carry: a test's isolation is only as deep as the deepest path component the production
    /// code derives from. Randomize the DIRECTORY, so the isolation survives the next time something moves
    /// the derivation up a level. Callers that need two service instances to see the SAME state (the
    /// gateway-restart cases) must call this ONCE and share the result deliberately.
    /// </summary>
    private static string TempPersist()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "voice-sessions.json");
    }

    private WingmanVoiceService NewService(
        Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?>? conversationReader = null)
    {
        // The flag methods never touch the brain; a provider that throws proves that.
        Func<TenantId, WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _) => throw new InvalidOperationException("brain must not be called for flag state");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        return new WingmanVoiceService(brain, new KeyVault(vaultPath), Settings, TempPersist(),
            conversationReader: conversationReader);
    }

    /// <summary>
    /// The conversation the Gateway has STORED for a session, in the widget shape the narration reads
    /// (turn-push mission, phase 3). This is the seam that replaced the "turns" command the narration used
    /// to send down the tunnel, so it is the seam every generation test now sets up.
    ///
    /// It is a small class rather than a bare lambda for two reasons that tests here depend on. It COUNTS
    /// the reads, which is how a test can still prove the service looked at the conversation before deciding
    /// to skip - the assertion that used to be made against the tunnel stub's hit counter. And the stored
    /// value is settable, so one test can drive the sequence that actually happens in production: nothing
    /// stored yet, then the Director pushes the turn, then the next sweep narrates it.
    /// </summary>
    private sealed class StoredConversationStub
    {
        private IReadOnlyList<CcDirector.Gateway.Contracts.TurnWidgetDto>? _widgets;
        private int _reads;

        private StoredConversationStub(IReadOnlyList<CcDirector.Gateway.Contracts.TurnWidgetDto>? widgets)
            => _widgets = widgets;

        /// <summary>How many times the service asked for this session's conversation.</summary>
        public int Reads => _reads;

        /// <summary>A conversation of the given widgets, in order, as (kind, content) pairs. "Text" is the
        /// agent's own words - the last of those is what a narration is made from.</summary>
        public static StoredConversationStub Of(params (string Kind, string Content)[] widgets)
            => new(Build(widgets));

        /// <summary>Nothing has been stored for this session yet - the Director has not pushed its turn.
        /// This is a WAIT, not a failure, and the service must treat it as one.</summary>
        public static StoredConversationStub NothingStored() => new(null);

        /// <summary>An agent that keeps no readable conversation AT ALL. Terminal: no wait and no retry will
        /// ever produce words to narrate, so the service must say so rather than wait quietly forever.</summary>
        public static StoredConversationStub NoConversationEverKept() => new(Build(Array.Empty<(string, string)>())) { _supported = false };

        /// <summary>The Director's push arrives: from now on the reader answers with these widgets.</summary>
        public void Store(params (string Kind, string Content)[] widgets) => _widgets = Build(widgets);

        private bool _supported = true;

        /// <summary>The delegate the service is constructed with.</summary>
        public Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?> Reader
            => (_, _) =>
            {
                Interlocked.Increment(ref _reads);
                return _widgets is null
                    ? null
                    : new CcDirector.Gateway.History.StoredConversation(_supported, _widgets);
            };

        private static List<CcDirector.Gateway.Contracts.TurnWidgetDto> Build((string Kind, string Content)[] widgets)
        {
            var built = new List<CcDirector.Gateway.Contracts.TurnWidgetDto>();
            foreach (var (kind, content) in widgets)
                built.Add(new CcDirector.Gateway.Contracts.TurnWidgetDto { Kind = kind, Content = content });
            return built;
        }
    }

    [Fact]
    public async Task GenerateAsync_WhenTheAgentKeepsNoConversation_IsTerminal_AndStandsTheSweepDown()
    {
        // THE CAPABILITY THAT NEARLY WENT WITH THE TUNNEL READ. An agent with no conversation to read used to
        // answer "unsupported" on the transcript read, which recorded a terminal verdict: the voice screen
        // said "Voice unavailable" and the sweep stopped spending its small per-cycle budget on a session
        // that could never produce a narration. Reading the store removed the failing read and, with it, the
        // only producer of that verdict - such a session would have read as an ordinary quiet wait, forever,
        // with nothing said anywhere. The Director already pushes the fact, so the store carries it.
        var conversation = StoredConversationStub.NoConversationEverKept();
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-terminal-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var svc = ServiceWithBrainAndTts(new RecordingBrain(), new byte[] { 1, 2, 3 }, persistPath, conversation.Reader);
            var tunnel = new TunnelStub();

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(tunnel));

            Assert.Equal(HostedAiState.Unavailable, svc.ReadFailedFor(TenantId.Local, "sid-1"));
            Assert.True(svc.ShouldSkipSweep(TenantId.Local, "sid-1"));
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
            // And NOT the honest-but-wrong "waiting on a prompt" answer, which is what a reader would be told
            // to keep waiting for.
            Assert.False(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void IsGenerating_DefaultsFalse()
    {
        var svc = NewService();
        Assert.False(svc.IsGenerating(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void BeginGenerating_ThenIsGenerating_IsTrue()
    {
        var svc = NewService();
        svc.BeginGenerating(TenantId.Local, "sid-1");
        Assert.True(svc.IsGenerating(TenantId.Local, "sid-1"));
        // Independent per session: a second session is unaffected.
        Assert.False(svc.IsGenerating(TenantId.Local, "sid-2"));
    }

    [Fact]
    public void EndGenerating_ClearsTheFlag()
    {
        var svc = NewService();
        svc.BeginGenerating(TenantId.Local, "sid-1");
        svc.EndGenerating(TenantId.Local, "sid-1");
        Assert.False(svc.IsGenerating(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void OnSessionWorking_ClearsGenerating()
    {
        // A new turn (blue) supersedes any in-flight wingman run for the previous turn, so the
        // yellow marker must drop - raw activity wins while the agent works.
        var svc = NewService();
        svc.BeginGenerating(TenantId.Local, "sid-1");
        svc.OnSessionWorking(TenantId.Local, "sid-1");
        Assert.False(svc.IsGenerating(TenantId.Local, "sid-1"));
    }

    // ---------- Durable audio cache (issue #553) ----------

    /// <summary>Build a service over a SPECIFIC persist path so a second instance can reload from
    /// the same on-disk cache (the gateway-restart case). The empty vault means TtsAsync returns null.</summary>
    private WingmanVoiceService ServiceAt(string persistPath)
    {
        Func<TenantId, WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _) => throw new InvalidOperationException("brain must not be called");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        return Warmed(new WingmanVoiceService(brain, new KeyVault(vaultPath), Settings, persistPath));
    }

    /// <summary>Remove the whole per-test directory. It deletes the DIRECTORY rather than picking out the
    /// individual files it expects, because <see cref="TempPersist"/> owns that directory outright, and a
    /// cleanup that enumerates known filenames goes stale the moment the layout underneath changes - which
    /// is exactly what happened when the per-tenant partition was introduced under it.</summary>
    private static void Cleanup(string persistPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(persistPath);
            if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task StoreSpokenAsync_WithFailingTts_DoesNotMarkReady()
    {
        // No OpenAI key in the vault -> TtsAsync returns null -> the "if anything fails, remove the
        // triangle" rule: the session is a voice session but has NO playable audio, so no triangle.
        var svc = NewService();
        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "a spoken summary", "the reply");
        Assert.True(svc.IsVoiceSession(TenantId.Local, "sid-1"));
        Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.DoesNotContain("sid-1", svc.ReadySessionIds(TenantId.Local));
    }

    [Fact]
    public async Task StoreSpokenAsync_WithEmptySpoken_DoesNotMarkReady()
    {
        var svc = NewService();
        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "   ", "the reply");
        Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
    }

    [Fact]
    public void ReadyAudio_PersistsAndReloadsAcrossRestart()
    {
        // A successful synthesis is durable: a fresh service over the same persist path reloads the
        // ready audio, so the triangle/playability survives a gateway restart and a tap still plays.
        // ONE root, shared DELIBERATELY by the two service instances below: this test is the gateway-restart
        // case, and a second instance that could not see what the first wrote would not be testing anything.
        var persistPath = TempPersist();
        try
        {
            var svc = ServiceAt(persistPath);
            var audio = new byte[] { 1, 2, 3, 4, 5 };
            svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken text", "reply text", audio, "audio/wav");
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));

            // Simulate a gateway restart: a brand-new service over the same path.
            var reloaded = ServiceAt(persistPath);
            Assert.True(reloaded.HasVoice(TenantId.Local, "sid-1"));
            Assert.Contains("sid-1", reloaded.ReadySessionIds(TenantId.Local));
            var got = reloaded.GetAudio(TenantId.Local, "sid-1");
            Assert.NotNull(got);
            Assert.Equal(audio, got);
            var ready = reloaded.Get(TenantId.Local, "sid-1");
            Assert.NotNull(ready);
            Assert.Equal("spoken text", ready.Spoken);
            Assert.Equal("reply text", ready.Reply);
            Assert.Equal("audio/wav", ready.ContentType);
        }
        finally { Cleanup(persistPath); }
    }

    [Fact]
    public void ReadyAudio_ReloadsLegacyWavCacheWithDetectedContentType()
    {
        // Older cache metadata had no content type. Kokoro can return WAV bytes, so reload must detect
        // RIFF instead of serving those bytes as audio/mpeg.
        //
        // The seed goes in the LOCAL TENANT PARTITION, which is where the service actually reads its cache
        // from now. It used to be written to <root>/voice-audio, the pre-partition location. That kept
        // passing after the partition landed - but only because the self-host legacy migration moved the
        // file into the partition before the load ran, which is not what this test is about. It would have
        // gone on passing with the partition deleted entirely, so it was proving nothing about it while
        // silently testing a different code path than its name claims. Seeding the real location keeps this
        // test on content-type detection; the migration has its own coverage in
        // WingmanVoiceTenantPartitionTests.
        var persistPath = TempPersist();
        try
        {
            var dir = Path.GetDirectoryName(persistPath)!;
            var audioDir = Path.Combine(dir, "tenants", "local", "voice-audio");
            Directory.CreateDirectory(audioDir);
            File.WriteAllBytes(Path.Combine(audioDir, "sid-1.mp3"), new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3 });
            File.WriteAllText(Path.Combine(audioDir, "sid-1.json"),
                "{\"Spoken\":\"spoken\",\"Reply\":\"reply\",\"AtUtc\":\"2026-01-01T00:00:00Z\"}");

            var reloaded = ServiceAt(persistPath);

            var ready = reloaded.Get(TenantId.Local, "sid-1");
            Assert.NotNull(ready);
            Assert.Equal("audio/wav", ready.ContentType);
        }
        finally { Cleanup(persistPath); }
    }

    [Fact]
    public void OnSessionWorking_DeletesDurableAudio()
    {
        // A new turn drops the stale audio from disk too, so a 5s-stale list row cannot point at
        // audio that no longer exists (which would 404 on /audio).
        // ONE root, shared DELIBERATELY by the two service instances below: this test is the gateway-restart
        // case, and a second instance that could not see what the first wrote would not be testing anything.
        var persistPath = TempPersist();
        try
        {
            var svc = ServiceAt(persistPath);
            svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 9, 9, 9 });
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));

            svc.OnSessionWorking(TenantId.Local, "sid-1");
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));

            // A restart must NOT resurrect the dropped audio.
            var reloaded = ServiceAt(persistPath);
            Assert.False(reloaded.HasVoice(TenantId.Local, "sid-1"));
        }
        finally { Cleanup(persistPath); }
    }

    // ---------- Re-narrate only when the reply actually changed (issue #1322, identity-aware) ----------

    /// <summary>
    /// A tunnel-connected "Director" that answers every verb successfully and counts how many commands it
    /// was sent. GenerateAsync still reaches the owning Director over the TUNNEL-ONLY SessionVerbClient, so
    /// this stub is the sendCommand the client dispatches to.
    ///
    /// It no longer serves a "turns" body. The narration used to fetch the session's conversation by sending
    /// that verb down the tunnel; since the turn-push mission's third phase it reads the conversation the
    /// Gateway has already stored, so the only thing left riding this transport during a generation is the
    /// LIVE SCREEN read. A test that wants to say something about the conversation sets up a
    /// <see cref="StoredConversationStub"/> instead, and its own read counter is the signal that used to be
    /// read off this one.
    /// </summary>
    private sealed class TunnelStub
    {
        private int _hits;
        public int Hits => _hits;

        public CcDirector.Gateway.Contracts.ScreenGridResponse? ScreenGrid { get; init; }

        public CcDirector.Gateway.Api.DirectorCommandRouter.SendDirectorCommandAsync SendCommand => (_, command, _) =>
        {
            Interlocked.Increment(ref _hits);
            if (command.Verb == "screen-grid" && ScreenGrid is not null)
            {
                var body = JsonSerializer.Serialize(ScreenGrid, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Task.FromResult<CcDirector.Gateway.Contracts.DirectorCommandResult?>(
                    CcDirector.Gateway.Contracts.DirectorCommandResult.Success(body));
            }
            return Task.FromResult<CcDirector.Gateway.Contracts.DirectorCommandResult?>(
                CcDirector.Gateway.Contracts.DirectorCommandResult.Success());
        };
    }

    /// <summary>A brain that records how many times it was asked and returns a canned, marker-wrapped
    /// spoken line - so a test can prove whether GenerateAsync actually ran the (re)narration.</summary>
    private sealed class RecordingBrain : IAgentBrain
    {
        private int _askCount;
        public int AskCount => _askCount;
        public string? SessionId => "recording-brain";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _askCount);
            var wrapped = $"{SessionAskRunner.AnswerBeginMarker}\nnarrated spoken text\n{SessionAskRunner.AnswerEndMarker}";
            return Task.FromResult(new AskResult { Text = wrapped, ReplySeconds = 0.1 });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    /// <summary>A brain whose ask never answers - it throws <see cref="TimeoutException"/>, the shape a
    /// bounded model-leg deadline (HostedInferenceBrain) now takes when the hosted worker stalls. Proves
    /// the turn-end path maps a model NON-ANSWER to the calm Retrying state, not a silent FAILED.</summary>
    private sealed class TimingOutBrain : IAgentBrain
    {
        private int _askCount;
        public int AskCount => _askCount;
        public string? SessionId => "timing-out-brain";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _askCount);
            throw new TimeoutException("The wingman model call did not answer within 60 seconds.");
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    [Fact]
    public async Task GenerateAsync_WhenModelLegTimesOut_RecordsRetrying_NoAudio_NoSilentFailure()
    {
        // The MODEL leg (translation) stalling used to hang the whole generation for 180s and then die as
        // a bare FAILED - the session sat red "needs you" with no audio and NO reason, so "half the fleet
        // is stuck and generate does nothing". The model leg now mirrors the speech leg: a non-answer is
        // Retrying (calm "voice on its way", the sweep retries), never a silent failure and never
        // ServiceDown. Nothing plays, but the phone knows why - and it is one session's state, nobody else's.
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modeltimeout-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new TimingOutBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 7, 7, 7 }, persistPath, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, brain.AskCount);                                          // it tried the model
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));                                      // nothing to play
            Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));   // and it says WHY, calmly
            Assert.NotEqual(HostedAiState.ServiceDown, svc.VoiceUnavailableFor(TenantId.Local, "sid-1")); // a non-answer is not "down"
            Assert.False(svc.IsGenerating(TenantId.Local, "sid-1"));                                  // the yellow window closed
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public void NoteRetrying_RecordsRetryingState_ForTheExplainPath()
    {
        // The on-demand "generate" (explain) path calls this when its translation times out, so the phone
        // shows "voice on its way" instead of the old false 502 "this session's computer is offline".
        var svc = NewService();
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
        svc.NoteRetrying(TenantId.Local, "sid-1");
        Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
    }

    // ---------- "Nothing to narrate": the session is waiting on a prompt, no text reply to read ----------

    [Fact]
    public async Task GenerateAsync_NoTextWidget_RecordsNothingToNarrate_NoAudio_NoFailureReason()
    {
        // The screenshot's session: waiting on a prompt/menu, so the latest turn has no Text widget. The
        // auto path must record the honest "nothing to narrate" fact (so the Voice screen says so), call
        // no model, produce no audio, and set NO failure reason - it is not a failure, it is just empty.
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("ToolUse", "running a tool"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-nothing-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 1 }, persistPath, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));   // NOT a failure - no Retrying/ServiceDown
            Assert.Equal(0, brain.AskCount);                 // nothing to translate, so the model was never called
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public async Task GenerateAsync_WhenPiFailsAfterTheLatestUserMessage_NarratesTheTerminalFailure()
    {
        // Production shape from testing pi: an old successful reply remains in stored history, the person's
        // later message has no reply, and the only current answer is the failure drawn on the live terminal.
        var director = new TunnelStub
        {
            ScreenGrid = new CcDirector.Gateway.Contracts.ScreenGridResponse
            {
                SessionId = "sid-1",
                HasGrid = true,
                Rows = new List<string>
                {
                    "continue with the work",
                    "Error: API key auth failed for provider openai-mindzie",
                    ">",
                },
            },
        };
        var conversation = StoredConversationStub.Of(
            ("Text", "The older successful answer."),
            ("UserMessage", "continue with the work"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-pi-terminal-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 4, 2 }, persistPath, conversation.Reader);

            await svc.GenerateAsync(
                TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, brain.AskCount);
            var ready = Assert.IsType<WingmanVoiceService.VoiceReady>(svc.Get(TenantId.Local, "sid-1"));
            Assert.Contains("API key auth failed", ready.Reply);
            Assert.DoesNotContain("older successful", ready.Reply, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(ready.SourceIdentity);
            Assert.NotEqual(ready.Reply, ready.SourceIdentity);
            Assert.False(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));

            // A Gateway restart retains the source identity, so it does not forget what the clip describes.
            var reloaded = ServiceAt(persistPath);
            var reloadedReady = Assert.IsType<WingmanVoiceService.VoiceReady>(
                reloaded.Get(TenantId.Local, "sid-1"));
            Assert.Equal(ready.SourceIdentity, reloadedReady.SourceIdentity);

            // The stable source identity suppresses a duplicate narration of the same unchanged failure.
            await svc.GenerateAsync(
                TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.Equal(1, brain.AskCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public async Task GenerateAsync_WithTextWidget_ClearsStaleNothingToNarrate_AndNarrates()
    {
        // A text reply appeared: the auto path clears any stale "nothing to narrate" and makes the voice.
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to read"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-nowtext-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 9, 9, 9 }, persistPath, conversation.Reader);
            svc.SetNothingToNarrate(TenantId.Local, "sid-1", true);   // a stale marker from an earlier empty read

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: false);

            Assert.False(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));   // cleared - there IS text now
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void SetNothingToNarrate_TogglesTheFact()
    {
        var svc = NewService();
        Assert.False(svc.NothingToNarrateFor(TenantId.Local, "s"));
        svc.SetNothingToNarrate(TenantId.Local, "s", true);
        Assert.True(svc.NothingToNarrateFor(TenantId.Local, "s"));
        svc.SetNothingToNarrate(TenantId.Local, "s", false);
        Assert.False(svc.NothingToNarrateFor(TenantId.Local, "s"));
    }

    [Fact]
    public void OnSessionWorking_ClearsNothingToNarrate()
    {
        // A new turn supersedes the old "nothing to narrate" verdict - it is re-evaluated on the next turn-end.
        var svc = NewService();
        svc.SetNothingToNarrate(TenantId.Local, "s", true);
        svc.OnSessionWorking(TenantId.Local, "s");
        Assert.False(svc.NothingToNarrateFor(TenantId.Local, "s"));
    }

    [Fact]
    public void Unmark_ClearsNothingToNarrate()
    {
        var svc = NewService();
        svc.Mark(TenantId.Local, "s");
        svc.SetNothingToNarrate(TenantId.Local, "s", true);
        svc.Unmark(TenantId.Local, "s");
        Assert.False(svc.NothingToNarrateFor(TenantId.Local, "s"));
    }

    // ---------- A FAILED read is not "nothing to say" (issue #2561), and there is no read left to fail ----------

    /// <summary>
    /// THE LESSON, KEPT: a failed read of a session's conversation must never be mistaken for "this session
    /// has nothing to say". Getting that wrong is issue #2561 - a missing transcript, an unreadable one, a
    /// parse exception and an agent with no history provider were all recorded as "nothing to narrate", a
    /// non-failure that is never retried and raises nothing anywhere, and a Pi session observed on 12 August
    /// sat silent for 48 minutes because of it.
    ///
    /// The lesson now holds STRUCTURALLY rather than by a check. The narration reads the conversation the
    /// Gateway has already stored, so there is no tunnel read left to fail: the five statuses this test used
    /// to enumerate one by one - no_transcript, no_jsonl, parse_error, empty_history and no_session_id - were
    /// each a shape of a failed "turns" command, and not one of them can arise any more. There is nothing to
    /// tell apart, so nothing to tell apart wrongly.
    ///
    /// What is left in that space is the one honest waiting state: nothing has been stored for this session
    /// yet, because the Director has not pushed its turn. That is a WAIT, not a failure and not an attempt.
    /// So the service must record NEITHER a read failure NOR nothing-to-narrate, must not call the model, and
    /// must produce no audio - it simply comes back on the next sweep.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenNothingIsStoredYet_RecordsNeitherAReadFailureNorNothingToNarrate()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.NothingStored();
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-nostore-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 1 }, persistPath, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(conversation.Reads >= 1);                              // it did look for the words
            Assert.Null(svc.ReadFailedFor(TenantId.Local, "sid-1"));           // ...and a wait is not a failed read
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));     // ...so the row carries no reason at all
            Assert.False(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));    // ...and it is NOT "nothing to say" either
            // NEGATIVE CONTROL for the too-old arm below: a Director that CAN send simply has not sent
            // yet, and nothing here may accuse its machine of being out of date.
            Assert.False(svc.DirectorCannotSendConversationFor(TenantId.Local, "sid-1"));
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));               // nothing to play yet
            Assert.Equal(0, brain.AskCount);                                   // and nothing to translate, so no spend
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The owning Director being unreachable no longer silences the narration, which is the whole gain of
    /// moving the conversation into the Gateway's own store. It used to be fatal: the conversation was
    /// fetched by a command sent down the tunnel, so a Director that had dropped off produced no words, and
    /// this test asserted the consolation prize - that the session at least said "voice on its way" instead
    /// of the lie "nothing to narrate".
    ///
    /// Now the words are already here. The only thing the tunnel still carries during a generation is the
    /// live screen read, which the narration explicitly does not depend on. So an unreachable Director costs
    /// this session a screen verdict and nothing else: it still narrates, and it still records no failure.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenTheDirectorIsUnreachable_StillNarratesFromTheStoredConversation()
    {
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-readnull-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 1 }, persistPath, conversation.Reader);
            // A null command result is what DirectorCommandRouter yields for an unreachable Director.
            CcDirector.Gateway.Api.DirectorCommandRouter.SendDirectorCommandAsync unreachable =
                (_, _, _) => Task.FromResult<CcDirector.Gateway.Contracts.DirectorCommandResult?>(null);
            var route = new CcDirector.Gateway.Api.SessionVerbClient(
                new CcDirector.Gateway.Contracts.DirectorDto { DirectorId = "d1", ControlEndpoint = "http://tunnel-only" },
                unreachable);

            await svc.GenerateAsync(TenantId.Local, "sid-1", route, CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, brain.AskCount);                                   // it translated the stored reply
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));                // and there is something to play
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));     // no failure was recorded
            Assert.False(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));    // and never the old lie
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The other direction, so the fix cannot be "say nothing about everything": a conversation that IS
    /// stored and genuinely contains no text reply is still the honest "nothing to narrate", and still raises
    /// no failure. This is the state a session waiting on a prompt is actually in, and it must survive the
    /// move of the conversation from a tunnel read into the Gateway's own store.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenReadSucceedsWithNoText_StillRecordsNothingToNarrate()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("ToolUse", "running a tool"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-oknotext-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 1 }, persistPath, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));   // still NOT a failure
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// A read that answers ends the read-failure state it caused, so the session does not carry a stale
    /// "voice on its way" forever once the transcript appears. Cleared BEFORE the reply check, because the
    /// display fold consults the unavailable state ahead of nothing-to-narrate and would otherwise mask it.
    /// Seeded through NoteReadFailed so the test proves the READ's own state clears - seeding the generic
    /// NoteRetrying instead would have proved nothing about provenance, which is the point of the split.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenReadRecovers_ClearsTheReadFailureRetrying()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("ToolUse", "running a tool"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-recover-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var svc = ServiceWithBrainAndTts(new RecordingBrain(), new byte[] { 1 }, persistPath, conversation.Reader);
            svc.NoteReadFailed(TenantId.Local, "sid-1", HostedAiState.Retrying);   // left behind by an earlier failed read

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Null(svc.ReadFailedFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));    // nothing else was standing, so the row is clean
            Assert.True(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));    // and the honest verdict is not masked
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }


    /// <summary>
    /// A session with nothing stored yet must stay IN the sweep, so it narrates the moment its words arrive.
    ///
    /// This is the successor to the old "unsupported" case. A tunnel read could answer that the agent exposed
    /// no conversation history at all, which retrying could never fix, so it took a terminal state - partly so
    /// the screen stopped promising a narration that was not coming, and partly so such sessions stopped
    /// starving the sweep's three-generations-a-cycle budget. Neither pressure exists now: a read of the store
    /// costs nothing and cannot fail, so a session waiting for its first push is simply cheap to ask again.
    ///
    /// What must therefore be true, and is what this test pins: the wait never stands the sweep down
    /// (<see cref="WingmanVoiceService.ShouldSkipSweep"/> stays false), and the session narrates on the very
    /// next attempt once the Director's push has landed. The alternative - recording something terminal on a
    /// session that has merely not spoken yet - would be the permanent silence this whole change removes.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenNothingIsStoredYet_DoesNotStandTheSweepDown_AndNarratesOnceTurnsArrive()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.NothingStored();
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-unsup-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 1 }, persistPath, conversation.Reader);

            // The turn has ended but the Director has not pushed it yet - the sweep finds nothing to read.
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.False(svc.ShouldSkipSweep(TenantId.Local, "sid-1"));       // still in the sweep - this is what recovers it
            Assert.Null(svc.ReadFailedFor(TenantId.Local, "sid-1"));          // nothing terminal, nothing retryable
            Assert.False(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));
            Assert.Equal(0, brain.AskCount);
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));

            // The push lands, and the next sweep of the same session narrates it.
            conversation.Store(("Text", "the reply the Director finally pushed"));
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, brain.AskCount);
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// A conversation that is not there yet must NEVER replace a standing account condition. "Add credit" is
    /// actionable and certain; a session whose words have not been pushed yet says nothing whatever about the
    /// account. An early version of the read-failure fix wrote the read's state into the SAME dictionary as
    /// the account's, so one unreachable tunnel downgraded "Voice needs credit" to "voice on its way" - found
    /// in review, and the reason the two facts are stored apart.
    ///
    /// The pressure is lower now, because a wait records nothing at all, which is the second assertion here.
    /// The precedence still has to hold, though: whatever the narration path learns on a pass where it has no
    /// words, the reader must still be told the one thing they can act on.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenNothingIsStoredYet_DoesNotOverwriteAStandingAccountCondition()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.NothingStored();
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-acct-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var svc = ServiceWithBrainAndTts(new RecordingBrain(), new byte[] { 1 }, persistPath, conversation.Reader);
            svc.NoteUnavailableForTest(TenantId.Local, "sid-1", HostedAiState.NeedsCredits);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            // The account condition still wins: it is the more actionable and the more certain of the two.
            Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            // ...and the wait added nothing of its own to argue with it.
            Assert.Null(svc.ReadFailedFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The mirror: a successful read clears only the READ's own state. A Retrying set by the MODEL leg or the
    /// speech leg is not evidence a transcript read can speak to, and erasing it flipped the row to "no
    /// narration yet" for the length of another slow attempt and back again - found in review.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenReadRecovers_DoesNotEraseAModelLegRetrying()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("ToolUse", "running a tool"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelretry-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var svc = ServiceWithBrainAndTts(new RecordingBrain(), new byte[] { 1 }, persistPath, conversation.Reader);
            svc.NoteRetrying(TenantId.Local, "sid-1");   // the MODEL leg's own state, on the shared map

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.ReadFailedFor(TenantId.Local, "sid-1"));   // the read's own store is empty - it answered
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The terminal skip is BOUNDED. Found in review: its first version was an unbounded skip whose only
    /// escape was a Working transition, which the hosted push path observes on a 15-second sampler and can
    /// miss entirely - so a stale terminal verdict would have survived forever and no later sweep could have
    /// found the recovery. The sweep's question must therefore be time-limited, not "is it terminal".
    /// </summary>
    [Fact]
    public void SweepSkip_IsBounded_AndAppliesOnlyToATerminalVerdict()
    {
        var svc = NewService();

        // Nothing recorded: never skipped.
        Assert.False(svc.ShouldSkipSweep(TenantId.Local, "s"));

        // A RETRYABLE failure must not stand the sweep down at all - it is the thing the sweep exists to
        // retry, and skipping it would recreate the permanent silence this whole change removes.
        svc.NoteReadFailed(TenantId.Local, "s", HostedAiState.Retrying);
        Assert.False(svc.ShouldSkipSweep(TenantId.Local, "s"));

        // A TERMINAL one stands it down - for now.
        svc.NoteReadFailed(TenantId.Local, "s", HostedAiState.Unavailable);
        Assert.True(svc.ShouldSkipSweep(TenantId.Local, "s"));

        // ...and the verdict itself is still reported while the skip holds, so the screen keeps saying why.
        Assert.Equal(HostedAiState.Unavailable, svc.VoiceUnavailableFor(TenantId.Local, "s"));
    }

    /// <summary>
    /// A terminal read verdict outranks a stale shared condition. Found in review: ranking the shared map
    /// first let a stale "add credit" sit in front of "this agent has no conversation to read" - and for such
    /// a session nothing clears the shared value, because it can never reach a successful synthesis. The
    /// reader would be told to fix it with credit, which cannot fix it.
    /// </summary>
    [Fact]
    public void ATerminalReadVerdict_OutranksAStaleAccountCondition()
    {
        var svc = NewService();
        svc.NoteUnavailableForTest(TenantId.Local, "s", HostedAiState.NeedsCredits);
        svc.NoteReadFailed(TenantId.Local, "s", HostedAiState.Unavailable);

        Assert.Equal(HostedAiState.Unavailable, svc.VoiceUnavailableFor(TenantId.Local, "s"));
    }

    [Fact]
    public void ARetryableReadVerdict_StillYieldsToAStandingAccountCondition()
    {
        // The other direction, unchanged: "add credit" is the more actionable and the more certain of the
        // two, and a read that merely has not answered yet says nothing that should displace it.
        var svc = NewService();
        svc.NoteUnavailableForTest(TenantId.Local, "s", HostedAiState.NeedsCredits);
        svc.NoteReadFailed(TenantId.Local, "s", HostedAiState.Retrying);

        Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor(TenantId.Local, "s"));
    }

    [Fact]
    public void ANewTurnReopensTheRead()
    {
        // A terminal read state must not outlive the turn that produced it: a new turn is a new transcript to
        // try, and it is what lets a session the sweep had skipped back in.
        var svc = NewService();
        svc.NoteReadFailed(TenantId.Local, "s", HostedAiState.Unavailable);
        svc.OnSessionWorking(TenantId.Local, "s");
        Assert.Null(svc.ReadFailedFor(TenantId.Local, "s"));
    }

    /// <summary>A voice service wired to a recording brain and a text-to-speech stub that returns
    /// <paramref name="audio"/>, so the full turn-end path (read the stored conversation -> translate ->
    /// synthesize -> store) runs without a live model or provider. <paramref name="conversationReader"/> is
    /// what the narration reads its words from; leaving it null is the honest "this Gateway has stored
    /// nothing" case, which is what the tests about waiting want.</summary>
    private WingmanVoiceService ServiceWithBrainAndTts(IAgentBrain brain, byte[] audio, string persistPath,
        Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?>? conversationReader = null,
        Func<TenantId, string, bool>? directorCannotSendConversation = null)
    {
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var http = new HttpClient(new TtsStubHandler(HttpStatusCode.OK, "", audio));
        return new WingmanVoiceService((_, _, _) => Task.FromResult(brain), vault, Settings, persistPath,
            ttsHttpClient: http, conversationReader: conversationReader,
            directorCannotSendConversation: directorCannotSendConversation);
    }

    /// <summary>Like <see cref="ServiceWithBrainAndTts"/> but with a caller-supplied speech transport, so a
    /// test can drive the full GenerateAsync path (model translation + speech) against a stateful handler.</summary>
    private WingmanVoiceService ServiceWithBrainAndHandler(IAgentBrain brain, HttpMessageHandler handler, string persistPath,
        Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?>? conversationReader = null)
    {
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var http = new HttpClient(handler);
        return new WingmanVoiceService((_, _, _) => Task.FromResult(brain), vault, Settings, persistPath,
            ttsHttpClient: http, conversationReader: conversationReader);
    }

    /// <summary>Gateway Cleanup mission (the cut): GenerateAsync takes a tunnel-only SessionVerbClient. This
    /// binds one to the stub's sendCommand, which is now the transport for the LIVE SCREEN read alone - the
    /// conversation itself comes from the stored-conversation reader, not from the tunnel.</summary>
    private static CcDirector.Gateway.Api.SessionVerbClient RouteFor(TunnelStub stub) =>
        new(new CcDirector.Gateway.Contracts.DirectorDto { DirectorId = "d1", ControlEndpoint = "http://tunnel-only" },
            stub.SendCommand);

    [Fact]
    public async Task GenerateAsync_WhenCurrentReplyDiffersFromCache_Regenerates()
    {
        // THE FIX (regression for the #1322 bare-HasVoice guard): when the session's CURRENT last reply
        // differs from the one already narrated, the turn-end MUST regenerate - even though a cached clip
        // exists. The old guard skipped here whenever the Working transition was missed, leaving the
        // phone replaying a stale interim narration while the history had moved on to the real answer.
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the NEW final answer"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-regen-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 4, 4, 4 }, persistPath, conversation.Reader);
            svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "old spoken", "the OLD interim reply", new byte[] { 1, 2, 3 });
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: false);

            Assert.Equal(1, brain.AskCount);                          // it regenerated (translated the new reply)
            var ready = svc.Get(TenantId.Local, "sid-1");
            Assert.NotNull(ready);
            Assert.Equal("the NEW final answer", ready!.Reply);       // the cache now holds the CURRENT reply
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public async Task GenerateAsync_WhenCurrentReplyMatchesCache_SkipsQuietly()
    {
        // Issue #1322 preserved: when the CURRENT last reply is the EXACT one already narrated, the
        // turn-end reads the conversation to compare but does NOT regenerate - it never calls the brain,
        // never re-mints audio, and never flips the session yellow, so a client mid-play is not disturbed.
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the same reply"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-same-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 9, 9 }, persistPath, conversation.Reader);
            svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "old spoken", "the same reply", new byte[] { 1, 2, 3 });
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(0, brain.AskCount);              // never regenerated
            Assert.True(conversation.Reads >= 1);         // but it DID read the conversation to compare (identity-aware, not blind)
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));           // the existing clip is untouched
            Assert.False(svc.IsGenerating(TenantId.Local, "sid-1"));      // and it never flipped the session yellow
            Assert.Equal(new byte[] { 1, 2, 3 }, svc.GetAudio(TenantId.Local, "sid-1"));   // same original audio, not re-minted
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    // ---------- ShouldRegenerate decision (pure, no brain / no fetch) ----------

    [Fact]
    public void ShouldRegenerate_NoCachedNarration_IsTrue()
    {
        var svc = NewService();
        Assert.True(svc.ShouldRegenerate(TenantId.Local, "sid-x", "a reply to narrate"));
    }

    [Fact]
    public void ShouldRegenerate_EmptyOrNullCurrentReply_IsFalse()
    {
        // Nothing to narrate yet - do not touch or regenerate.
        var svc = NewService();
        Assert.False(svc.ShouldRegenerate(TenantId.Local, "sid-x", null));
        Assert.False(svc.ShouldRegenerate(TenantId.Local, "sid-x", "   "));
    }

    [Fact]
    public void ShouldRegenerate_SameReplyAlreadyCached_IsFalse()
    {
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "the reply text", new byte[] { 1 });
        Assert.False(svc.ShouldRegenerate(TenantId.Local, "sid-1", "the reply text"));
    }

    [Fact]
    public void ShouldRegenerate_SameReplyIgnoringSurroundingWhitespace_IsFalse()
    {
        // The two sources are the same JSONL text block; incidental leading/trailing whitespace must
        // not force a needless re-mint (which would restart a listener's clip).
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "the reply text", new byte[] { 1 });
        Assert.False(svc.ShouldRegenerate(TenantId.Local, "sid-1", "  the reply text\n"));
    }

    [Fact]
    public void ShouldRegenerate_ChangedReply_IsTrue()
    {
        // The exact bug: an interim reply was narrated, then the real answer landed. A changed reply
        // must regenerate even though a cached clip exists.
        var svc = NewService();
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "the interim reply", new byte[] { 1 });
        Assert.True(svc.ShouldRegenerate(TenantId.Local, "sid-1", "the FINAL answer"));
    }

    // ---------- Turn voice off / Unmark (issue #859) ----------

    [Fact]
    public void Unmark_AfterMark_RemovesFromVoiceSessionSet()
    {
        // Turning voice off stops the session being a voice session, so the turn-end watcher and the
        // background sweep (both gate on IsVoiceSession / VoiceSessionIds) skip it - no more per-turn
        // Opus + text-to-speech spend.
        var svc = NewService();
        svc.Mark(TenantId.Local, "sid-1");
        svc.Mark(TenantId.Local, "sid-2");
        Assert.True(svc.IsVoiceSession(TenantId.Local, "sid-1"));

        svc.Unmark(TenantId.Local, "sid-1");

        Assert.False(svc.IsVoiceSession(TenantId.Local, "sid-1"));
        Assert.DoesNotContain("sid-1", svc.VoiceSessionIds(TenantId.Local));
        // Independent per session: a second voice session is unaffected.
        Assert.True(svc.IsVoiceSession(TenantId.Local, "sid-2"));
        Assert.Contains("sid-2", svc.VoiceSessionIds(TenantId.Local));
    }

    [Fact]
    public void Unmark_DropsTheReadyClip()
    {
        // After unmark, GET /wingman/voice/ready (ReadySessionIds) must no longer list the session,
        // so the roster/phone stop offering a stale clip.
        var svc = NewService();
        svc.Mark(TenantId.Local, "sid-1");
        svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 1, 2, 3 });
        Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));

        svc.Unmark(TenantId.Local, "sid-1");

        Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
        Assert.DoesNotContain("sid-1", svc.ReadySessionIds(TenantId.Local));
    }

    [Fact]
    public void Unmark_PersistsAcrossRestart()
    {
        // The removal is durable: a gateway restart must NOT bring the session back as a voice
        // session (otherwise turn-end re-narration would resume on its own after a restart).
        // ONE root, shared DELIBERATELY by the two service instances below: this test is the gateway-restart
        // case, and a second instance that could not see what the first wrote would not be testing anything.
        var persistPath = TempPersist();
        try
        {
            var svc = ServiceAt(persistPath);
            svc.Mark(TenantId.Local, "sid-1");
            svc.StoreReadyAudioForTest(TenantId.Local, "sid-1", "spoken", "reply", new byte[] { 7, 7, 7 });
            Assert.True(svc.IsVoiceSession(TenantId.Local, "sid-1"));

            svc.Unmark(TenantId.Local, "sid-1");

            // Simulate a gateway restart over the same persist path.
            var reloaded = ServiceAt(persistPath);
            Assert.False(reloaded.IsVoiceSession(TenantId.Local, "sid-1"));
            Assert.DoesNotContain("sid-1", reloaded.VoiceSessionIds(TenantId.Local));
            Assert.False(reloaded.HasVoice(TenantId.Local, "sid-1")); // and the durable clip is gone too
        }
        finally { Cleanup(persistPath); }
    }

    [Fact]
    public void Unmark_UnknownSession_IsNoOp()
    {
        // Idempotent: unmarking a session that was never a voice session does nothing and does not throw.
        var svc = NewService();
        svc.Unmark(TenantId.Local, "never-marked");
        Assert.False(svc.IsVoiceSession(TenantId.Local, "never-marked"));
    }

    // ---------- Voice-unavailable state (issue #939): no more silent turn-end failures ----------

    /// <summary>A stub text-to-speech transport: returns the given status + body, or audio bytes on
    /// success. Lets a test drive TtsAsync to a 402 / success without a live provider call.</summary>
    private sealed class TtsStubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly byte[]? _audio;
        public TtsStubHandler(HttpStatusCode status, string body, byte[]? audio = null) { _status = status; _body = body; _audio = audio; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(_status)
            {
                Content = _audio is not null
                    ? new ByteArrayContent(_audio)
                    : new StringContent(_body, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }

    /// <summary>Regression transport for the shared-gate removal: the FIRST speech call fails 5xx (which
    /// armed the old fleet-wide cooldown), and every later call signals it was reached and then BLOCKS on
    /// <see cref="Release"/> before returning audio. Holding a call in-flight lets a test pin the one call
    /// the old gate would have let through (the probe) so that a concurrent second session's skip, if the
    /// gate still existed, is observed deterministically instead of on a timer. <see cref="Entered"/> counts
    /// the later (success-path) calls that actually reached the provider.</summary>
    private sealed class FirstFails5xxThenBlockingSuccessHandler : HttpMessageHandler
    {
        private readonly byte[] _audio;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        private int _entered;
        public FirstFails5xxThenBlockingSuccessHandler(byte[] audio) => _audio = audio;
        public int Entered => Volatile.Read(ref _entered);
        public void Release() => _release.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n == 1)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                { Content = new StringContent("{\"error\":\"upstream\"}", Encoding.UTF8, "application/json") };
            Interlocked.Increment(ref _entered);
            await _release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_audio) };
        }
    }

    /// <summary>A voice service whose text-to-speech goes to a stub returning <paramref name="status"/>.
    /// Both provider keys are set so the call proceeds regardless of the machine's configured mode -
    /// the stub ignores the URL, so the mapped state depends only on the response.</summary>
    private WingmanVoiceService ServiceWithTts(HttpStatusCode status, string body, byte[]? audio = null)
    {
        Func<TenantId, WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _) => throw new InvalidOperationException("brain must not be called for the store-spoken path");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        // A fresh root per CALL. Each invocation of this helper builds an independent service, so they must
        // not share state; the restart tests get their sharing by calling TempPersist() once themselves.
        var persistPath = TempPersist();
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var http = new HttpClient(new TtsStubHandler(status, body, audio));
        return new WingmanVoiceService(brain, vault, Settings, persistPath, ttsHttpClient: http);
    }

    [Fact]
    public void VoiceUnavailableFor_DefaultsNull()
    {
        var svc = NewService();
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
    }

    [Fact]
    public async Task StoreSpokenAsync_OutOfCredits402_RecordsNeedsCredits_NoSilentFailure()
    {
        // Issue #939: a 402 out-of-credits at turn-end must no longer be swallowed - it records the
        // shared NeedsCredits state (and leaves no play triangle).
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "a spoken summary", "the reply");

        Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
        Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
    }

    [Fact]
    public async Task StoreSpokenAsync_MonthlyLimit402_RecordsCapReached()
    {
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"monthly_limit_reached\"}}");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "a spoken summary", "the reply");

        Assert.Equal(HostedAiState.CapReached, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
    }

    [Fact]
    public async Task StoreSpokenAsync_Success_MarksReady_AndClearsUnavailable()
    {
        // A successful synthesis marks the session ready AND clears any prior unavailable-state
        // (dismissible: the next good turn removes the banner).
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "spoken", "reply");
        Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));

        var good = ServiceWithTts(HttpStatusCode.OK, "", audio: new byte[] { 1, 2, 3, 4 });
        // Re-run on the SAME service would need a mutable stub; instead prove success on a fresh call
        // clears + marks ready. Seed the unavailable state first via a failing service is covered above;
        // here assert the success path's postconditions directly.
        await good.StoreSpokenAsync(TenantId.Local, "sid-2", "spoken", "reply");
        Assert.True(good.HasVoice(TenantId.Local, "sid-2"));
        Assert.Null(good.VoiceUnavailableFor(TenantId.Local, "sid-2"));
    }

    /// <summary>A text-to-speech transport that throws <see cref="OperationCanceledException"/> - the
    /// shape a per-attempt timeout takes - for the first <c>_timeouts</c> calls, then returns 200 with
    /// audio. TtsSynthesis converts each such throw (while the caller's token is live) into a retry, so
    /// this drives the retry path without a real 15-second wait. With <see cref="int.MaxValue"/> it
    /// always times out.</summary>
    private sealed class TtsTimeoutHandler : HttpMessageHandler
    {
        private readonly int _timeouts;
        private int _calls;
        public int Calls => _calls;
        public TtsTimeoutHandler(int timeouts) { _timeouts = timeouts; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n <= _timeouts)
                throw new OperationCanceledException("simulated per-attempt timeout");
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 9, 9, 9 }),
            };
            return Task.FromResult(resp);
        }
    }

    private WingmanVoiceService ServiceWithHandler(HttpMessageHandler handler)
    {
        Func<TenantId, WingmanModelRole, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _) => throw new InvalidOperationException("brain must not be called for the store-spoken path");
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        // A fresh root per CALL. Each invocation of this helper builds an independent service, so they must
        // not share state; the restart tests get their sharing by calling TempPersist() once themselves.
        var persistPath = TempPersist();
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        return new WingmanVoiceService(brain, vault, Settings, persistPath, ttsHttpClient: new HttpClient(handler));
    }

    [Fact]
    public async Task StoreSpokenAsync_AStalledCall_RecoversOnTheSessionsNextAttempt_NotByRetryingInside()
    {
        // This test used to assert the IN-CALL retry (attempt 1 stalls, attempt 2 returns audio, one
        // call to StoreSpokenAsync produces voice). That retry is gone, deliberately, and this now pins
        // the recovery that actually works.
        //
        // The in-call retry never did its job in production. It assumed attempt 1 eats the cold start
        // and attempt 2 lands warm; the live log says otherwise, every time:
        //     attempt 1/2 timed out after 33s (709 chars); retrying
        //     attempt 2/2 timed out after 33s (709 chars); giving up
        // Cancelling attempt 1 plausibly cancels the model load it just triggered, so attempt 2 starts
        // the cold start again rather than arriving after it. It bought a doubled wait and a doubled
        // load on a struggling provider, for the same failure - and on /wingman/tts, where a human is
        // waiting, it doubled the worst case to two minutes.
        //
        // Recovery belongs one level up, and this is the owner's design in one line: the call either
        // works or it does not; if it does not, THAT SESSION waits and tries again, and nobody else is
        // affected. The voice sweep re-attempts any session without audio, so the second attempt here is
        // what the sweep does seconds later.
        var handler = new TtsTimeoutHandler(timeouts: 1);
        var svc = ServiceWithHandler(handler);

        // The stalled call fails - one attempt, bounded, and it says so honestly. A timeout is the
        // absence of an answer, so it is Retrying ("audio on its way, trying again"), NOT ServiceDown.
        await svc.StoreSpokenAsync(TenantId.Local, "sid-retry", "spoken", "reply");
        Assert.Equal(1, handler.Calls);                                        // exactly one attempt: no in-call retry
        Assert.False(svc.HasVoice(TenantId.Local, "sid-retry"));                               // nothing to play
        Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-retry"));

        // The session tries again - the provider is fine now, and it just works.
        await svc.StoreSpokenAsync(TenantId.Local, "sid-retry", "spoken", "reply");
        Assert.Equal(2, handler.Calls);
        Assert.True(svc.HasVoice(TenantId.Local, "sid-retry"));
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-retry"));
    }

    [Fact]
    public async Task StoreSpokenAsync_TtsTimesOutEveryAttempt_GivesUpBounded_NoReady()
    {
        // Regression: when every attempt stalls, TtsSynthesis gives up after a BOUNDED number of
        // attempts (no infinite spin, no 60-second freeze) and the turn-end records no audio.
        var handler = new TtsTimeoutHandler(timeouts: int.MaxValue);
        var svc = ServiceWithHandler(handler);
        await svc.StoreSpokenAsync(TenantId.Local, "sid-dead", "spoken", "reply");

        Assert.Equal(TtsSynthesis.Attempts, handler.Calls);    // exactly the attempt cap, then stop
        Assert.False(svc.HasVoice(TenantId.Local, "sid-dead"));
    }

    // ---- The 2026-07-15 outage: the service failed for ~45 minutes and the phone blamed the user's own
    // machine ("the Gateway has not made one, or this session's computer is offline"). Both false. The
    // reason was KNOWN here and discarded three lines from where it was known, because no state meant
    // "the service is down". These pin the whole path: an ANSWERED failure becomes ServiceDown, that
    // state carries copy the phone can render, and it survives to the DTO the phone actually reads. A
    // call that never answered (a timeout) is Retrying, not ServiceDown - the split proven below.

    [Theory]
    [InlineData(500)]   // provider blew up
    [InlineData(502)]   // our cloud could not reach it
    [InlineData(503)]   // upstream unavailable
    [InlineData(504)]   // upstream timed out / fast-failed
    public async Task StoreSpokenAsync_TtsFails_RecordsServiceDown_NotSilence(int status)
    {
        // Every one of these used to return a bare null ("other provider error: logged, no shared
        // state"), so the session recorded NOTHING and the phone invented a cause.
        var svc = ServiceWithTts((HttpStatusCode)status, "{\"error\":\"upstream\"}");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-down", "spoken", "reply");

        Assert.False(svc.HasVoice(TenantId.Local, "sid-down"));
        Assert.Equal(HostedAiState.ServiceDown, svc.VoiceUnavailableFor(TenantId.Local, "sid-down"));
    }

    [Fact]
    public async Task StoreSpokenAsync_TtsTimesOutEveryAttempt_RecordsRetrying_NotServiceDown()
    {
        // The TimeoutException that TtsSynthesis exists to bound was the ONE failure that stamped
        // nothing at all - swallowed by a bare catch. It must stamp SOMETHING (so the phone is not left
        // guessing), but that something is Retrying, NOT ServiceDown: a call that never answered is the
        // absence of evidence about the service. Stamping ServiceDown here made the phone say "Voice
        // service down" on a single slow call - a claim we cannot support - which is the wording bug
        // this test now guards against.
        var handler = new TtsTimeoutHandler(timeouts: int.MaxValue);
        var svc = ServiceWithHandler(handler);
        await svc.StoreSpokenAsync(TenantId.Local, "sid-timeout", "spoken", "reply");

        Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-timeout"));
        Assert.NotEqual(HostedAiState.ServiceDown, svc.VoiceUnavailableFor(TenantId.Local, "sid-timeout"));
    }

    // ONE SLOW NARRATION MUST NOT SILENCE THE FLEET - AND NOW IT CANNOT, BY CONSTRUCTION.
    //
    // There is no shared cooldown gate any more (removed 2026-07-17). Each session calls the hosted relay
    // on its own and records only its OWN state; there is no cross-session mechanism left for one call's
    // failure to reach another. These tests pin that each failing call reports its own honest state and
    // nothing more - the fleet-wide silencing that a shared gate could cause is now structurally absent.
    //
    // Measured against the live fleet on 2026-07-15 (why the gate had to go): the speech endpoint answered
    // an 871-character narration in 1.7 seconds from the Gateway's own machine, while the Gateway logged
    // 1031 attempt-1 timeouts and 759 give-ups and NOT ONE of 17 sessions held audio - yet asking on
    // demand produced real audio for 13 of 13 reachable sessions. The service was never down; the shared
    // gate was, armed by evidence that never justified it.
    [Fact]
    public async Task StoreSpokenAsync_OneTimeout_RecordsOnlyItsOwnRetryingState()
    {
        // One timeout is not evidence about the SERVICE - it can be one long narration or one stalled
        // worker. The session reports its own state (Retrying - nothing to play yet, trying again) and
        // touches nothing else; every other session is entirely unaffected because nothing couples them.
        var handler = new TtsTimeoutHandler(timeouts: TtsSynthesis.Attempts);
        var svc = ServiceWithHandler(handler);

        await svc.StoreSpokenAsync(TenantId.Local, "sid-slow", "spoken", "reply");

        Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-slow"));
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-other"));   // a session that never called is untouched
    }

    [Fact]
    public async Task StoreSpokenAsync_ManyTimeouts_EachSessionKeepsOnlyItsOwnState()
    {
        // A RUN of timeouts is not evidence stacking toward "the service is down" - the timeouts are not
        // independent, they are the same slow provider. With the shared gate gone, a run of them simply
        // leaves each session with its own Retrying state and nothing crosses between them.
        //
        // A timeout is the ABSENCE of evidence: the service said nothing, so we learned nothing about it.
        // Each session retries on its own (the voice sweep revisits any session without audio). Load is
        // bounded only by the provider's own 429 backoff (there is no fixed fleet-wide concurrency cap).
        var handler = new TtsTimeoutHandler(timeouts: int.MaxValue);
        var svc = ServiceWithHandler(handler);

        await svc.StoreSpokenAsync(TenantId.Local, "sid-a", "spoken", "reply");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-b", "spoken", "reply");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-c", "spoken", "reply");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-d", "spoken", "reply");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-e", "spoken", "reply");

        // Each session tells the truth about ITSELF: nothing to play yet, retrying - Retrying, never
        // ServiceDown, because none of these calls got an answer from the service.
        Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-a"));
        Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-e"));
    }

    [Fact]
    public async Task GenerateAsync_OneSessions5xx_DoesNotSilenceAConcurrentSessionsVoice()
    {
        // THE regression for this whole change (2026-07-17), and it FAILS on the pre-change gated code.
        //
        // Old shape: a 5xx armed a shared fleet-wide speech cooldown, after which GenerateAsync let exactly
        // ONE session through (the half-open probe) and SKIPPED every other concurrent session with no
        // audio. Session A gets a 5xx here (which armed that cooldown on the old code), then B and C run
        // CONCURRENTLY: on the old code one of them is the probe and the OTHER is skipped voiceless; on the
        // new code there is no gate, so BOTH reach the provider and BOTH get audio. The probe is held
        // in-flight (the handler blocks), so the skip - if the gate still existed - is observed
        // deterministically, not on a timer.
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-nogate-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var handler = new FirstFails5xxThenBlockingSuccessHandler(new byte[] { 5, 5, 5 });
            var svc = ServiceWithBrainAndHandler(new RecordingBrain(), handler, persistPath, conversation.Reader);

            // A fails 5xx first - on the OLD code this armed the shared cooldown before B/C ever ask.
            await svc.GenerateAsync(TenantId.Local, "sid-A", RouteFor(director), CancellationToken.None, showReadingWindow: false);
            Assert.False(svc.HasVoice(TenantId.Local, "sid-A"));
            Assert.Equal(HostedAiState.ServiceDown, svc.VoiceUnavailableFor(TenantId.Local, "sid-A"));

            // B and C race. Their success-path speech call blocks in the handler, so the ONE the old gate
            // would let through sits in-flight and cannot free the probe slot - making the old code's skip
            // of the other deterministic rather than timing-dependent.
            var b = svc.GenerateAsync(TenantId.Local, "sid-B", RouteFor(director), CancellationToken.None, showReadingWindow: false);
            var c = svc.GenerateAsync(TenantId.Local, "sid-C", RouteFor(director), CancellationToken.None, showReadingWindow: false);

            // Wait - without a fixed sleep - until EITHER both reached the provider (new code) OR one of
            // them returned early having been skipped (old code). Bounded by an overall deadline so a hang
            // fails loudly instead of stalling.
            var overall = Task.Delay(TimeSpan.FromSeconds(30));
            while (handler.Entered < 2 && !b.IsCompleted && !c.IsCompleted)
            {
                if (await Task.WhenAny(Task.Delay(10), overall) == overall)
                    throw new TimeoutException("neither the second provider call nor an early skip was observed");
            }

            handler.Release();     // let the blocked success call(s) finish
            await Task.WhenAll(b, c);

            // The whole point: NO session's voice was collateral damage to another session's 5xx. Both
            // reached the provider and both are playable. On the pre-change gated code exactly one of these
            // is false, so this pair of assertions IS the regression.
            Assert.True(svc.HasVoice(TenantId.Local, "sid-B"), "session B's voice must not be gated by session A's failure");
            Assert.True(svc.HasVoice(TenantId.Local, "sid-C"), "session C's voice must not be gated by session A's failure");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    [Fact]
    public async Task StoreSpokenAsync_TtsOutOfCredits_StillBlamesTheAccount_NotTheService()
    {
        // The control. 402 is the user's to fix, so it must NOT be swept into ServiceDown - telling
        // someone "not your fault, retrying" when they are out of credit would strand them forever.
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-402", "spoken", "reply");

        var state = svc.VoiceUnavailableFor(TenantId.Local, "sid-402");
        Assert.NotNull(state);
        Assert.NotEqual(HostedAiState.ServiceDown, state);
    }

    [Fact]
    public async Task ServiceDown_HasCopyThePhoneCanRender_WithNoButton()
    {
        // The field was DEAD ON ARRIVAL for months: stamped on every 3s poll with zero readers, while
        // two views hardcoded a false string. Recording the state is only half - it has to arrive as
        // something renderable. And it must offer NO call to action: during an outage a button hits the
        // same dead service and fails the same way, which is what made the owner blame himself.
        var dto = HostedAiHttp.Dto(HostedAiState.ServiceDown);

        Assert.NotNull(dto);
        Assert.False(string.IsNullOrWhiteSpace(dto!.Text));
        Assert.True(string.IsNullOrEmpty(dto.CtaLabel), "a service outage must offer no button - it cannot work");
        Assert.Equal(nameof(HostedAiState.ServiceDown), dto.State);
    }

    [Fact]
    public async Task StoreSpokenAsync_TtsRecovers_ClearsServiceDown()
    {
        // Voice must come back BY ITSELF when the service returns (the idle sweep regenerates), so a
        // success has to clear the state - otherwise the phone would keep saying "down" after recovery.
        var svc = ServiceWithTts(HttpStatusCode.OK, "", audio: new byte[] { 1, 2, 3, 4 });
        await svc.StoreSpokenAsync(TenantId.Local, "sid-back", "spoken", "reply");

        Assert.True(svc.HasVoice(TenantId.Local, "sid-back"));
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-back"));
    }

    [Fact]
    public async Task OnSessionWorking_ClearsVoiceUnavailable()
    {
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "spoken", "reply");
        Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));

        svc.OnSessionWorking(TenantId.Local, "sid-1");
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
    }

    [Fact]
    public async Task Unmark_ClearsVoiceUnavailable()
    {
        var svc = ServiceWithTts(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
        await svc.StoreSpokenAsync(TenantId.Local, "sid-1", "spoken", "reply");
        Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));

        svc.Unmark(TenantId.Local, "sid-1");
        Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
    }

    /// <summary>
    /// Wait for the ready-audio cache to finish loading before handing the service to a test. The cache is
    /// read in the BACKGROUND in production so its cost cannot sit in front of the port bind (issue #2203);
    /// a test that asserts on reloaded audio would otherwise be racing that read. Nothing in the serving
    /// path waits like this - a cache still loading behaves as a miss and regenerates.
    /// </summary>
    private static WingmanVoiceService Warmed(WingmanVoiceService svc)
    {
        Assert.True(svc.ReadyAudioWarmup.Wait(TimeSpan.FromSeconds(30)),
            "the ready-audio warm load did not finish within 30 seconds");
        return svc;
    }

    /// <summary>
    /// THE 2026-09-02 WEDGE. Turn-push phases 3a/3b went live on the hosted Gateway while the newest
    /// RELEASED Director predated the pusher by a fortnight, so the store never filled for anybody. The arm
    /// above reads an empty store as "the Director has not pushed YET" and comes back on the next sweep -
    /// correct for a Director that HAS the pusher, and a promise that never ends for one that does not.
    /// Eleven of the owner's twenty-one sessions sat yellow reading "Voice did not arrive after 3m..22m"
    /// while those sessions had actually earned a red "Needs you" he could no longer see.
    ///
    /// So the service must record the fact the fold turns into "Update DevThrottle", and it must do so
    /// WITHOUT spending anything and WITHOUT standing the sweep down - re-checking is free, and it is what
    /// makes the recovery automatic the moment that machine is updated.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenTheOwningDirectorCannotSendConversations_SaysSoInsteadOfWaitingForever()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.NothingStored();
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-tooold-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 1 }, persistPath, conversation.Reader,
                directorCannotSendConversation: (_, _) => true);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(svc.DirectorCannotSendConversationFor(TenantId.Local, "sid-1"));   // the fact the screen renders
            Assert.Equal(0, brain.AskCount);                                               // nothing translated, nothing spent
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
            // NOT a read failure. NoteReadFailed(Unavailable) would render through the hosted-AI arm and say
            // "Voice unavailable" in the shared account-condition voice, for something that is neither the
            // account's fault nor anything to do with hosted AI.
            Assert.Null(svc.ReadFailedFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            // NOT "waiting on a prompt" either - nobody here has read a conversation to know that.
            Assert.False(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));
            // And the sweep keeps coming back, so updating that machine restores narration with nobody
            // touching the Gateway. A skip here would make the recovery need a restart.
            Assert.False(svc.ShouldSkipSweep(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The marker describes a MACHINE, and machines get updated. Once a conversation actually arrives that
    /// computer has demonstrated it can send one - whatever it last said about itself on Hello, and whoever
    /// owns the session by then - so the sentence must come off the screen by itself.
    /// </summary>
    [Fact]
    public async Task DirectorCannotSendMarker_ClearsAsSoonAsAConversationArrives()
    {
        var director = new TunnelStub();
        var empty = StoredConversationStub.NothingStored();
        var full = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var updated = false;
        Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?> reader =
            (t, sid) => updated ? full.Reader(t, sid) : empty.Reader(t, sid);
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-tooold-clear-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var svc = ServiceWithBrainAndTts(new RecordingBrain(), new byte[] { 1 }, persistPath, reader,
                directorCannotSendConversation: (_, _) => true);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.True(svc.DirectorCannotSendConversationFor(TenantId.Local, "sid-1"));   // control: it was set

            updated = true;   // that computer was updated and pushed its conversation
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(full.Reads >= 1);
            Assert.False(svc.DirectorCannotSendConversationFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // ---------- The model leg's OWN bounded retry, and the end of the promise (issue #2676) ----------

    /// <summary>
    /// A brain that does not answer for its first <c>failures</c> asks and answers normally after that -
    /// the shape of a model stall that clears, which is what a bounded retry is for. Counts its asks so a
    /// test can prove a SECOND attempt was made rather than inferring it from the audio.
    /// </summary>
    private sealed class StallsThenAnswersBrain : IAgentBrain
    {
        private readonly int _failures;
        private int _askCount;
        public StallsThenAnswersBrain(int failures) => _failures = failures;
        public int AskCount => _askCount;
        public string? SessionId => "stalls-then-answers-brain";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _askCount);
            if (n <= _failures) throw new TimeoutException("The wingman model call did not answer within 60 seconds.");
            var wrapped = $"{SessionAskRunner.AnswerBeginMarker}\nnarrated spoken text\n{SessionAskRunner.AnswerEndMarker}";
            return Task.FromResult(new AskResult { Text = wrapped, ReplySeconds = 0.1 });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    /// <summary>Poll a condition rather than sleeping a fixed time: the re-attempt runs on the thread pool
    /// after its backoff, so a fixed wait would be either flaky or slow. Returns the condition's final value
    /// on the deadline so the caller's own assertion reports what was actually missing.</summary>
    private static async Task<bool> Eventually(Func<bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    /// <summary>
    /// THE 2026-09-04 WEDGE. A model-leg timeout recorded Retrying and logged "the session retries on its
    /// own". Nothing retried it. The only mechanism that would ever come back was the shared voice sweep,
    /// whose whole per-cycle budget another account's unnarratable sessions were consuming (issue #2675), so
    /// in practice the turn was never narrated: three sessions sat yellow for eleven minutes, eighteen
    /// minutes, and until they were snoozed, each showing "retrying automatically, it should come through
    /// shortly" with nothing scheduled anywhere.
    ///
    /// So the leg that failed books the re-attempt itself. This proves the whole promise end to end: a
    /// stall that clears produces AUDIO, without a sweep, without a new turn, and without anybody pressing
    /// anything.
    ///
    /// REVERT-PROOF: put the old body back in the IsModelDidNotAnswer catch (record Retrying, return) and
    /// this goes RED on the "never led to another attempt" assertion. Confirmed against the pre-fix code.
    /// </summary>
    [Fact]
    public async Task WhenTheModelDoesNotAnswer_TheVoicePathBooksItsOwnReattempt_AndTheTurnIsNarrated()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelretry-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new StallsThenAnswersBrain(failures: 1);
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 5, 5, 5 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            // CONTROL, and the exact state the old code stopped at: one attempt, no audio, calm "on its way".
            Assert.Equal(1, brain.AskCount);
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
            Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            // ...and the promise is honest here, because an attempt really is booked.
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));

            // THE THING THAT DID NOT EXIST: a second attempt, made by the voice path, on its own.
            Assert.True(await Eventually(() => svc.HasVoice(TenantId.Local, "sid-1")),
                "the model timeout never led to another attempt - the turn stayed silent");
            Assert.Equal(2, brain.AskCount);

            // A narration arrived, so nothing is owed and nothing was abandoned.
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The retry is BOUNDED, and where it runs out the screen stops promising. "Voice is taking a moment -
    /// retrying automatically. It should come through shortly" said about a turn no loop will touch again is
    /// an absence dressed as progress; the honest answer is that the turn was not narrated.
    /// </summary>
    [Fact]
    public async Task WhenEveryReattemptIsSpent_TheNarrationIsAbandoned_AndTheScreenStopsPromisingAudio()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelgiveup-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new TimingOutBrain();   // never answers, so every re-attempt is spent
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 5 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(await Eventually(() => svc.NarrationAbandonedFor(TenantId.Local, "sid-1")),
                "the retry budget never ran out, so nothing ever told the reader the turn was not narrated");
            // The cap held: the first attempt plus exactly the two booked re-attempts, and no more.
            //
            // SETTLE BEFORE COUNTING. Asserting the moment the verdict appears would also pass if a fourth
            // re-attempt were still sitting on a timer - the count would simply be read before it fired, and
            // a runaway ladder would look identical to a capped one (found in review). The wait is many
            // times the 30ms backoff, so anything booked would have run by now.
            await Task.Delay(500);
            Assert.Equal(3, brain.AskCount);
            Assert.True(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // and it stayed abandoned
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));

            // AND THE SCREEN SAYS SO. The fold is the only thing a client renders, so the state is honest
            // only if the fold turns it into an honest sentence.
            var display = VoiceDisplayFold.Fold(
                voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
                unavailable: svc.VoiceUnavailableFor(TenantId.Local, "sid-1"),
                nothingToNarrate: false,
                narrationAbandoned: svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            Assert.Equal("notNarrated", display.Kind);
            Assert.DoesNotContain("on its way", display.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("shortly", display.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The budget belongs to the TURN, not to the session. A new turn arriving after an abandoned one must
    /// start with a full set of re-attempts and a clean screen - otherwise one stalled turn would spend the
    /// next turn's retries, and "not narrated" would sit on a narration nobody had tried yet.
    /// </summary>
    [Fact]
    public async Task ANewTurn_ClearsTheAbandonedVerdict_AndGivesTheTurnItsOwnRetryBudget()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelreset-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new TimingOutBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 5 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(30));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.True(await Eventually(() => svc.NarrationAbandonedFor(TenantId.Local, "sid-1")));   // control
            var asksBefore = brain.AskCount;

            svc.OnSessionWorking(TenantId.Local, "sid-1");   // the agent starts a new turn

            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));

            // The new turn gets its own attempt AND its own re-attempt - the budget was genuinely reset,
            // not merely hidden from the screen. Pinned EXACTLY rather than as a lower bound: ">= 2 more"
            // is also satisfied by a ladder that has stopped respecting its cap (found in review).
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.True(await Eventually(() => brain.AskCount >= asksBefore + 2),
                "the new turn did not get its own re-attempt - the old turn's spent budget carried over");
            await Task.Delay(500);
            Assert.Equal(asksBefore + 2, brain.AskCount);   // one attempt, one re-attempt, and no more
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>Switching voice off must not leave a re-attempt booked against a session nobody wants
    /// narrated, and must not leave "not narrated" standing on it either.</summary>
    [Fact]
    public async Task Unmark_ClearsTheAbandonedVerdict()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelunmark-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var svc = ServiceWithBrainAndTts(new TimingOutBrain(), new byte[] { 5 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest();   // no re-attempts at all: the first non-answer is the last

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.True(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // control: with no budget, immediate

            svc.Unmark(TenantId.Local, "sid-1");

            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// A re-attempt booked for the PREVIOUS turn must stand down, not narrate.
    ///
    /// Found in review, and the harm is specific rather than theoretical. A retry that woke up during the
    /// next turn could win the per-session coalescing race against that turn's own narration, make it return
    /// having done nothing, and then store the OLD turn's clip. The session would then HAVE audio, so the
    /// sweep - which skips any session with audio - would never come back to it, and the phone would play a
    /// stale narration of the wrong turn for as long as the session lived.
    /// </summary>
    [Fact]
    public async Task AReattemptBookedForASupersededTurn_StandsDown_InsteadOfNarratingTheOldTurn()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelsuperseded-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new TimingOutBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 5 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(400));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.Equal(1, brain.AskCount);                                   // control: the attempt happened
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));  // control: a re-attempt is booked

            // A new turn starts while that re-attempt is still waiting out its backoff.
            svc.OnSessionWorking(TenantId.Local, "sid-1");

            // It must never run. Waited well past its backoff so this is a real absence, not an early read.
            await Task.Delay(900);
            Assert.Equal(1, brain.AskCount);
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));               // no stale clip was stored
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));  // and no verdict about the old turn
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// A re-attempt never turns voice back ON by attempting. It wakes up as much as a minute and a half
    /// after it was booked, and voice may have been switched off in between; every other caller of
    /// GenerateAsync marks the session as a side effect, which for a retry would mean the Gateway resuming a
    /// narration for a session whose owner had just stopped one (found in review).
    ///
    /// WHAT THIS DOES NOT CLAIM, stated because the narrower truth is the useful one. A retry that goes on
    /// to SUCCEED still marks the session, because StoreSpokenAsync marks on the success path for every
    /// caller - that is shared, pre-existing behaviour, identical for an ordinary turn-end narration, and
    /// its window is the length of one generation rather than the length of a backoff. What is closed here
    /// is the wide window: the ninety seconds a booked re-attempt spends waiting, during which it must be
    /// able to wake up and do nothing at all.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_OnTheRetryPath_DoesNotMakeASessionAVoiceSessionByAttempting()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelnomark-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            // A brain that never answers, so this is purely about the ATTEMPT: no synthesis, no store, and
            // therefore nothing but the entry-point marking under test.
            var svc = ServiceWithBrainAndTts(new TimingOutBrain(), new byte[] { 5 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest();   // no rungs, so the attempt does not book more work
            Assert.False(svc.IsVoiceSession(TenantId.Local, "sid-1"));   // control: it is not one to start with

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None,
                showReadingWindow: false, markAsVoiceSession: false);

            Assert.False(svc.IsVoiceSession(TenantId.Local, "sid-1"));   // still not one

            // POSITIVE CONTROL: the ordinary path DOES mark on the very same failing attempt, so the
            // assertion above is about the flag and not about a call that quietly did nothing.
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: false);
            Assert.True(svc.IsVoiceSession(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// A CONCURRENT FAILURE SPENDS NOTHING AND CLAIMS NOTHING while a re-attempt is booked.
    ///
    /// A concurrent attempt - the background sweep, or a person pressing Generate - can fail while a booked
    /// re-attempt is still waiting out its backoff. Two things must not happen: it must not push the count
    /// past the cap and declare the turn abandoned while a task genuinely exists, and it must not book a
    /// SECOND rung on top of the one already pending, which would spend the turn's ladder at twice the rate
    /// and orphan the first timer.
    ///
    /// THE LADDER HAS TWO RUNGS ON PURPOSE. The first version of this test used one, so the concurrent
    /// failure landed on the abandoning path - where a second guard (the pending check inside the
    /// abandonment itself) already held the line. The mutation audit caught it: removing the stand-aside
    /// killed no test at all. With two rungs the concurrent failure lands on the BOOKING path, which is the
    /// behaviour the stand-aside is actually for, and the attempt count tells the two apart - four calls
    /// when each rung is spent once, three when the concurrent failure took a rung it did not own.
    /// </summary>
    [Fact]
    public async Task AConcurrentFailure_WhileAReattemptIsBooked_SpendsNothingAndClaimsNothing()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelpending-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new TimingOutBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 5 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.Equal(1, brain.AskCount);
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // control: rung one is booked

            // The sweep comes past and fails too, while that rung is still pending.
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: false);

            Assert.Equal(2, brain.AskCount);                                    // it really did attempt
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // and it claimed nothing
            Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));

            // POSITIVE CONTROL: the verdict is not simply unreachable - once both booked rungs have run and
            // failed, nothing is pending and the turn IS abandoned.
            Assert.True(await Eventually(() => svc.NarrationAbandonedFor(TenantId.Local, "sid-1")),
                "the abandoned verdict never arrived, so the assertions above proved nothing");

            // AND THE LADDER WAS SPENT ONE RUNG AT A TIME: two direct attempts plus the two booked
            // re-attempts. Three would mean the concurrent failure had taken a rung that was not its own,
            // orphaning the first timer and burning the turn's budget at twice the rate.
            await Task.Delay(500);
            Assert.Equal(4, brain.AskCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// A NEW REPLY GETS ITS OWN BUDGET EVEN WHEN THE WORKING TRANSITION IS MISSED.
    ///
    /// Resetting the ladder on the Working edge alone would have been the same mistake issue #1322 already
    /// taught this file about the narration cache: that edge is observed on a racy sampled boundary and a
    /// quick turn can be missed entirely. A turn whose edge was missed would then inherit the previous
    /// turn's exhausted budget and be denied its first re-attempt - silent for exactly the reason the change
    /// exists to prevent (found in review). The budget is keyed on the reply instead, so this test never
    /// calls OnSessionWorking.
    /// </summary>
    [Fact]
    public async Task ANewReply_GetsItsOwnRetryBudget_EvenWithoutTheWorkingTransition()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the FIRST reply"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelreplykey-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new TimingOutBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 5 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(40));   // one rung

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.True(await Eventually(() => svc.NarrationAbandonedFor(TenantId.Local, "sid-1")));   // control
            var asksBefore = brain.AskCount;

            // The agent answered again. NO OnSessionWorking - this is the missed edge.
            conversation.Store(("Text", "the SECOND reply"));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            // The new reply was attempted AND given its own re-attempt, rather than inheriting an exhausted
            // ladder and being abandoned on its first failure.
            Assert.True(await Eventually(() => brain.AskCount >= asksBefore + 2),
                "the new reply inherited the previous reply's spent budget and was denied its re-attempt");
            await Task.Delay(400);
            Assert.Equal(asksBefore + 2, brain.AskCount);   // its own ladder, and the cap still holds
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// "Nothing to read aloud" is FRESHER evidence than an old turn's model timeout, so it supersedes the
    /// abandoned verdict rather than sitting behind it. Found in review: the fold gives abandoned precedence,
    /// so without this clear a session parked on a prompt would be told the model did not answer.
    /// </summary>
    [Fact]
    public async Task NothingToNarrate_SupersedesAnAbandonedNarration()
    {
        var director = new TunnelStub();
        var withReply = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var empty = StoredConversationStub.Of(("Prompt", "pick one"));
        var useEmpty = false;
        Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?> reader =
            (t, sid) => useEmpty ? empty.Reader(t, sid) : withReply.Reader(t, sid);
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modelnothing-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var svc = ServiceWithBrainAndTts(new TimingOutBrain(), new byte[] { 5 }, persistPath, reader);
            svc.UseModelRetryBackoffForTest();   // no rungs: the first non-answer abandons immediately

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.True(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // control

            // The session is now parked on a prompt with no text reply.
            useEmpty = true;
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            // ...so the screen says the honest thing, and does not blame a model that was never asked.
            var display = VoiceDisplayFold.Fold(
                voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
                unavailable: null,
                nothingToNarrate: svc.NothingToNarrateFor(TenantId.Local, "sid-1"),
                narrationAbandoned: svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            Assert.Equal("nothingToNarrate", display.Kind);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // ---------- The rate-limit arm: an answered "not now" gets the same bounded ladder ----------

    /// <summary>
    /// A brain that is rate limited for its first <c>refusals</c> asks and answers normally after that -
    /// the shape of a provider throttling us briefly, which is the case a bounded retry is for. The
    /// Retry-After it reports is the provider's own hint, or null for a 429 that sent no header.
    /// </summary>
    private sealed class RateLimitedThenAnswersBrain : IAgentBrain
    {
        private readonly int _refusals;
        private readonly TimeSpan? _retryAfter;
        private int _askCount;
        public RateLimitedThenAnswersBrain(int refusals, TimeSpan? retryAfter = null)
        {
            _refusals = refusals;
            _retryAfter = retryAfter;
        }
        public int AskCount => _askCount;
        public string? SessionId => "rate-limited-brain";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _askCount);
            if (n <= _refusals)
                throw new WingmanModelRateLimitedException(
                    "The wingman model call failed: 429 TooManyRequests.", _retryAfter);
            var wrapped = $"{SessionAskRunner.AnswerBeginMarker}\nnarrated spoken text\n{SessionAskRunner.AnswerEndMarker}";
            return Task.FromResult(new AskResult { Text = wrapped, ReplySeconds = 0.1 });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    /// <summary>
    /// THE SAME DEFECT THE TIMEOUT HAD, one arm along. A 429 recorded the calm Retrying state and logged
    /// "this session retries on its own next turn-end / idle sweep". Nothing did: the handler propagated the
    /// exception out of the narration attempt to a wrapper that scheduled nothing, and the only thing that
    /// would ever come back was the shared voice sweep. The screen said "retrying automatically, it should
    /// come through shortly" about a turn no loop was going to touch again.
    ///
    /// A provider throttling one call is transient by definition, so it gets the bounded ladder the voice
    /// path already owns - and this proves it end to end: a refusal that clears produces AUDIO, without a
    /// sweep, without a new turn, and without anybody pressing anything.
    /// </summary>
    [Fact]
    public async Task WhenTheModelIsRateLimited_TheVoicePathBooksItsOwnReattempt_AndTheTurnIsNarrated()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-429retry-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RateLimitedThenAnswersBrain(refusals: 1);
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 6, 6, 6 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            // CONTROL, and the exact state the old code stopped at: one refused call, no audio, calm state.
            Assert.Equal(1, brain.AskCount);
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
            Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            // ...and the promise is honest here, because an attempt really is booked.
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));

            // THE THING THAT DID NOT EXIST: a second attempt, made by the voice path, on its own.
            Assert.True(await Eventually(() => svc.HasVoice(TenantId.Local, "sid-1")),
                "the rate limit never led to another attempt - the turn stayed silent");
            Assert.Equal(2, brain.AskCount);
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The provider's Retry-After is honoured EXACTLY when it asks for longer than the rung's own backoff -
    /// waiting as long as we were asked is the one thing a rate limit knows that a timeout does not.
    ///
    /// Pinned on the booked delay rather than on how long the test sleeps: a timing assertion for this is
    /// either flaky or slow, and neither reads as evidence.
    /// </summary>
    [Fact]
    public async Task AProvidersRetryAfter_IsHonoured_WhenItAsksForLongerThanTheRung()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-429after-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            // Real delays, kept SHORT. An earlier draft booked a 45-second re-attempt and returned, leaving
            // an untracked timer to wake inside the shared test process long after this test had deleted its
            // own directory (found in review). The numbers only have to differ, not be large.
            var brain = new RateLimitedThenAnswersBrain(refusals: 5, retryAfter: TimeSpan.FromMilliseconds(800));
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 6 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(20));   // rung 20ms, provider wants 800ms

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(TimeSpan.FromMilliseconds(800), svc.LastBookedRetryDelayForTest);
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // it IS booked, so the promise holds
            Assert.Equal(1, brain.AskCount);

            // The seam records what we DECIDED; the brain shows what we DID - see the speech-leg twin of
            // this test for why the recorded delay alone is not evidence.
            await Task.Delay(300);
            Assert.Equal(1, brain.AskCount);   // 15x the rung has passed and it has not called again

            Assert.True(await Eventually(() => brain.AskCount >= 2),
                "the booked re-attempt never ran at all - the recorded delay described work nobody did");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// ...and it is NOT honoured below the rung. A provider answering "retry in one second" is describing
    /// its own window, not licensing us to re-enter a ladder we back off on purpose - calling again a
    /// second after a 429 is how a rate limit becomes a storm.
    /// </summary>
    [Fact]
    public async Task AProvidersRetryAfter_DoesNotShortenTheRung_WhenItAsksForLess()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-429short-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RateLimitedThenAnswersBrain(refusals: 5, retryAfter: TimeSpan.FromMilliseconds(10));
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 6 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(300));   // rung 300ms, provider wants 10ms

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(TimeSpan.FromMilliseconds(300), svc.LastBookedRetryDelayForTest);
            await Task.Delay(600);   // let the one booked re-attempt run, so nothing outlives the test
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// A DELAY WE WILL NOT WAIT OUT IS NOT A RETRY. A provider is free to ask for an hour, and booking that
    /// would leave "Voice on its way" on a screen for an hour while the service was perfectly satisfied it
    /// had told the truth - which is the exact sentence this whole area exists to stop. Past the ceiling
    /// nothing is booked and the screen reports a turn that was not narrated.
    ///
    /// Note what is NOT claimed: the session is not given up on. The background sweep still comes back to
    /// it, so this is a refusal to PROMISE, never a refusal to try again.
    /// </summary>
    [Fact]
    public async Task ARetryAfterBeyondTheCeiling_IsNotBooked_AndTheScreenStopsPromisingAudio()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-429huge-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RateLimitedThenAnswersBrain(refusals: 5, retryAfter: TimeSpan.FromHours(1));
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 6 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Null(svc.LastBookedRetryDelayForTest);                      // nothing was booked
            Assert.True(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // and the screen says so

            // Nothing runs later either - waited many times the (tiny) rung so this is a real absence.
            await Task.Delay(400);
            Assert.Equal(1, brain.AskCount);

            // AND THE SENTENCE IS TRUE OF THIS PATH. Asserted as the WHOLE message, not as the absence of
            // two phrases: an absence check passes on an empty message and passed on the wrong message this
            // very test was written to catch - it said "the re-attempts for it are used up", which is false
            // here because the rung is deliberately put back (found in review).
            var display = VoiceDisplayFold.Fold(
                voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
                unavailable: svc.VoiceUnavailableFor(TenantId.Local, "sid-1"),
                nothingToNarrate: false,
                narrationAbandoned: svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            Assert.Equal("notNarrated", display.Kind);
            Assert.Equal("This turn has no narration, and nothing further is scheduled to make one. "
                       + "Read the turn, or ask for the narration again.", display.Message);
            Assert.Equal("Turn not narrated", display.Label);
            Assert.True(display.CanGenerate);   // the one action left that can still work
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// A refused rung is PUT BACK, not silently spent. The ceiling declines to book THIS re-attempt; it does
    /// not shorten the turn's ladder, so a later attempt - the sweep's, or a person pressing Generate - can
    /// still use the rung if the provider has stopped asking for an unreasonable wait by then.
    /// </summary>
    [Fact]
    public async Task ARungRefusedByTheCeiling_IsStillAvailableToALaterAttempt()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-429rung-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            // Just over the two-minute ceiling, and short enough that the not-before hold it arms expires
            // inside the test - the hold is what stops the sweep calling the provider again before its own
            // deadline, so a test that ignored it would be measuring a world that no longer exists.
            TimeSpan? asked = TimeSpan.FromMilliseconds(2500);
            var brain = new SwitchableRateLimitBrain(() => asked);
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 6 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(50));   // ONE rung
            svc.UseMaxSingleRetryWaitForTest(TimeSpan.FromSeconds(1));        // ...and a ceiling below the ask

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.True(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // control: refused, nothing booked
            Assert.Null(svc.LastBookedRetryDelayForTest);
            Assert.Equal(1, brain.AskCount);

            // WHILE THE HOLD LASTS the model is not called again at all, however often the sweep comes past.
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: false);
            Assert.Equal(1, brain.AskCount);
            Assert.Null(svc.LastBookedRetryDelayForTest);

            // Once the provider's own deadline passes, and with it now asking for something reasonable, the
            // rung is STILL there to spend - the refusal withdrew a promise, it did not shorten the ladder.
            await Task.Delay(2700);
            asked = TimeSpan.FromMilliseconds(10);
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: false);

            Assert.Equal(2, brain.AskCount);
            Assert.Equal(TimeSpan.FromMilliseconds(50), svc.LastBookedRetryDelayForTest);   // rung 50ms beats the 10ms ask
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            await Task.Delay(300);   // let it run, so nothing outlives the test
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>A brain that is always rate limited, with a Retry-After the test can change between
    /// attempts.</summary>
    private sealed class SwitchableRateLimitBrain : IAgentBrain
    {
        private readonly Func<TimeSpan?> _retryAfter;
        private int _askCount;
        public SwitchableRateLimitBrain(Func<TimeSpan?> retryAfter) => _retryAfter = retryAfter;
        public int AskCount => _askCount;
        public string? SessionId => "switchable-rate-limit-brain";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _askCount);
            throw new WingmanModelRateLimitedException(
                "The wingman model call failed: 429 TooManyRequests.", _retryAfter());
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    /// <summary>
    /// ONE LADDER FOR THE TURN, however it fails. A turn that times out twice and is then rate limited must
    /// not get a fresh set of re-attempts because the failure changed shape - that would double the spend on
    /// exactly the turn that is already costing the most, and it is the obvious mistake if the two arms keep
    /// separate counts.
    /// </summary>
    [Fact]
    public async Task TheRetryLadderIsSharedBetweenANonAnswerAndARateLimit()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-429shared-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            // Times out first, then is rate limited for ever after - so the ladder is climbed by BOTH arms.
            var brain = new TimesOutThenRateLimitedBrain(timeouts: 1);
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 6 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(await Eventually(() => svc.NarrationAbandonedFor(TenantId.Local, "sid-1")),
                "the shared ladder never ran out - the two failure shapes are keeping separate counts");
            // The first attempt plus exactly the two rungs, whichever arm spent them. Settled first, so a
            // rung still sitting on a timer would be counted rather than missed.
            await Task.Delay(500);
            Assert.Equal(3, brain.AskCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>A brain that times out for its first <c>timeouts</c> asks and is rate limited after that, so
    /// one turn climbs the ladder through both arms.</summary>
    private sealed class TimesOutThenRateLimitedBrain : IAgentBrain
    {
        private readonly int _timeouts;
        private int _askCount;
        public TimesOutThenRateLimitedBrain(int timeouts) => _timeouts = timeouts;
        public int AskCount => _askCount;
        public string? SessionId => "timeout-then-429-brain";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _askCount);
            if (n <= _timeouts) throw new TimeoutException("The wingman model call did not answer within 60 seconds.");
            throw new WingmanModelRateLimitedException("The wingman model call failed: 429 TooManyRequests.", null);
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    /// <summary>
    /// A REFUSAL DOES NOT ENTITLE US TO KEEP CALLING. Found in review: the ceiling declines to BOOK a
    /// re-attempt when a provider asks for longer than this service will hold a promise across, and puts the
    /// rung back - but the background sweep comes past every 45 seconds, so without a hold it would take the
    /// rung, be refused, put it back, and call the model again each time. That calls the provider long
    /// before the deadline it named, which is exactly what Retry-After exists to prevent and how a rate
    /// limit becomes a storm.
    /// </summary>
    [Fact]
    public async Task AfterTheCeilingRefusesABooking_TheModelIsNotCalledAgainUntilTheProvidersDeadlinePasses()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-429hold-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new SwitchableRateLimitBrain(() => TimeSpan.FromSeconds(30));
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 6 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
            svc.UseMaxSingleRetryWaitForTest(TimeSpan.FromSeconds(1));   // 30s asked > 1s ceiling -> refused

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.Equal(1, brain.AskCount);                                   // control: it was called once
            Assert.True(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // control: nothing booked

            // Five sweeps' worth of attempts, all inside the provider's 30-second window.
            for (var i = 0; i < 5; i++)
                await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: false);

            Assert.Equal(1, brain.AskCount);   // the provider was not called again, once
            Assert.Null(svc.LastBookedRetryDelayForTest);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// THE HOLD BELONGS TO THE TURN, so a NEW reply is never held back by the previous turn's refusal. The
    /// per-session cooldowns this file used to have were removed because one condition silenced work that
    /// had nothing to do with it; this must not reintroduce that in miniature.
    /// </summary>
    [Fact]
    public async Task TheProvidersHold_DoesNotDelayANewTurnsNarration()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the FIRST reply"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-429holdturn-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var refuse = true;
            var brain = new RefusesThenNarratesBrain(() => refuse);
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 6, 6 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(20));
            svc.UseMaxSingleRetryWaitForTest(TimeSpan.FromSeconds(1));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.True(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // control: held off for 10 minutes

            // The agent answers again. The hold was armed against the OLD reply, so this one is narrated at
            // once - no waiting out somebody else's deadline.
            refuse = false;
            conversation.Store(("Text", "the SECOND reply"));
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>A brain that is rate limited with a very long Retry-After while the flag says so, and
    /// narrates normally once it does not.</summary>
    private sealed class RefusesThenNarratesBrain : IAgentBrain
    {
        private readonly Func<bool> _refuse;
        private int _askCount;
        public RefusesThenNarratesBrain(Func<bool> refuse) => _refuse = refuse;
        public int AskCount => _askCount;
        public string? SessionId => "refuses-then-narrates-brain";
        public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _askCount);
            if (_refuse())
                throw new WingmanModelRateLimitedException(
                    "The wingman model call failed: 429 TooManyRequests.", TimeSpan.FromMinutes(10));
            var wrapped = $"{SessionAskRunner.AnswerBeginMarker}\nnarrated spoken text\n{SessionAskRunner.AnswerEndMarker}";
            return Task.FromResult(new AskResult { Text = wrapped, ReplySeconds = 0.1 });
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    /// <summary>A brain that blocks inside the model call until the test releases it, then fails - so a
    /// test can hold an attempt open and change the world underneath it, which is the only way to drive the
    /// interleaving the ownership guards exist for.</summary>
    private sealed class BlockingThenFailingBrain : IAgentBrain
    {
        private readonly SemaphoreSlim _release = new(0);
        private readonly SemaphoreSlim _entered = new(0);
        private int _askCount;
        public int AskCount => _askCount;
        public string? SessionId => "blocking-brain";
        /// <summary>Waits until the model call has actually started.</summary>
        public Task EnteredAsync() => _entered.WaitAsync(TimeSpan.FromSeconds(20));
        /// <summary>Lets the blocked model call fail.</summary>
        public void Release() => _release.Release();
        public async Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _askCount);
            _entered.Release();
            await _release.WaitAsync(TimeSpan.FromSeconds(20), ct);
            throw new TimeoutException("The wingman model call did not answer within 60 seconds.");
        }
        public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
        public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
        public void Dispose() { }
    }

    /// <summary>
    /// THE HONESTY INVARIANT, DRIVEN THROUGH THE RACE IT EXISTS FOR. An attempt that is still inside the
    /// model when a NEW TURN starts must say nothing at all when it finally fails - not "not narrated", not
    /// a booked re-attempt against a turn that is over.
    ///
    /// Found in review, and worth spelling out because the first version of the guard did not catch it. The
    /// ledger cannot answer "is this mine?" on its own: a cleared entry and a never-created one are the same
    /// absence, so a late attempt simply created a fresh entry, found nothing pending against it, and
    /// stamped the terminal sentence onto a turn that had just begun. The reply key does not settle it
    /// either, because the next turn may carry the same text. A turn epoch does.
    ///
    /// The ladder is set to ZERO rungs so the late attempt lands squarely on the abandoning path - the one
    /// that publishes a verdict, and therefore the one that must be silenced.
    /// </summary>
    [Fact]
    public async Task AnAttemptThatFinishesAfterANewTurnStarted_SaysNothingAtAll()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-epoch-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new BlockingThenFailingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 6 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest();   // no rungs: a failure lands on the abandoning path

            var attempt = svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            await brain.EnteredAsync();          // the model call is running and holding

            // A new turn starts while that attempt is still inside the model.
            svc.OnSessionWorking(TenantId.Local, "sid-1");

            brain.Release();                     // ...and only now does the old attempt fail
            await attempt;

            // It reported NOTHING about the turn that has already moved on.
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));

            // POSITIVE CONTROL: the same failure DOES speak when no turn has superseded it, so the assertion
            // above is about the epoch and not about a path that records nothing anyway.
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            brain.Release();
            Assert.True(await Eventually(() => svc.NarrationAbandonedFor(TenantId.Local, "sid-1")),
                "the control failed: this path records no verdict even without a superseding turn");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    // ---------- The SPEECH leg: the third place that said "retries on its own" ----------

    /// <summary>
    /// A speech provider that refuses with 429 for its first <c>refusals</c> calls and returns audio after
    /// that, optionally carrying a Retry-After. Counts its calls, so a test can prove a SECOND call was
    /// made rather than inferring it from the audio.
    /// </summary>
    private sealed class TtsRateLimitedHandler : HttpMessageHandler
    {
        private readonly int _refusals;
        private readonly TimeSpan? _retryAfter;
        private int _calls;
        public int Calls => _calls;
        public TtsRateLimitedHandler(int refusals, TimeSpan? retryAfter = null)
        {
            _refusals = refusals;
            _retryAfter = retryAfter;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            if (n > _refusals)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[] { 8, 8, 8 }),
                });
            var resp = new HttpResponseMessage((HttpStatusCode)429)
            {
                Content = new StringContent("{\"error\":\"rate limited\"}", Encoding.UTF8, "application/json"),
            };
            if (_retryAfter is { } ra)
                resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
            return Task.FromResult(resp);
        }
    }

    /// <summary>A speech provider that always answers with the same failing status, counting its calls.</summary>
    private sealed class TtsAlwaysFailsHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private int _calls;
        public int Calls => _calls;
        public TtsAlwaysFailsHandler(HttpStatusCode status) => _status = status;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("{\"error\":\"upstream\"}", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A service whose MODEL answers normally and whose SPEECH leg is the handler under test - the
    /// shape every narration-path speech test needs, and the one the existing helpers do not build.</summary>
    private WingmanVoiceService ServiceWithBrainAndTtsHandler(IAgentBrain brain, HttpMessageHandler handler, string persistPath,
        Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?>? conversationReader = null)
    {
        // The vault lives beside the persist file, INSIDE the directory the test deletes - the older helpers
        // drop theirs loose in the machine temp directory and never remove it (found in review).
        var vaultPath = Path.Combine(Path.GetDirectoryName(persistPath)!, "test.vault");
        Directory.CreateDirectory(Path.GetDirectoryName(persistPath)!);
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        return new WingmanVoiceService((_, _, _) => Task.FromResult(brain), vault, Settings, persistPath,
            ttsHttpClient: new HttpClient(handler), conversationReader: conversationReader);
    }

    /// <summary>
    /// THE THIRD FALSE PROMISE, and the one whose SCREEN lied too. The speech leg said "this session retries
    /// on its own" in three places and scheduled nothing in any of them; two of the three recorded
    /// ServiceDown, so their screens at least showed a specific red verdict, but the NON-ANSWER arm records
    /// Retrying - which the fold renders as the calm "Voice is taking a moment, retrying automatically. It
    /// should come through shortly". A turn with a perfectly good narration text and no audio sat behind
    /// that sentence with nothing making audio.
    ///
    /// The model leg's ladder now covers this leg too. Proved end to end: a speech stall that clears
    /// produces AUDIO, with no sweep, no new turn, and nobody pressing anything.
    /// </summary>
    [Fact]
    public async Task WhenTheSpeechLegDoesNotAnswer_TheVoicePathBooksItsOwnReattempt_AndTheTurnIsNarrated()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-ttsretry-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            // TtsSynthesis makes its own bounded attempts inside one call, so the handler must refuse
            // enough times to exhaust those before the call itself fails - which is exactly the failure
            // this test is about, the whole speech CALL not answering.
            var handler = new TtsTimeoutHandler(timeouts: 2);
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTtsHandler(brain, handler, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            // CONTROL, and the exact state the old code stopped at: the model answered, the speech leg did
            // not, no audio, and the calm sentence on the screen.
            Assert.Equal(1, brain.AskCount);
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));
            Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));   // honest: an attempt IS booked

            // THE THING THAT DID NOT EXIST.
            Assert.True(await Eventually(() => svc.HasVoice(TenantId.Local, "sid-1")),
                "the speech-leg stall never led to another attempt - the turn stayed silent behind 'voice on its way'");
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>A speech 429 is transient by definition, so it gets the same ladder - and the same end to
    /// end proof, audio arriving with nobody pressing anything.</summary>
    [Fact]
    public async Task WhenTheSpeechLegIsRateLimited_TheVoicePathBooksItsOwnReattempt_AndTheTurnIsNarrated()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-tts429-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var handler = new TtsRateLimitedHandler(refusals: 1);
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, handler.Calls);                       // control: refused once
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));

            Assert.True(await Eventually(() => svc.HasVoice(TenantId.Local, "sid-1")),
                "the speech 429 never led to another attempt - the turn stayed silent");
            Assert.Equal(2, handler.Calls);                       // and exactly one re-attempt made the audio
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The speech provider's Retry-After is honoured exactly, the same as the model leg's. It was parsed
    /// into a log line and dropped.
    /// </summary>
    [Fact]
    public async Task ASpeechProvidersRetryAfter_IsHonoured_WhenItAsksForLongerThanTheRung()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-tts429after-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var handler = new TtsRateLimitedHandler(refusals: 5, retryAfter: TimeSpan.FromMilliseconds(800));
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(20));   // rung 20ms, provider wants 800ms

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(TimeSpan.FromMilliseconds(800), svc.LastBookedRetryDelayForTest);
            Assert.Equal(1, handler.Calls);

            // THE SEAM RECORDS WHAT WE DECIDED; THE HANDLER SHOWS WHAT WE DID. Found in review: asserting
            // the recorded delay alone passes if the task is dropped, fires immediately, or fires at the
            // wrong time. So the wait itself is observed - still one call well past the RUNG, and a second
            // call after the PROVIDER'S number.
            await Task.Delay(300);
            Assert.Equal(1, handler.Calls);   // 15x the rung has passed and it has not called again

            Assert.True(await Eventually(() => handler.Calls >= 2),
                "the booked re-attempt never ran at all - the recorded delay described work nobody did");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// WHERE THE LADDER RUNS OUT, THE SPEECH LEG'S SCREEN STOPS PROMISING TOO. This is the assertion the
    /// whole change is for: the non-answer arm records Retrying, which renders as the calm "retrying
    /// automatically, it should come through shortly", and before this that sentence stood for ever with
    /// nothing making audio.
    /// </summary>
    [Fact]
    public async Task WhenEverySpeechReattemptIsSpent_TheScreenStopsPromisingAudio()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-ttsgiveup-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var handler = new TtsTimeoutHandler(timeouts: int.MaxValue);   // never answers
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(await Eventually(() => svc.NarrationAbandonedFor(TenantId.Local, "sid-1")),
                "the speech ladder never ran out, so nothing ever told the reader the turn was not narrated");
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));

            // The WHOLE sentence, not the absence of a phrase.
            var display = VoiceDisplayFold.Fold(
                voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
                unavailable: svc.VoiceUnavailableFor(TenantId.Local, "sid-1"),
                nothingToNarrate: false,
                narrationAbandoned: svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            Assert.Equal("notNarrated", display.Kind);
            Assert.Equal("Turn not narrated", display.Label);
            Assert.Equal("This turn has no narration, and nothing further is scheduled to make one. "
                       + "Read the turn, or ask for the narration again.", display.Message);

            // NEGATIVE CONTROL: the same facts WITHOUT the abandoned fact are the calm sentence this
            // change exists to end - so the assertion above is about the fix and not about a state that
            // renders that way regardless.
            var promising = VoiceDisplayFold.Fold(
                voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
                unavailable: svc.VoiceUnavailableFor(TenantId.Local, "sid-1"),
                nothingToNarrate: false, narrationAbandoned: false);
            Assert.Equal("retrying", promising.Kind);
            Assert.Equal("Voice on its way", promising.Label);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// AN ANSWERED 5xx IS NOT RETRIED, and the screen it produces is unchanged. The service told us it is
    /// failing - a claim the red "Voice service down" panel is right to make, and one a second call seconds
    /// later has no reason to change. Only its LOG was wrong, and a log is not assertable here; what is
    /// assertable is that no re-attempt is booked and the verdict is the one it always was.
    /// </summary>
    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task AnAnsweredSpeechFailure_BooksNoReattempt_AndKeepsItsOwnVerdict(int status)
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-tts5xx-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var handler = new TtsAlwaysFailsHandler((HttpStatusCode)status);
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, handler.Calls);   // control: it was called once
            await Task.Delay(400);            // many times the rung, so this is a real absence
            Assert.Equal(1, handler.Calls);   // and never again

            Assert.Null(svc.LastBookedRetryDelayForTest);
            Assert.Equal(HostedAiState.ServiceDown, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            var display = VoiceDisplayFold.Fold(
                voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
                unavailable: svc.VoiceUnavailableFor(TenantId.Local, "sid-1"),
                nothingToNarrate: false,
                narrationAbandoned: svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            Assert.Equal("serviceDown", display.Kind);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// ONE LADDER FOR THE TURN, ACROSS BOTH LEGS. A turn that loses its narration text to a stalled model
    /// and then its audio to a stalled speech provider has failed twice, not started again. Giving each leg
    /// its own three re-attempts would spend six on the turn already costing the most, which is the obvious
    /// mistake if the two legs keep separate counts.
    /// </summary>
    [Fact]
    public async Task TheRetryLadderIsSharedBetweenTheModelLegAndTheSpeechLeg()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-ttsshared-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            // The model stalls once, then answers for ever; the speech leg never answers. So the first
            // attempt is spent by the MODEL leg and every later one by the SPEECH leg.
            var brain = new StallsThenAnswersBrain(failures: 1);
            var handler = new TtsTimeoutHandler(timeouts: int.MaxValue);
            var svc = ServiceWithBrainAndTtsHandler(brain, handler, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(await Eventually(() => svc.NarrationAbandonedFor(TenantId.Local, "sid-1")),
                "the shared ladder never ran out - the two legs are keeping separate counts");

            // Three narration attempts in total: one that the model lost, and two that reached the speech
            // leg. Settled first, so a fourth still on a timer is counted rather than missed.
            await Task.Delay(500);
            Assert.Equal(3, brain.AskCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// THE ON-DEMAND PATH IS UNCHANGED, asserted rather than assumed. StoreSpokenAsync now hands back what
    /// the speech leg did, and the narration path acts on it - but a caller that does not book must still
    /// record exactly the state it recorded before, or this change would have quietly turned a specific red
    /// verdict into a calm wait on the one path that has no re-attempt behind it.
    /// </summary>
    [Fact]
    public async Task TheOnDemandPath_StillRecordsTheSameVerdict_AndBooksNothing()
    {
        var handler = new TtsRateLimitedHandler(refusals: int.MaxValue, retryAfter: TimeSpan.FromSeconds(30));
        var svc = ServiceWithHandler(handler);

        var attempt = await svc.StoreSpokenAsync(TenantId.Local, "sid-429", "spoken", "reply");

        // The verdict the phone reads is the one it always was for an answered refusal.
        Assert.Equal(HostedAiState.ServiceDown, svc.VoiceUnavailableFor(TenantId.Local, "sid-429"));
        Assert.False(svc.HasVoice(TenantId.Local, "sid-429"));
        Assert.False(svc.NarrationAbandonedFor(TenantId.Local, "sid-429"));

        // ...and the outcome it hands back carries what a booking caller would need, which is the whole
        // point of the return value: the provider's number, and that this one is worth another go.
        Assert.False(attempt.ProducedAudio);
        Assert.True(attempt.WorthAnotherAttempt);
        Assert.Equal(TimeSpan.FromSeconds(30), attempt.RetryAfter);

        // Nothing was booked by this path - one call, and no more.
        Assert.Equal(1, handler.Calls);
        await Task.Delay(300);
        Assert.Equal(1, handler.Calls);
        Assert.Null(svc.LastBookedRetryDelayForTest);
    }

    /// <summary>
    /// AN OLD TIMER CANNOT STEAL A NEWLY BOOKED RE-ATTEMPT.
    ///
    /// Found in review, and then found again by the mutation audit: removing the RESERVATION from the
    /// waking timer's ownership check killed no test at all, so the guard was standing on nothing. The
    /// reply key cannot answer this, which is the whole point - a turn can be cleared and the next attempt
    /// carry the SAME reply text, so an old timer matching on text alone believes the new reservation is
    /// its own, takes the pending marker off it, and runs. The screen is then promising audio behind a
    /// task that will stand down when it fires.
    ///
    /// The window is made wide on purpose. The first re-attempt is booked at t=0 to fire at t=3s; the turn
    /// is cleared at t=0.05s; a second attempt at t=1.5s books its OWN reservation to fire at t=4.5s. At
    /// t=3s the first timer wakes into a ledger that matches on reply text and is pending - and must
    /// recognise that it is not its own.
    /// </summary>
    [Fact]
    public async Task AnOldTimer_CannotStealAReattemptBookedByALaterAttempt()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-reservation-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new TimingOutBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 7 }, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.Equal(1, brain.AskCount);   // control: the first attempt ran and booked reservation one

            await Task.Delay(50);
            svc.OnSessionWorking(TenantId.Local, "sid-1");   // the turn is cleared; that reservation is orphaned

            await Task.Delay(1450);
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.Equal(2, brain.AskCount);   // control: the second attempt ran and booked reservation TWO

            // Past the first timer's deadline and well before the second's. The orphaned timer has woken by
            // now; if it took the newer reservation for its own it would have run a third attempt here.
            await Task.Delay(2000);
            Assert.Equal(2, brain.AskCount);

            // AND THE PRESENCE THAT MAKES THAT ABSENCE MEAN SOMETHING. Found in review: "still two" is also
            // what a machine too busy to run either timer would report, so on its own it certifies nothing.
            // The SECOND reservation must still fire - which proves the timers were alive, that the window
            // the assertion above measured was real, and that standing the orphan down did not cost the turn
            // its re-attempt. A stall long enough to fake the assertion above would fail this one.
            Assert.True(await Eventually(() => brain.AskCount >= 3),
                "the live reservation never fired either - the machinery was asleep, so the count above proved nothing");
            Assert.Equal(3, brain.AskCount);

            svc.Unmark(TenantId.Local, "sid-1");   // retire the ladder so the next rung's timer stands down
            await Task.Delay(50);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>A speech provider that answers 200 with an EMPTY body - no audio, and no failure status to
    /// classify it by. The outcome nobody enumerated.</summary>
    private sealed class TtsEmptyBodyHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            });
        }
    }

    /// <summary>
    /// NO AUDIO AND NOTHING BOOKED IS THE ABANDONED CASE, WHATEVER PRODUCED IT.
    ///
    /// Found in review. The speech leg can return no audio and record NO state at all - a 200 carrying an
    /// empty body is the reachable one - and that outcome is not any of the three failures the
    /// classification names. With an earlier attempt having left the calm Retrying standing, the screen went
    /// back to promising audio with nothing behind it: this change's own defect, arriving through the one
    /// door its own enumeration did not cover.
    ///
    /// The sequence is the one that makes it visible: a first attempt that DOES leave Retrying and a booked
    /// rung, then an empty-bodied answer once the ladder is spent.
    /// </summary>
    [Fact]
    public async Task ASpeechAnswerWithNoAudioAndNoFailureState_StillStopsThePromise()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-ttsempty-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var handler = new TtsEmptyBodyHandler();
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, persistPath, conversation.Reader);
            svc.UseModelRetryBackoffForTest();   // no rungs, so this attempt books nothing

            // Seed the calm promise an earlier attempt would have left behind.
            svc.NoteRetrying(TenantId.Local, "sid-1");
            Assert.Equal(HostedAiState.Retrying, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, handler.Calls);                       // control: the speech leg really was called
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));  // and produced nothing

            // The promise is withdrawn, so the screen reports the turn rather than an arrival.
            Assert.True(svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            var display = VoiceDisplayFold.Fold(
                voiceMode: true, agentWorking: false, hasAudio: false, generating: false,
                unavailable: svc.VoiceUnavailableFor(TenantId.Local, "sid-1"),
                nothingToNarrate: false,
                narrationAbandoned: svc.NarrationAbandonedFor(TenantId.Local, "sid-1"));
            Assert.Equal("notNarrated", display.Kind);
            Assert.Equal("This turn has no narration, and nothing further is scheduled to make one. "
                       + "Read the turn, or ask for the narration again.", display.Message);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

}
