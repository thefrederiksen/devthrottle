using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation.Providers;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// A one-shot transcription smoke test for the Cockpit Settings &gt; Transcription page. The status
/// dot on that page only tells the user a key is STORED, never that it actually WORKS - this route
/// closes that gap. The page records a short microphone clip, posts the raw audio here, and the
/// Gateway runs it through the SAME configured mode and key the live dictation pipeline uses,
/// returning the text it heard. So "yes, my speech is becoming text with my current settings" is
/// provable in one click.
///
///   POST /transcription/test   (raw audio body; Content-Type is the clip's MIME type)
///       -&gt; 200 { text, mode, model }     transcription succeeded
///       -&gt; 400 { error }                  no audio in the request body
///       -&gt; 409 { error, mode }            no key set for the current mode, OR the mode is local
///       -&gt; 502 { error }                  the provider rejected the request or the key
///
/// The remote-mode test uses the OpenAI-compatible BATCH endpoint (record then upload), which both
/// remote providers implement - OpenAI with gpt-4o-transcribe and the DevThrottle/Groq proxy with
/// whisper-large-v3 - so a test works in either remote mode regardless of the live pipeline's
/// transport.
///
/// Local mode (issue #541) has no key and no remote endpoint, so this smoke test - whose whole job
/// is to prove a STORED KEY actually works - does not apply. It returns 409 with a clear
/// "local mode - no key required; the local model transcribes in-process" message. The live
/// /wingman/transcribe path is what exercises local transcription end to end. Inherits the
/// host-wide token middleware like every other Gateway route.
/// </summary>
internal static class TranscriptionTestEndpoint
{
    public static void Map(IEndpointRouteBuilder app, KeyVault vault)
    {
        app.MapPost("/transcription/test", async (HttpContext ctx) =>
        {
            // Resolve the configured mode -> (baseUrl, keyName, model) from the same pure resolver the
            // live routing uses, so the test exercises exactly the user's current setting.
            var mode = TranscriptionModeConfig.Get();
            var endpoint = TranscriptionEndpointResolver.Resolve(mode);

            // Local mode (issue #541): in-process, no stored key to validate. This smoke test exists
            // to prove a stored key works, so it does not apply - return a clear 409 explaining that
            // the local model transcribes in-process (the live /wingman/transcribe path exercises it).
            if (endpoint.IsLocal)
            {
                FileLog.Write("[TranscriptionTestEndpoint] POST /transcription/test: mode=local, no key required");
                return Results.Json(
                    new { error = "local mode - no key required; the local model transcribes in-process", mode = endpoint.Mode.ToConfigString() },
                    statusCode: StatusCodes.Status409Conflict);
            }

            var key = vault.Get(endpoint.RequireKeyName());
            if (string.IsNullOrWhiteSpace(key))
            {
                FileLog.Write($"[TranscriptionTestEndpoint] POST /transcription/test: mode={endpoint.Mode.ToConfigString()}, no key for {endpoint.KeyName}");
                return Results.Json(
                    new { error = "no key set for the current transcription mode", mode = endpoint.Mode.ToConfigString() },
                    statusCode: StatusCodes.Status409Conflict);
            }

            // Read the whole clip into memory. These are short test clips (a few seconds), so the
            // memory cost is trivial and bounded by the page's record length.
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var audio = ms.ToArray();
            if (audio.Length == 0)
                return Results.BadRequest(new { error = "no audio in the request body" });

            // The browser's MediaRecorder names the codec (e.g. "audio/webm;codecs=opus"); the
            // provider strips parameters and the server detects the codec from the bytes. An empty
            // Content-Type only happens for a hand-rolled request - name the page's default so the
            // multipart upload still carries a sensible MIME type.
            var contentType = string.IsNullOrWhiteSpace(ctx.Request.ContentType)
                ? "audio/webm"
                : ctx.Request.ContentType;

            FileLog.Write($"[TranscriptionTestEndpoint] POST /transcription/test: mode={endpoint.Mode.ToConfigString()}, model={endpoint.Model}, audio={audio.Length} bytes, contentType={contentType}");

            try
            {
                await using var provider = new OpenAiTranscriptionProvider(
                    apiKey: key,
                    model: endpoint.Model,
                    audioContentType: contentType,
                    audioFileName: "audio.webm",
                    baseUrl: endpoint.BaseUrl);

                await provider.StartAsync("", ctx.RequestAborted);
                await provider.PushAudioAsync(audio, ctx.RequestAborted);
                var text = await provider.StopAsync(ctx.RequestAborted);

                FileLog.Write($"[TranscriptionTestEndpoint] POST /transcription/test OK: text_len={text.Length}");
                return Results.Json(new { text, mode = endpoint.Mode.ToConfigString(), model = endpoint.Model });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TranscriptionTestEndpoint] POST /transcription/test FAILED: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
