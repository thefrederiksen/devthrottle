using CcDirector.Core.Utilities;

namespace CcDirector.Gateway;

/// <summary>
/// The fail-CLOSED startup gate for the hosted Gateway (production-readiness item MH-3 / TOP-ISSUES #8).
///
/// A hosted deployment must PROVE its full contract at boot or REFUSE to run. Before this gate, the hosted
/// public image could silently fall back to Local single-tenant / no-auth semantics: <see cref="GatewayHostedMode.IsHosted"/>
/// treated a missing <c>CC_GATEWAY_HOSTED</c> as "not hosted", so a slot swap, config restore, or lost
/// environment variable booted the SAME image with the async tenant boundary gone, live-money entitlement
/// enforcement relaxed, and every hosted refusal deactivated - a catastrophic SILENT downgrade rather than a
/// loud failure.
///
/// The gate keys off the IMMUTABLE identity (<see cref="GatewayHostedMode.IsHostedImage"/> - the compiled-in
/// <see cref="HostedGatewayImageAttribute"/> on the marked launcher OR the image-wide
/// <see cref="GatewayHostedMode.HostedImageMarkerFileName"/> marker baked into the published artifact), NOT
/// off the droppable <c>CC_GATEWAY_HOSTED</c> toggle. So when the running DEPLOYMENT is the hosted build, the
/// full contract is REQUIRED even if the toggle was dropped, and even if the container entrypoint is
/// overridden to the unmarked <c>CcDirector.Gateway.dll</c> that also ships in the image - a dropped toggle,
/// or a swapped entry executable, now crashes the boot instead of downgrading it.
///
/// THE HOSTED CONTRACT (all must hold, checked here at startup):
///  1. Hosted mode ON       - <c>CC_GATEWAY_HOSTED=1</c>. This is what drives the async tenant boundary AND
///     live-entitlement enforcement (<see cref="GatewayHost"/> builds its <see cref="Tenancy.EntitlementRegistry"/>
///     with live-mode required exactly when the mode it read once at startup is hosted), so requiring it here
///     transitively guarantees both.
///  2. Auth ENABLED         - the auth-disable debug flags (<c>CC_GATEWAY_NO_AUTH=1</c>, <c>CC_GATEWAY_AUTH=0</c>)
///     are REJECTED: they must not be honorable in production, so their presence is a contract violation.
///  3. Public HTTPS URL     - <c>CC_GATEWAY_PUBLIC_URL</c> is set to an <c>https://</c> URL.
///  4. PostgreSQL provider  - <c>CC_GATEWAY_DB_CONNECTION</c> is set (the hosted Gateway does NOT run on the
///     local SQLite file). The migrations themselves are applied fail-loud by
///     <see cref="Data.GatewayDatabase"/> (it throws if Migrate() fails), so "migrations applied" is enforced
///     by construction once this precondition holds.
///
/// SELF-HOST IS UNAFFECTED. When the running executable is not the hosted image (the desktop tray, the dev
/// console host, the test runner), this gate does nothing at all - self-host stays a distinct artifact with
/// its byte-identical, unchanged behavior.
/// </summary>
public static class HostedStartupContract
{
    /// <summary>
    /// Assert the hosted contract against the live environment when this executable is the hosted image, and
    /// throw <see cref="InvalidOperationException"/> listing every violation if it does not hold. A no-op on
    /// any non-hosted-image build. Called ONCE, early in <see cref="GatewayEntryPoint.Run"/>, before the host
    /// is built - so a violation fails the boot loud instead of downgrading it.
    /// </summary>
    public static void AssertFromEnvironment()
        => Assert(GatewayHostedMode.IsHostedImage, Environment.GetEnvironmentVariable);

    /// <summary>
    /// Testable core of <see cref="AssertFromEnvironment"/>: a no-op unless <paramref name="isHostedImage"/>
    /// is true, otherwise reads the five contract values through <paramref name="readEnv"/>, and throws
    /// <see cref="InvalidOperationException"/> listing every violation if the contract does not hold. The
    /// seam lets a test drive both the self-host no-op and the hosted-image refusal deterministically,
    /// without depending on the process's real entry assembly or environment.
    /// </summary>
    internal static void Assert(bool isHostedImage, Func<string, string?> readEnv)
    {
        if (!isHostedImage)
            return;

        var violations = CheckHostedContract(
            hosted: readEnv(GatewayHostedMode.HostedEnvVar),
            noAuth: readEnv(GatewayHost.AuthDisabledEnvVar),
            authToggle: readEnv(GatewayHost.AuthEnabledEnvVar),
            publicUrl: readEnv(GatewayPublicUrl.PublicBaseUrlEnvVar),
            dbConnection: readEnv(Data.GatewayDatabase.PostgresConnectionEnvVar));

        if (violations.Count == 0)
        {
            FileLog.Write("[HostedStartupContract] hosted image: full hosted contract satisfied (hosted mode + auth + HTTPS public URL + PostgreSQL). Starting.");
            return;
        }

        var message =
            "REFUSING TO START. This is the hosted Gateway image, but the hosted contract is not satisfied. " +
            "A hosted deployment must not fall back to Local single-tenant / no-auth semantics, so it fails " +
            "closed rather than downgrade. Fix the following and restart:" +
            Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", violations);

        FileLog.Write("[HostedStartupContract] " + message.Replace(Environment.NewLine, " | "));
        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Pure hosted-contract check: given the raw environment values, return the list of violations (empty
    /// when the contract holds). Fully unit-testable without touching the process environment. The caller is
    /// responsible for only invoking this when the executable IS the hosted image.
    /// </summary>
    /// <param name="hosted">The <c>CC_GATEWAY_HOSTED</c> value.</param>
    /// <param name="noAuth">The <c>CC_GATEWAY_NO_AUTH</c> value.</param>
    /// <param name="authToggle">The <c>CC_GATEWAY_AUTH</c> value.</param>
    /// <param name="publicUrl">The <c>CC_GATEWAY_PUBLIC_URL</c> value.</param>
    /// <param name="dbConnection">The <c>CC_GATEWAY_DB_CONNECTION</c> value.</param>
    public static IReadOnlyList<string> CheckHostedContract(
        string? hosted, string? noAuth, string? authToggle, string? publicUrl, string? dbConnection)
    {
        var violations = new List<string>();

        // 1. Hosted mode ON. This single check also guarantees live-entitlement enforcement, because the
        //    GatewayHost builds the EntitlementRegistry with live-mode required exactly when its options say hosted.
        if (!string.Equals(hosted, "1", StringComparison.Ordinal))
            violations.Add(
                $"{GatewayHostedMode.HostedEnvVar} must be \"1\" (hosted mode drives the tenant boundary and " +
                $"live-entitlement enforcement); it is {Describe(hosted)}.");

        // 2. Auth must be ENABLED - the debug disable flags are rejected outright in production.
        if (string.Equals(noAuth, "1", StringComparison.Ordinal))
            violations.Add(
                $"{GatewayHost.AuthDisabledEnvVar}=1 disables the auth gate and is not honorable on the hosted " +
                "image; remove it.");
        if (string.Equals(authToggle, "0", StringComparison.Ordinal))
            violations.Add(
                $"{GatewayHost.AuthEnabledEnvVar}=0 disables the auth gate and is not honorable on the hosted " +
                "image; remove it.");

        // 3. Public HTTPS URL. A bare "https://" prefix is NOT enough: GatewayPublicUrl only trims and
        //    appends a surface path, so a value like "https://" (no host) would pass a prefix check and then
        //    be served as a broken base URL with no later fail-loud guard. The value must PARSE as an
        //    absolute https URL with a real host, in the base-URL shape (no query or fragment).
        if (string.IsNullOrWhiteSpace(publicUrl))
            violations.Add(
                $"{GatewayPublicUrl.PublicBaseUrlEnvVar} must be set to the public base URL (for example " +
                $"https://gateway.devthrottle.com); it is {Describe(publicUrl)}.");
        else if (!IsValidHttpsBaseUrl(publicUrl))
            violations.Add(
                $"{GatewayPublicUrl.PublicBaseUrlEnvVar} must be an absolute https:// URL with a host (for " +
                $"example https://gateway.devthrottle.com) on the hosted image; it is {Describe(publicUrl)}.");

        // 4. PostgreSQL provider. GatewayDatabase applies the migrations fail-loud once this is set, so the
        //    "migrations applied" part of the contract is enforced by construction from here.
        if (string.IsNullOrWhiteSpace(dbConnection))
            violations.Add(
                $"{Data.GatewayDatabase.PostgresConnectionEnvVar} must be set to the PostgreSQL connection " +
                "string (the hosted Gateway does not run on the local SQLite file); it is " +
                $"{Describe(dbConnection)}.");

        return violations;
    }

    /// <summary>
    /// True when <paramref name="value"/> is a well-formed public BASE URL for the hosted Gateway: it parses
    /// as an ABSOLUTE URI, its scheme is exactly <c>https</c>, it has a non-empty host, and it carries no
    /// query or fragment (a base URL onto which a surface path is appended). This rejects the
    /// prefix-only values a <c>StartsWith("https://")</c> check let through - <c>https://</c> (no host),
    /// <c>https:///path</c> (empty host), <c>https://:443</c> (empty host) - and non-https schemes, while
    /// accepting a host with an optional port and path (for example <c>https://gateway.devthrottle.com</c> or
    /// <c>https://host:8443</c>).
    /// </summary>
    private static bool IsValidHttpsBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return false;

        // Uri normalizes the scheme to lower case, so an ordinal compare to the https scheme constant is
        // exact and case-correct.
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrEmpty(uri.Host))
            return false;

        // A base URL carries no query or fragment; a surface path is appended to it, so those would only ever
        // corrupt the derived URL.
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        return true;
    }

    /// <summary>Describe an environment value for a violation message without ever echoing a secret: unset,
    /// blank, or "set" - never the value itself (a connection string carries credentials).</summary>
    private static string Describe(string? value)
        => value is null ? "unset" : string.IsNullOrWhiteSpace(value) ? "blank" : "set to an unexpected value";
}
