namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The four values of <see cref="SessionDto.VerdictState"/>: where the Wingman is with a session's last stop
/// (the Wingman-on-every-turn mission, slice D). String constants on the wire, the same convention as
/// <see cref="HoldStates"/>.
/// </summary>
public static class VerdictStates
{
    /// <summary>No verdict to show: never judged, invalidated by a Working transition, or the account's colour
    /// switch is off. The row is exactly what the detector made it.</summary>
    public const string None = "none";

    /// <summary>A verdict about this stop is being formed right now.</summary>
    public const string Reading = "reading";

    /// <summary>An accepted verdict is on the row.</summary>
    public const string Judged = "judged";

    /// <summary>The judge's answer was refused, or never came. The refused record is on the row with its reason,
    /// and the row stays exactly as the detector left it.</summary>
    public const string Failed = "failed";
}
