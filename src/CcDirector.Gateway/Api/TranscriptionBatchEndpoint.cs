using CcDirector.Core;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Transcription;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The single Gateway endpoint that turns audio into text (issue #839). A caller sends the raw audio
/// bytes and the content type; the Gateway resolves the configured mode and the key, runs the
/// DevThrottle-compatible batch endpoint, and returns the text. The caller never sees or
/// handles the key - it only sends audio and receives text.
///
///   POST /transcription            (raw audio body; Content-Type is the clip's MIME type)
///        ?correct=true             also run the validated dictionary correction (default: raw text)
///       -&gt; 200 { text, mode, model }   transcription succeeded
///       -&gt; 400 { error }                no audio in the request body
///       -&gt; 409 { error, mode }          no key set for the current mode
///       -&gt; 402 { error, code, mode }    the DevThrottle account is out of credits (issue #885)
///       -&gt; 502 { error }                the provider rejected the request or the key
///
/// Legacy mode values migrate forward and go through this one endpoint - the resolution and provider
/// choice live in <see cref="GatewayTranscriptionService"/>, the single owner. Inherits
/// the host-wide token middleware like every other Gateway route.
///
/// Whether the dictionary correction runs is the caller's choice via <c>?correct=true</c>: the
/// Settings "Test it" button leaves it OFF so it proves the RAW transcription path (a term swap would
/// mask a transcription problem), while the phone voice screen turns it ON so the words match the
/// user's glossary.
/// </summary>
internal static class TranscriptionBatchEndpoint
{
    public static void Map(
        IEndpointRouteBuilder app,
        bool hosted,
        KeyVault vault,
        // REQUIRED AND NON-NULLABLE (finding I1-01), and moved AHEAD of the optional tail so it cannot sit
        // in a defaulted position: a forgotten boundary must be a compile error, never a silent default.
        // Self-host callers construct it over the SingleTenantContext.
        Tenancy.HostedTenantBoundary tenantBoundary,
        TranscriptionHistoryLog? history = null,
        TranscriptionAudioArchive? audioArchive = null,
        TranscriptStore? transcripts = null)
    {
        app.MapPost("/transcription", async (HttpContext ctx) =>
        {
            // Per-tenant (issue #2059): the transcription-health record this turn writes goes to the caller's
            // own partition. A request with no resolvable tenant is refused (403), never Local.
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "a tenant could not be resolved for this request" },
                    statusCode: StatusCodes.Status403Forbidden);

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

            var service = new GatewayTranscriptionService(vault, audioArchive ?? new TranscriptionAudioArchive(hosted), history: history, transcripts: transcripts);
            var result = await service.TranscribeAsync(audio, fileName, contentType, correct, ctx.RequestAborted, tenant: reqTenant.Value, source: "batch");

            return result.Outcome switch
            {
                TranscriptionOutcome.Ok => Results.Json(new { text = result.Text, mode = result.Mode, model = result.Model }),
                TranscriptionOutcome.NoAudio => Results.BadRequest(new { error = result.Error }),
                TranscriptionOutcome.NoKey => Results.Json(new { error = result.Error, mode = result.Mode }, statusCode: StatusCodes.Status409Conflict),
                // Out of credits (issue #885): HTTP 402 with the machine-readable code so the client
                // shows the add-credits state and keeps the recording, never a raw error.
                TranscriptionOutcome.OutOfCredits => Results.Json(new { error = result.Error, code = result.Code, mode = result.Mode }, statusCode: StatusCodes.Status402PaymentRequired),
                // Permanent, non-retryable (issue #1139): unsupported/undecodable format or too large to
                // reduce. A 4xx (415) so the durable dictation loop STOPS rather than resending forever.
                TranscriptionOutcome.PermanentError => Results.Json(new { error = result.Error, code = result.Code, mode = result.Mode }, statusCode: StatusCodes.Status415UnsupportedMediaType),
                TranscriptionOutcome.ProviderError => Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Json(new { error = "unknown transcription outcome" }, statusCode: StatusCodes.Status500InternalServerError),
            };
        });
    }
}
