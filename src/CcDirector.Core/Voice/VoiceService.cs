using System.Text;
using System.Text.RegularExpressions;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Sessions;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Voice;

/// <summary>
/// One-shot voice command pipeline for the Director UI.
///
/// Pipeline:
///   1. Audio blob in (webm/opus, mp3, wav, m4a ...).
///   2. The shared <see cref="BatchTranscriptionPipeline"/> transcribes the whole clip ONCE using
///      the Gateway-selected method, then applies the validated dictionary corrector only.
///   3. Regex/keyword intent parser maps the transcript -> a structured command.
///   4. Executor runs the command against SessionManager and returns a spoken-style reply.
///
/// Transcription method (base URL + key + model) comes entirely from the Gateway routing resolver
/// (<see cref="OpenAiKeyResolver.ResolveEndpointAsync"/>): there is NO hardcoded whisper-1 /
/// api.openai.com endpoint here, and the only post-transcription transform is the dictionary
/// corrector - the free-text language-model cleanup was removed (issue #587). Intent parsing is
/// deliberately small and extensible. v1 supports six intents:
///   ListSessions, ListWaiting, DescribeSession, OpenSession, SendToSession, InterruptSession.
/// Anything else returns Status="unknown_command" with a helpful reply.
/// </summary>
public sealed class VoiceService
{
    private readonly SessionManager _sessionManager;
    private readonly AgentOptions _options;

    public VoiceService(SessionManager sessionManager, AgentOptions options)
    {
        _sessionManager = sessionManager;
        _options = options;
    }

    /// <summary>True when an OpenAI key is configured (env var or appsettings).</summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_options.ResolveOpenAiKey());

    /// <summary>
    /// End-to-end: audio in, response out. Caller passes the upload stream
    /// (will be consumed once) and a hint about the file name (used for the
    /// Content-Type sniff the transcription provider does on the form field).
    /// </summary>
    public async Task<VoiceCommandResponse> HandleAsync(
        Stream audio, string fileName, CancellationToken ct = default)
    {
        var resolver = new OpenAiKeyResolver();
        var routing = await resolver.ResolveEndpointAsync(ct);
        if (routing is null)
        {
            FileLog.Write("[VoiceService] HandleAsync: no transcription routing available");
            return new VoiceCommandResponse
            {
                Status = "no_key",
                ReplyText = resolver.UnavailableMessage,
                Error = "transcription routing unavailable",
            };
        }

        BatchTranscriptionResult transcription;
        try
        {
            transcription = await TranscribeAndCorrectAsync(audio, fileName, routing, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceService] Transcribe FAILED: {ex.Message}");
            return new VoiceCommandResponse
            {
                Status = "transcribe_failed",
                ReplyText = "I could not transcribe that. Try again.",
                Error = ex.Message,
            };
        }

        var transcript = transcription.RawTranscript;
        FileLog.Write($"[VoiceService] Transcript: \"{Truncate(transcript, 200)}\"");

        var command = IntentParser.Parse(transcript);
        FileLog.Write($"[VoiceService] Intent: {command.Intent}, target=\"{command.Target}\", payload=\"{Truncate(command.Payload ?? "", 80)}\"");

        try
        {
            var response = Execute(transcript, command);
            response.CleanedTranscript = transcription.CorrectedTranscript;
            response.CleanupReason = transcription.Reason;
            return response;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceService] Execute FAILED: {ex.Message}");
            return new VoiceCommandResponse
            {
                Transcript = transcript,
                CleanedTranscript = transcription.CorrectedTranscript,
                CleanupReason = transcription.Reason,
                Intent = command.Intent.ToString(),
                Status = "execute_failed",
                ReplyText = $"I heard you but ran into an error: {ex.Message}",
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Transcribe an already-assembled audio blob through the ONE shared batch pipeline, returning
    /// the raw transcript and the dictionary-corrected text. Used by the resumable /voice/utterance
    /// path, which reassembles the chunked upload into one blob and then needs the transcribe+correct
    /// half of <see cref="HandleAsync"/> WITHOUT the command intent parsing. The only text change is
    /// swapping known dictionary terms; there is no free-text cleanup. Routing (agent vs wingman) is
    /// the caller's button choice and never alters the transcript. Transcription failures surface as
    /// "transcribe_failed"; the dictionary step fails open (corrected == raw).
    /// </summary>
    public async Task<VoiceCommandResponse> TranscribeAndCleanAsync(
        Stream audio, string fileName, string repoPath, CancellationToken ct = default)
    {
        var routing = await new OpenAiKeyResolver().ResolveEndpointAsync(ct);
        if (routing is null)
        {
            FileLog.Write("[VoiceService] TranscribeAndCleanAsync: no transcription routing available");
            return new VoiceCommandResponse
            {
                Status = "no_key",
                Error = "transcription routing unavailable",
            };
        }

        BatchTranscriptionResult transcription;
        try
        {
            transcription = await TranscribeAndCorrectAsync(audio, fileName, routing, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceService] TranscribeAndCleanAsync transcribe FAILED: {ex.Message}");
            return new VoiceCommandResponse { Status = "transcribe_failed", Error = ex.Message };
        }

        FileLog.Write($"[VoiceService] Utterance transcript: \"{Truncate(transcription.RawTranscript, 200)}\"");

        return new VoiceCommandResponse
        {
            Transcript = transcription.RawTranscript,
            CleanedTranscript = transcription.CorrectedTranscript,
            CleanupReason = transcription.Reason,
            Status = "ok",
        };
    }

    /// <summary>
    /// Run the ONE shared batch pipeline: whole-audio batch transcription via the resolved method,
    /// then the validated dictionary corrector only. Resolves the live dictionary per call via
    /// <see cref="DictionaryResolver"/> (#253) so a Cockpit edit takes effect on the next utterance.
    /// </summary>
    private async Task<BatchTranscriptionResult> TranscribeAndCorrectAsync(
        Stream audio, string fileName, ResolvedTranscription routing, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, ct);
        var audioBytes = buffer.ToArray();

        var dictionary = await new DictionaryResolver(_options).ResolveAsync(ct);
        using var pipeline = new BatchTranscriptionPipeline(cleanupModel: _options.DictationCleanupModel);
        var result = await pipeline.TranscribeAsync(audioBytes, fileName, routing, dictionary, "default", ct);
        FileLog.Write($"[VoiceService] dictionary correction: applied={result.DictionaryApplied} changed={result.ChangedWords.Count} reason=\"{result.Reason}\"");
        return result;
    }

    // ====== Execute the parsed command =================================================

    private VoiceCommandResponse Execute(string transcript, VoiceCommand cmd)
    {
        var sessions = _sessionManager.ListSessions().ToList();

        switch (cmd.Intent)
        {
            case VoiceIntent.ListSessions:
                return ReplyListSessions(transcript, sessions);

            case VoiceIntent.ListWaiting:
                return ReplyListWaiting(transcript, sessions);

            case VoiceIntent.DescribeSession:
                return ReplyDescribe(transcript, cmd.Target, sessions);

            case VoiceIntent.OpenSession:
                return ReplyOpen(transcript, cmd.Target, sessions);

            case VoiceIntent.SendToSession:
                return ReplySend(transcript, cmd.Target, cmd.Payload ?? "", sessions);

            case VoiceIntent.InterruptSession:
                return ReplyInterrupt(transcript, cmd.Target, sessions);

            default:
                return new VoiceCommandResponse
                {
                    Transcript = transcript,
                    Intent = "Unknown",
                    Status = "unknown_command",
                    ReplyText = "I did not catch a command. Try \"what sessions are running\" or \"open pi\".",
                };
        }
    }

    private VoiceCommandResponse ReplyListSessions(string transcript, IList<Session> sessions)
    {
        if (sessions.Count == 0)
        {
            return new VoiceCommandResponse
            {
                Transcript = transcript,
                Intent = "ListSessions",
                ReplyText = "No sessions are running on this Director.",
            };
        }

        var lines = sessions
            .OrderByDescending(s => SortKey(s.ActivityState))
            .Take(8)
            .Select(s => $"{DisplayName(s)} ({s.ActivityState})")
            .ToList();

        var summary = sessions.Count == 1
            ? $"One session is running: {lines[0]}."
            : $"{sessions.Count} sessions are running. " + string.Join(", ", lines) + ".";

        return new VoiceCommandResponse
        {
            Transcript = transcript,
            Intent = "ListSessions",
            ReplyText = summary,
            Suggestions = sessions.Take(3)
                .Select(s => new VoiceSuggestion { Label = $"Open {DisplayName(s)}", Kind = "open", SessionId = s.Id.ToString() })
                .ToList(),
        };
    }

    private VoiceCommandResponse ReplyListWaiting(string transcript, IList<Session> sessions)
    {
        var waiting = sessions
            .Where(s => s.ActivityState is ActivityState.WaitingForInput or ActivityState.WaitingForPerm)
            .ToList();

        if (waiting.Count == 0)
        {
            return new VoiceCommandResponse
            {
                Transcript = transcript,
                Intent = "ListWaiting",
                ReplyText = "Nothing is waiting on you right now.",
            };
        }

        var summary = waiting.Count == 1
            ? $"{DisplayName(waiting[0])} is waiting for input."
            : $"{waiting.Count} sessions are waiting on you: " + string.Join(", ", waiting.Select(DisplayName)) + ".";

        return new VoiceCommandResponse
        {
            Transcript = transcript,
            Intent = "ListWaiting",
            ReplyText = summary,
            Suggestions = waiting.Take(3)
                .Select(s => new VoiceSuggestion { Label = $"Open {DisplayName(s)}", Kind = "open", SessionId = s.Id.ToString() })
                .ToList(),
        };
    }

    private VoiceCommandResponse ReplyDescribe(string transcript, string? targetHint, IList<Session> sessions)
    {
        var target = ResolveTarget(targetHint, sessions);
        if (target is null)
        {
            return NoSuchSession(transcript, "DescribeSession", targetHint);
        }

        var reply = new StringBuilder();
        reply.Append(DisplayName(target));
        reply.Append(" is ");
        reply.Append(target.ActivityState switch
        {
            ActivityState.Idle => "idle",
            ActivityState.Working => "working",
            ActivityState.WaitingForInput => "waiting for input",
            ActivityState.WaitingForPerm => "waiting for permission",
            ActivityState.Starting => "starting up",
            ActivityState.Exited => "exited",
            _ => target.ActivityState.ToString().ToLowerInvariant(),
        });
        reply.Append(". Repo: ");
        reply.Append(Path.GetFileName(target.RepoPath.TrimEnd('\\', '/')));
        reply.Append('.');

        return new VoiceCommandResponse
        {
            Transcript = transcript,
            Intent = "DescribeSession",
            TargetSessionId = target.Id.ToString(),
            TargetSessionName = DisplayName(target),
            ReplyText = reply.ToString(),
            Suggestions = new List<VoiceSuggestion>
            {
                new() { Label = $"Open {DisplayName(target)}", Kind = "open", SessionId = target.Id.ToString() },
            },
        };
    }

    private VoiceCommandResponse ReplyOpen(string transcript, string? targetHint, IList<Session> sessions)
    {
        var target = ResolveTarget(targetHint, sessions);
        if (target is null)
            return NoSuchSession(transcript, "OpenSession", targetHint);

        return new VoiceCommandResponse
        {
            Transcript = transcript,
            Intent = "OpenSession",
            TargetSessionId = target.Id.ToString(),
            TargetSessionName = DisplayName(target),
            ReplyText = $"Opening {DisplayName(target)}.",
            Suggestions = new List<VoiceSuggestion>
            {
                new() { Label = $"Open {DisplayName(target)}", Kind = "open", SessionId = target.Id.ToString() },
            },
        };
    }

    private VoiceCommandResponse ReplySend(string transcript, string? targetHint, string payload, IList<Session> sessions)
    {
        var target = ResolveTarget(targetHint, sessions);
        if (target is null)
            return NoSuchSession(transcript, "SendToSession", targetHint);

        if (string.IsNullOrWhiteSpace(payload))
        {
            return new VoiceCommandResponse
            {
                Transcript = transcript,
                Intent = "SendToSession",
                TargetSessionId = target.Id.ToString(),
                TargetSessionName = DisplayName(target),
                ReplyText = $"What would you like to send to {DisplayName(target)}?",
                Status = "unknown_command",
            };
        }

        // Do NOT send immediately. Surface as a confirmation suggestion so the user
        // taps once before a destructive prompt actually goes to the session.
        return new VoiceCommandResponse
        {
            Transcript = transcript,
            Intent = "SendToSession",
            TargetSessionId = target.Id.ToString(),
            TargetSessionName = DisplayName(target),
            ReplyText = $"Send to {DisplayName(target)}: \"{Truncate(payload, 200)}\"? Tap the button to confirm.",
            Suggestions = new List<VoiceSuggestion>
            {
                new() { Label = $"Send to {DisplayName(target)}", Kind = "send", SessionId = target.Id.ToString(), PromptText = payload },
                new() { Label = $"Open {DisplayName(target)}", Kind = "open", SessionId = target.Id.ToString() },
            },
        };
    }

    private VoiceCommandResponse ReplyInterrupt(string transcript, string? targetHint, IList<Session> sessions)
    {
        var target = ResolveTarget(targetHint, sessions);
        if (target is null)
            return NoSuchSession(transcript, "InterruptSession", targetHint);

        return new VoiceCommandResponse
        {
            Transcript = transcript,
            Intent = "InterruptSession",
            TargetSessionId = target.Id.ToString(),
            TargetSessionName = DisplayName(target),
            ReplyText = $"Interrupt {DisplayName(target)}? Tap the button to confirm.",
            Suggestions = new List<VoiceSuggestion>
            {
                new() { Label = $"Interrupt {DisplayName(target)}", Kind = "interrupt", SessionId = target.Id.ToString() },
            },
        };
    }

    private static VoiceCommandResponse NoSuchSession(string transcript, string intent, string? hint)
    {
        return new VoiceCommandResponse
        {
            Transcript = transcript,
            Intent = intent,
            Status = "unknown_command",
            ReplyText = string.IsNullOrEmpty(hint)
                ? "Which session did you mean?"
                : $"I could not find a session matching '{hint}'.",
        };
    }

    // ====== Helpers ====================================================================

    private static string DisplayName(Session s)
    {
        if (!string.IsNullOrWhiteSpace(s.CustomName))
            return s.CustomName!.Trim();
        var folder = Path.GetFileName(s.RepoPath.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(folder) ? s.Id.ToString()[..8] : folder;
    }

    private static int SortKey(ActivityState state) => state switch
    {
        ActivityState.WaitingForPerm => 10,
        ActivityState.WaitingForInput => 9,
        ActivityState.Working => 5,
        ActivityState.Idle => 3,
        ActivityState.Starting => 2,
        _ => 0,
    };

    /// <summary>
    /// Match a transcript-supplied target name to a live session.
    /// Strategy: case-insensitive substring match against CustomName first,
    /// then against the repo folder name. Pick the shortest match (most specific).
    /// </summary>
    private static Session? ResolveTarget(string? hint, IList<Session> sessions)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        var needle = hint.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(needle)) return null;

        // Pass 1: exact match on display name
        foreach (var s in sessions)
        {
            if (DisplayName(s).Equals(needle, StringComparison.OrdinalIgnoreCase))
                return s;
        }

        // Pass 2: substring on display name
        var candidates = sessions
            .Where(s => DisplayName(s).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => DisplayName(s).Length)
            .ToList();
        if (candidates.Count > 0) return candidates[0];

        // Pass 3: substring on repo path
        candidates = sessions
            .Where(s => s.RepoPath.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.RepoPath.Length)
            .ToList();
        return candidates.Count > 0 ? candidates[0] : null;
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}

// ============================================================================
// Intent model + parser. Kept private to this file - a future Haiku-based
// classifier would replace IntentParser.Parse without changing the surface.
// ============================================================================

internal enum VoiceIntent
{
    Unknown,
    ListSessions,
    ListWaiting,
    DescribeSession,
    OpenSession,
    SendToSession,
    InterruptSession,
}

internal sealed record VoiceCommand(VoiceIntent Intent, string? Target, string? Payload);

internal static class IntentParser
{
    // Order matters: most-specific / fleet-level patterns first.  In particular
    // ListSessions must come before OpenSession because "show me my sessions"
    // and "show sessions" would otherwise be eaten by OpenSession's "show X".
    private static readonly (Regex Pattern, Func<Match, VoiceCommand> Build)[] Rules = new (Regex, Func<Match, VoiceCommand>)[]
    {
        // ----- Fleet-level (no target) -----------------------------------------------
        // "what sessions are running" | "list sessions" | "what's running" | "show (me) (my) sessions"
        (new Regex(@"^\s*(?:what\s+sessions(?:\s+are\s+running)?|list\s+sessions|what(?:'s|\s+is)\s+running|show(?:\s+me)?\s+(?:my\s+)?sessions)\s*[.?!]?\s*$",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         _ => new VoiceCommand(VoiceIntent.ListSessions, null, null)),

        // "what's waiting" | "what's pending" | "what needs me" | "who's waiting"
        (new Regex(@"^\s*(?:what(?:'s|\s+is)\s+(?:waiting|pending)|what\s+needs\s+me|who(?:'s|\s+is)\s+waiting)\s*[.?!]?\s*$",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         _ => new VoiceCommand(VoiceIntent.ListWaiting, null, null)),

        // ----- Session-level (target = remaining text) -------------------------------
        // "send to X: Y"   |   "tell X to Y"
        (new Regex(@"^\s*(?:send|say|tell)\s+(?:to\s+)?(?<target>[^:,]+?)(?:\s*[:,]\s*|\s+to\s+)(?<payload>.+?)\s*[.?!]?\s*$",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         m => new VoiceCommand(VoiceIntent.SendToSession, m.Groups["target"].Value, m.Groups["payload"].Value)),

        // "interrupt X" | "stop X" | "cancel X"
        (new Regex(@"^\s*(?:interrupt|stop|cancel)\s+(?<target>.+?)\s*[.?!]?\s*$",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         m => new VoiceCommand(VoiceIntent.InterruptSession, m.Groups["target"].Value, null)),

        // "what is X doing" | "what's X doing" | "what's X up to" | "tell me about X"
        (new Regex(@"^\s*(?:what(?:'s|\s+is)\s+(?<target>.+?)\s+(?:doing|up\s+to|working\s+on)|tell\s+me\s+about\s+(?<target2>.+?))\s*[.?!]?\s*$",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         m => new VoiceCommand(VoiceIntent.DescribeSession,
                               m.Groups["target"].Success ? m.Groups["target"].Value : m.Groups["target2"].Value,
                               null)),

        // "open X" | "switch to X" | "show me X" | "show X"
        (new Regex(@"^\s*(?:open|switch\s+to|show(?:\s+me)?)\s+(?<target>.+?)\s*[.?!]?\s*$",
                   RegexOptions.IgnoreCase | RegexOptions.Compiled),
         m => new VoiceCommand(VoiceIntent.OpenSession, m.Groups["target"].Value, null)),
    };

    public static VoiceCommand Parse(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new VoiceCommand(VoiceIntent.Unknown, null, null);

        var text = transcript.Trim();
        foreach (var (pattern, build) in Rules)
        {
            var m = pattern.Match(text);
            if (m.Success) return build(m);
        }
        return new VoiceCommand(VoiceIntent.Unknown, null, null);
    }
}
