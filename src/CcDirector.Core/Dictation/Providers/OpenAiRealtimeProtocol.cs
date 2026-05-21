using System.Text.Json;

namespace CcDirector.Core.Dictation.Providers;

/// <summary>
/// Pure protocol helpers for OpenAI's Realtime transcription WebSocket API.
/// All I/O happens in <see cref="OpenAiRealtimeProvider"/>; this class only
/// builds outbound JSON frames and parses inbound ones, so it can be tested
/// without network access.
///
/// API reference: https://platform.openai.com/docs/api-reference/realtime
/// Connection URL: <c>wss://api.openai.com/v1/realtime?intent=transcription</c>
/// with headers <c>Authorization: Bearer ...</c> and <c>OpenAI-Beta: realtime=v1</c>.
/// </summary>
internal static class OpenAiRealtimeProtocol
{
    /// <summary>
    /// Build the <c>session.update</c> frame for the GA realtime
    /// transcription API. Configures the session as transcription type,
    /// sets the input audio format to PCM at 24 kHz, and supplies the
    /// vocabulary-biasing prompt. Sent immediately after the WebSocket
    /// opens and the server has emitted <c>session.created</c>.
    /// </summary>
    public static string BuildSessionUpdate(string model, string sttPrompt)
    {
        var payload = new
        {
            type = "session.update",
            session = new
            {
                type = "transcription",
                audio = new
                {
                    input = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 },
                        transcription = new
                        {
                            model = model,
                            prompt = sttPrompt ?? "",
                        },
                        // Disable server VAD: in walkie-talkie use the
                        // human releases the button to mark end of speech,
                        // and we call input_audio_buffer.commit to flush.
                        // Server VAD would either over-commit (auto-cut at
                        // a natural pause mid-sentence) or under-deliver
                        // (filter silence into 0 ms of audio, which the
                        // commit endpoint rejects).
                        turn_detection = (object?)null,
                    },
                },
            },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Build an <c>input_audio_buffer.append</c> frame carrying a chunk of
    /// PCM16 audio. The chunk is base64-encoded per the API contract.
    /// </summary>
    public static string BuildAudioAppend(ReadOnlyMemory<byte> pcm16)
    {
        var base64 = Convert.ToBase64String(pcm16.Span);
        var payload = new
        {
            type = "input_audio_buffer.append",
            audio = base64,
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Build an <c>input_audio_buffer.commit</c> frame that tells the
    /// server the speaker has stopped. The server responds with the final
    /// transcription event after committing.
    /// </summary>
    public static string BuildAudioCommit()
        => "{\"type\":\"input_audio_buffer.commit\"}";

    /// <summary>
    /// Parse an inbound JSON frame into a typed event. Returns
    /// <see cref="OtherEvent"/> for any frame we do not care about so
    /// callers can log it without branching on null.
    /// </summary>
    public static RealtimeEvent Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new OtherEvent("");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                return new OtherEvent("");
            var type = typeProp.GetString() ?? "";

            switch (type)
            {
                case "conversation.item.input_audio_transcription.delta":
                    var delta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
                    return new DeltaEvent(delta);

                case "conversation.item.input_audio_transcription.completed":
                    var transcript = root.TryGetProperty("transcript", out var t) ? t.GetString() ?? "" : "";
                    return new CompletedEvent(transcript);

                case "error":
                    var errMsg = "unknown error";
                    if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
                    {
                        if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                            errMsg = m.GetString() ?? errMsg;
                    }
                    return new ErrorEvent(errMsg);

                default:
                    return new OtherEvent(type);
            }
        }
        catch (JsonException)
        {
            return new OtherEvent("");
        }
    }
}

/// <summary>Base type for inbound Realtime events the provider handles.</summary>
public abstract record RealtimeEvent;

/// <summary>Partial transcript update; <see cref="Delta"/> is appended to the running transcript.</summary>
public sealed record DeltaEvent(string Delta) : RealtimeEvent;

/// <summary>Final transcript for the most recent committed audio buffer.</summary>
public sealed record CompletedEvent(string Transcript) : RealtimeEvent;

/// <summary>Server-reported error.</summary>
public sealed record ErrorEvent(string Message) : RealtimeEvent;

/// <summary>Any other event type we do not act on (session.updated, response.done, etc.).</summary>
public sealed record OtherEvent(string Type) : RealtimeEvent;
