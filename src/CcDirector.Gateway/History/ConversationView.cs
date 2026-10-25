using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.History;

/// <summary>
/// One read of a session's current conversation for a cursor request (issue #4387): the head, how many messages
/// there are, and the index of each - but not the messages themselves. A caller rebuilds only the messages it is
/// about to send, with <see cref="Messages"/>, so a poll whose answer is "nothing new" rebuilds none at all.
///
/// A snapshot: taken inside the store's lock, read outside it. Nothing in it changes when the store goes on to
/// hold more.
/// </summary>
public sealed class ConversationView
{
    private readonly Func<int, HistoryMessageDto> _build;
    private readonly byte[][] _digests;
    private readonly bool[] _startsTurn;

    /// <param name="build">Rebuilds message i, fresh, on each call.</param>
    internal ConversationView(SessionTurnHeadEntity? head, int count, Func<int, HistoryMessageDto> build, ConversationIndex index)
    {
        Head = head;
        Count = count;
        _build = build;
        (_digests, _startsTurn) = index.Copy(count);
    }

    /// <summary>A view of messages already built, for the tests of what is answered from a view.</summary>
    internal static ConversationView Of(
        string tenant, string sessionId, string generation, List<HistoryMessageDto> messages, System.Text.Json.JsonSerializerOptions options)
    {
        using var index = new ConversationIndex(tenant, sessionId, generation, options);
        foreach (var message in messages)
            index.Append(message);
        var json = messages.Select(m => System.Text.Json.JsonSerializer.Serialize(m, options)).ToList();
        return new ConversationView(null, messages.Count,
            i => System.Text.Json.JsonSerializer.Deserialize<HistoryMessageDto>(json[i], options)!, index);
    }

    /// <summary>The stored head, or null when nothing has ever been pushed for the session.</summary>
    public SessionTurnHeadEntity? Head { get; }

    /// <summary>How many messages the current conversation has.</summary>
    public int Count { get; }

    /// <summary>The cursor hash of the first <paramref name="count"/> messages, for 0 to <see cref="Count"/>.</summary>
    public byte[] DigestAt(int count) => _digests[count];

    /// <summary>Whether message <paramref name="index"/> starts a turn (<see cref="ConversationWindow.StartsTurn"/>).</summary>
    public bool StartsTurn(int index) => _startsTurn[index];

    /// <summary>Messages [<paramref name="from"/>, <paramref name="to"/>), rebuilt fresh, exactly as
    /// <see cref="SessionTurnStore.ReadCurrent"/> builds them.</summary>
    public List<HistoryMessageDto> Messages(int from, int to)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(to, Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(from, to);
        var messages = new List<HistoryMessageDto>(to - from);
        for (var i = from; i < to; i++)
            messages.Add(_build(i));
        return messages;
    }
}
