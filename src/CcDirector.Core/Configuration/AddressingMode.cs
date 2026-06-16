namespace CcDirector.Core.Configuration;

/// <summary>
/// How machines in the fleet address ONE ANOTHER (issue #457). This governs which host
/// every cross-machine URL uses; loopback (127.0.0.1 / localhost) is NEVER a cross-machine
/// address under any mode.
///
///   - <see cref="Tailscale"/> (default): a Director advertises its Tailscale Serve front
///     door (<c>https://&lt;magicdns&gt;:&lt;port&gt;</c>); the tailnet is the trust boundary.
///   - <see cref="Lan"/>: a Director advertises its real LAN IPv4 (<c>http://&lt;lan-ip&gt;:&lt;port&gt;</c>)
///     and binds its Control API to a routable interface. Because the raw port then carries
///     no tailnet auth boundary, LAN mode REQUIRES the Director's auth to be enabled - the
///     bind fails loudly otherwise rather than exposing an open Control API on the LAN.
/// </summary>
public enum AddressingMode
{
    /// <summary>Advertise the Tailscale Serve front door. The fleet default.</summary>
    Tailscale = 0,

    /// <summary>Advertise the machine's real LAN IPv4. Requires Director auth.</summary>
    Lan = 1,
}

/// <summary>Parse/format helpers for <see cref="AddressingMode"/>. Pure - unit-tested.</summary>
public static class AddressingModeExtensions
{
    /// <summary>The config.json string form: "tailscale" or "lan".</summary>
    public static string ToConfigString(this AddressingMode mode) => mode switch
    {
        AddressingMode.Tailscale => "tailscale",
        AddressingMode.Lan => "lan",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown addressing mode"),
    };

    /// <summary>
    /// Parse a config.json value. Null/empty/whitespace yields the default
    /// (<see cref="AddressingMode.Tailscale"/>). Any other unrecognized value THROWS with the
    /// allowed set named (no-fallback rule: a typo must not silently pick a mode).
    /// </summary>
    public static AddressingMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AddressingMode.Tailscale;
        return value.Trim().ToLowerInvariant() switch
        {
            "tailscale" => AddressingMode.Tailscale,
            "lan" => AddressingMode.Lan,
            _ => throw new ArgumentException(
                $"addressing_mode '{value}' is not valid - it must be \"tailscale\" or \"lan\".", nameof(value)),
        };
    }

    /// <summary>True when <paramref name="value"/> is a recognized mode (for input validation).</summary>
    public static bool IsValid(string? value)
    {
        try { Parse(value); return true; }
        catch (ArgumentException) { return false; }
    }
}
