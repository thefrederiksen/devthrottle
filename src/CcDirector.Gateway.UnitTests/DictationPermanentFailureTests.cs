using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Transcription;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The server half of the interim loop-stop fix (issue #1185): a PERMANENT transcription failure on the
/// dictation complete path is mapped to a parked FAILED record + the HTTP 422 { permanent, reason } client
/// contract, instead of the generic retryable 502 that made the durable queue re-drive forever.
///
/// The transcription service is sealed and constructed inside the running host, so these tests drive the
/// exact mapping seam <see cref="GatewayDictationEndpoint.MapNonOkTranscription"/> with a fabricated
/// <see cref="GatewayTranscriptionResult"/> for each outcome - which is precisely where the classification,
/// the reason translation, the FAILED park (keeping the chunks), and the guard all live.
/// </summary>
public sealed class DictationPermanentFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-perm-" + Guid.NewGuid().ToString("N"));
    private readonly VoiceUploadStore _store;

    public DictationPermanentFailureTests() => _store = new VoiceUploadStore(_root, TenantId.Local);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* test cleanup */ }
    }

    [Theory]
    [InlineData("audio_too_large", "audio-too-large")]
    [InlineData("unsupported_format", "unsupported-format")]
    [InlineData("non_decodable", "unsupported-format")]
    public async Task PermanentError_MapsTo422_ParksFailed_KeepsChunks(string code, string expectedReason)
    {
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Encoding.UTF8.GetBytes("AAA"), null);
        var result = GatewayTranscriptionResult.PermanentError("mode", "model", code, "the clip cannot transcribe");

        var outcome = GatewayDictationEndpoint.MapNonOkTranscription(result, id, _store);

        // The client contract: HTTP 422 { permanent:true, reason:<translated> }.
        Assert.NotNull(outcome);
        var (status, body) = await ExecuteAsync(outcome!.ToResult());
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, status);
        Assert.True(body.GetProperty("permanent").GetBoolean());
        Assert.Equal(expectedReason, body.GetProperty("reason").GetString());

        // The record is parked FAILED with the reason code, its chunks retained, and it is not pending.
        var record = _store.ReadRecord(id);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Failed, record!.State);
        Assert.Equal(code, record.Reason);
        Assert.False(_store.IsPending(id));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, Guid.Parse(id).ToString("N")), "*.part"));
    }

    // ===== THESE TWO TESTS USED TO ASSERT DEFECT 19. Read this before you "fix" them back. =========
    //
    // They were named Guard_*_IsNotMappedToPermanent_AndDoesNotParkTheRecord, and each asserted TWO things
    // in one breath:
    //
    //   (a) a retryable outcome must not be RECLASSIFIED as permanent - it keeps its 502/402 contract so the
    //       durable queue re-drives it. That half is CORRECT, it is the whole point of issue #1185's guard,
    //       and it is still asserted below. Do not lose it.
    //   (b) `Assert.Null(_store.ReadRecord(id))` - the record must be left unparked. THAT HALF WAS THE BUG.
    //       An unparked record stays PENDING, and PENDING paints the session orange with no bound, so the
    //       session read "Uploading from phone" about an upload that was going nowhere. OBSERVED: upload
    //       f13cb4b6d9d0, 12 July 2026, orange for 1h30m across four Gateway restarts before it finally
    //       delivered 362 characters.
    //
    // Parking FAILED does NOT give up on the words: FAILED keeps the staged chunks and the next
    // register/complete clears it back to PENDING and re-drives. f13cb4b6d9d0 would still have delivered at
    // 07:40. The only thing parking removes is the lie on the dot between attempts.
    //
    // A green test is not proof. These two were green, and they were defending the defect.

    [Fact]
    public void ProviderError_KeepsItsRetryableContract_ButParksTheRecordSoTheColourIsBounded()
    {
        // (a) the CORRECT half, unchanged: a provider error is NOT reclassified as permanent.
        var id = _store.Register(null);
        _store.MarkPending(id, "session-1");
        var result = GatewayTranscriptionResult.ProviderError("mode", "model", "provider rejected the key");

        var outcome = GatewayDictationEndpoint.MapNonOkTranscription(result, id, _store);

        Assert.NotNull(outcome);
        Assert.False(outcome!.IsIncomplete);
        Assert.False(outcome.Terminal, "a provider error is retryable - the client re-drives it");

        // (b) the INVERTED half - this is the defect-19 fix. The record parks FAILED, so it stops painting.
        var record = _store.ReadRecord(id);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Failed, record!.State);
        Assert.False(_store.IsPending(id), "a parked record must not paint the session 'Uploading from phone'");
        Assert.False(_store.IsSessionLocked("session-1"), "the wedged orange is exactly this returning true forever");

        // ...and the words are NOT lost: the owning session is preserved and a retry re-enters PENDING.
        Assert.Equal("session-1", record.SessionId);
        Assert.True(_store.ClearFailed(id), "the retry must be able to re-drive the parked record");
        Assert.True(_store.IsPending(id));
    }

    [Fact]
    public async Task OutOfCredits_KeepsIts402Contract_ButParksTheRecordSoTheColourIsBounded()
    {
        // NOTE: out-of-credits has NEVER been observed to fire - zero OutOfCredits in any log on this
        // machine, ever, across 846 terminal outcomes. An earlier draft of the specification called it the
        // "everyday" cause of the wedged orange; that was invented and is disproven. This test asserts the
        // MECHANISM only. It says nothing about the cause.
        var id = _store.Register(null);
        await _store.StoreChunkAsync(id, 0, Encoding.UTF8.GetBytes("AAA"), null);
        _store.MarkPending(id, "session-1");
        var result = GatewayTranscriptionResult.OutOfCredits("mode", "model", "insufficient_credits", "no credits");

        var outcome = GatewayDictationEndpoint.MapNonOkTranscription(result, id, _store);

        // (a) the CORRECT half, unchanged: never permanent, so adding credit and retrying still delivers.
        Assert.NotNull(outcome);
        Assert.False(outcome!.Terminal);

        // (b) the INVERTED half: parked, so the session stops claiming an upload is in progress. The parked
        // reason is the PROVIDER'S OWN CODE (phase 5 review round: the outcome read rebuilds the 402's exact
        // hosted-AI state from it), never a literal of our own.
        var record = _store.ReadRecord(id);
        Assert.NotNull(record);
        Assert.Equal(DictationDeliveryState.Failed, record!.State);
        Assert.Equal("insufficient_credits", record.Reason);
        Assert.False(_store.IsSessionLocked("session-1"));

        // The recording is KEPT: adding credit and retrying must still deliver the words.
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, Guid.Parse(id).ToString("N")), "*.part"));
    }

    [Fact]
    public void Ok_ReturnsNull_SoTheCallerContinuesToInject()
    {
        var id = _store.Register(null);
        var result = GatewayTranscriptionResult.Ok("hello there", "mode", "model");

        Assert.Null(GatewayDictationEndpoint.MapNonOkTranscription(result, id, _store));
        Assert.Null(_store.ReadRecord(id));
    }

    [Theory]
    [InlineData("audio_too_large", "audio-too-large")]
    [InlineData("unsupported_format", "unsupported-format")]
    [InlineData("non_decodable", "unsupported-format")]
    [InlineData("something_unexpected", "unsupported-format")]
    public void TranslatePermanentReason_MapsEveryCode(string code, string expected)
    {
        Assert.Equal(expected, GatewayDictationEndpoint.TranslatePermanentReason(code));
    }

    [Fact]
    public void Permanent_Outcome_IsNotTerminal_AndDoesNotKeepTheOrangeMark()
    {
        // Permanent is terminal-for-this-attempt (clears the orange mark: not IsIncomplete) but
        // retryable-for-the-record (not Terminal: the always-remove drops it from the single-flight cache so
        // a retry re-runs, and the FAILED record can re-enter PENDING).
        var outcome = DictationOutcome.Permanent("audio-too-large");
        Assert.False(outcome.IsIncomplete, "a permanent failure clears the transcribing mark");
        Assert.False(outcome.Terminal, "a permanent failure is user-retryable, not a cached terminal");
    }

    // Execute a minimal-API IResult against an in-memory context and read back the status + JSON body. The
    // JSON result resolves logging + JSON options from the request services, so a minimal provider is wired.
    private static async Task<(int status, JsonElement body)> ExecuteAsync(IResult result)
    {
        var provider = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = provider };
        using var ms = new MemoryStream();
        ctx.Response.Body = ms;
        await result.ExecuteAsync(ctx);
        ms.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ms);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }
}
