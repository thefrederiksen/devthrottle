using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Keeps a ready-to-play spoken summary for each "voice session" (issue #531 follow-up). Once the
/// phone uses voice on a session it becomes a voice session; from then on, every time a turn
/// finishes and the session is waiting for the user, the gateway automatically re-runs the wingman
/// translation AND the OpenAI text-to-speech and stores the result here - so the phone's session
/// list can show "voice ready" with a play button, play it without entering, and entering is
/// instant (the voice is already made). Since issue #549 the turn-end trigger is the always-running
/// TurnEndWatcher, which calls <see cref="GenerateAsync"/> directly for voice sessions on turn-end
/// (the retired turn-brief pipeline no longer mediates it).
/// </summary>
public sealed class WingmanVoiceService
{
    public sealed record VoiceReady(string Spoken, string Reply, byte[] Audio, DateTime AtUtc);

    private const string TtsModel = "tts-1";
    private const string TtsVoice = "nova";

    private readonly WingmanTranslator _translator;
    private readonly KeyVault _vault;
    private readonly DirectorEndpointClient _client;
    private readonly WingmanTrainingStore _training;
    private readonly ConcurrentDictionary<string, byte> _voiceSessions = new();   // sid -> marker
    private readonly ConcurrentDictionary<string, VoiceReady> _ready = new();      // sid -> spoken+audio
    private readonly ConcurrentDictionary<string, byte> _generating = new();       // sid -> wingman is running now
    private readonly string _persistPath;
    private readonly string _audioDir;

    /// <summary>On-disk shape of one ready session's metadata (the audio bytes live next to it as
    /// an .mp3). Persisted so the play triangle / playability survives a gateway restart (issue #553).</summary>
    private sealed record PersistedVoice(string Spoken, string Reply, DateTime AtUtc);

    public WingmanVoiceService(Func<CancellationToken, Task<IAgentBrain>> brainProvider, KeyVault vault, DirectorEndpointClient client, string? persistPath = null, WingmanTrainingStore? training = null, Func<string>? instructionsProvider = null)
    {
        _translator = new WingmanTranslator(brainProvider, instructionsProvider: instructionsProvider);
        _vault = vault;
        _client = client;
        _training = training ?? new WingmanTrainingStore();
        // Which sessions are voice sessions survives a gateway restart. Issue #553: the per-session
        // audio cache is now ALSO durable - it is persisted next to voice-sessions.json under a
        // "voice-audio" folder so the triangle does not vanish-then-reappear-empty across a restart
        // and a tap after restart plays. Tests pass an isolated path so the two never collide.
        _persistPath = persistPath ?? Path.Combine(CcStorage.Root(), "voice-sessions.json");
        var baseDir = Path.GetDirectoryName(_persistPath);
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = CcStorage.Root();
        _audioDir = Path.Combine(baseDir, "voice-audio");
        LoadVoiceSessions();
        LoadReadyAudio();
    }

    private void LoadVoiceSessions()
    {
        try
        {
            if (!File.Exists(_persistPath)) return;
            var ids = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_persistPath));
            if (ids is not null) foreach (var id in ids) if (!string.IsNullOrWhiteSpace(id)) _voiceSessions[id] = 1;
            FileLog.Write($"[WingmanVoiceService] loaded {_voiceSessions.Count} voice session(s) from disk");
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] load voice sessions FAILED: {ex.Message}"); }
    }

    private void SaveVoiceSessions()
    {
        try { File.WriteAllText(_persistPath, JsonSerializer.Serialize(_voiceSessions.Keys.ToArray())); }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] save voice sessions FAILED: {ex.Message}"); }
    }

    /// <summary>Restore the per-session ready audio cache from disk on startup (issue #553) so
    /// HasVoice / ReadySessionIds survive a gateway restart. A session is only loaded ready when BOTH
    /// its metadata (.json) and its audio (.mp3, non-empty) are present - the "if anything fails,
    /// remove the triangle" rule extends to a half-written or missing cache.</summary>
    private void LoadReadyAudio()
    {
        try
        {
            if (!Directory.Exists(_audioDir)) return;
            var loaded = 0;
            foreach (var metaPath in Directory.EnumerateFiles(_audioDir, "*.json"))
            {
                var sid = Path.GetFileNameWithoutExtension(metaPath);
                var audioPath = Path.Combine(_audioDir, sid + ".mp3");
                if (!File.Exists(audioPath)) continue;
                var audio = File.ReadAllBytes(audioPath);
                if (audio.Length == 0) continue;
                var meta = JsonSerializer.Deserialize<PersistedVoice>(File.ReadAllText(metaPath));
                if (meta is null) continue;
                _ready[sid] = new VoiceReady(meta.Spoken, meta.Reply, audio, meta.AtUtc);
                loaded++;
            }
            FileLog.Write($"[WingmanVoiceService] loaded {loaded} ready voice audio cache(s) from disk");
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] load ready audio FAILED: {ex.Message}"); }
    }

    private void SaveReadyAudio(string sid, VoiceReady ready)
    {
        try
        {
            Directory.CreateDirectory(_audioDir);
            // Write the audio first, then the metadata, so a startup load (which requires BOTH the
            // .mp3 and the .json) never sees a session ready before its bytes are on disk.
            File.WriteAllBytes(Path.Combine(_audioDir, sid + ".mp3"), ready.Audio);
            File.WriteAllText(Path.Combine(_audioDir, sid + ".json"),
                JsonSerializer.Serialize(new PersistedVoice(ready.Spoken, ready.Reply, ready.AtUtc)));
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] save ready audio FAILED sid={sid}: {ex.Message}"); }
    }

    private void DeleteReadyAudio(string sid)
    {
        try
        {
            var meta = Path.Combine(_audioDir, sid + ".json");
            var audio = Path.Combine(_audioDir, sid + ".mp3");
            if (File.Exists(meta)) File.Delete(meta);
            if (File.Exists(audio)) File.Delete(audio);
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] delete ready audio FAILED sid={sid}: {ex.Message}"); }
    }

    /// <summary>This session has had voice used on it at least once.</summary>
    public bool IsVoiceSession(string sid) => _voiceSessions.ContainsKey(sid);

    /// <summary>Every session the gateway is keeping voice for (the persisted set).</summary>
    public IReadOnlyCollection<string> VoiceSessionIds() => _voiceSessions.Keys.ToArray();

    /// <summary>True when this session currently has a fresh, playable cached summary.</summary>
    public bool HasVoice(string sid) => _ready.ContainsKey(sid);

    /// <summary>The sessions that currently have a ready, playable spoken summary.</summary>
    public IReadOnlyCollection<string> ReadySessionIds() => _ready.Keys.ToArray();

    /// <summary>
    /// True while the wingman is actively producing this session's spoken summary (issue #531
    /// voice mode). This is the window the session must show YELLOW - "kind of not ready yet" -
    /// before flipping back to red when it needs the user again. The gateway surfaces it through
    /// the "Briefing" yellow path in the /sessions aggregation (see GatewayEndpoints voiceGeneratingFor).
    /// </summary>
    public bool IsGenerating(string sid) => _generating.ContainsKey(sid);

    /// <summary>Mark the wingman as running for this session (turns the session yellow).</summary>
    public void BeginGenerating(string sid) => _generating[sid] = 1;

    /// <summary>The wingman finished running for this session (back to red / its raw color).</summary>
    public void EndGenerating(string sid) => _generating.TryRemove(sid, out _);

    /// <summary>
    /// Capture one wingman summary for the training dataset (no-op unless the setting is on).
    /// Best-effort and fire-and-forget at the call site so it never delays a voice turn; the
    /// store fetches up to 20,000 chars of the session terminal and appends the record itself.
    /// </summary>
    public Task CaptureTrainingAsync(string endpoint, string sid, string source, string reply, string recentContext, string spoken, double replySeconds, CancellationToken ct = default)
        => _training.CaptureAsync(_client, endpoint, sid, source, reply, recentContext, spoken, replySeconds, ct);

    public VoiceReady? Get(string sid) => _ready.TryGetValue(sid, out var v) ? v : null;
    public byte[]? GetAudio(string sid) => _ready.TryGetValue(sid, out var v) ? v.Audio : null;

    /// <summary>Mark the session as a voice session (persisted, so the gateway keeps its voice fresh
    /// across restarts via the background sweep + turn-end).</summary>
    public void Mark(string sid) { if (_voiceSessions.TryAdd(sid, 1)) SaveVoiceSessions(); }

    /// <summary>
    /// A new turn just started on this session, so the cached spoken summary + audio are now stale.
    /// Drop them immediately - the list stops showing it "voice ready", and nothing stale gets
    /// served or played. The session stays a voice session, so when the turn finishes the turn-end
    /// hook regenerates a fresh summary. Called on the Working transition.
    /// </summary>
    public void OnSessionWorking(string sid)
    {
        // A new turn (blue) supersedes any in-flight generation for the old turn, so drop the
        // yellow "wingman running" marker too - raw activity wins while the agent works.
        _generating.TryRemove(sid, out _);
        if (_ready.TryRemove(sid, out _))
        {
            DeleteReadyAudio(sid);   // issue #553: keep the durable cache in step so a stale tap can't 404
            FileLog.Write($"[WingmanVoiceService] voice + text cache cleared (session working): sid={sid}");
        }
    }

    /// <summary>
    /// Store a spoken summary that a caller already produced (the on-demand explain / voice-turn
    /// paths), synthesize its audio, and mark the session as a voice session. Best-effort: if the
    /// audio can't be made (no key / outage) the session is still marked, so turn-end retries.
    /// </summary>
    public async Task StoreSpokenAsync(string sid, string spoken, string reply, CancellationToken ct = default)
    {
        Mark(sid);
        if (string.IsNullOrWhiteSpace(spoken)) return;
        var audio = await TtsAsync(spoken, ct);
        // The "if anything fails, remove the triangle" rule: when synthesis returns null/empty we
        // leave _ready WITHOUT this session, so HasVoice stays false and no triangle shows. Only a
        // real, playable summary becomes ready - and is persisted (issue #553) so it survives a restart.
        if (audio is { Length: > 0 })
            StoreReady(sid, spoken, reply ?? "", audio);
    }

    /// <summary>Mark a session ready with already-synthesized audio: update the in-memory cache and
    /// persist it to disk so it survives a gateway restart (issue #553). The single place the success
    /// branch lives - the test seam (<see cref="StoreReadyAudioForTest"/>) reuses it so persistence is
    /// exercised without a live OpenAI call.</summary>
    private void StoreReady(string sid, string spoken, string reply, byte[] audio)
    {
        var ready = new VoiceReady(spoken, reply, audio, DateTime.UtcNow);
        _ready[sid] = ready;
        SaveReadyAudio(sid, ready);
    }

    /// <summary>Test seam: store ready audio exactly as a successful synthesis would (in-memory +
    /// durable), so the persistence round-trip can be tested without calling OpenAI.</summary>
    internal void StoreReadyAudioForTest(string sid, string spoken, string reply, byte[] audio)
        => StoreReady(sid, spoken, reply, audio);

    /// <summary>
    /// Regenerate the voice for a session from its latest turn: read the last reply, translate it,
    /// synthesize audio, store. Called on every turn-end for voice sessions (background, best-effort
    /// - it swallows its own failures so a turn is never blocked on voice).
    /// </summary>
    public async Task GenerateAsync(string sid, string endpoint, CancellationToken ct = default)
    {
        try
        {
            Mark(sid);
            var turns = await _client.GetTurnsAsync(endpoint, sid, ct);
            var widgets = turns?.Widgets ?? new List<TurnWidgetDto>();
            var lastReply = widgets.LastOrDefault(w => w.Kind == "Text")?.Content;
            if (string.IsNullOrWhiteSpace(lastReply)) return;  // nothing to say yet
            // Recent conversation so the wingman can add context to a short/terse latest reply.
            var recentContext = WingmanTranslator.BuildRecentContext(widgets);
            // The wingman is now running for this session - show it yellow until the summary lands.
            BeginGenerating(sid);
            try
            {
                var t = await _translator.TranslateAsync(recentContext, lastReply, ct);
                await StoreSpokenAsync(sid, t.Spoken, lastReply, ct);
                FileLog.Write($"[WingmanVoiceService] voice ready: sid={sid}, spokenLen={t.Spoken.Length}");
                // Training capture (no-op unless the setting is on); fire-and-forget so it never
                // delays the turn. CancellationToken.None so a captured turn is not lost on shutdown.
                _ = _training.CaptureAsync(_client, endpoint, sid, "generate", lastReply, recentContext, t.Spoken, t.ReplySeconds, CancellationToken.None);
            }
            finally { EndGenerating(sid); }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[WingmanVoiceService] GenerateAsync sid={sid} FAILED: {ex.Message}");
        }
    }

    private async Task<byte[]?> TtsAsync(string text, CancellationToken ct)
    {
        var key = _vault.Get("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        var input = text.Length > 4000 ? text[..4000] : text;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var payload = JsonContent.Create(new { model = TtsModel, voice = TtsVoice, input, response_format = "mp3" });
            using var resp = await http.PostAsync("https://api.openai.com/v1/audio/speech", payload, ct);
            if (!resp.IsSuccessStatusCode) { FileLog.Write($"[WingmanVoiceService] tts {(int)resp.StatusCode}"); return null; }
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex) { FileLog.Write($"[WingmanVoiceService] tts FAILED: {ex.Message}"); return null; }
    }
}
