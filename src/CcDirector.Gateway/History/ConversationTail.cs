using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.History;

/// <summary>
/// The conversation TAIL (traffic optimization, phase 2): a client that already holds the start of a
/// conversation is sent only what comes after it.
///
/// Phase 1 made an unchanged chat poll a 304 with no body. But a session that is being worked on changes on
/// every few polls, and each change re-sent the whole conversation - up to a megabyte compressed for a long
/// one. The client now sends back the CURSOR it was handed with its last answer, and when the Gateway can
/// prove that what the client holds is exactly the start of what it would send now, it sends only the rest.
///
/// WHAT THE CURSOR PROVES, and why it is enough. The cursor is <c>v1.{count}.{hash}</c>, where the hash is a
/// SHA-256 over the tenant, the session, the stored generation, and each of the first <c>count</c> messages
/// AS THIS GATEWAY SERIALIZES THEM (the host's own JSON options, the same bytes a full answer carries). To
/// answer with a tail the Gateway recomputes that hash over its CURRENT first <c>count</c> messages; only an
/// exact match allows it. So:
/// <list type="bullet">
/// <item>a generation switch (a /clear, a new transcript) changes the hash - full;</item>
/// <item>a conversation now SHORTER than the cursor's count cannot be matched - full;</item>
/// <item>an earlier message that changed in any byte changes the hash - full;</item>
/// <item>another account's cursor, or another session's, hashes a different tenant or session - full;</item>
/// <item>anything that does not parse - an old cursor version, a tampered one, garbage - full.</item>
/// </list>
/// The rule is: when in doubt, full. A full answer is always correct; a tail is only an optimisation of it,
/// taken when the prefix is proven equal. There is no case that yields a gap or a duplicate, because the tail
/// starts exactly at the count the client said it holds and that the hash proved.
///
/// CAN THE LAST MESSAGE CHANGE AFTER IT IS STORED? No. Stored turns are insert-only: a push skips every
/// ordinal already held (<see cref="SessionTurnStore.Append"/>), so a turn, once served, is served with the
/// same bytes for as long as its generation is current - pinned by
/// <c>ConversationTailStoreTests.AStoredTurnNeverChanges_SoTheTailNeverResendsIt</c>. The tail therefore
/// does not re-send the last message. And correctness does not rest on that rule: if a stored turn ever did
/// change, the hash over the prefix would no longer match and the answer would be full.
///
/// WHAT ALWAYS COMES WITH A TAIL. Every field of the answer other than <c>messages</c> - the stale notice,
/// the empty text, the history state, the status - is folded over the WHOLE conversation first
/// (<see cref="SessionConversationFold"/>) and then copied unchanged, so a tail answer's envelope is the
/// full answer's envelope, field for field.
/// </summary>
public static class ConversationTail
{
    /// <summary>The cursor format version. A cursor of any other version is answered in full.</summary>
    public const string Version = "v1";

    /// <summary>The longest cursor accepted at all; anything longer is not one of ours.</summary>
    public const int MaxCursorLength = 128;

    private static readonly byte[] Domain = Encoding.UTF8.GetBytes("devthrottle/conversation-tail/v1");

    /// <summary>What the Gateway answers a cursor request with.</summary>
    /// <param name="TailFrom">How many messages the client already holds and keeps; 0 means this answer is the
    /// whole conversation and replaces what the client holds.</param>
    /// <param name="Messages">The messages to send: the tail after <paramref name="TailFrom"/>, or all of them.</param>
    /// <param name="Cursor">The cursor for the WHOLE conversation after this answer - what the client sends next.</param>
    /// <param name="Reason">Why the answer is full, for the log; null when it is a tail.</param>
    public readonly record struct Answer(int TailFrom, List<HistoryMessageDto> Messages, string Cursor, string? Reason);

    /// <summary>
    /// Decide the answer for a request that carried <paramref name="cursor"/> (empty when the client holds
    /// nothing yet) against the current conversation <paramref name="messages"/>.
    /// </summary>
    public static Answer Decide(
        string? cursor,
        string tenant,
        string sessionId,
        string generation,
        List<HistoryMessageDto> messages,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(options);

        // One pass over the conversation: the hash is taken at the claimed count on the way past, and the
        // same running hash carries on to the end for the next cursor.
        string? reason = null;
        var claimed = -1;
        byte[]? claimedHash = null;
        if (string.IsNullOrEmpty(cursor))
            reason = "no cursor";
        else if (!TryParse(cursor, out claimed, out claimedHash))
            reason = "cursor does not parse";
        else if (claimed > messages.Count)
            reason = $"cursor holds {claimed} message(s), conversation has {messages.Count}";

        using var hash = Start(tenant, sessionId, generation);
        var matched = false;
        for (var i = 0; i <= messages.Count; i++)
        {
            if (reason is null && i == claimed)
            {
                matched = CryptographicOperations.FixedTimeEquals(hash.GetCurrentHash(), claimedHash);
                if (!matched)
                    reason = "cursor does not match the conversation held here";
            }
            if (i < messages.Count)
                AppendMessage(hash, messages[i], options);
        }
        var next = Format(messages.Count, hash.GetCurrentHash());

        if (!matched)
            return new Answer(0, messages, next, reason);
        return new Answer(claimed, messages.GetRange(claimed, messages.Count - claimed), next, null);
    }

    /// <summary>The cursor for the first <paramref name="count"/> of <paramref name="messages"/>. What a client
    /// holding exactly those would have been handed.</summary>
    public static string CursorFor(
        string tenant, string sessionId, string generation, IReadOnlyList<HistoryMessageDto> messages, int count,
        JsonSerializerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, messages.Count);
        using var hash = Start(tenant, sessionId, generation);
        for (var i = 0; i < count; i++)
            AppendMessage(hash, messages[i], options);
        return Format(count, hash.GetCurrentHash());
    }

    private static IncrementalHash Start(string tenant, string sessionId, string generation)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, Domain);
        AppendField(hash, Encoding.UTF8.GetBytes(tenant));
        AppendField(hash, Encoding.UTF8.GetBytes(sessionId));
        AppendField(hash, Encoding.UTF8.GetBytes(generation));
        return hash;
    }

    private static void AppendMessage(IncrementalHash hash, HistoryMessageDto message, JsonSerializerOptions options) =>
        AppendField(hash, JsonSerializer.SerializeToUtf8Bytes(message, options));

    // Length-prefixed, so no two different sequences of fields can produce the same byte stream.
    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static string Format(int count, byte[] digest) =>
        $"{Version}.{count.ToString(CultureInfo.InvariantCulture)}.{Convert.ToHexString(digest).ToLowerInvariant()}";

    private static bool TryParse(string cursor, out int count, out byte[] digest)
    {
        count = -1;
        digest = Array.Empty<byte>();
        if (cursor.Length > MaxCursorLength)
            return false;
        var parts = cursor.Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Version, StringComparison.Ordinal))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out count) || count < 0)
            return false;
        if (parts[2].Length != 64 || !parts[2].All(Uri.IsHexDigit))
            return false;
        digest = Convert.FromHexString(parts[2]);
        return true;
    }
}
