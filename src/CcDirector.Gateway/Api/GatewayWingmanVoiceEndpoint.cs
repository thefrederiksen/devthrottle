using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Services;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Voice;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The wingman-voice surface for the Cockpit's Voice tab (issue #531). Two shapes, both
/// backed by the gateway's one persistent, configured wingman session (the warm brain) via
/// <see cref="WingmanTranslator"/> - never <c>--print</c>:
///
///   POST /sessions/{sid}/wingman/voice-turn  { text }
///        Drive ONE turn of the working session: send the text into the session (on its
///        owning Director), wait for the turn to finish, read the agent's reply, then have
///        the wingman translate it into a faithful, speakable summary. Returns
///        { reply, spoken, replySeconds }. The Voice tab shows the spoken summary silently;
///        the person taps to hear it.
///
///   POST /wingman/ask-direct  { text }
///        The direct-to-wingman path: the person talks to the wingman itself, NOT the
///        working session. Returns { spoken }.
///
/// This is the text-and-voice-shared pipeline: the Text tab and the Voice tab both call
/// /wingman/voice-turn with the person's message (typed, or spoken-then-transcribed), so the
/// text tab proves the translation and the voice tab inherits it unchanged.
/// </summary>
internal static class GatewayWingmanVoiceEndpoint
{
    /// <summary>How long to wait for one working-session turn to finish before giving up.</summary>
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Let the transcript flush after the session goes quiet before reading the reply.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2.5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>OpenAI text-to-speech defaults: tts-1 is fast and the "nova" voice is far more
    /// natural than the browser's built-in speech synthesis (issue #531 follow-up).</summary>
    private const string TtsModel = "tts-1";
    private const string TtsVoice = "nova";
    private const int TtsMaxChars = 4000; // OpenAI per-call cap is 4096; spoken summaries are short.

    public static void Map(
        IEndpointRouteBuilder app,
        DirectorRegistry registry,
        DirectorEndpointClient client,
        Func<CancellationToken, Task<IAgentBrain>> brainProvider,
        KeyVault vault,
        WingmanVoiceService voice,
        Func<string>? instructionsProvider = null)
    {
        var translator = new WingmanTranslator(brainProvider, instructionsProvider: instructionsProvider);

        // The single Gateway owner of speech-to-text (issue #839): both batch transcribe paths below
        // (the resumable /wingman/utterance/complete and the one-shot /wingman/transcribe) go through
        // it, so they resolve the mode + key and pick the provider (local Whisper or the resolved
        // remote endpoint) exactly the same way every other batch caller does - no second resolver,
        // and DevThrottle/on-device modes are honored, not just bring-your-own OpenAI.
        var transcription = new Transcription.GatewayTranscriptionService(vault);

        // Which voice sessions have a ready, playable spoken summary right now (the phone's list
        // shows a play button on these and can play without entering).
        app.MapGet("/wingman/voice/ready", () => Results.Json(new { sids = voice.ReadySessionIds() }));

        // The precomputed spoken summary for a session (instant on entry - no re-read needed).
        app.MapGet("/sessions/{sid}/wingman/voice", (string sid) =>
        {
            var v = voice.Get(sid);
            return v is null
                ? Results.Json(new { ready = false })
                : Results.Json(new { ready = true, spoken = v.Spoken, reply = v.Reply, generatedAt = v.AtUtc });
        });

        // The precomputed audio for a session - streamed so the list can play it with one tap.
        app.MapGet("/sessions/{sid}/wingman/voice/audio", (string sid) =>
        {
            var audio = voice.GetAudio(sid);
            return audio is { Length: > 0 }
                ? Results.Bytes(audio, "audio/mpeg", enableRangeProcessing: true)
                : Results.Json(new { error = "no voice ready for this session" }, statusCode: StatusCodes.Status404NotFound);
        });

        // Turn voice off for a session (issue #859): unmark it as a voice session so the gateway
        // STOPS spending the per-turn Opus translation + OpenAI text-to-speech on it. This is the
        // counterpart to the marking that POST /sessions/{sid}/wingman/explain performs on entry; the
        // phone's "Turn voice off" calls it alongside the Director's /voice-mode { enabled:false }.
        // Gateway-side only and read-only - it clears the voice marker + cached clip and sends nothing
        // into the session. Idempotent: stopping a session that was not a voice session is a no-op 200.
        app.MapPost("/sessions/{sid}/wingman/voice/stop", (string sid) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] voice/stop sid={sid}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            voice.Unmark(sid);
            return Results.Json(new { stopped = true });
        });
        // Resumable, idempotent piece-by-piece upload store (the same one the native app path uses):
        // chunks land on disk under a stable upload id and survive between retry attempts, so the
        // phone can keep re-sending pieces until the whole recording is through.
        var uploads = new VoiceUploadStore();

        // ===== Resumable transcription upload (issue #531: drive-safe, keeps trying) =====
        // The phone records locally (works offline), then ships the recording in pieces here and
        // keeps retrying until every piece lands - no user buttons. When all pieces are in, the
        // assembled audio is transcribed, the validated dictionary correction is applied (the same
        // engine every other surface uses; raw is returned in local mode or on any cleanup error),
        // and the corrected text is returned.
        //   POST   /wingman/utterance/upload                  -> { upload_id }
        //   PUT    /wingman/utterance/{id}/chunk/{i}           -> { ok }   (idempotent)
        //   POST   /wingman/utterance/{id}/complete {total}    -> { transcript } | 409 { missing }
        app.MapPost("/wingman/utterance/upload", (HttpContext ctx) =>
        {
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            var id = uploads.Register(string.IsNullOrWhiteSpace(key) ? null : key);
            return Results.Json(new { upload_id = id });
        });

        app.MapPut("/wingman/utterance/{uploadId}/chunk/{index:int}", async (string uploadId, int index, HttpContext ctx, CancellationToken ct) =>
        {
            if (!uploads.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);
            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            try { await uploads.StoreChunkAsync(uploadId, index, ms.ToArray(), string.IsNullOrEmpty(sha) ? null : sha, ct); return Results.Json(new { ok = true, index }); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest); }
        });

        app.MapPost("/wingman/utterance/{uploadId}/complete", async (string uploadId, UtteranceCompleteRequest? req, CancellationToken ct) =>
        {
            if (req is null || req.TotalChunks <= 0)
                return Results.Json(new { error = "totalChunks (>0) is required" }, statusCode: StatusCodes.Status400BadRequest);
            if (!uploads.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            // Mode-aware (issue #541): in Local mode there is NO key check - transcription runs
            // in-process. In a remote mode (byo/devthrottle) the configured key must be present. The
            // single transcription owner resolves this; check it BEFORE assembling so a no-key request
            // does not pay the reassembly cost.
            var routing = transcription.Resolve();
            if (!routing.IsLocal && routing.Key is null)
                return Results.Json(new { error = $"no key configured for transcription mode {routing.Mode.ToConfigString()}" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            AssembleResult assembled;
            try { assembled = await uploads.AssembleAsync(uploadId, req.TotalChunks, ct); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest); }

            if (assembled.Status == "unknown_upload")
                return Results.Json(new { error = "unknown upload id" }, statusCode: StatusCodes.Status404NotFound);
            if (assembled.Status == "incomplete")
                return Results.Json(new { status = "incomplete", missing = assembled.Missing }, statusCode: StatusCodes.Status409Conflict);

            var assembledAudio = assembled.Audio;
            if (assembledAudio is null || assembledAudio.Length == 0)
            {
                uploads.Delete(uploadId);
                return Results.Json(new { error = "assembled recording was empty" }, statusCode: StatusCodes.Status502BadGateway);
            }

            // Transcribe through the single owner WITH the validated dictionary correction applied (the
            // SAME engine every other surface uses; fails open to raw in local mode or on any error).
            var result = await transcription.TranscribeAsync(
                assembledAudio, "audio." + (req.Ext ?? "webm"), req.Mime ?? "audio/webm", applyCorrection: true, ct);
            uploads.Delete(uploadId);
            if (result.Outcome != Transcription.TranscriptionOutcome.Ok)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
            FileLog.Write($"[GatewayWingmanVoice] utterance complete {uploadId}: mode={result.Mode}, chars={result.Text?.Length ?? 0}");
            return Results.Json(new { transcript = result.Text });
        });

        // OpenAI text-to-speech for the mobile Voice screen: turn the wingman's spoken summary into
        // natural-sounding audio (the browser's own voice is robotic). Returns audio/mpeg bytes the
        // page plays in an <audio> element. The key comes from the gateway key vault.
        app.MapPost("/wingman/tts", async (WingmanTtsRequest? req, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            var key = vault.Get("OPENAI_API_KEY");
            if (string.IsNullOrWhiteSpace(key))
                return Results.Json(new { error = "no OpenAI key configured in the gateway vault" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var input = req.Text.Length > TtsMaxChars ? req.Text[..TtsMaxChars] : req.Text;
            var voice = string.IsNullOrWhiteSpace(req.Voice) ? TtsVoice : req.Voice.Trim();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var payload = JsonContent.Create(new { model = TtsModel, voice, input, response_format = "mp3" });
                using var resp = await http.PostAsync("https://api.openai.com/v1/audio/speech", payload, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    FileLog.Write($"[GatewayWingmanVoice] tts OpenAI {(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
                    return Results.Json(new { error = $"OpenAI text-to-speech returned {(int)resp.StatusCode}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                FileLog.Write($"[GatewayWingmanVoice] tts ok: chars={input.Length}, bytes={bytes.Length}, voice={voice}");
                return Results.Bytes(bytes, "audio/mpeg");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] tts FAILED: {ex.Message}");
                return Results.Json(new { error = "text-to-speech failed: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/sessions/{sid}/wingman/voice-turn", async (string sid, WingmanVoiceTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}, textLen={req?.Text?.Length ?? 0}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            var endpoint = await ResolveEndpointAsync(sid, registry, client, ct);
            if (endpoint is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);

            // Menu handling (issue #531): if the agent is RIGHT NOW showing an on-screen menu, the
            // person's words are a CHOICE, not a new prompt. Detect it, map the words to an option,
            // and PRESS that option (raw keystrokes) - never type the spoken words as a prompt.
            var menu = await DetectMenuAtAsync(client, translator, endpoint, sid, ct);
            if (menu.IsMenu)
            {
                var idx = WingmanMenuLogic.MatchOption(menu, req.Text);
                if (idx < 0) idx = await translator.MapChoiceAsync(menu, req.Text, ct);
                if (idx >= 0 && idx < menu.Options.Count)
                {
                    var opt = menu.Options[idx];
                    var submit = string.Equals(menu.SelectionMode, "multiple", StringComparison.OrdinalIgnoreCase) ? menu.Submit : "";
                    FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: menu choice -> option {idx + 1}");
                    return await PressAndSummarizeAsync(client, translator, voice, endpoint, sid, opt.Send, submit, $"Selecting option {idx + 1}. ", "voice-menu", ct);
                }
                // Heard them, but no confident option: re-read the menu and send NOTHING (don't burn the turn).
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: menu present, choice unclear");
                return Results.Json(new { reply = "", spoken = "I didn't catch which one. " + menu.Spoken, needsChoice = true, menu = MenuJson(menu) });
            }

            // We are about to start a new turn, so the cached spoken summary + audio are now stale.
            // Clear them DETERMINISTICALLY here (do not rely on observing the Working state, which is
            // racy for fast turns) - the list stops showing it ready and nothing stale plays. The
            // fresh summary is stored below once the agent replies.
            voice.OnSessionWorking(sid);

            // Snapshot the widget list BEFORE sending: gives both (a) the count for the issue #366
            // guard (only read widgets that are new after the send) and (b) the prior conversation
            // for the wingman so it can resolve references in the agent's reply. The current question
            // is appended below; BuildRecentContext is called on the pre-send snapshot so it
            // excludes the new turn and includes only what came before.
            var snapshotWidgets = (await client.GetTurnsAsync(endpoint, sid, ct))?.Widgets
                ?? new List<TurnWidgetDto>();
            var widgetsBefore = snapshotWidgets.Count;
            var priorContext = WingmanTranslator.BuildRecentContext(snapshotWidgets);

            var (ok, _, sendErr) = await client.PostPromptAsync(endpoint, sid, new PromptRequest { Text = req.Text, AppendEnter = true }, ct);
            if (!ok)
                return Results.Json(new { error = "send failed: " + sendErr }, statusCode: StatusCodes.Status502BadGateway);

            var reply = await WaitForReplyAsync(client, endpoint, sid, widgetsBefore, ct);
            if (string.IsNullOrWhiteSpace(reply))
                return Results.Json(new { error = "the agent did not produce a reply in time" }, statusCode: StatusCodes.Status504GatewayTimeout);

            // The agent replied; now the wingman translates it. This is gateway-owned work
            // (CancellationToken.None) so navigating away does not lose the summary, and the
            // session shows YELLOW while the wingman runs, then back to red (issue #531 voice mode).
            voice.BeginGenerating(sid);
            try
            {
                // Full context: prior exchanges from the pre-send snapshot + the current question,
                // so the wingman can resolve references like "that file" or "the bug I mentioned".
                var recentContext = string.IsNullOrWhiteSpace(priorContext)
                    ? "You: " + req.Text.Trim()
                    : priorContext + "\n\nYou: " + req.Text.Trim();
                var t = await translator.TranslateAsync(recentContext, reply, CancellationToken.None);
                await voice.StoreSpokenAsync(sid, t.Spoken, reply, CancellationToken.None);   // make it a voice session + cache audio
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: replyLen={reply.Length}, spokenLen={t.Spoken.Length}");
                // Training capture (no-op unless the setting is on); fire-and-forget so it adds no latency.
                _ = voice.CaptureTrainingAsync(endpoint, sid, "voice-turn", reply, recentContext, t.Spoken, t.ReplySeconds, CancellationToken.None);
                return Results.Json(new { reply, spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid} translate FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman translation failed: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            finally { voice.EndGenerating(sid); }
        });

        // Transcription (issue #531 follow-up): the phone records audio locally (survives a bad
        // connection / reload), then ships the recording here to be transcribed - the same
        // record-then-ship-then-transcribe shape the native mobile app uses. Robustness lives on
        // the CLIENT (save-first + retry the upload); this endpoint is the single transcribe step.
        // Audio arrives as a multipart 'audio' file; the raw transcript then runs through the
        // validated dictionary correction (raw is returned in local mode or on any cleanup error).
        // Returns { transcript }.
        app.MapPost("/wingman/transcribe", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.Json(new { error = "send the recording as multipart form-data with an 'audio' file" },
                    statusCode: StatusCodes.Status400BadRequest);

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.Json(new { error = "no audio in the upload" }, statusCode: StatusCodes.Status400BadRequest);

            // Mode-aware (issue #541): Local mode transcribes in-process with no key; remote modes
            // (byo/devthrottle) require the configured key to be present in the vault. The single
            // transcription owner resolves this and runs the right provider.
            var routing = transcription.Resolve();
            if (!routing.IsLocal && routing.Key is null)
                return Results.Json(new { error = $"no key configured for transcription mode {routing.Mode.ToConfigString()}" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            byte[] bytes;
            using (var ms = new MemoryStream()) { await file.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
            var fileName = string.IsNullOrWhiteSpace(file.FileName) ? "audio.webm" : file.FileName;
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

            // Transcribe WITH the validated dictionary correction applied (the SAME engine every other
            // surface uses; fails open to the raw transcript in local mode or on any cleanup error).
            var result = await transcription.TranscribeAsync(bytes, fileName, contentType, applyCorrection: true, ct);
            if (result.Outcome != Transcription.TranscriptionOutcome.Ok)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
            return Results.Json(new { transcript = result.Text });
        });

        // Read-only "explain what's happening" (issue #531): the wingman reads the session's
        // LAST completed turn and speaks a faithful summary of it - WITHOUT sending anything into
        // the session. This is what the mobile Voice screen fires the moment you open a session,
        // so you get a spoken summary even though a normal (text) session never produced voice.
        app.MapPost("/sessions/{sid}/wingman/explain", async (string sid, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);

            var endpoint = await ResolveEndpointAsync(sid, registry, client, ct);
            if (endpoint is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);

            var turns = await client.GetTurnsAsync(endpoint, sid, ct);
            var widgets = turns?.Widgets ?? new List<TurnWidgetDto>();
            var lastReply = widgets.LastOrDefault(w => w.Kind == "Text")?.Content;
            // Recent conversation so the wingman can give context to a short/terse reply.
            var recentContext = WingmanTranslator.BuildRecentContext(widgets);

            voice.Mark(sid);   // opening voice on a session makes it a voice session (kept fresh on turn-end)
            if (string.IsNullOrWhiteSpace(lastReply))
            {
                // A fresh or text-only session with nothing to read yet: a truthful canned line,
                // no brain call.
                return Results.Json(new
                {
                    reply = "",
                    spoken = "This session has not produced anything to summarize yet. Ask it something and I will read the answer back to you.",
                    replySeconds = 0.0,
                    nothingYet = true,
                });
            }

            // The GATEWAY owns this work, not the page (issue #531 voice mode): run the translation
            // and synthesis on CancellationToken.None so it COMPLETES and caches even if the phone
            // navigates away or the request is abandoned mid-read - returning to the session then
            // loads the finished summary from cache instead of losing it. Mark the session generating
            // so it shows YELLOW ("not ready yet") for the duration, then back to red.
            voice.BeginGenerating(sid);
            try
            {
                var t = await translator.TranslateAsync(recentContext, lastReply, CancellationToken.None);
                await voice.StoreSpokenAsync(sid, t.Spoken, lastReply, CancellationToken.None);   // cache spoken + audio, ready to play
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}: replyLen={lastReply.Length}, spokenLen={t.Spoken.Length}");
                // Training capture (no-op unless the setting is on); fire-and-forget so it adds no latency.
                _ = voice.CaptureTrainingAsync(endpoint, sid, "explain", lastReply, recentContext, t.Spoken, t.ReplySeconds, CancellationToken.None);
                return Results.Json(new { reply = lastReply, spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid} FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman could not summarize: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            finally { voice.EndGenerating(sid); }
        });

        app.MapPost("/wingman/ask-direct", async (WingmanVoiceTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] ask-direct textLen={req?.Text?.Length ?? 0}");
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var t = await translator.AskDirectAsync(req.Text, ct);
                return Results.Json(new { spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] ask-direct FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman failed: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // The DevThrottle product/docs Q&A path (issue #472): the Cockpit Learning page posts a
        // free-text question ABOUT THE PRODUCT here and the warm brain answers it, grounded in a
        // DevThrottle system prompt. The Cockpit talks only to the Gateway, never a Director - this
        // is that Gateway endpoint. Same warm brain as ask-direct, different grounding.
        app.MapPost("/wingman/ask-devthrottle", async (WingmanVoiceTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] ask-devthrottle textLen={req?.Text?.Length ?? 0}");
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var t = await translator.AskAboutDevThrottleAsync(req.Text, ct);
                FileLog.Write($"[GatewayWingmanVoice] ask-devthrottle OK: answerLen={t.Spoken.Length}, replySeconds={t.ReplySeconds:F1}");
                return Results.Json(new { spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] ask-devthrottle FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman failed: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Menu handling (issue #531): is the agent showing an on-screen menu right now, and what are
        // the options? The phone reads this on entry to render pressable option buttons and speak the
        // choices. { isMenu, question, spoken, selectionMode, submit, options:[{key,send,note,recommended}] }.
        app.MapGet("/sessions/{sid}/wingman/menu", async (string sid, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] menu sid={sid}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            var endpoint = await ResolveEndpointAsync(sid, registry, client, ct);
            if (endpoint is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);
            var menu = await DetectMenuAtAsync(client, translator, endpoint, sid, ct);
            return Results.Json(MenuJson(menu));
        });

        // Press a specific menu option (the phone's option-button tap): send the exact keystrokes,
        // then wait for the agent's result and translate it back. { send, submit? } -> { reply, spoken }.
        app.MapPost("/sessions/{sid}/wingman/menu-press", async (string sid, WingmanMenuPressRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] menu-press sid={sid}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            if (req is null || string.IsNullOrEmpty(req.Send))
                return Results.Json(new { error = "send is required" }, statusCode: StatusCodes.Status400BadRequest);
            var endpoint = await ResolveEndpointAsync(sid, registry, client, ct);
            if (endpoint is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);
            return await PressAndSummarizeAsync(client, translator, voice, endpoint, sid, req.Send, req.Submit, null, "menu-press", ct);
        });
    }

    /// <summary>Fetch the session terminal and, only when it cheaply looks like a menu, ask the warm
    /// brain to extract it. Returns IsMenu=false on any miss - the caller treats input as a prompt.</summary>
    private static async Task<WingmanMenu> DetectMenuAtAsync(
        DirectorEndpointClient client, WingmanTranslator translator, string endpoint, string sid, CancellationToken ct)
    {
        Contracts.BufferResponse? buf;
        try { buf = await client.GetBufferAsync(endpoint, sid, lines: null, raw: false, since: null, ct); }
        catch { buf = null; }
        var terminal = buf?.Text ?? "";
        if (!WingmanMenuLogic.LooksLikeMenu(terminal)) return new WingmanMenu { IsMenu = false };
        return await translator.DetectMenuAsync(terminal, ct);
    }

    /// <summary>Press an option's keystrokes (then the multi-select submit, if any), wait for the
    /// agent's resulting turn, translate it, cache it, and return the spoken summary. Shared by the
    /// option-button tap (menu-press) and the spoken-choice path (voice-turn).</summary>
    private static async Task<IResult> PressAndSummarizeAsync(
        DirectorEndpointClient client, WingmanTranslator translator, WingmanVoiceService voice,
        string endpoint, string sid, string send, string? submit, string? confirmPrefix, string source, CancellationToken ct)
    {
        voice.OnSessionWorking(sid);   // a new turn is coming; drop the stale cached summary
        var before = await CountTextWidgetsAsync(client, endpoint, sid, ct);

        var (ok, _, err) = await client.PostPromptAsync(endpoint, sid, new PromptRequest { Text = send, AppendEnter = false }, ct);
        if (!ok)
            return Results.Json(new { error = "press failed: " + err }, statusCode: StatusCodes.Status502BadGateway);
        if (!string.IsNullOrEmpty(submit))
        {
            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { }
            await client.PostPromptAsync(endpoint, sid, new PromptRequest { Text = submit, AppendEnter = false }, CancellationToken.None);
        }
        FileLog.Write($"[GatewayWingmanVoice] {source} sid={sid}: pressed send=\"{Escape(send)}\" submit=\"{Escape(submit)}\"");

        var prefix = confirmPrefix ?? "";
        var reply = await WaitForReplyAsync(client, endpoint, sid, before, ct);
        if (string.IsNullOrWhiteSpace(reply))
            return Results.Json(new { reply = "", spoken = prefix + "Done. The agent is working - I'll have the result shortly.", pressed = true });

        voice.BeginGenerating(sid);
        try
        {
            var t = await translator.TranslateAsync("(you picked a menu option)", reply, CancellationToken.None);
            var spoken = prefix + t.Spoken;
            await voice.StoreSpokenAsync(sid, spoken, reply, CancellationToken.None);
            _ = voice.CaptureTrainingAsync(endpoint, sid, source, reply, "(menu pick)", spoken, t.ReplySeconds, CancellationToken.None);
            FileLog.Write($"[GatewayWingmanVoice] {source} sid={sid}: replyLen={reply.Length}, spokenLen={spoken.Length}");
            return Results.Json(new { reply, spoken, replySeconds = t.ReplySeconds, pressed = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayWingmanVoice] {source} sid={sid} translate FAILED: {ex.Message}");
            return Results.Json(new { error = "wingman translation failed: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
        finally { voice.EndGenerating(sid); }
    }

    private static string Escape(string? s) => (s ?? "").Replace("\r", "\\r").Replace("\n", "\\n");

    /// <summary>Shape a <see cref="WingmanMenu"/> for the JSON response (camelCase the phone reads).</summary>
    private static object MenuJson(WingmanMenu m) => new
    {
        isMenu = m.IsMenu,
        question = m.Question,
        spoken = m.Spoken,
        selectionMode = m.SelectionMode,
        submit = m.Submit,
        options = m.Options.Select(o => new { key = o.Key, send = o.Send, note = o.Note, recommended = o.Recommended }).ToList(),
    };

    /// <summary>How long the reply transcript must stop growing before we treat the turn as done.</summary>
    private static readonly TimeSpan ReplyStable = TimeSpan.FromSeconds(2.0);

    /// <summary>
    /// Wait for the agent's reply by polling the TRANSCRIPT (not the fragile live session-state
    /// read across the gateway-to-Director hop): once a new Text widget appears beyond
    /// <paramref name="widgetsBefore"/> and stops growing for <see cref="ReplyStable"/>, that is the
    /// reply. Transient null/hiccup reads from the Director are tolerated (we just keep polling) so a
    /// busy Director mid-turn never makes us give up early - the only way out without a reply is the
    /// full <see cref="TurnTimeout"/>. Returns the reply text, or null if none landed in time.
    /// </summary>
    private static async Task<string?> WaitForReplyAsync(
        DirectorEndpointClient client, string endpoint, string sid, int widgetsBefore, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TurnTimeout;
        string? reply = null;
        var stableCount = -1;
        var stableSince = DateTime.MinValue;
        var consecutiveNulls = 0;
        while (DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(PollInterval, ct); } catch (OperationCanceledException) { return reply; }

            TurnsResponse? turns;
            try { turns = await client.GetTurnsAsync(endpoint, sid, ct); }
            catch { turns = null; }

            var widgets = turns?.Widgets;
            if (widgets is null)
            {
                // A transient gateway->Director hiccup (the Director is busy running the turn). Do
                // NOT give up - keep polling. Only a long run of failures (the Director is truly
                // gone) ends it, and even then we fall through to the deadline.
                consecutiveNulls++;
                if (consecutiveNulls > 40) { FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: director unreachable for replies"); break; }
                continue;
            }
            consecutiveNulls = 0;

            var lastText = widgets.Skip(widgetsBefore).LastOrDefault(w => w.Kind == "Text");
            if (lastText is not null && !string.IsNullOrWhiteSpace(lastText.Content))
            {
                reply = lastText.Content;
                if (widgets.Count != stableCount) { stableCount = widgets.Count; stableSince = DateTime.UtcNow; }
                else if (DateTime.UtcNow - stableSince >= ReplyStable)
                {
                    FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: reply read, len={reply.Length}");
                    return reply;
                }
            }
        }
        FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: returning {(reply is null ? "NO reply" : "last reply len=" + reply.Length)} after wait");
        return reply;
    }

    private static async Task<string?> ReadNewReplyAsync(
        DirectorEndpointClient client, string endpoint, string sid, int widgetsBefore, CancellationToken ct)
    {
        var turns = await client.GetTurnsAsync(endpoint, sid, ct);
        if (turns?.Widgets is null) return null;
        var last = turns.Widgets.Skip(widgetsBefore).LastOrDefault(w => w.Kind == "Text");
        return last?.Content;
    }

    private static async Task<int> CountTextWidgetsAsync(
        DirectorEndpointClient client, string endpoint, string sid, CancellationToken ct)
    {
        var turns = await client.GetTurnsAsync(endpoint, sid, ct);
        return turns?.Widgets?.Count ?? 0;
    }

    /// <summary>Find the dialable Control API base URL of the Director that owns this session.</summary>
    private static async Task<string?> ResolveEndpointAsync(
        string sid, DirectorRegistry registry, DirectorEndpointClient client, CancellationToken ct)
    {
        foreach (var d in registry.ListDirectors())
        {
            var ep = (d.ControlEndpoint ?? d.TailnetEndpoint ?? "").TrimEnd('/');
            if (string.IsNullOrWhiteSpace(ep)) continue;
            var s = await client.GetSessionAsync(ep, sid, ct);
            if (s is not null) return ep;
        }
        return null;
    }
}

/// <summary>Body of the wingman voice-turn and ask-direct routes: the person's message.</summary>
public sealed class WingmanVoiceTurnRequest
{
    public string Text { get; set; } = "";
}

/// <summary>Body of the menu-press route: the exact keystrokes that pick an option, and (for a
/// multi-select menu) the completing submit keystroke.</summary>
public sealed class WingmanMenuPressRequest
{
    public string Send { get; set; } = "";
    public string? Submit { get; set; }
}

/// <summary>Body of the wingman text-to-speech route: the text to speak and an optional voice.</summary>
public sealed class WingmanTtsRequest
{
    public string Text { get; set; } = "";
    public string? Voice { get; set; }
}

/// <summary>Body of the resumable-utterance complete route: how many chunks to reassemble and the
/// recording's MIME type / file extension (so OpenAI gets a correctly-named file).</summary>
public sealed class UtteranceCompleteRequest
{
    public int TotalChunks { get; set; }
    public string? Mime { get; set; }
    public string? Ext { get; set; }
}
