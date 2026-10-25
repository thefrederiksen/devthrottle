using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.History;

/// <summary>
/// What a cursor request needs to know about each message of a conversation, kept so it is worked out ONCE per
/// message rather than on every poll (issue #4387): the cursor hash after the message, and whether it starts a turn.
///
/// Why. Chat polls every 2.5 seconds per open screen. Each poll used to rebuild every stored message, serialise each
/// one again to hash the prefix and check the cursor, and only then cut the answer to the window or to the empty
/// tail - about 2 MB of work for a 2,000-message session, inside the store's one lock, for an answer that was
/// usually "nothing new". With the index a poll checks the cursor against a stored hash and rebuilds only the
/// messages it sends.
///
/// The hashes are exactly <see cref="ConversationTail"/>'s: the same running SHA-256 over the same bytes, taken after
/// each message instead of once at the end. So a cursor checked here is the cursor a full read would have checked.
/// An index is built for one tenant, session, generation and set of JSON options; it only ever grows, because stored
/// turns are insert-only, and the store drops it with the rows it was built from.
/// </summary>
public sealed class ConversationIndex : IDisposable
{
    private readonly IncrementalHash _hash;
    // _digests[k] is the cursor hash of the first k messages, so it always holds one more entry than _startsTurn.
    private readonly List<byte[]> _digests = new();
    private readonly List<bool> _startsTurn = new();

    public ConversationIndex(string tenant, string sessionId, string generation, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(sessionId);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(options);
        Tenant = tenant;
        SessionId = sessionId;
        Generation = generation;
        Options = options;
        _hash = ConversationTail.Start(tenant, sessionId, generation);
        _digests.Add(_hash.GetCurrentHash());
    }

    public string Tenant { get; }
    public string SessionId { get; }
    public string Generation { get; }
    public JsonSerializerOptions Options { get; }

    /// <summary>How many messages are indexed.</summary>
    public int Count => _startsTurn.Count;

    /// <summary>True when this index hashes for exactly this tenant, generation and options.</summary>
    public bool IsFor(string tenant, string generation, JsonSerializerOptions options) =>
        string.Equals(Tenant, tenant, StringComparison.Ordinal)
        && string.Equals(Generation, generation, StringComparison.Ordinal)
        && ReferenceEquals(Options, options);

    /// <summary>Index the next message of the conversation.</summary>
    public void Append(HistoryMessageDto message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ConversationTail.AppendMessage(_hash, message, Options);
        _digests.Add(_hash.GetCurrentHash());
        _startsTurn.Add(ConversationWindow.StartsTurn(message));
    }

    /// <summary>The cursor hash of the first <paramref name="count"/> messages.</summary>
    public byte[] DigestAt(int count) => _digests[count];

    /// <summary>Whether message <paramref name="index"/> starts a turn.</summary>
    public bool StartsTurn(int index) => _startsTurn[index];

    /// <summary>A copy of the first <paramref name="count"/> entries, safe to read after the store's lock is let go
    /// while this index goes on growing.</summary>
    internal (byte[][] Digests, bool[] StartsTurn) Copy(int count) =>
        (_digests.GetRange(0, count + 1).ToArray(), _startsTurn.GetRange(0, count).ToArray());

    public void Dispose() => _hash.Dispose();
}
