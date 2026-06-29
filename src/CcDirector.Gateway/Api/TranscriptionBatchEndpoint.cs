using CcDirector.Core;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The single Gateway endpoint that turns audio into text (issue #839). A caller sends the raw audio
/// bytes and the content type; the Gateway resolves the configured mode and the key, runs the right
/// provider (in-process Whisper for on-device mode, or the OpenAI-compatible batch endpoint for the
/// remote modes), and returns the text. The caller never sees or handles the key - it only sends
/// audio and receives text.
///
///   POST /transcription            (raw audio body; Content-Type is the clip's MIME type)
///        ?correct=true             also run the validated dictionary correction (default: raw text)
///       -&gt; 200 { text, mode, model }   transcription succeeded
///       -&gt; 400 { error }                no audio in the request body
///       -&gt; 409 { error, mode }          no key set for the current remote mode
///       -&gt; 502 { error }                the provider rejected the request or the key
///
/// On-device (local) mode and the remote modes (bring-your-own OpenAI, DevThrottle) all go through
/// this one endpoint - the resolution and the provider choice live in
/// <see cref="GatewayTranscriptionService"/>, the single owner. Inherits the host-wide token
/// middleware like every other Gateway route.
///
/// Whether the dictionary correction runs is the caller's choice via <c>?correct=true</c>: the
/// Settings "Test it" button leaves it OFF so it proves the RAW transcription path (a term swap would
/// mask a transcription problem), while the phone voice screen turns it ON so the words match the
/// user's glossary.
/// </summary>
internal static class TranscriptionBatchEndpoint
{
    public static void Map(IEndpointRouteBuilder app, KeyVault vault)
    {
        app.MapPost("/transcription", async (HttpContext ctx) =>
        {
            var correct = string.Equals(ctx.Request.Query["correct"].ToString(), "true", StringComparison.OrdinalIgnoreCase);

            // Read the whole clip into memory. These are short clips (seconds), so the memory cost is
            // trivial and bounded by the recording length.
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            var audio = ms.ToArray();

            // The browser's MediaRecorder names the codec (e.g. "audio/webm;codecs=opus"); the provider
            // strips parameters and detects the codec from the bytes. An empty Content-Type only happens
            // for a hand-rolled request - name the page's default so the upload carries a sensible MIME type.
            var contentType = string.IsNullOrWhiteSpace(ctx.Request.ContentType) ? "audio/webm" : ctx.Request.ContentType;
            var fileName = "audio." + GatewayTranscriptionService.ExtensionFor(contentType);

            FileLog.Write($"[TranscriptionBatchEndpoint] POST /transcription: bytes={audio.Length}, contentType={contentType}, correct={correct}");

            var service = new GatewayTranscriptionService(vault);
            var result = await service.TranscribeAsync(audio, fileName, contentType, correct, ctx.RequestAborted);

            return result.Outcome switch
            {
                TranscriptionOutcome.Ok => Results.Json(new { text = result.Text, mode = result.Mode, model = result.Model }),
                TranscriptionOutcome.NoAudio => Results.BadRequest(new { error = result.Error }),
                TranscriptionOutcome.NoKey => Results.Json(new { error = result.Error, mode = result.Mode }, statusCode: StatusCodes.Status409Conflict),
                TranscriptionOutcome.ProviderError => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Json(new { error = "unknown transcription outcome" }, statusCode: StatusCodes.Status500InternalServerError),
            };
        });
    }
}
