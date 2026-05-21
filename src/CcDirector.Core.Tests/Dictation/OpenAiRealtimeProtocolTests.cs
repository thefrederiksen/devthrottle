using System.Text.Json;
using CcDirector.Core.Dictation.Providers;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Pure-protocol unit tests for <see cref="OpenAiRealtimeProtocol"/>. No
/// WebSocket I/O is exercised here; the real-network sanity check lives
/// in the integration test suite.
/// </summary>
public sealed class OpenAiRealtimeProtocolTests
{
    [Fact]
    public void BuildSessionUpdate_IncludesModelAndPrompt()
    {
        var json = OpenAiRealtimeProtocol.BuildSessionUpdate(
            "gpt-4o-transcribe",
            "Glossary: mindzie, CenCon.");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("session.update", doc.RootElement.GetProperty("type").GetString());
        var session = doc.RootElement.GetProperty("session");
        Assert.Equal("transcription", session.GetProperty("type").GetString());
        var input = session.GetProperty("audio").GetProperty("input");
        var format = input.GetProperty("format");
        Assert.Equal("audio/pcm", format.GetProperty("type").GetString());
        Assert.Equal(24000, format.GetProperty("rate").GetInt32());
        var stt = input.GetProperty("transcription");
        Assert.Equal("gpt-4o-transcribe", stt.GetProperty("model").GetString());
        Assert.Equal("Glossary: mindzie, CenCon.", stt.GetProperty("prompt").GetString());
        // turn_detection must be explicitly null so the server does not
        // gate the audio buffer behind VAD; walkie-talkie use commits
        // manually via input_audio_buffer.commit.
        Assert.Equal(JsonValueKind.Null, input.GetProperty("turn_detection").ValueKind);
    }

    [Fact]
    public void BuildSessionUpdate_NullPrompt_BecomesEmptyString()
    {
        var json = OpenAiRealtimeProtocol.BuildSessionUpdate("gpt-4o-transcribe", null!);
        using var doc = JsonDocument.Parse(json);
        var prompt = doc.RootElement.GetProperty("session")
                                   .GetProperty("audio")
                                   .GetProperty("input")
                                   .GetProperty("transcription")
                                   .GetProperty("prompt").GetString();
        Assert.Equal("", prompt);
    }

    [Fact]
    public void BuildAudioAppend_Base64EncodesChunk()
    {
        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        var json = OpenAiRealtimeProtocol.BuildAudioAppend(bytes);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("input_audio_buffer.append", doc.RootElement.GetProperty("type").GetString());
        var base64 = doc.RootElement.GetProperty("audio").GetString();
        Assert.Equal(Convert.ToBase64String(bytes), base64);
    }

    [Fact]
    public void BuildAudioCommit_IsConstantFrame()
    {
        var json = OpenAiRealtimeProtocol.BuildAudioCommit();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("input_audio_buffer.commit", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Parse_DeltaEvent_ExtractsDelta()
    {
        var json = """{"type":"conversation.item.input_audio_transcription.delta","delta":"hello"}""";
        var evt = OpenAiRealtimeProtocol.Parse(json);
        var delta = Assert.IsType<DeltaEvent>(evt);
        Assert.Equal("hello", delta.Delta);
    }

    [Fact]
    public void Parse_CompletedEvent_ExtractsTranscript()
    {
        var json = """{"type":"conversation.item.input_audio_transcription.completed","transcript":"hello world"}""";
        var evt = OpenAiRealtimeProtocol.Parse(json);
        var done = Assert.IsType<CompletedEvent>(evt);
        Assert.Equal("hello world", done.Transcript);
    }

    [Fact]
    public void Parse_ErrorEvent_ExtractsMessage()
    {
        var json = """{"type":"error","error":{"message":"rate limited","code":"rate_limit_exceeded"}}""";
        var evt = OpenAiRealtimeProtocol.Parse(json);
        var err = Assert.IsType<ErrorEvent>(evt);
        Assert.Equal("rate limited", err.Message);
    }

    [Fact]
    public void Parse_ErrorEvent_MissingMessage_FallsBackToUnknown()
    {
        var json = """{"type":"error","error":{}}""";
        var evt = OpenAiRealtimeProtocol.Parse(json);
        var err = Assert.IsType<ErrorEvent>(evt);
        Assert.Equal("unknown error", err.Message);
    }

    [Fact]
    public void Parse_UnknownType_ReturnsOtherEvent()
    {
        var json = """{"type":"transcription_session.updated"}""";
        var evt = OpenAiRealtimeProtocol.Parse(json);
        var other = Assert.IsType<OtherEvent>(evt);
        Assert.Equal("transcription_session.updated", other.Type);
    }

    [Fact]
    public void Parse_NoTypeField_ReturnsEmptyOtherEvent()
    {
        var json = """{"foo":"bar"}""";
        var evt = OpenAiRealtimeProtocol.Parse(json);
        var other = Assert.IsType<OtherEvent>(evt);
        Assert.Equal("", other.Type);
    }

    [Fact]
    public void Parse_InvalidJson_DoesNotThrow()
    {
        var evt = OpenAiRealtimeProtocol.Parse("not json at all");
        Assert.IsType<OtherEvent>(evt);
    }

    [Fact]
    public void Parse_EmptyString_DoesNotThrow()
    {
        var evt = OpenAiRealtimeProtocol.Parse("");
        Assert.IsType<OtherEvent>(evt);
    }
}
