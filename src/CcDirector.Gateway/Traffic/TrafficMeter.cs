using System.Collections.Concurrent;

namespace CcDirector.Gateway.Traffic;

/// <summary>
/// Traffic optimization, phase 3: IN-MEMORY counters of what the Gateway sends and receives, so "how far are we
/// from the minimum traffic" can be answered with a measurement instead of a guess.
///
/// WHAT A CELL IS. One cell per (hour, account, kind, name, client). The kind says what was counted:
/// <see cref="Http"/> (an HTTP request, named by its route TEMPLATE), <see cref="WebSocket"/> (bytes on an
/// accepted WebSocket, named by its route template), <see cref="SignalRIn"/> and <see cref="SignalROut"/> (one
/// SignalR message, named by its hub method) and <see cref="Outbound"/> (a call the Gateway itself makes, named
/// by the destination host). Totals are summed at READ time, so the hot path touches one cell, never two.
///
/// WHAT IS NEVER RECORDED. No body, no token, no query string and no identifier from a path: every name is a
/// route template, a hub method from a closed set, or a host the Gateway chose itself. Everything a client can
/// choose freely (an HTTP method, an inbound hub method) is folded onto a closed set before it becomes a name,
/// so a hostile client cannot grow this dictionary without bound.
///
/// WHAT IT COSTS. A cell lookup is one lock-free <see cref="ConcurrentDictionary{TKey,TValue}"/> read (an add
/// once per cell per hour), and every counter is an <see cref="Interlocked"/> add. There is no lock on the hot
/// path. Cells older than <see cref="WindowHours"/> are dropped the first time the hour turns over.
///
/// Nothing is written to the database: a restart resets the counters, and <see cref="CountingSinceUtc"/> says
/// from when they count.
/// </summary>
public sealed class TrafficMeter
{
    /// <summary>How many hourly buckets are kept, the current one included.</summary>
    public const int WindowHours = 48;

    public const string Http = "http";
    public const string WebSocket = "websocket";
    public const string SignalRIn = "signalr-in";
    public const string SignalROut = "signalr-out";
    public const string Outbound = "outbound";

    /// <summary>The account of traffic no account could be resolved for: unauthenticated requests, the
    /// administrator surfaces, and work the Gateway does outside any request.</summary>
    public const string NoAccount = "(none)";

    /// <summary>The client slot of a row where the client kind does not apply (outbound calls, and SignalR
    /// messages sent from outside a hub connection).</summary>
    public const string NoClient = "-";

    private readonly ConcurrentDictionary<TrafficKey, TrafficCounters> _cells = new();
    private readonly Func<DateTimeOffset> _clock;
    private long _lastPrunedHour;

    public TrafficMeter(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        CountingSinceUtc = _clock();
        _lastPrunedHour = HourOf(CountingSinceUtc);
    }

    /// <summary>When these counters started counting: the Gateway's start, since nothing survives a restart.</summary>
    public DateTimeOffset CountingSinceUtc { get; }

    /// <summary>The counters for this hour's cell. Never null, never throws for valid arguments.</summary>
    public TrafficCounters Cell(string kind, string name, string client, string? account)
    {
        var hour = HourOf(_clock());
        if (hour != Interlocked.Read(ref _lastPrunedHour))
            PruneOnce(hour);
        var key = new TrafficKey(hour, account ?? NoAccount, kind, name, client);
        return _cells.GetOrAdd(key, static _ => new TrafficCounters());
    }

    /// <summary>
    /// Every cell inside the window, oldest hour first. <paramref name="account"/> null means every account;
    /// otherwise only that account's cells are returned - never a total that includes anybody else.
    /// </summary>
    public IReadOnlyList<TrafficRow> Rows(string? account = null, int hours = WindowHours)
    {
        hours = Math.Clamp(hours, 1, WindowHours);
        var now = HourOf(_clock());
        var oldest = now - (hours - 1) * TimeSpan.TicksPerHour;
        var rows = new List<TrafficRow>();
        foreach (var (key, counters) in _cells)
        {
            if (key.HourTicks < oldest || key.HourTicks > now) continue;
            if (account is not null && !string.Equals(key.Account, account, StringComparison.Ordinal)) continue;
            rows.Add(new TrafficRow(new DateTimeOffset(key.HourTicks, TimeSpan.Zero), key.Account, key.Kind, key.Name, key.Client, counters.Read()));
        }

        rows.Sort(static (a, b) =>
        {
            var c = a.HourUtc.CompareTo(b.HourUtc);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Account, b.Account);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Kind, b.Kind);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Name, b.Name);
            return c != 0 ? c : string.CompareOrdinal(a.Client, b.Client);
        });
        return rows;
    }

    internal static long HourOf(DateTimeOffset at)
    {
        var ticks = at.UtcTicks;
        return ticks - ticks % TimeSpan.TicksPerHour;
    }

    private void PruneOnce(long hour)
    {
        var seen = Interlocked.Read(ref _lastPrunedHour);
        if (hour <= seen || Interlocked.CompareExchange(ref _lastPrunedHour, hour, seen) != seen)
            return;
        var oldest = hour - (WindowHours - 1) * TimeSpan.TicksPerHour;
        foreach (var key in _cells.Keys)
        {
            if (key.HourTicks < oldest)
                _cells.TryRemove(key, out _);
        }
    }

    private readonly record struct TrafficKey(long HourTicks, string Account, string Kind, string Name, string Client);
}

/// <summary>The counters of one cell. Every field is only ever changed with <see cref="Interlocked"/>.</summary>
public sealed class TrafficCounters
{
    private long _count;
    private long _bytesOut;
    private long _bytesIn;
    private long _headerBytesOut;
    private long _notModified;
    private long _historyTail;
    private long _historyFull;

    /// <summary>One HTTP request, with what it cost on the wire.</summary>
    public void AddRequest(long bytesOut, long bytesIn, long headerBytesOut, bool notModified, bool? historyTail)
    {
        Interlocked.Increment(ref _count);
        if (bytesOut > 0) Interlocked.Add(ref _bytesOut, bytesOut);
        if (bytesIn > 0) Interlocked.Add(ref _bytesIn, bytesIn);
        if (headerBytesOut > 0) Interlocked.Add(ref _headerBytesOut, headerBytesOut);
        if (notModified) Interlocked.Increment(ref _notModified);
        if (historyTail == true) Interlocked.Increment(ref _historyTail);
        else if (historyTail == false) Interlocked.Increment(ref _historyFull);
    }

    /// <summary>One message (a SignalR message, a WebSocket frame, an outbound call) and its bytes.</summary>
    public void AddMessage(long bytesOut, long bytesIn)
    {
        Interlocked.Increment(ref _count);
        if (bytesOut > 0) Interlocked.Add(ref _bytesOut, bytesOut);
        if (bytesIn > 0) Interlocked.Add(ref _bytesIn, bytesIn);
    }

    /// <summary>Bytes only, no new message (a WebSocket frame that continues a message).</summary>
    public void AddBytes(long bytesOut, long bytesIn)
    {
        if (bytesOut > 0) Interlocked.Add(ref _bytesOut, bytesOut);
        if (bytesIn > 0) Interlocked.Add(ref _bytesIn, bytesIn);
    }

    public TrafficNumbers Read() => new(
        Interlocked.Read(ref _count),
        Interlocked.Read(ref _bytesOut),
        Interlocked.Read(ref _bytesIn),
        Interlocked.Read(ref _headerBytesOut),
        Interlocked.Read(ref _notModified),
        Interlocked.Read(ref _historyTail),
        Interlocked.Read(ref _historyFull));
}

/// <summary>A cell's numbers at the moment it was read.</summary>
public readonly record struct TrafficNumbers(
    long Count, long BytesOut, long BytesIn, long HeaderBytesOut, long NotModified, long HistoryTail, long HistoryFull)
{
    public TrafficNumbers Plus(TrafficNumbers o) => new(
        Count + o.Count, BytesOut + o.BytesOut, BytesIn + o.BytesIn, HeaderBytesOut + o.HeaderBytesOut,
        NotModified + o.NotModified, HistoryTail + o.HistoryTail, HistoryFull + o.HistoryFull);
}

/// <summary>One cell as the read route reports it.</summary>
public sealed record TrafficRow(DateTimeOffset HourUtc, string Account, string Kind, string Name, string Client, TrafficNumbers Numbers);
