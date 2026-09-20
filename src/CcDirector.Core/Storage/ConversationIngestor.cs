using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Agents;
using CcDirector.Core.Gemini;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Storage;

/// <summary>
/// Captures each session's conversation and PUSHES it to the Gateway's prompt log (issue #1551).
///
/// The split, and why it is this way round:
/// - The Director captures. It is the only thing that sees a prompt at all - desktop-local input never
///   reaches the Gateway - and the only thing that knows whether it was typed or spoken and from which
///   surface.
/// - The Gateway stores. It is what the whole fleet reports to, so anyone asking for history asks it
///   and the answer is already there rather than scattered across machines; and it is what moves to the
///   server, so the log moves with it.
///
/// The Director keeps NO copy. Whatever the Gateway accepted is the record.
///
/// Trigger: the same one TurnReviewLogger uses - a session flipping to
/// <see cref="ActivityState.WaitingForInput"/>, i.e. our own detector deciding the agent is done and
/// needs the user. That is exactly when new messages exist, so there is no polling.
///
/// Source: <see cref="SessionHistoryReader"/>, the agent-neutral facade over all supported agents. Only
/// <see cref="ConversationPartKind.Text"/> parts are kept - tool calls and their results are dropped
/// (most of the volume, not the signal), and thinking blocks with them.
///
/// Watermarking: agents rewrite and compact their transcripts, so "how many did I read last time" is
/// not durable. Each pushed message's identity (timestamp + role + text hash) is remembered per source
/// and persisted, so re-reading the same transcript cannot double-push. State is a watermark, NOT a log
/// - it holds hashes, never text - and lives at base/prompt-ingest-state.json.
///
/// Read-only over sessions and fail-safe throughout: ingest must never break a turn.
/// </summary>
public sealed class ConversationIngestor : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly IPromptSink _sink;
    private readonly ConcurrentDictionary<Guid, Action<ActivityState, ActivityState>> _handlers = new();
    private readonly IngestState _state = IngestState.Load();

    /// <summary>
    /// One gate per SOURCE, so only one ingest of a given source runs at a time.
    ///
    /// Without it the watermark check is a check-then-act race: an ingest reads AlreadyWritten, pushes
    /// over HTTP, and only marks the messages afterwards - because it must not mark before the Gateway
    /// accepts (the Director keeps no copy). Two ingests of one source that start inside that window
    /// both see "not written", and BOTH push. Every message is duplicated in a Gateway log that appends
    /// blindly and never dedupes. The trigger fires per turn end on a Task.Run, so two quick turn ends
    /// are all it takes.
    ///
    /// Keyed by SOURCE and not by session, because the source is what the watermark itself is keyed by
    /// (see <see cref="ScopeKey"/>). A per-session gate would still duplicate for exactly the agents the
    /// scope key exists for: Copilot, OpenCode and Gemini resolve their conversation by REPOSITORY, so
    /// two Director sessions on one repository read the same conversation and would hold different
    /// gates. Locking the source locks the thing actually being read and written.
    ///
    /// Not cleaned up when a session goes: a source outlives any one session (that is the point of the
    /// scope key), and an entry is a bare SemaphoreSlim keyed by a transcript path or repository. It
    /// grows with the number of distinct sources one Director process has ever ingested, which is small
    /// and bounded by real work. Removing entries would need refcounting against in-flight waiters, and
    /// that risk is not worth the few bytes.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    private bool _started;
    private int _disposed;

    /// <param name="sessionManager">The sessions to watch.</param>
    /// <param name="sink">Where captured messages go - the Gateway in production.</param>
    /// <param name="activityProducer">The activity-evidence producer (docs/PLAN-trustworthy-working-start-
    /// 2026-07-24.md): each NEW assistant reply detected here is also recorded as a
    /// turn-observed-in-transcript event - the ground truth that a real turn happened, which the shadow
    /// Working classifier is judged against. Optional; null means no evidence is recorded.</param>
    public ConversationIngestor(SessionManager sessionManager, IPromptSink sink,
        Activity.ActivityEventProducer? activityProducer = null)
    {
        _sessionManager = sessionManager;
        _sink = sink;
        _activityProducer = activityProducer;
    }

    private readonly Activity.ActivityEventProducer? _activityProducer;

    public void Start()
    {
        if (_started) return;
        _started = true;
        FileLog.Write("[ConversationIngestor] Start");
        _sessionManager.OnSessionCreated += WireSession;
        _sessionManager.OnSessionRemoved += UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            WireSession(s);
    }

    private void WireSession(Session session)
    {
        if (_handlers.ContainsKey(session.Id)) return;

        Action<ActivityState, ActivityState> handler = (_, @new) =>
        {
            // The single trigger: the agent just finished a turn, so new messages exist to capture.
            if (@new != ActivityState.WaitingForInput) return;
            // Off the event thread - reading a transcript and pushing over HTTP must not stall the UI.
            Task.Run(() => IngestAsync(session));
        };
        _handlers[session.Id] = handler;
        session.OnActivityStateChanged += handler;
    }

    private void UnwireSession(Session session)
    {
        if (_handlers.TryRemove(session.Id, out var h))
            session.OnActivityStateChanged -= h;
        InputOriginBuffer.Forget(session.Id.ToString());
        PromptAuthorBuffer.Forget(session.Id.ToString());
        // Deliberately keep this source's seen-set: a session can be removed and restored, and its
        // transcript survives, so forgetting would re-push everything it ever said.
    }

    /// <summary>
    /// Capture any not-yet-pushed messages for this session and send them to the Gateway. Public so a
    /// backfill can drive it directly. Never throws.
    ///
    /// Runs one at a time per SOURCE - see <see cref="_gates"/> for why that matters and why the lock is
    /// on the source rather than the session.
    /// </summary>
    public async Task IngestAsync(Session session)
    {
        try
        {
            if (!SessionHistoryReader.IsSupported(session)) return;

            var scope = ScopeKey(session);
            var gate = _gates.GetOrAdd(scope, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await IngestOnceAsync(session, scope).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConversationIngestor] Ingest FAILED (swallowed) session={session.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// One pass of capture-and-push for a source. The caller holds that source's gate, so the whole
    /// read-check-push-mark sequence below is atomic with respect to any other ingest of the same
    /// source - which is what makes the watermark check meaningful.
    ///
    /// The transcript is deliberately read INSIDE the gate: a caller that queued behind another must
    /// re-read and re-check against the watermark the first one just wrote, or it would push what it
    /// decided to push before waiting.
    /// </summary>
    private async Task IngestOnceAsync(Session session, string scope)
    {
        var history = ReadForRecord(session);
        if (history.Messages.Count == 0) return;

        var origins = InputOriginBuffer.For(session.Id.ToString());
        var toPush = new List<PromptRecord>();
        var pushed = new List<(DateTime ts, ConversationRole role, string text, bool fromAgent)>();

        foreach (var message in history.Messages)
        {
            var text = TextOf(message);
            if (string.IsNullOrWhiteSpace(text)) continue;

            // Gemini carries no timestamps (its history is scraped from the terminal buffer), so
            // there is nothing real to stamp with. Record capture time and mark it as ours, rather
            // than letting an inferred time read as a measured one.
            var fromAgent = message.Timestamp.HasValue;
            var ts = message.Timestamp?.UtcDateTime ?? DateTime.UtcNow;

            if (_state.AlreadyWritten(scope, ts, message.Role, text, fromAgent)) continue;

            var isUser = message.Role == ConversationRole.User;
            var origin = isUser ? MatchOrigin(origins, ts, fromAgent) : null;

            toPush.Add(new PromptRecord
            {
                TsUtc = ts,
                Machine = Environment.MachineName,
                SessionId = session.Id.ToString(),
                ContextId = ContextIdFor(session, message),
                SessionName = session.CustomName,
                RepoPath = session.RepoPath,
                Agent = session.AgentKind.ToString(),
                MissionName = session.MissionName,
                Role = isUser ? "user" : "assistant",
                Modality = origin?.Modality,
                // A user message we could not attribute is "unknown", never a guess. An assistant
                // reply has no origin at all, so it stays null.
                Surface = isUser ? (origin?.Surface ?? "unknown") : null,
                TimestampFromAgent = fromAgent,
                CharCount = text.Length,
                WordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
                Text = text,
            });
            pushed.Add((ts, message.Role, text, fromAgent));

            // Ground truth for the shadow Working classifier: a NEW assistant reply proves a real turn
            // happened. Recorded at DETECTION time, not after the prompt push - the event id derives
            // deterministically from the same content identity this loop dedupes on, so a re-detection
            // (an earlier prompt push failed and the message comes around again) replays the same
            // identity and the Gateway acknowledges a duplicate instead of storing a second observation.
            if (!isUser)
                _activityProducer?.RecordTurnObserved(session, ts, ContextIdFor(session, message),
                    $"{scope}|{(fromAgent ? ts.ToString("O") : "no-ts")}|{text}");
        }

        if (toPush.Count == 0) return;

        // Only mark as recorded once the Gateway has actually accepted them. The Director keeps no
        // copy, so marking before a failed push would lose the messages permanently - the next
        // ingest would skip them as already-done.
        var accepted = await _sink.PushAsync(toPush).ConfigureAwait(false);
        if (!accepted)
        {
            FileLog.Write($"[ConversationIngestor] session={session.Id}: Gateway did not accept {toPush.Count} message(s); will retry on the next turn");
            return;
        }

        foreach (var (ts, role, text, fromAgent) in pushed)
            _state.MarkWritten(scope, ts, role, text, fromAgent);
        _state.Save();
        FileLog.Write($"[ConversationIngestor] session={session.Id} pushed {toPush.Count} message(s) to the Gateway");
    }

    /// <summary>
    /// Which agent CONTEXT a message belonged to - what you group by to replay one conversation as the
    /// agent saw it, and what resets when the context is cleared.
    ///
    /// Prefer the id the source itself stamped on the message: Claude and Gemini both carry one, and
    /// only a per-message value is correct for a source that holds several contexts in one file. Fall
    /// back to the transcript file's own name, which for the file-per-context agents (Codex, Pi, Grok)
    /// IS the context's identity - a new context means a new file.
    ///
    /// Null when the agent exposes no context identity at all (Copilot and OpenCode resolve out of a
    /// shared store by repo). Recorded as absent rather than invented; the Director session id still
    /// groups the work.
    /// </summary>
    private static string? ContextIdFor(Session session, ConversationMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.ContextId)) return message.ContextId;

        return session.AgentKind switch
        {
            AgentKind.Copilot or AgentKind.OpenCode => null,
            _ => TranscriptStem(session),
        };
    }

    /// <summary>The transcript file's name without extension - the context identity for the agents that
    /// keep one file per context. Null when no transcript path resolves.</summary>
    private static string? TranscriptStem(Session session)
    {
        var path = SessionHistoryReader.ResolveTranscriptPath(session);
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// What the dedupe is scoped to: the SOURCE we read, not the Director session that triggered the
    /// read. These are not the same thing, and assuming they were is a duplication bug.
    ///
    /// Claude, Codex, Pi and Grok keep a transcript per agent session, so the source is that file and
    /// scoping by Director session happens to agree. But Copilot and OpenCode resolve their
    /// conversation by REPO out of one shared SQLite store, and Gemini's logs.json is keyed by repo
    /// too - so two Director sessions on one repo read the SAME conversation. Scoped by session, each
    /// would keep its own seen-set and push every message twice. Scoped by source, they share one.
    /// </summary>
    private static string ScopeKey(Session session) => session.AgentKind switch
    {
        AgentKind.Gemini => $"gemini|{session.RepoPath}",
        AgentKind.Copilot => $"copilot|{session.RepoPath}",
        AgentKind.OpenCode => $"opencode|{session.RepoPath}",
        _ => SessionHistoryReader.ResolveTranscriptPath(session) ?? session.Id.ToString(),
    };

    /// <summary>
    /// The conversation to capture for this session. Normally the agent-neutral
    /// <see cref="SessionHistoryReader"/> facade - with ONE deliberate exception.
    ///
    /// Gemini persists no transcript, so the facade builds its history by scraping the terminal
    /// scrollback into a single unstructured, untimestamped blob. That is right for the History tab
    /// (it is the only place Gemini's replies exist) and wrong here: the blob grows every turn, so it
    /// is a different message each time and capturing it would push the whole conversation again on
    /// every turn, forever. Gemini's own logs.json has the user's prompts with real timestamps, which
    /// is what a durable record needs - so the record reads that instead, and honestly has no Gemini
    /// replies rather than a re-pushed screen dump. See <see cref="GeminiPromptLogReader"/>.
    /// </summary>
    private static ConversationHistory ReadForRecord(Session session)
        => session.AgentKind == AgentKind.Gemini
            ? GeminiPromptLogReader.Read(session.RepoPath)
            : SessionHistoryReader.Read(session);

    /// <summary>Concatenate a message's Text parts. Tool calls, tool results, and thinking are dropped.</summary>
    private static string TextOf(ConversationMessage message)
    {
        var parts = message.Parts
            .Where(p => p.Kind == ConversationPartKind.Text)
            .Select(p => p.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t));
        return string.Join("\n", parts).Trim();
    }

    /// <summary>
    /// The closest origin to this message, or null when none is close enough. The join is deliberately
    /// tight: an origin is noted the instant a submission crosses a choke point, and the agent stamps
    /// the message on receipt, so a real pair is seconds apart. Anything further out is not evidence,
    /// and an unmatched message is honestly "unknown".
    /// </summary>
    private static InputOriginEvent? MatchOrigin(IReadOnlyList<InputOriginEvent> origins, DateTime ts, bool timestampFromAgent)
    {
        // Without a real agent timestamp there is nothing to join on - we would be matching our own
        // capture clock against submission times, which is not a measurement.
        if (!timestampFromAgent || origins.Count == 0) return null;

        InputOriginEvent? best = null;
        var bestDelta = TimeSpan.MaxValue;
        foreach (var o in origins)
        {
            var delta = (o.TsUtc - ts).Duration();
            if (delta < bestDelta) { bestDelta = delta; best = o; }
        }
        return bestDelta <= MatchWindow ? best : null;
    }

    /// <summary>How far apart a submission and its transcript message may be and still be the same event.</summary>
    private static readonly TimeSpan MatchWindow = TimeSpan.FromSeconds(30);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _sessionManager.OnSessionCreated -= WireSession;
        _sessionManager.OnSessionRemoved -= UnwireSession;
        foreach (var s in _sessionManager.ListSessions())
            UnwireSession(s);
        _handlers.Clear();
        foreach (var gate in _gates.Values)
            gate.Dispose();
        _gates.Clear();
        _state.Save();
    }
}

/// <summary>Where captured messages go. The Gateway in production; a fake in tests.</summary>
public interface IPromptSink
{
    /// <summary>Push messages. Returns true only when they are safely stored - the Director keeps no
    /// copy, so a false here means "not recorded" and the caller must not mark them done.</summary>
    Task<bool> PushAsync(IReadOnlyList<PromptRecord> records);
}

/// <summary>
/// Which messages have already been pushed, per SOURCE (see ConversationIngestor.ScopeKey - a
/// transcript file, or a repo for the agents whose history is repo-resolved). Identity is timestamp +
/// role + text hash - NOT a message index, because agents rewrite and compact their transcripts and an
/// index would slide.
///
/// This is a watermark, not a log: it holds hashes and never text. The Director keeps no copy of the
/// conversation; this only records what it has already handed to the Gateway, so a restart cannot
/// re-push a whole history.
///
/// Because the file outlives the process, every key written into it must be computable identically by
/// the NEXT process - see <see cref="ContentHash"/>. Anything seeded per process reads as a miss after
/// a restart, which is the one failure this file exists to prevent.
/// </summary>
internal sealed class IngestState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _seen = new();

    private static string Path_ => System.IO.Path.Combine(CcStorage.Root(), "prompt-ingest-state.json");

    private static string Key(DateTime ts, ConversationRole role, string text, bool tsFromAgent)
    {
        // A message with no agent timestamp is stamped with OUR clock, which differs on every read -
        // so its key must not include the time or nothing would ever dedupe. Text + role is what we
        // genuinely have for those agents.
        var timePart = tsFromAgent ? ts.ToString("O") : "no-ts";
        return $"{timePart}|{(int)role}|{ContentHash(text)}";
    }

    /// <summary>
    /// A hash of the text that is the SAME in every process, forever. This MUST NOT be
    /// <c>string.GetHashCode()</c>: .NET randomizes its seed per process, so keys built from it stop
    /// matching the moment the Director restarts - every recomputed key misses, AlreadyWritten answers
    /// false for everything, and the first turn end after startup re-pushes the entire conversation
    /// history into a Gateway log that appends blindly and never dedupes.
    ///
    /// Truncated to 128 bits: this only needs to tell two messages apart within one source, and the key
    /// already carries the timestamp and role.
    /// </summary>
    private static string ContentHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();

    public bool AlreadyWritten(string scope, DateTime ts, ConversationRole role, string text, bool tsFromAgent)
    {
        lock (_gate)
        {
            return _seen.TryGetValue(scope, out var set) && set.Contains(Key(ts, role, text, tsFromAgent));
        }
    }

    public void MarkWritten(string scope, DateTime ts, ConversationRole role, string text, bool tsFromAgent)
    {
        lock (_gate)
        {
            if (!_seen.TryGetValue(scope, out var set))
                _seen[scope] = set = new HashSet<string>();
            set.Add(Key(ts, role, text, tsFromAgent));
        }
    }

    public static IngestState Load()
    {
        var state = new IngestState();
        try
        {
            if (!File.Exists(Path_)) return state;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllText(Path_));
            if (loaded is not null)
                foreach (var kv in loaded) state._seen[kv.Key] = kv.Value;
        }
        catch (Exception ex)
        {
            // A lost watermark means re-pushing, not data loss. Log it and start clean rather than
            // failing the Director over a state file.
            FileLog.Write($"[ConversationIngestor] ingest-state load FAILED, starting clean: {ex.Message}");
        }
        return state;
    }

    public void Save()
    {
        try
        {
            lock (_gate)
            {
                CcStorage.Ensure(CcStorage.Root());
                var tmp = Path_ + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_seen));
                File.Move(tmp, Path_, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ConversationIngestor] ingest-state save FAILED (swallowed): {ex.Message}");
        }
    }
}
