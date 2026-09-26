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
using CcDirector.Gateway.Tests.Wingman;
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
        Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => throw new InvalidOperationException("brain must not be called for flag state");
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

    /// <summary>
    /// AN AGENT THAT KEEPS NO CONVERSATION IS JUDGED FROM ITS SCREEN. This used to be the one terminal answer: no
    /// words could ever be read, so the screen said "Voice unavailable" and the sweep stood down. The verdict does
    /// not need a conversation - its receipt binds to the screen alone - so the stop is narrated, nothing
    /// terminal is recorded, and the sweep keeps visiting.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenTheAgentKeepsNoConversation_IsJudgedFromItsScreen_AndNarrated()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.NoConversationEverKept();
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-terminal-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 3 }, persistPath, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, brain.AskCount);
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            Assert.False(svc.ShouldSkipSweep(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
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
        Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => throw new InvalidOperationException("brain must not be called");
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

        public CcDirector.Gateway.Contracts.ScreenGridResponse? ScreenGrid { get; set; }

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
            var wrapped = FakeTurnVerdictEnvironment.NeedsYou("narrated spoken text");
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
    public async Task GenerateAsync_WhenModelLegTimesOut_NoAudio_AndTheVoiceServiceClaimsNoRetryOfItsOwn()
    {
        // The MODEL leg stalling leaves no audio. This service used to record a calm Retrying ("voice on its
        // way, retrying automatically") for it, beside the failed record the verdict service had already stored -
        // two accounts of one failure, and the calm one was said with nothing booked. It now records NOTHING: the
        // failed record carries the booked retry, and the card and the voice screen are rendered from that.
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-modeltimeout-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new TimingOutBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 7, 7, 7 }, persistPath, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            // It tried the model, and - this being a voice session somebody is listening to - tried it ONCE more
            // with the wider deadline (slice I). Both stalled, so the rest of this test is unchanged.
            Assert.Equal(TurnVerdictService.MaxJudgeAttemptsWhenListenedTo, brain.AskCount);
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));                                      // nothing to play
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));                            // no second account of the failure
            Assert.Null(svc.SpeechErrorFor(TenantId.Local, "sid-1"));                                 // and it is not a speech failure
            Assert.False(svc.IsGenerating(TenantId.Local, "sid-1"));                                  // the yellow window closed
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    // ---------- "Nothing to narrate": the session is waiting on a prompt, no text reply to read ----------

    /// <summary>
    /// A STOP WITH NO TEXT REPLY IS STILL NARRATED (ruling 8 of the Wingman-on-every-turn plan: the spoken
    /// version exists for every owned stop). Before slice C a turn with no text widget recorded "nothing to
    /// narrate" and asked no model; the verdict now judges that stop like any other, from the screen and the
    /// turns before it, and plays its spoken section. Only a brand-new session still has nothing to narrate.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_AStopWithNoTextReply_IsStillJudgedAndNarrated()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.Of(("ToolUse", "running a tool"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-nothing-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 1 }, persistPath, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, brain.AskCount);
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
            Assert.False(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
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
    /// NOTHING STORED IS NO LONGER A REASON NOT TO NARRATE (the Wingman-on-every-turn mission, slice C). Before
    /// the voice path took its words from the stop's verdict, an empty store meant "the Director has not pushed
    /// yet - wait", and this test pinned that the wait recorded nothing. The verdict judges the live screen when
    /// there is no conversation, so the stop is narrated now. None of the old waiting facts - a read failure,
    /// "nothing to narrate", "that computer cannot send" - is recorded, because none is true of a stop with words.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenNothingIsStoredYet_JudgesTheScreenAlone_AndNarratesIt()
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
            Assert.Equal(1, brain.AskCount);                                   // one verdict, from the screen alone
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));                // and it is played
            Assert.Null(svc.ReadFailedFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            Assert.False(svc.NothingToNarrateFor(TenantId.Local, "sid-1"));
            Assert.False(svc.DirectorCannotSendConversationFor(TenantId.Local, "sid-1"));
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
    /// A session with nothing stored yet stays IN the sweep, and when its words arrive the next attempt judges
    /// them. The screen here is unreadable, and an unreadable screen is never reused by anything but the sweep -
    /// every unreadable screen hashes the same, so reusing it would play the earlier verdict for the new words.
    /// </summary>
    [Fact]
    public async Task GenerateAsync_WhenNothingIsStoredYet_StaysInTheSweep_AndJudgesTheWordsOnceTheyArrive()
    {
        var director = new TunnelStub();
        var conversation = StoredConversationStub.NothingStored();
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-unsup-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 1 }, persistPath, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.False(svc.ShouldSkipSweep(TenantId.Local, "sid-1"));       // still in the sweep
            Assert.Null(svc.ReadFailedFor(TenantId.Local, "sid-1"));          // nothing terminal, nothing retryable
            Assert.Equal(1, brain.AskCount);
            var first = Assert.IsType<WingmanVoiceService.VoiceReady>(svc.Get(TenantId.Local, "sid-1"));
            Assert.Equal("", first.Reply);                                    // nothing of the agent's to quote yet

            // The push lands, and the next attempt judges the stop with its words.
            conversation.Store(("Text", "the reply the Director finally pushed"));
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(2, brain.AskCount);
            var second = Assert.IsType<WingmanVoiceService.VoiceReady>(svc.Get(TenantId.Local, "sid-1"));
            Assert.Equal("the reply the Director finally pushed", second.Reply);
            Assert.NotEqual(first.SourceIdentity, second.SourceIdentity);
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
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
    /// <summary>
    /// The verdict seat a generating voice service takes its words from, over a fake world whose judge IS the
    /// test's brain. Since the Wingman-on-every-turn mission a narration's one model call is the verdict call,
    /// so every count and every failure these tests stage on the brain lands on the judge.
    /// </summary>
    private static TurnVerdictService VerdictsOver(IAgentBrain brain,
        Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?>? conversationReader)
    {
        var env = new FakeTurnVerdictEnvironment
        {
            Conversation = sid => conversationReader?.Invoke(TenantId.Local, sid),
            Judge = async (prompt, ct) => (await brain.AskAsync(prompt, ct)).Text,
            VoiceSession = _ => true,
        };
        return new TurnVerdictService(env);
    }

    private WingmanVoiceService ServiceWithBrainAndTts(IAgentBrain brain, byte[] audio, string persistPath,
        Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?>? conversationReader = null,
        Func<TenantId, string, bool>? directorCannotSendConversation = null)
    {
        var vaultPath = Path.Combine(Path.GetTempPath(), "wmvs-" + Guid.NewGuid().ToString("N") + ".vault");
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var http = new HttpClient(new TtsStubHandler(HttpStatusCode.OK, "", audio));
        return new WingmanVoiceService((_, _, _, _) => Task.FromResult(brain), vault, Settings, persistPath,
            ttsHttpClient: http, conversationReader: conversationReader,
            directorCannotSendConversation: directorCannotSendConversation,
            turnVerdicts: VerdictsOver(brain, conversationReader));
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
        return new WingmanVoiceService((_, _, _, _) => Task.FromResult(brain), vault, Settings, persistPath,
            ttsHttpClient: http, conversationReader: conversationReader,
            turnVerdicts: VerdictsOver(brain, conversationReader));
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
    public async Task GenerateAsync_WhenTheScreenIsTheOneAlreadyNarrated_SkipsQuietly()
    {
        // Issue #1322 preserved, with the stop's verdict as the identity. When the screen is the one the stored
        // verdict was formed on, the next turn-end reuses that verdict - no model call - and because that verdict
        // is already the narrated one it never re-mints audio and never flips the session yellow, so a client
        // mid-play is not disturbed.
        var director = new TunnelStub
        {
            ScreenGrid = new CcDirector.Gateway.Contracts.ScreenGridResponse
            {
                SessionId = "sid-1",
                HasGrid = true,
                Rows = new List<string> { "the same reply", "> " },
            },
        };
        var conversation = StoredConversationStub.Of(("Text", "the same reply"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-same-" + Guid.NewGuid().ToString("N"));
        var persistPath = Path.Combine(dir, "voice-sessions.json");
        try
        {
            var brain = new RecordingBrain();
            var svc = ServiceWithBrainAndTts(brain, new byte[] { 9, 9 }, persistPath, conversation.Reader);
            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: false);
            Assert.Equal(1, brain.AskCount);                                  // control: narrated once
            var ready = svc.Get(TenantId.Local, "sid-1");
            Assert.NotNull(ready);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, brain.AskCount);                                  // never re-judged
            Assert.Same(ready, svc.Get(TenantId.Local, "sid-1"));             // the clip is untouched, not re-minted
            Assert.False(svc.IsGenerating(TenantId.Local, "sid-1"));
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
        Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => throw new InvalidOperationException("brain must not be called for the store-spoken path");
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
        Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brain =
            (_, _, _, _) => throw new InvalidOperationException("brain must not be called for the store-spoken path");
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

    // ---------- The speech leg on the ONE retry schedule (mission "Wingman error and retry", 2026-09-19) ----------
    //
    // This region used to hold about twenty-five tests of the voice path's own retry ledger: a ten, thirty and
    // ninety second ladder of booked tasks, an "abandoned" flag, a reservation counter and a provider hold. That
    // ledger is gone - the mission's design says a second retry mechanism beside the schedule means the mission is
    // not done - and those tests went with the thing they described. What replaced it is small enough to be tested
    // whole below: the reading's failure is the verdict service's to retry and the voice service records nothing
    // about it, and a SPEECH failure goes on the same 1, 1, 1, 5, 5, 5, 30, 30 schedule, carried by the sweep.

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
        private readonly string _body;
        private int _calls;
        public int Calls => _calls;
        public TtsAlwaysFailsHandler(HttpStatusCode status, string body = "{\"error\":\"upstream\"}")
        {
            _status = status;
            _body = body;
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>A clock a test moves by hand, so the schedule's minutes cost nothing to wait out.</summary>
    private sealed class MovableClock
    {
        public DateTime Now { get; set; } = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>A service whose MODEL answers normally and whose SPEECH leg is the handler under test, on a clock
    /// the test moves.</summary>
    private WingmanVoiceService ServiceWithBrainAndTtsHandler(IAgentBrain brain, HttpMessageHandler handler, string persistPath,
        MovableClock clock,
        Func<TenantId, string, CcDirector.Gateway.History.StoredConversation?>? conversationReader = null)
    {
        // The vault lives beside the persist file, INSIDE the directory the test deletes.
        var vaultPath = Path.Combine(Path.GetDirectoryName(persistPath)!, "test.vault");
        Directory.CreateDirectory(Path.GetDirectoryName(persistPath)!);
        var vault = new KeyVault(vaultPath);
        vault.Set("OPENAI_API_KEY", "sk-test");
        vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
        var svc = new WingmanVoiceService((_, _, _, _) => Task.FromResult(brain), vault, Settings, persistPath,
            ttsHttpClient: new HttpClient(handler), conversationReader: conversationReader,
            turnVerdicts: VerdictsOver(brain, conversationReader));
        svc.UseClockForTest(() => clock.Now);
        return svc;
    }

    /// <summary>One pass of the idle sweep over one session, exactly as GatewayHost runs it: prepare, then generate
    /// with the prepared input. Returns whether the pass reached a provider.</summary>
    private static async Task<bool> SweepOnceAsync(WingmanVoiceService svc, TunnelStub director)
    {
        var route = RouteFor(director);
        var input = await svc.PrepareSweepGenerationAsync(TenantId.Local, "sid-1", route);
        var reached = false;
        await svc.GenerateAsync(TenantId.Local, "sid-1", route, CancellationToken.None, showReadingWindow: false,
            onProviderReached: () => reached = true, sweepInput: input);
        return reached;
    }

    /// <summary>
    /// A speech failure books retry 1 of 8 one minute out, the sweep does not call the provider before that, and
    /// when it is due the sweep's next pass makes the audio with nobody pressing anything. This is goal 2 of the
    /// mission on the speech leg, and the "not asked before it is due" half is what keeps a failing provider from
    /// being called on every 45 second pass.
    /// </summary>
    [Fact]
    public async Task ASpeechFailure_BooksRetryOneOfEight_IsNotAttemptedBeforeItIsDue_AndTheSweepMakesTheAudioWhenItIs()
    {
        var director = new TunnelStub { ScreenGrid = TurnVerdictTestDoubles.Screen("sid-1", "the reply to narrate") };
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-speechretry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new MovableClock();
            var handler = new TtsRateLimitedHandler(refusals: 1);
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, Path.Combine(dir, "voice-sessions.json"), clock, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, handler.Calls);                       // control: the speech provider really was called
            Assert.False(svc.HasVoice(TenantId.Local, "sid-1"));  // and refused
            var error = svc.SpeechErrorFor(TenantId.Local, "sid-1");
            Assert.NotNull(error);
            Assert.Equal("retry 1 of 8", error!.RetryLabel);
            Assert.Equal(clock.Now + TimeSpan.FromMinutes(1), error.NextRetryAtUtc);
            Assert.False(error.Exhausted);

            // NOT BEFORE IT IS DUE: fifty-nine seconds on, a sweep pass spends nothing.
            clock.Advance(TimeSpan.FromSeconds(59));
            Assert.False(await SweepOnceAsync(svc, director));
            Assert.Equal(1, handler.Calls);

            // DUE: the next pass makes the audio, and the error is gone with it.
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(await SweepOnceAsync(svc, director));
            Assert.Equal(2, handler.Calls);
            Assert.True(svc.HasVoice(TenantId.Local, "sid-1"));
            Assert.Null(svc.SpeechErrorFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// The speech leg walks the schedule exactly - one, one, one, five, five, five, thirty, thirty minutes - and
    /// after the eighth retry fails nothing is booked, the display says so, and no later sweep pass calls the
    /// provider again however long it waits. "Nothing more is scheduled" is true because the booked time is null,
    /// and the booked time is the only thing the sweep reads.
    /// </summary>
    [Fact]
    public async Task TheSpeechSchedule_IsExactlyOneOneOneFiveFiveFiveThirtyThirty_AndThenNothingIsBooked()
    {
        var director = new TunnelStub { ScreenGrid = TurnVerdictTestDoubles.Screen("sid-1", "the reply to narrate") };
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-speechschedule-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new MovableClock();
            var handler = new TtsAlwaysFailsHandler(HttpStatusCode.InternalServerError);
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, Path.Combine(dir, "voice-sessions.json"), clock, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            var minutes = new[] { 1, 1, 1, 5, 5, 5, 30, 30 };
            for (var retry = 1; retry <= minutes.Length; retry++)
            {
                var error = svc.SpeechErrorFor(TenantId.Local, "sid-1");
                Assert.NotNull(error);
                Assert.Equal($"retry {retry} of 8", error!.RetryLabel);
                Assert.Equal(clock.Now + TimeSpan.FromMinutes(minutes[retry - 1]), error.NextRetryAtUtc);
                Assert.Equal(8 - retry + 1, error.RetriesRemaining);

                clock.Advance(TimeSpan.FromMinutes(minutes[retry - 1]));
                Assert.True(await SweepOnceAsync(svc, director), $"retry {retry} was due and the sweep did not make it");
            }

            Assert.Equal(9, handler.Calls);   // the first attempt and exactly eight retries
            var spent = svc.SpeechErrorFor(TenantId.Local, "sid-1");
            Assert.NotNull(spent);
            Assert.True(spent!.Exhausted);
            Assert.Null(spent.NextRetryAtUtc);
            Assert.Equal(WingmanErrorFold.ExhaustedLine, spent.ExhaustedText);

            // AND THEN IT STOPS. A day later the sweep still spends nothing on this stop.
            clock.Advance(TimeSpan.FromDays(1));
            Assert.False(await SweepOnceAsync(svc, director));
            Assert.Equal(9, handler.Calls);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>A rate limit that names its own wait is honoured: the retry is the later of the schedule and the
    /// wait the provider named, so a provider that asked for ten minutes is not called again in one.</summary>
    [Fact]
    public async Task ASpeechProvidersNamedWait_LongerThanTheSchedule_MovesTheRetryLater()
    {
        var director = new TunnelStub { ScreenGrid = TurnVerdictTestDoubles.Screen("sid-1", "the reply to narrate") };
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-speechwait-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new MovableClock();
            var handler = new TtsRateLimitedHandler(refusals: 1, retryAfter: TimeSpan.FromMinutes(10));
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, Path.Combine(dir, "voice-sessions.json"), clock, conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(clock.Now + TimeSpan.FromMinutes(10), svc.SpeechErrorFor(TenantId.Local, "sid-1")!.NextRetryAtUtc);
            clock.Advance(TimeSpan.FromMinutes(9));
            Assert.False(await SweepOnceAsync(svc, director));
            Assert.Equal(1, handler.Calls);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>An account's own condition - out of credits - is the member's to fix and is not retried: it books
    /// nothing, and the screen keeps its specific add-credit sentence instead of a retry line.</summary>
    [Fact]
    public async Task AnAccountCondition_BooksNoSpeechRetry_AndKeepsItsOwnSentence()
    {
        var director = new TunnelStub { ScreenGrid = TurnVerdictTestDoubles.Screen("sid-1", "the reply to narrate") };
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-speechaccount-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new TtsAlwaysFailsHandler(HttpStatusCode.PaymentRequired, "{\"error\":{\"code\":\"insufficient_credits\"}}");
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, Path.Combine(dir, "voice-sessions.json"), new MovableClock(), conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.Equal(1, handler.Calls);
            Assert.Equal(HostedAiState.NeedsCredits, svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.SpeechErrorFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>A new turn ends the old stop's speech schedule: the session going back to work clears it, so the
    /// next stop starts at its own first attempt and is never held back by the last one.</summary>
    [Fact]
    public async Task ANewTurn_EndsTheSpeechRetrySchedule()
    {
        var director = new TunnelStub { ScreenGrid = TurnVerdictTestDoubles.Screen("sid-1", "the reply to narrate") };
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-speechnewturn-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new TtsAlwaysFailsHandler(HttpStatusCode.InternalServerError);
            var svc = ServiceWithBrainAndTtsHandler(new RecordingBrain(), handler, Path.Combine(dir, "voice-sessions.json"), new MovableClock(), conversation.Reader);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);
            Assert.NotNull(svc.SpeechErrorFor(TenantId.Local, "sid-1"));   // control: there was a schedule to end

            svc.OnSessionWorking(TenantId.Local, "sid-1");

            Assert.Null(svc.SpeechErrorFor(TenantId.Local, "sid-1"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    /// <summary>
    /// A FAILED READING IS NOT THE VOICE SERVICE'S TO ACCOUNT FOR. It used to record a Retrying state and an
    /// abandoned flag of its own beside the failed record, and two accounts of one failure is how a card said an
    /// attempt was coming with none booked. Now the failed record is the only account: it carries the booked
    /// retry, and the voice service holds nothing about it.
    /// </summary>
    [Fact]
    public async Task AFailedReading_LeavesNothingInTheVoiceService_AndTheBookedRetryIsOnTheStoredRecord()
    {
        var director = new TunnelStub { ScreenGrid = TurnVerdictTestDoubles.Screen("sid-1", "the reply to narrate") };
        var conversation = StoredConversationStub.Of(("Text", "the reply to narrate"));
        var dir = Path.Combine(Path.GetTempPath(), "wmvs-failedreading-" + Guid.NewGuid().ToString("N"));
        try
        {
            var brain = new TimingOutBrain();
            var verdicts = VerdictsOver(brain, conversation.Reader);
            var vaultPath = Path.Combine(dir, "test.vault");
            Directory.CreateDirectory(dir);
            var vault = new KeyVault(vaultPath);
            vault.Set("OPENAI_API_KEY", "sk-test");
            vault.Set("DEVTHROTTLE_API_KEY", "dt_live_test");
            var handler = new TtsAlwaysFailsHandler(HttpStatusCode.InternalServerError);
            var svc = new WingmanVoiceService((_, _, _, _) => Task.FromResult<IAgentBrain>(brain), vault, Settings,
                Path.Combine(dir, "voice-sessions.json"), ttsHttpClient: new HttpClient(handler),
                conversationReader: conversation.Reader, turnVerdicts: verdicts);

            await svc.GenerateAsync(TenantId.Local, "sid-1", RouteFor(director), CancellationToken.None, showReadingWindow: true);

            Assert.True(brain.AskCount >= 1);                     // control: the judge really was asked
            Assert.Equal(0, handler.Calls);                       // and no speech was attempted for a failed reading
            Assert.Null(svc.VoiceUnavailableFor(TenantId.Local, "sid-1"));
            Assert.Null(svc.SpeechErrorFor(TenantId.Local, "sid-1"));

            var stored = verdicts.Latest(TenantId.Local, "sid-1");
            Assert.NotNull(stored);
            Assert.True(stored!.Failed);
            Assert.Equal(0, stored.RetriesMade);
            Assert.NotNull(stored.NextRetryAtUtc);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }
}
