using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.AgentBrain;

/// <summary>
/// Reusable client for a warm, headless Claude Code session owned by a CC Director.
/// Pure REST against the Director's Control API - no PTY code, no filesystem access,
/// works cross-machine. Validated by the issue #172 spike; the three determinism
/// rules from playground/headless-brain/RESULTS.md are implemented here so callers
/// never see them:
///
///   1. Quiet gate - never send while the terminal is repainting (idleSeconds clock).
///   2. The JSONL transcript is the answer channel - replies are read via /turns
///      (full text) and /usage (token accounting), never the terminal screen and
///      never the activity-state flip (10s detector threshold).
///   3. Relink after /clear - /clear starts a new claude-internal session id; the
///      Director is repointed via /relink before any further reads.
/// </summary>
public sealed class AgentBrainClient : IAgentBrain
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly string[] ReadyStates = ["Idle", "WaitingForInput"];

    private readonly AgentBrainOptions _options;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private string? _sessionId;

    /// <summary>The Director-side session GUID, or null before CreateSessionAsync/AttachAsync.</summary>
    public string? SessionId => _sessionId;

    public AgentBrainOptions Options => _options;

    private AgentBrainClient(AgentBrainOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
        _log = options.Log ?? BrainLog.Write;
    }

    /// <summary>
    /// Connect to a Director and verify it is alive (GET /healthz). Throws when the
    /// Director is unreachable - there is no degraded mode.
    /// </summary>
    public static async Task<AgentBrainClient> ConnectAsync(
        AgentBrainOptions options, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.DirectorUrl))
            throw new AgentBrainException("ConnectAsync: DirectorUrl is required");

        var http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.BaseAddress = new Uri(options.DirectorUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrEmpty(options.BearerToken))
            http.DefaultRequestHeaders.Authorization = new("Bearer", options.BearerToken);

        var client = new AgentBrainClient(options, http);
        client._log($"[AgentBrain] ConnectAsync: {options.DirectorUrl}");

        var resp = await http.GetAsync("healthz", ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new AgentBrainException(
                $"ConnectAsync: GET /healthz returned {(int)resp.StatusCode} from {options.DirectorUrl}");

        client._log("[AgentBrain] ConnectAsync OK");
        return client;
    }

    /// <summary>Director /healthz payload (version, session count) for display.</summary>
    public async Task<JsonElement> GetDirectorHealthAsync(CancellationToken ct = default)
    {
        return await GetJsonAsync<JsonElement>("healthz", ct);
    }

    /// <summary>
    /// Create the brain session (POST /sessions) and wait until it accepts prompts.
    /// </summary>
    public async Task CreateSessionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.RepoPath))
            throw new AgentBrainException("CreateSessionAsync: RepoPath is required");

        _log($"[AgentBrain] CreateSessionAsync: repo={_options.RepoPath}");
        var resp = await _http.PostAsJsonAsync("sessions", new
        {
            repoPath = _options.RepoPath,
            agent = "ClaudeCode",
            wingmanEnabled = false,
        }, JsonOpts, ct);

        if (resp.StatusCode != HttpStatusCode.Created)
            throw new AgentBrainException(
                $"CreateSessionAsync: POST /sessions returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");

        var dto = await resp.Content.ReadFromJsonAsync<SessionDto>(JsonOpts, ct)
                  ?? throw new AgentBrainException("CreateSessionAsync: empty response body");
        _sessionId = dto.SessionId;
        _log($"[AgentBrain] CreateSessionAsync: sid={_sessionId}, state={dto.ActivityState}");

        await WaitForQuietAsync(_options.CreateTimeoutSeconds, ct);
        _log("[AgentBrain] CreateSessionAsync: session READY");
    }

    /// <summary>Adopt an existing Director session as the brain.</summary>
    public async Task AttachAsync(string sessionId, CancellationToken ct = default)
    {
        _log($"[AgentBrain] AttachAsync: sid={sessionId}");
        var dto = await GetSessionAsync(sessionId, ct);
        if (dto.Status is "Exited" or "Failed")
            throw new AgentBrainException($"AttachAsync: session {sessionId} is {dto.Status}");
        _sessionId = sessionId;
    }

    /// <summary>
    /// Send a prompt and return the FULL reply text, read from the JSONL transcript.
    /// Waits for the quiet gate, posts the prompt, then polls /turns until a new
    /// Text widget appears AND the transcript has been stable for
    /// <see cref="AgentBrainOptions.ReplyStableSeconds"/>.
    /// </summary>
    public async Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
    {
        var sid = RequireSession();
        if (string.IsNullOrWhiteSpace(prompt))
            throw new AgentBrainException("AskAsync: prompt is empty");

        await WaitForQuietAsync(_options.QuietTimeoutSeconds, ct);

        var turnsBefore = await GetTurnsAsync(sid, ct);
        var widgetsBefore = turnsBefore.Status == "ok" ? turnsBefore.Widgets.Count : 0;

        _log($"[AgentBrain] AskAsync: sid={sid}, len={prompt.Length}, widgetsBefore={widgetsBefore}");
        var t0 = DateTime.UtcNow;
        await PostPromptAsync(sid, prompt, ct);

        var deadline = t0.AddSeconds(_options.AskTimeoutSeconds);
        string? answer = null;
        double replySeconds = 0;
        int stableCount = -1;
        DateTime stableSince = DateTime.MinValue;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var turns = await GetTurnsAsync(sid, ct);
            if (turns.Status == "ok")
            {
                // The transcript must contain a NEW Text widget...
                var newWidgets = turns.Widgets.Skip(widgetsBefore).ToList();
                var lastText = newWidgets.LastOrDefault(w => w.Kind == "Text");
                if (lastText is not null && !string.IsNullOrWhiteSpace(lastText.Content))
                {
                    if (answer is null)
                        replySeconds = (DateTime.UtcNow - t0).TotalSeconds;
                    answer = lastText.Content;

                    // ...and be stable (no widget growth) before we accept the answer,
                    // so multi-block replies are returned whole.
                    if (turns.Widgets.Count != stableCount)
                    {
                        stableCount = turns.Widgets.Count;
                        stableSince = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - stableSince).TotalSeconds >= _options.ReplyStableSeconds)
                    {
                        break;
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
        }

        if (answer is null)
            throw new AgentBrainException(
                $"AskAsync: no reply in the transcript after {_options.AskTimeoutSeconds}s " +
                $"(sid={sid}, state={(await GetSessionAsync(sid, ct)).ActivityState})");

        var usage = await TryGetUsageAsync(sid, ct);
        _log($"[AgentBrain] AskAsync OK: replySeconds={replySeconds:F1}, " +
             $"answerLen={answer.Length}, context={usage?.ContextTokens ?? 0}");

        return new AskResult
        {
            Text = answer,
            ReplySeconds = replySeconds,
            ContextTokens = usage?.ContextTokens ?? 0,
        };
    }

    /// <summary>
    /// Abort the current turn via the Director's escape endpoint (sends Esc into the
    /// session's PTY). The session stays alive and prompt-ready.
    /// </summary>
    public async Task CancelAsync(CancellationToken ct = default)
    {
        var sid = RequireSession();
        _log($"[AgentBrain] CancelAsync: sid={sid}");
        var resp = await _http.PostAsync($"sessions/{sid}/escape", content: null, ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new AgentBrainException($"CancelAsync: POST /escape returned {(int)resp.StatusCode}");
    }

    /// <summary>
    /// Reset the session's conversation context WITHOUT restarting the process:
    /// send /clear, wait for the new claude-internal session id to appear, relink
    /// the Director to it. The session stays warm throughout.
    /// </summary>
    public async Task<ClearResult> ClearAsync(CancellationToken ct = default)
    {
        var sid = RequireSession();
        await WaitForQuietAsync(_options.QuietTimeoutSeconds, ct);

        var turns = await GetTurnsAsync(sid, ct);
        var oldId = turns.ClaudeSessionId ?? "";
        _log($"[AgentBrain] ClearAsync: sid={sid}, oldClaudeSessionId={oldId}");

        var t0 = DateTime.UtcNow;
        await PostPromptAsync(sid, "/clear", ct);

        // /clear writes the new transcript file immediately; discover it remotely via
        // /claude-transcripts (raw file listing, newest first) - rule 3, no filesystem
        // access. NOT /claude-sessions: that endpoint is built from claude.exe's lazily
        // written sessions-index.json and does not contain the new transcript yet.
        var deadline = t0.AddSeconds(_options.ClearTimeoutSeconds);
        string? newId = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var transcripts = await GetJsonAsync<List<TranscriptFile>>(
                $"claude-transcripts?repo={Uri.EscapeDataString(_options.RepoPath)}", ct);
            // The repo may hold many OLD transcripts; the post-/clear one is the entry
            // that is both different from the pre-clear id and recent (>= t0 minus clock
            // slack). Plain "first != oldId" would happily pick last week's transcript.
            var candidate = transcripts.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.ClaudeSessionId)
                && s.ClaudeSessionId != oldId
                && s.LastWriteUtc >= t0.AddSeconds(-10));
            if (candidate is not null)
            {
                newId = candidate.ClaudeSessionId;
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
        }

        if (newId is null)
            throw new AgentBrainException(
                $"ClearAsync: no new claude session id appeared within {_options.ClearTimeoutSeconds}s " +
                $"(sid={sid}, oldId={oldId})");

        var relink = await _http.PostAsJsonAsync(
            $"sessions/{sid}/relink", new { claudeSessionId = newId }, JsonOpts, ct);
        if (relink.StatusCode != HttpStatusCode.OK)
            throw new AgentBrainException(
                $"ClearAsync: POST /relink returned {(int)relink.StatusCode}");

        // Let the post-/clear repaint finish before the caller's next send (rule 1).
        await WaitForQuietAsync(_options.QuietTimeoutSeconds, ct);

        var seconds = (DateTime.UtcNow - t0).TotalSeconds;
        _log($"[AgentBrain] ClearAsync OK: newClaudeSessionId={newId}, seconds={seconds:F1}");
        return new ClearResult
        {
            OldClaudeSessionId = oldId,
            NewClaudeSessionId = newId,
            Seconds = seconds,
        };
    }

    /// <summary>
    /// Hard recovery: kill the current session (if any) and create a fresh one in the
    /// same repo. The handle stays valid; <see cref="SessionId"/> changes.
    /// </summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        _log($"[AgentBrain] RestartAsync: oldSid={_sessionId ?? "none"}");
        if (_sessionId is not null)
        {
            // A dead/stuck session may already be gone Director-side; DELETE of a missing
            // session is a 404 and that is fine for restart semantics - the goal is a
            // fresh session, not a successful funeral.
            var resp = await _http.DeleteAsync($"sessions/{_sessionId}", ct);
            _log($"[AgentBrain] RestartAsync: DELETE old -> {(int)resp.StatusCode}");
            _sessionId = null;
        }
        await CreateSessionAsync(ct);
    }

    /// <summary>Terminate the session (DELETE /sessions/{sid}).</summary>
    public async Task KillAsync(CancellationToken ct = default)
    {
        var sid = RequireSession();
        _log($"[AgentBrain] KillAsync: sid={sid}");
        var resp = await _http.DeleteAsync($"sessions/{sid}", ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new AgentBrainException($"KillAsync: DELETE returned {(int)resp.StatusCode}");
        _sessionId = null;
    }

    /// <summary>Health snapshot: process status, activity state, idle clock, token usage.
    /// Deliberately unlogged: UIs poll this every second or two, and logging each poll
    /// would drown the daily log file. Real operations log their own entry/exit.</summary>
    public async Task<BrainHealth> GetHealthAsync(CancellationToken ct = default)
    {
        var sid = RequireSession();
        var dto = await GetSessionAsync(sid, ct);
        var usage = await TryGetUsageAsync(sid, ct);
        return new BrainHealth
        {
            IsAlive = dto.Status is not ("Exited" or "Failed") && dto.ActivityState != "Exited",
            Status = dto.Status,
            ActivityState = dto.ActivityState,
            IdleSeconds = dto.IdleSeconds,
            ContextTokens = usage?.ContextTokens ?? 0,
            TurnCount = usage?.Turns.Count ?? 0,
        };
    }

    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------- internals

    private string RequireSession() =>
        _sessionId ?? throw new AgentBrainException(
            "No active session. Call CreateSessionAsync or AttachAsync first.");

    private async Task PostPromptAsync(string sid, string text, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync(
            $"sessions/{sid}/prompt", new { text, appendEnter = true }, JsonOpts, ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new AgentBrainException(
                $"PostPrompt: returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>
    /// Rule 1: block until the session is in a prompt-accepting state AND the terminal
    /// has been byte-silent for QuietSeconds, per the Director's own idle clock.
    /// </summary>
    private async Task WaitForQuietAsync(double timeoutSeconds, CancellationToken ct)
    {
        var sid = RequireSession();
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        SessionDto dto;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            dto = await GetSessionAsync(sid, ct);
            if (dto.ActivityState == "Exited" || dto.Status is "Exited" or "Failed")
                throw new AgentBrainException(
                    $"WaitForQuiet: session {sid} is dead (status={dto.Status}, state={dto.ActivityState}). RestartAsync to recover.");
            if (ReadyStates.Contains(dto.ActivityState) && dto.IdleSeconds >= _options.QuietSeconds)
                return;
            if (DateTime.UtcNow >= deadline)
                throw new AgentBrainException(
                    $"WaitForQuiet: session {sid} not quiet after {timeoutSeconds}s " +
                    $"(state={dto.ActivityState}, idle={dto.IdleSeconds:F1}s)");
            await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
        }
    }

    private async Task<SessionDto> GetSessionAsync(string sid, CancellationToken ct) =>
        await GetJsonAsync<SessionDto>($"sessions/{sid}", ct);

    private async Task<TurnsResponse> GetTurnsAsync(string sid, CancellationToken ct) =>
        await GetJsonAsync<TurnsResponse>($"sessions/{sid}/turns", ct);

    private async Task<SessionUsageDto?> TryGetUsageAsync(string sid, CancellationToken ct)
    {
        // /usage is 404 until the session has a transcript (first turn) - that is a
        // defined protocol state, not an error to hide.
        var resp = await _http.GetAsync($"sessions/{sid}/usage", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new AgentBrainException($"GET /usage returned {(int)resp.StatusCode}");
        return await resp.Content.ReadFromJsonAsync<SessionUsageDto>(JsonOpts, ct);
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        var resp = await _http.GetAsync(path, ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new AgentBrainException(
                $"GET /{path} returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
        var value = await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
        if (value is null)
            throw new AgentBrainException($"GET /{path} returned an empty body");
        return value;
    }
}
