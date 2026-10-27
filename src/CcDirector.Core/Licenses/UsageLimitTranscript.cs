using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CcDirector.Core.Licenses;

/// <summary>How a Claude Code session's last turn ended, read from its transcript.</summary>
public enum LastTurnKind
{
    /// <summary>The turn's last record is Claude Code's usage-limit reply.</summary>
    LimitReply,

    /// <summary>The turn ended any other way, or input has reached the agent since the limit reply.</summary>
    Other,

    /// <summary>The agent has not written a conversation yet: no file, an empty one, or one holding no user or
    /// assistant record (a session just started, or just cleared). An ordinary state, not a fault.</summary>
    NoConversation,

    /// <summary>The transcript could not be read, so nothing is known about the turn.</summary>
    Unreadable,
}

/// <summary>What <see cref="UsageLimitTranscript.ReadLastTurn"/> found. <see cref="RecordUuid"/> and
/// <see cref="AtUtc"/> are set for a limit reply; <see cref="Reason"/> says why a transcript was unreadable.</summary>
public sealed record LastTurn(LastTurnKind Kind, string? RecordUuid, DateTime? AtUtc, string? Reason)
{
    public static LastTurn Other { get; } = new(LastTurnKind.Other, null, null, null);
    public static LastTurn NoConversation(string reason) => new(LastTurnKind.NoConversation, null, null, reason);
    public static LastTurn Unreadable(string reason) => new(LastTurnKind.Unreadable, null, null, reason);
}

/// <summary>
/// The Loud When Stranded mission, step 6: recognises a Claude Code session whose last turn ended on a usage-limit
/// reply, from the session's TRANSCRIPT, never its screen. The screen is drawn in many shapes and quoted by agents in
/// reports; the transcript record is structured and is the agent's own statement of what happened.
///
/// The record, as Claude Code wrote it on 26 Oct 2026 (session 102 and session 115 on SOREN_NORTH): an assistant
/// record with <c>"isApiErrorMessage": true</c> and <c>"apiError": "usage_limit_reached"</c> (alongside
/// <c>"error": "rate_limit"</c>, <c>"apiErrorStatus": 429</c> and <c>"model": "&lt;synthetic&gt;"</c>, which are not
/// required). The turn's LAST conversation record - the last user or assistant record outside a side chain - must be
/// that one: a user record after it means input reached the agent since, and the session is not stopped on the limit.
/// Every other record type (system turn duration, attachments, request markers, file history) is not part of the
/// conversation and is passed over.
/// </summary>
public static class UsageLimitTranscript
{
    /// <summary>The <c>apiError</c> value of a usage-limit reply.</summary>
    public const string UsageLimitReached = "usage_limit_reached";

    /// <summary>The first part of the file's end that is read. Claude Code transcripts run to tens of megabytes; the
    /// last turn is at the end.</summary>
    internal const int FirstTailBytes = 256 * 1024;

    /// <summary>The most of the file's end that is read before the transcript is called unreadable. One tool result can
    /// be large, so the tail doubles up to this when it holds no complete conversation record.</summary>
    internal const int MaxTailBytes = 16 * 1024 * 1024;

    /// <summary>How the last turn in the transcript at <paramref name="path"/> ended.</summary>
    public static LastTurn ReadLastTurn(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return LastTurn.NoConversation($"the transcript {path} has not been written yet");

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (length == 0) return LastTurn.NoConversation($"the transcript {path} is empty");

        for (var tail = FirstTailBytes; ; tail *= 2)
        {
            var take = (int)Math.Min(length, tail);
            stream.Seek(length - take, SeekOrigin.Begin);
            var bytes = new byte[take];
            stream.ReadExactly(bytes);
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            // The first line of a tail that does not start at the file's start is a fragment of a longer line.
            var first = take < length ? 1 : 0;
            var found = LastConversationRecord(lines, first);
            if (found is not null) return found;
            if (take == length)
                return LastTurn.NoConversation($"the transcript {path} holds no user or assistant record yet");
            if (tail >= MaxTailBytes)
                return LastTurn.Unreadable($"no user or assistant record was found in the last {take} bytes of {path}");
        }
    }

    /// <summary>The verdict on the last conversation record among <paramref name="lines"/> from <paramref name="first"/>,
    /// or null when none of them is one.</summary>
    internal static LastTurn? LastConversationRecord(IReadOnlyList<string> lines, int first)
    {
        var lastLine = true;
        for (var i = lines.Count - 1; i >= first; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            var isLastLine = lastLine;
            lastLine = false;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // Claude Code may be writing the last line right now; a half-written line is not a record yet.
                if (isLastLine) continue;
                return LastTurn.Unreadable($"a transcript line {lines.Count - 1 - i} from the end is not JSON");
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                var type = StringOf(root, "type");
                if (type is not ("user" or "assistant")) continue;
                if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;
                return IsLimitReply(root, type) ? LimitReplyFrom(root) : LastTurn.Other;
            }
        }
        return null;
    }

    private static bool IsLimitReply(JsonElement record, string type) =>
        type == "assistant"
        && record.TryGetProperty("isApiErrorMessage", out var flag) && flag.ValueKind == JsonValueKind.True
        && StringOf(record, "apiError") == UsageLimitReached;

    private static LastTurn LimitReplyFrom(JsonElement record)
    {
        var uuid = StringOf(record, "uuid");
        if (uuid is null || !Guid.TryParse(uuid, out _))
            return LastTurn.Unreadable("the usage-limit record carries no uuid");
        var at = StringOf(record, "timestamp");
        if (at is null || !DateTime.TryParse(at, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var atUtc))
            return LastTurn.Unreadable($"the usage-limit record {uuid} carries no readable timestamp");
        return new LastTurn(LastTurnKind.LimitReply, uuid, DateTime.SpecifyKind(atUtc, DateTimeKind.Utc), null);
    }

    private static string? StringOf(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
