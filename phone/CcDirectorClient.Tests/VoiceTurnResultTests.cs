using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

/// <summary>
/// Wire-shape tests for the Gateway's async voice-turn responses (issue #378).
/// The submit response is snake_case (turn_id / expires_at - the Gateway's contract,
/// see GatewayVoiceTurnEndpoint), NOT camelCase like the Director's other routes, so
/// these tests pin the mapping that a casual case-insensitive deserialize would miss.
/// </summary>
public class VoiceTurnResultTests
{
    // ===== ParseSubmit =====================================================

    [Fact]
    public void ParseSubmit_SnakeCaseGatewayBody_MapsTurnIdAndExpiry()
    {
        var json = "{\"turn_id\":\"3f6e2d1c-9a8b-4c7d-b5e4-aa11bb22cc33\",\"expires_at\":\"2026-06-12T23:30:00Z\"}";

        var result = VoiceTurnResults.ParseSubmit(json);

        Assert.Equal("3f6e2d1c-9a8b-4c7d-b5e4-aa11bb22cc33", result.TurnId);
        Assert.Equal(new DateTimeOffset(2026, 6, 12, 23, 30, 0, TimeSpan.Zero), result.ExpiresAt);
    }

    [Fact]
    public void ParseSubmit_MissingTurnId_Throws()
    {
        var json = "{\"expires_at\":\"2026-06-12T23:30:00Z\"}";

        Assert.Throws<InvalidOperationException>(() => VoiceTurnResults.ParseSubmit(json));
    }

    [Fact]
    public void ParseSubmit_UnparseableExpiry_StillReturnsTurnId()
    {
        var json = "{\"turn_id\":\"abc\",\"expires_at\":\"not-a-date\"}";

        var result = VoiceTurnResults.ParseSubmit(json);

        Assert.Equal("abc", result.TurnId);
        Assert.Null(result.ExpiresAt);
    }

    // ===== ParsePoll =======================================================

    [Fact]
    public void ParsePoll_ReplyStage_CarriesSummaryAndAudio()
    {
        var json = "{\"turn_id\":\"abc\",\"stage\":\"reply\",\"transcript\":\"what is left\"," +
                   "\"summary\":\"All done. Two tests remain red.\",\"audioBase64\":\"AAEC\"," +
                   "\"message\":null,\"expires_at\":\"2026-06-12T23:30:00Z\"}";

        var result = VoiceTurnResults.ParsePoll(json);

        Assert.Equal("reply", result.Stage);
        Assert.Equal("what is left", result.Transcript);
        Assert.Equal("All done. Two tests remain red.", result.Summary);
        Assert.Equal("AAEC", result.AudioBase64);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ParsePoll_ErrorStage_CarriesMessage()
    {
        var json = "{\"turn_id\":\"abc\",\"stage\":\"error\",\"transcript\":null,\"summary\":null," +
                   "\"audioBase64\":null,\"message\":\"director unreachable: timeout\"}";

        var result = VoiceTurnResults.ParsePoll(json);

        Assert.Equal("error", result.Stage);
        Assert.Equal("director unreachable: timeout", result.Error);
        Assert.Null(result.Summary);
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("transcribing")]
    [InlineData("waiting")]
    [InlineData("thinking")]
    [InlineData("summarizing")]
    public void ParsePoll_ProgressStages_PassThrough(string stage)
    {
        var json = $"{{\"turn_id\":\"abc\",\"stage\":\"{stage}\"}}";

        var result = VoiceTurnResults.ParsePoll(json);

        Assert.Equal(stage, result.Stage);
    }

    [Fact]
    public void ParsePoll_MissingStage_MapsToUnknown()
    {
        var json = "{\"turn_id\":\"abc\"}";

        var result = VoiceTurnResults.ParsePoll(json);

        Assert.Equal("unknown", result.Stage);
    }

    // ===== ParsePoll: slim poll (issue #407) ===============================

    [Fact]
    public void ParsePoll_SlimReply_CarriesAudioReadyAndLength_NoBytes()
    {
        // The new slim poll: it advertises that audio is ready and how long it is, but does NOT
        // carry the base64 bytes (audioBase64 is null). The client fetches the audio separately.
        var json = "{\"turn_id\":\"abc\",\"stage\":\"reply\",\"summary\":\"All done.\"," +
                   "\"audioReady\":true,\"audioLength\":2158560,\"audioBase64\":null," +
                   "\"message\":null,\"expires_at\":\"2026-06-12T23:30:00Z\"}";

        var result = VoiceTurnResults.ParsePoll(json);

        Assert.Equal("reply", result.Stage);
        Assert.Equal("All done.", result.Summary);
        Assert.True(result.AudioReady);
        Assert.Equal(2158560, result.AudioLength);
        Assert.Null(result.AudioBase64);   // slim - the bytes are not in the poll
    }

    [Fact]
    public void ParsePoll_SlimReply_NoAudio_AudioReadyFalse()
    {
        // No TTS key: the reply has no audio, so audioReady is false and the client speaks the
        // on-screen summary only.
        var json = "{\"turn_id\":\"abc\",\"stage\":\"reply\",\"summary\":\"All done.\"," +
                   "\"audioReady\":false,\"audioLength\":0,\"audioBase64\":null}";

        var result = VoiceTurnResults.ParsePoll(json);

        Assert.False(result.AudioReady);
        Assert.Equal(0, result.AudioLength);
    }

    [Fact]
    public void ParsePoll_BackCompat_InlineAudioBase64_NoAudioReadyField_InfersReady()
    {
        // An OLDER Gateway that still inlines audioBase64 and omits audioReady: the parser must
        // still tell the caller a reply has audio (AudioReady inferred from non-empty base64) so
        // the client plays the inline bytes without a second round-trip.
        var json = "{\"turn_id\":\"abc\",\"stage\":\"reply\",\"summary\":\"ok\",\"audioBase64\":\"AAEC\"}";

        var result = VoiceTurnResults.ParsePoll(json);

        Assert.True(result.AudioReady);
        Assert.Equal("AAEC", result.AudioBase64);
    }
}
