using CcDirector.Core.Utilities;

namespace CcDirector.Core.Account;

/// <summary>
/// The DevThrottle backend's authentication endpoints and the public client key needed to call them
/// (issue #876). The backend is the Supabase project behind devthrottle.com; its refresh exchange is
/// <c>POST {auth}/token?grant_type=refresh_token</c> with the project's ANONYMOUS key in the
/// <c>apikey</c> header. Both values are public material - the anonymous key ships in the JavaScript
/// of every devthrottle.com visitor's browser and grants nothing by itself - so they are embedded at
/// build time, the <see cref="DevThrottleSigningKeys"/> precedent. Environment-variable overrides
/// exist for tests and for pointing an install at a different backend without a rebuild.
///
/// This replaces the pre-#876 gating where the refresh URL came ONLY from an environment variable no
/// install ever set - the same defect shape as the unset signing-secret gap fixed alongside the ES256
/// validation: production configuration must not depend on machine environment.
/// </summary>
public static class DevThrottleAuthBackend
{
    /// <summary>The environment variable that overrides the refresh-exchange endpoint. Unset in normal production use.</summary>
    public const string RefreshUrlEnvVar = "DEVTHROTTLE_REFRESH_URL";

    /// <summary>The environment variable that overrides the public anonymous key. Unset in normal production use.</summary>
    public const string AnonymousKeyEnvVar = "DEVTHROTTLE_AUTH_ANONYMOUS_KEY";

    /// <summary>The backend's refresh-exchange endpoint, embedded at build time.</summary>
    public const string ProductionRefreshUrl =
        "https://ompujpfrglgqvqprilxa.supabase.co/auth/v1/token?grant_type=refresh_token";

    /// <summary>
    /// The backend's public ANONYMOUS key, embedded at build time. Public material: it ships in the
    /// browser of every devthrottle.com visitor and only identifies the project - it cannot mint,
    /// refresh, or read anything without a user's own token accompanying it.
    /// </summary>
    public const string ProductionAnonymousKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9tcHVqcGZyZ2xncXZxcHJpbHhhIiwicm9sZSI6ImFub24iLCJpYXQiOjE3ODE2MTQ4OTksImV4cCI6MjA5NzE5MDg5OX0.YKq4AK2af5O0HbI9Q6ujaFrvRbLDeY8HSn-OdK6RAgo";

    /// <summary>
    /// Resolves the refresh-exchange endpoint: the environment override when set, otherwise the
    /// embedded production endpoint. Never null - since #876 the refresh exchange is always
    /// configured.
    /// </summary>
    public static string ResolveRefreshUrl()
    {
        var overrideValue = Environment.GetEnvironmentVariable(RefreshUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            FileLog.Write($"[DevThrottleAuthBackend] ResolveRefreshUrl: refresh endpoint resolved from {RefreshUrlEnvVar}");
            return overrideValue.Trim();
        }

        return ProductionRefreshUrl;
    }

    /// <summary>
    /// Resolves the public anonymous key sent as the <c>apikey</c> header: the environment override
    /// when set, otherwise the embedded production key. Never null.
    /// </summary>
    public static string ResolveAnonymousKey()
    {
        var overrideValue = Environment.GetEnvironmentVariable(AnonymousKeyEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            FileLog.Write($"[DevThrottleAuthBackend] ResolveAnonymousKey: anonymous key resolved from {AnonymousKeyEnvVar}");
            return overrideValue.Trim();
        }

        return ProductionAnonymousKey;
    }
}
