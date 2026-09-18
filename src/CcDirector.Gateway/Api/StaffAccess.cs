using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// WHO IS STAFF, AND WHAT THAT BUYS: the Wingman's debug view, and nothing else.
///
/// THERE IS NO ADMIN-USER CONCEPT IN THIS PRODUCT, and this does not invent one. The existing admin
/// endpoints are gated by a machine-to-machine service token, which no browser holds, and no flag on a
/// signed-in user exists anywhere in the Gateway or in any client. The Wingman debug view needs one thing
/// those two cannot give it: a person, signed in on their own device, who may read RAW TERMINAL SCREENS,
/// WHOLE CONVERSATIONS, EVERY PROMPT AND EVERY MODEL ANSWER for their own account's sessions - the four
/// most sensitive things this Gateway holds. A customer must never be handed that view by accident, and
/// the shipped product must not grow a privilege ladder to serve one internal screen.
///
/// SO IT IS A LIST OF ADDRESSES, AND IT IS EMPTY UNLESS THE DEPLOYMENT SETS ONE. The environment variable
/// <see cref="EnvironmentVariable"/> carries them, comma or semicolon separated, compared without regard
/// to case. Nothing is shipped in the list: a Gateway that nobody configured grants this to nobody, which
/// is the only safe default for a flag whose whole job is to widen what one reader can see.
///
/// IT NEVER WIDENS THE TENANT BOUNDARY. Staff read the debug view for sessions IN THEIR OWN ACCOUNT, over
/// the same tenant resolution and the same device-key requirement every other read takes. This answers
/// "may this reader see the prompts behind their own account's readings", never "may this reader see
/// another account". A staff address with no account on this Gateway sees nothing at all.
/// </summary>
public static class StaffAccess
{
    /// <summary>The environment variable naming the staff addresses. Empty or unset grants this to nobody.</summary>
    public const string EnvironmentVariable = "DEVTHROTTLE_STAFF_EMAILS";

    private static readonly char[] Separators = { ',', ';' };

    /// <summary>
    /// Is this address on the deployment's staff list? False for a null or blank address, and false for
    /// every address when the list is unset - see the class note on why that is the default.
    /// </summary>
    /// <param name="email">The signed-in account's own email, as the tenant registry recorded it.</param>
    /// <param name="configured">The raw list. Production passes the environment variable; a test passes its own.</param>
    public static bool IsStaff(string? email, string? configured)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(configured)) return false;
        var needle = email.Trim();
        foreach (var entry in configured.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (string.Equals(entry, needle, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>The same question, read against this process's own environment.</summary>
    public static bool IsStaff(string? email)
        => IsStaff(email, Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>How many addresses the deployment configured, for a startup log line that says whether the
    /// debug view can be reached at all without naming anybody.</summary>
    public static int ConfiguredCount(string? configured)
        => string.IsNullOrWhiteSpace(configured)
            ? 0
            : configured.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

    /// <summary>Write the startup line. Never names an address: the count is what an operator needs.</summary>
    public static void LogConfiguration()
        => FileLog.Write($"[StaffAccess] the Wingman debug view is open to {ConfiguredCount(Environment.GetEnvironmentVariable(EnvironmentVariable))} " +
                         $"configured staff address(es) ({EnvironmentVariable})");
}
