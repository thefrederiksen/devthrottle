namespace CcDirector.Core.ApiMode;

/// <summary>
/// The numbers an API-mode session hands out: the event <c>sequence</c>, the Director's turn ids and the
/// command ids written to the agent. One instance lives as long as the DevThrottle session, across every
/// agent process it starts (take the wheel and hand back start a new one), so a sequence keeps rising and
/// a turn id is never reused within the session.
///
/// Not thread-safe: a dialect is fed from one place at a time, and so is this.
/// </summary>
public sealed class SessionEventNumbering
{
    private long _sequence;
    private long _turn;
    private long _command;

    /// <summary>The next event sequence number, starting at 1.</summary>
    public long NextSequence() => ++_sequence;

    /// <summary>The next turn id: <c>turn-1</c>, <c>turn-2</c>, ...</summary>
    public string NextTurnId() => $"turn-{++_turn}";

    /// <summary>The next command id: <c>dt-1</c>, <c>dt-2</c>, ... The agent repeats it on its response.</summary>
    public string NextCommandId() => $"dt-{++_command}";
}
