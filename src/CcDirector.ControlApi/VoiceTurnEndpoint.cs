using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice;
using CcDirector.Core.Voice.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.ControlApi;

/// <summary>
/// Maps POST /sessions/{id}/voice-turn - the server-side walkie-talkie turn (issue #351).
///
/// One call = one complete turn:
///   1. (optional) Transcribe the whole audio clip ONCE through the shared batch pipeline
///      (issue #587) using the user-selected transcription method (issue #592) - the base URL,
///      key, and model come from the Gateway routing resolver, NOT a hardcoded whisper-1 path -
///      then apply the validated dictionary corrector only (no free-text cleanup). The
///      completeness gate ran upstream (the Gateway reassembles the whole clip before
///      forwarding), so a partial upload never reaches transcription.
///   2. Wait until the session is ready (not mid-turn).
///   3. POST the text to the session via /prompt.
///   4. Poll until the turn is done.
///   5. Call the ClaudeSummarizer to produce 2-3 sentences of plain spoken prose.
///   6. Synthesize via TtsService and return audio bytes in the reply event.
///   7. If summarizer or TTS unavailable, fall back to SpokenText from the last
///      JSONL assistant message of THIS turn (the transcript length is snapshotted
///      before the text is posted, and only content appended after that offset is
///      read - issue #366), synthesize that, and return it - never goes silent.
///
/// Response: Server-Sent Events (text/event-stream). One data: JSON per stage, then
/// a final {"stage":"reply","summary":"...","audioBase64":"..."}. On any terminal
/// error the stream ends with {"stage":"error","message":"..."}.
///
/// The endpoint is intentionally stateless: multiple turns = multiple calls.
/// </summary>
internal static class VoiceTurnEndpoint
{
    // Max time to wait for a session to become ready before giving up.
    private static readonly TimeSpan ReadyWaitTimeout = TimeSpan.FromSeconds(60);
    // Max time to wait for a turn to complete after posting text.
    private static readonly TimeSpan TurnCompleteTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public static void Map(IEndpointRouteBuilder app, SessionManager sessionManager)
    {
        app.MapPost("/sessions/{sid}/voice-turn", async (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[VoiceTurnEndpoint] POST /sessions/{sid}/voice-turn from {ctx.Connection.RemoteIpAddress}");

            // --- validate session ---
            if (!Guid.TryParse(sid, out var guid))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "invalid session id format" }));
                return;
            }

            var session = sessionManager.GetSession(guid);
            if (session is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "session not found" }));
                return;
            }

            if (session.Status is SessionStatus.Exited or SessionStatus.Failed)
            {
                ctx.Response.StatusCode = StatusCodes.Status410Gone;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "session has exited" }));
                return;
            }

            var options = sessionManager.Options;
            var ct = ctx.RequestAborted;

            // Switch to SSE before any processing so stage events land at the client.
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";

            async Task EmitAsync(object payload)
            {
                var json = JsonSerializer.Serialize(payload);
                await ctx.Response.WriteAsync($"data: {json}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }

            try
            {
                // --- step 1: resolve input text ---
                string? inputText;
                if (ctx.Request.HasFormContentType)
                {
                    var form = await ctx.Request.ReadFormAsync(ct);

                    // text field takes precedence (pre-transcribed or test path)
                    var textField = form["text"].ToString();
                    if (!string.IsNullOrWhiteSpace(textField))
                    {
                        inputText = textField;
                    }
                    else
                    {
                        var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
                        if (file is null || file.Length == 0)
                        {
                            await EmitAsync(new { stage = "error", message = "either 'text' or 'audio' form field is required" });
                            return;
                        }

                        await EmitAsync(new { stage = "transcribing" });

                        // The whole clip is transcribed ONCE through the ONE shared batch pipeline
                        // (issue #587) using the user-selected method (issue #592): the base URL,
                        // key, and model come from the Gateway routing resolver, NOT a hardcoded
                        // whisper-1 / api.openai.com endpoint, so changing the selected method
                        // changes the base URL the upload is transcribed against. The only
                        // post-transcription transform is the validated dictionary corrector -
                        // there is NO free-text language-model cleanup - so an utterance with no
                        // dictionary term comes back byte-identical to the raw transcription. The
                        // completeness gate ran upstream (the Gateway reassembles the whole clip
                        // before forwarding it here), so the bytes are already complete.
                        await using var stream = file.OpenReadStream();
                        var voice = new VoiceService(sessionManager, options);
                        var transcription = await voice.TranscribeAndCleanAsync(stream, file.FileName, session.RepoPath, ct);

                        if (string.Equals(transcription.Status, "no_key", StringComparison.Ordinal))
                        {
                            FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: transcription routing unavailable (no key for selected method)");
                            await EmitAsync(new { stage = "error", message = "no_key: " + (transcription.Error ?? "transcription routing unavailable") });
                            return;
                        }
                        if (!string.Equals(transcription.Status, "ok", StringComparison.Ordinal))
                        {
                            FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: transcription FAILED: {transcription.Error}");
                            await EmitAsync(new { stage = "error", message = "transcription_failed: " + (transcription.Error ?? "unknown") });
                            return;
                        }

                        // Use the dictionary-corrected transcript (raw text plus known dictionary
                        // term swaps only) as the turn's input.
                        inputText = transcription.CleanedTranscript ?? transcription.Transcript;

                        await EmitAsync(new { stage = "transcript", text = inputText });
                    }
                }
                else
                {
                    // JSON body path
                    VoiceTurnRequest? req;
                    try
                    {
                        req = await ctx.Request.ReadFromJsonAsync<VoiceTurnRequest>(ct);
                    }
                    catch
                    {
                        await EmitAsync(new { stage = "error", message = "invalid JSON body; expected { \"text\": \"...\" }" });
                        return;
                    }

                    if (req is null || string.IsNullOrWhiteSpace(req.Text))
                    {
                        await EmitAsync(new { stage = "error", message = "text is required (send { \"text\": \"...\" } or multipart/form-data with 'text' or 'audio')" });
                        return;
                    }
                    inputText = req.Text;
                }

                // --- step 2: wait until session is ready ---
                var readyDeadline = DateTime.UtcNow + ReadyWaitTimeout;
                while (true)
                {
                    if (ct.IsCancellationRequested) return;
                    var st = session.ActivityState;
                    if (st is ActivityState.Idle or ActivityState.WaitingForInput)
                        break;
                    if (st is ActivityState.Exited || session.Status is SessionStatus.Exited or SessionStatus.Failed)
                    {
                        await EmitAsync(new { stage = "error", message = "session exited before the turn could start" });
                        return;
                    }
                    if (DateTime.UtcNow >= readyDeadline)
                    {
                        await EmitAsync(new { stage = "error", message = "timeout waiting for session to become ready" });
                        return;
                    }
                    await EmitAsync(new { stage = "waiting" });
                    try { await Task.Delay(PollInterval, ct); } catch (OperationCanceledException) { return; }
                }

                // --- step 3: post text to the session ---
                // Snapshot the transcript length BEFORE sending so the reply read
                // after the turn only sees assistant text appended for THIS turn.
                // Without this, a turn whose reply has not been written yet would
                // replay the PREVIOUS turn's output (issue #366).
                var offsetBefore = SnapshotTranscriptLength(session);
                FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: sending text len={inputText.Length}, transcriptOffsetBefore={offsetBefore}");
                try
                {
                    await session.SendTextAsync(inputText);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[VoiceTurnEndpoint] SendTextAsync FAILED: {ex.Message}");
                    await EmitAsync(new { stage = "error", message = "send_failed: " + ex.Message });
                    return;
                }

                // --- step 4: poll until the turn is done ---
                var turnDeadline = DateTime.UtcNow + TurnCompleteTimeout;
                while (true)
                {
                    if (ct.IsCancellationRequested) return;
                    try { await Task.Delay(PollInterval, ct); } catch (OperationCanceledException) { return; }

                    var st = session.ActivityState;
                    if (st is ActivityState.Idle or ActivityState.WaitingForInput or ActivityState.WaitingForPerm)
                        break;
                    if (st is ActivityState.Exited || session.Status is SessionStatus.Exited or SessionStatus.Failed)
                    {
                        await EmitAsync(new { stage = "error", message = "session exited mid-turn" });
                        return;
                    }
                    if (DateTime.UtcNow >= turnDeadline)
                    {
                        await EmitAsync(new { stage = "error", message = "timeout waiting for turn to complete" });
                        return;
                    }
                    await EmitAsync(new { stage = "thinking" });
                }

                // --- step 5: summarize the reply ---
                await EmitAsync(new { stage = "summarizing" });

                var rawReply = ReadNewAssistantText(session, offsetBefore);
                string summary;
                try
                {
                    var summarizer = new ClaudeSummarizer();
                    if (summarizer.IsAvailable && !string.IsNullOrWhiteSpace(rawReply))
                    {
                        summary = await summarizer.SummarizeAsync(rawReply, ct);
                        if (string.IsNullOrWhiteSpace(summary))
                        {
                            FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: summarizer returned empty, falling back");
                            summary = BuildFallbackSummary(rawReply);
                        }
                    }
                    else
                    {
                        FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: summarizer unavailable or no reply, using fallback");
                        summary = BuildFallbackSummary(rawReply);
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[VoiceTurnEndpoint] summarizer FAILED: {ex.Message}, using fallback");
                    summary = BuildFallbackSummary(rawReply);
                }

                if (string.IsNullOrWhiteSpace(summary))
                    summary = "Done.";

                // --- step 6: synthesize to audio ---
                var ttsSvc = new TtsService(options);
                byte[] audioBytes;
                if (!ttsSvc.IsAvailable)
                {
                    FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: TTS unavailable, returning empty audio");
                    audioBytes = Array.Empty<byte>();
                }
                else
                {
                    try
                    {
                        var ttsResult = await ttsSvc.GenerateAsync(summary, voiceOverride: null, modelOverride: null, ct);
                        if (!ttsResult.Success || ttsResult.AudioBytes is null)
                        {
                            FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: TTS failed ({ttsResult.Status}), returning empty audio");
                            audioBytes = Array.Empty<byte>();
                        }
                        else
                        {
                            audioBytes = ttsResult.AudioBytes;
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLog.Write($"[VoiceTurnEndpoint] TTS FAILED: {ex.Message}, returning empty audio");
                        audioBytes = Array.Empty<byte>();
                    }
                }

                // --- step 7: emit the reply event ---
                var audioBase64 = Convert.ToBase64String(audioBytes);
                FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: reply summary_len={summary.Length} audio_bytes={audioBytes.Length}");
                await EmitAsync(new { stage = "reply", summary, audioBase64 });
            }
            catch (OperationCanceledException)
            {
                FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: request cancelled");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[VoiceTurnEndpoint] sid={guid}: UNHANDLED: {ex.Message}");
                try
                {
                    await EmitAsync(new { stage = "error", message = "internal_error: " + ex.Message });
                }
                catch { /* best effort - response may already be partially written */ }
            }
            finally
            {
                // Explicitly complete the response so the client sees EOF on the SSE stream.
                // Without this, ASP.NET Core Minimal API may hold the chunked connection open
                // until Kestrel's idle timeout, causing clients to block on ReadAsStringAsync.
                try { await ctx.Response.CompleteAsync(); }
                catch { /* best effort */ }
            }
        });
    }

    // ===== Helpers =============================================================

    /// <summary>
    /// Snapshot the byte length of the session's linked JSONL transcript.
    /// Taken immediately before SendTextAsync so <see cref="ReadNewAssistantText"/>
    /// can restrict the reply read to content appended during THIS turn (issue #366).
    /// Returns 0 when the session is unlinked or the file does not exist yet
    /// (then everything in the file is new).
    /// </summary>
    private static long SnapshotTranscriptLength(Session session)
    {
        try
        {
            var jsonl = GetTranscriptPath(session);
            return jsonl is not null && File.Exists(jsonl) ? new FileInfo(jsonl).Length : 0L;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnEndpoint] SnapshotTranscriptLength FAILED: {ex.Message}");
            return 0L;
        }
    }

    /// <summary>
    /// Read the last assistant text block appended to the session's linked JSONL
    /// transcript at or after <paramref name="offsetBefore"/> (the byte length
    /// snapshotted before the turn's text was sent). Assistant content written
    /// for PREVIOUS turns sits below the offset and is never returned, so a turn
    /// whose reply has not been written yet yields "" instead of replaying the
    /// previous turn's output (issue #366). Returns empty string when the session
    /// is unlinked or the file is not there yet.
    /// </summary>
    private static string ReadNewAssistantText(Session session, long offsetBefore)
    {
        try
        {
            var jsonl = GetTranscriptPath(session);
            if (jsonl is null || !File.Exists(jsonl)) return "";
            var messages = Core.Claude.StreamMessageParser.ParseFileFromOffset(jsonl, offsetBefore);
            var summary = Core.Claude.SummaryBuilder.Build(messages);
            return summary.LastAssistantText ?? "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[VoiceTurnEndpoint] ReadNewAssistantText FAILED: {ex.Message}");
            return "";
        }
    }

    /// <summary>Path of the session's linked JSONL transcript, or null when unlinked.</summary>
    private static string? GetTranscriptPath(Session session)
    {
        if (string.IsNullOrEmpty(session.ClaudeSessionId)) return null;
        return Core.Claude.ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
    }

    /// <summary>
    /// Fallback when the LLM summarizer is unavailable or returns empty.
    /// Takes up to the first 500 characters of the raw reply, stripping
    /// obvious markdown so it sounds reasonable when read aloud.
    /// </summary>
    private static string BuildFallbackSummary(string? rawReply)
    {
        if (string.IsNullOrWhiteSpace(rawReply))
            return "";

        // Strip the most egregious markdown before truncating.
        var text = rawReply;
        // Remove code blocks (```...```)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"```[\s\S]*?```", "");
        // Remove inline code (`...`)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"`[^`]+`", "");
        // Remove headers (## Title)
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#+\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        // Remove bold/italic
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\*{1,3}([^*]+)\*{1,3}", "$1");
        // Collapse whitespace
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        // Limit length - a sentence or two is the fallback target.
        const int MaxFallbackChars = 500;
        if (text.Length > MaxFallbackChars)
            text = text[..MaxFallbackChars].Trim() + "...";

        return text;
    }
}

/// <summary>Request body for the JSON path of POST /sessions/{id}/voice-turn.</summary>
internal sealed class VoiceTurnRequest
{
    public string? Text { get; set; }
}
