using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// Whether the Teams routes are released on this Gateway (devthrottle_internal#2300, review finding F1). Teams merges
/// DARK: until the owner releases Teams, a deploy of main exposes no team route at all, so nobody can create a team
/// that has no bill behind it and that every background sweep would then visit for good.
///
/// Read from the environment variable <c>CC_GATEWAY_TEAMS</c>, the same way the hosted Gateway's other run-time
/// settings are supplied (an app setting on the hosted service). DEFAULT OFF: only the exact value <c>1</c> turns it
/// on; unset, empty or anything else is off. While it is off the Gateway does not map the team routes - a request to
/// one is answered as for any route that does not exist. The Gateway reads it once at construction; a test passes an
/// explicit override to the GatewayHost constructor instead.
/// </summary>
public static class TeamsReleaseSwitch
{
    /// <summary>The environment variable that releases Teams on this Gateway.</summary>
    public const string EnvVar = "CC_GATEWAY_TEAMS";

    /// <summary>Whether Teams is released in this process's environment.</summary>
    public static bool IsReleased()
    {
        var released = Parse(Environment.GetEnvironmentVariable(EnvVar));
        FileLog.Write($"[TeamsReleaseSwitch] IsReleased: {EnvVar}={(released ? "1 (released)" : "not 1 (dark)")}");
        return released;
    }

    /// <summary>The switch as one value reads. Pure, so it is tested directly.</summary>
    public static bool Parse(string? value) => string.Equals(value?.Trim(), "1", StringComparison.Ordinal);
}
