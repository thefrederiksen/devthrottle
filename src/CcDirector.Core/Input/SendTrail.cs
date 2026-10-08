using CcDirector.Core.Utilities;

namespace CcDirector.Core.Input;

/// <summary>
/// THE STEPS ONE SEND TOOK, KEPT WITH ITS DELIVERY RECORD (the Prompt Delivery mission, 8 October 2026).
///
/// WHY THIS EXISTS. On 7 October 2026 the owner's "go" was refused with a reason cut off at 300 characters
/// ("what it read after the clear: reading=HoldsText, tex..."), and the steps that led there - what the composer held
/// before the clear, which keys cleared it, what it held after - were scattered across a Director log of half a million
/// lines, between the heartbeats. Finding why took an hour of grepping. Every step a send takes is now written to the
/// Director log exactly as before AND kept on this trail, and the trail is written, in full, into the send's line in the
/// durable delivery record (<see cref="Sessions.DeliveryRecord"/>), beside its final state.
///
/// One trail belongs to one send: <see cref="Begin"/> starts it for the calling flow, and every step written from that
/// flow - including the parts of the send that run after the prompt verb has answered - lands on it. A step written
/// with no trail begun (a send with no delivery id, a doorbell) is logged and nothing more.
///
/// The trail holds what the composer showed, which can include the owner's own draft. It stays on this machine, in the
/// same folder as the delivery record, exactly as the Director log does.
/// </summary>
public sealed class SendTrail
{
    /// <summary>The most steps one trail keeps. A send takes a dozen; a runaway loop must not grow the record file.</summary>
    internal const int MaxSteps = 200;

    private static readonly AsyncLocal<SendTrail?> CurrentTrail = new();

    private readonly object _lock = new();
    private readonly List<string> _steps = [];
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private int _dropped;

    /// <summary>The trail of the send running in this flow, or null when none was begun.</summary>
    public static SendTrail? Current => CurrentTrail.Value;

    /// <summary>Start a trail for the send about to run in this flow. Everything the send calls from here on, awaited or
    /// not, writes to it.</summary>
    public static SendTrail Begin()
    {
        var trail = new SendTrail();
        CurrentTrail.Value = trail;
        return trail;
    }

    /// <summary>
    /// Write one step: to the Director log as <c>[tag] text</c>, and onto the current trail, if any, with the time since
    /// the trail began. The text is kept whole - never cut - so a reason can always be read in full afterwards.
    /// </summary>
    public static void Step(string tag, string text)
    {
        FileLog.Write($"[{tag}] {text}");
        CurrentTrail.Value?.Add($"[{tag}] {text}");
    }

    /// <summary>The steps so far, in order, each prefixed with the milliseconds since the trail began.</summary>
    public IReadOnlyList<string> Steps
    {
        get
        {
            lock (_lock)
            {
                if (_dropped == 0) return _steps.ToList();
                return [.. _steps, $"... {_dropped} more steps were not kept (a trail keeps {MaxSteps})"];
            }
        }
    }

    private void Add(string line)
    {
        var elapsed = (long)(DateTime.UtcNow - _startedUtc).TotalMilliseconds;
        lock (_lock)
        {
            if (_steps.Count >= MaxSteps) { _dropped++; return; }
            _steps.Add($"+{elapsed}ms {line}");
        }
    }
}
