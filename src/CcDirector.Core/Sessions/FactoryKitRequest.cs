namespace CcDirector.Core.Sessions;

/// <summary>
/// The kit a create must put in place before its agent starts (Factory Definition mission, phase 5): which factory,
/// published version, seat and kind; whether the seat works in the kit's own <c>work/</c> folder because it has no
/// repository (<see cref="Home"/>); and, on a restore, the kit folder the seat already had
/// (<see cref="ReturningDirectory"/>), which is refreshed in place rather than replaced by a new one; and, when a check
/// started the seat, what the check found (<see cref="Check"/>, phase 5b), written beside the kit as <c>check.json</c>.
/// </summary>
public sealed record FactoryKitRequest(string Factory, int Version, string Seat, string Kind, bool Home = false,
    string? ReturningDirectory = null, Gateway.Contracts.FactoryCheckDto? Check = null)
{
    /// <summary>One line for the log.</summary>
    public override string ToString() =>
        $"factory={Factory}, version={Version}, seat={Seat}, kind={Kind}, home={Home}, returning={ReturningDirectory ?? "(none)"}, " +
        $"check={(Check is null ? "(none)" : $"{Check.CheckPath} count={Check.Count}")}";
}

/// <summary>
/// A seat's kit could not be put in place, so the create is stopped (Factory Definition mission, phase 5). The message
/// is one sentence a person can act on, carrying the Gateway's own words when it refused.
/// </summary>
public sealed class FactoryKitUnavailableException : InvalidOperationException
{
    public FactoryKitUnavailableException(FactoryKitRequest kit, string reason, Exception? inner = null)
        : base($"No session was opened. It is the seat '{kit.Seat}' of the factory '{kit.Factory}', and a seat never " +
               $"starts without its kit; the kit of version {kit.Version} could not be put in place: {reason}", inner)
    {
        Kit = kit;
        Reason = reason;
    }

    /// <summary>The kit that could not be had.</summary>
    public FactoryKitRequest Kit { get; }

    /// <summary>Why, on its own - the message says it in full.</summary>
    public string Reason { get; }
}

/// <summary>
/// A seat's kit was put in place, but the seat may not start on this machine with this agent (Factory Definition
/// mission, phase 5): the agent cannot take the kit's rules or factory skills, or the machine does not hold a secret
/// the seat lists. The message is the one sentence a person reads; the kit this create wrote is already gone.
/// </summary>
public sealed class FactorySeatRefusedException : InvalidOperationException
{
    public FactorySeatRefusedException(FactoryKitRequest kit, string reason, Exception? inner = null) : base(reason, inner)
    {
        Kit = kit;
    }

    /// <summary>The seat that was refused.</summary>
    public FactoryKitRequest Kit { get; }
}
