using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Boxes;

/// <summary>
/// WHETHER FACTORY BOXES IS ON FOR AN ACCOUNT (the Factory Boxes mission, issue #4258, section 8 merge plan). The one
/// answer the Boxes routes (through <see cref="BoxesGate"/>) and <c>GET /account/status</c> read, so hiding the screen
/// and refusing the route can never disagree.
///
/// A LIST OF ACCOUNT IDS IN THE GATEWAY'S CONFIGURATION, EMPTY BY DEFAULT. The environment variable
/// <see cref="EnvironmentVariable"/> carries the tenant ids, comma or semicolon separated. Nothing ships in it, so a
/// Gateway nobody configured has Boxes off for every account. This is the <c>StaffAccess</c> pattern rather than the
/// recorded per-account switches (<c>MailDeskSwitch</c>, <c>FactoryAgentsSwitch</c>) on purpose: Boxes is shown to the
/// owner's account only until the mission's QA passes (owner, D7), so the list is a deployment setting that one
/// person changes with a deploy, and needs no administrator route or stored decision yet.
///
/// Tenant ids are compared exactly (they are GUID strings the Gateway minted); a blank entry is ignored.
/// </summary>
public sealed class BoxesSwitch
{
    /// <summary>The environment variable naming the accounts Boxes is on for. Empty or unset turns it on for nobody.</summary>
    public const string EnvironmentVariable = "DEVTHROTTLE_BOXES_TENANT_IDS";

    private static readonly char[] Separators = { ',', ';' };

    private readonly HashSet<string> _enabled;

    /// <param name="configured">The raw list. Production passes the environment variable; a test passes its own.</param>
    public BoxesSwitch(string? configured)
    {
        _enabled = string.IsNullOrWhiteSpace(configured)
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(configured.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.Ordinal);
    }

    /// <summary>The switch as this process's environment configures it, with one startup line saying how many accounts it is on for.</summary>
    public static BoxesSwitch FromEnvironment()
    {
        var boxes = new BoxesSwitch(Environment.GetEnvironmentVariable(EnvironmentVariable));
        FileLog.Write($"[BoxesSwitch] Factory Boxes is on for {boxes.ConfiguredCount} configured account(s) ({EnvironmentVariable})");
        return boxes;
    }

    /// <summary>How many accounts are listed. Logged at startup; an account id is never logged.</summary>
    public int ConfiguredCount => _enabled.Count;

    /// <summary>Whether Boxes is on for <paramref name="tenant"/>. False for an invalid tenant.</summary>
    public bool IsOn(TenantId tenant) => tenant.IsValid && _enabled.Contains(tenant.Value);
}
