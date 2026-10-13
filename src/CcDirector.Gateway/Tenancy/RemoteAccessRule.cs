using System.Globalization;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// THE PRE-LAUNCH GRACE (Pricing Change mission, B4): an account that reached the hosted Gateway before the new plans
/// launched keeps the Cockpit and the phone app until a cut-off the owner sets. Two instants, BOTH ABSENT UNLESS
/// STATED, and no date is written in code:
///
///  - <see cref="LaunchUtc"/>: an account is "created before launch" when its tenant row was minted before this
///    instant. ABSENT MEANS EVERY ACCOUNT COUNTS AS CREATED BEFORE LAUNCH, so a deploy with nothing set takes nothing
///    from anyone.
///  - <see cref="CutOffUtc"/>: the grace ends at this instant. ABSENT MEANS IT DOES NOT END. The owner sets it when he
///    approves the notice email to today's accounts.
///
/// The grace grants remote access and nothing else. It never grants an artificial-intelligence scope and it does not
/// change the tier an account is on.
///
/// HOW THE HOSTED GATEWAY IS GIVEN THEM: two environment variables, stated as ENV lines in the repository's Dockerfile
/// so they are part of the hosted image (the deploy writes no application setting of its own), and read once when the
/// host is built. Only a hosted Gateway reads them. Each is one instant in
/// Coordinated Universal Time written as <c>yyyy-MM-ddTHH:mm:ssZ</c>. A value that is set and cannot be read in that
/// form stops the Gateway from starting: reading a mistyped cut-off as "no cut-off" would leave the grace on for good
/// with nobody told.
/// </summary>
/// <param name="LaunchUtc">The launch instant, or null when none is set.</param>
/// <param name="CutOffUtc">The instant the grace ends, or null when none is set.</param>
public sealed record PreLaunchGrace(DateTime? LaunchUtc, DateTime? CutOffUtc)
{
    /// <summary>The environment variable holding the launch instant.</summary>
    public const string LaunchEnvVar = "CC_GATEWAY_PRICING_LAUNCH_UTC";

    /// <summary>The environment variable holding the instant the grace ends.</summary>
    public const string CutOffEnvVar = "CC_GATEWAY_REMOTE_ACCESS_GRACE_CUTOFF_UTC";

    /// <summary>The one form either instant is written in.</summary>
    public const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>Neither instant set: every account is in grace and the grace does not end.</summary>
    public static PreLaunchGrace NotSet { get; } = new(null, null);

    /// <summary>The grace this process's environment states.</summary>
    public static PreLaunchGrace FromEnvironment()
    {
        var grace = Parse(Environment.GetEnvironmentVariable(LaunchEnvVar), Environment.GetEnvironmentVariable(CutOffEnvVar));
        FileLog.Write($"[PreLaunchGrace] FromEnvironment: {LaunchEnvVar}={Describe(grace.LaunchUtc)} {CutOffEnvVar}={Describe(grace.CutOffUtc)}");
        return grace;
    }

    /// <summary>The grace two setting values state. Pure, so it is tested directly. A blank value is "not set".</summary>
    public static PreLaunchGrace Parse(string? launch, string? cutOff)
        => new(ParseInstant(LaunchEnvVar, launch), ParseInstant(CutOffEnvVar, cutOff));

    /// <summary>
    /// Is an account whose tenant row was minted at <paramref name="tenantCreatedAtUtc"/> in grace at
    /// <paramref name="nowUtc"/>?
    /// </summary>
    /// <param name="tenantCreatedAtUtc">When the account's tenant row was minted, or null for an account that has
    /// never reached the hosted Gateway. With a launch instant set, such an account is not in grace: it never had the
    /// Cockpit to keep.</param>
    /// <param name="nowUtc">The moment to judge against.</param>
    public bool Covers(DateTime? tenantCreatedAtUtc, DateTime nowUtc)
    {
        if (CutOffUtc is { } cutOff && nowUtc >= cutOff)
            return false;
        if (LaunchUtc is not { } launch)
            return true;
        return tenantCreatedAtUtc is { } created && created < launch;
    }

    private static DateTime? ParseInstant(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (DateTime.TryParseExact(value.Trim(), InstantFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
            return instant;
        throw new InvalidOperationException(
            $"The setting {name} is set to '{value}', which is not an instant written as yyyy-MM-ddTHH:mm:ssZ " +
            "(Coordinated Universal Time). Correct the value, or remove the setting to leave it unset. The Gateway " +
            "will not start on a value it cannot read, because guessing would change who keeps the Cockpit and the phone app.");
    }

    private static string Describe(DateTime? instant)
        => instant is { } value ? value.ToString(InstantFormat, CultureInfo.InvariantCulture) : "not set";
}

/// <summary>What <see cref="RemoteAccessRule.Decide"/> concluded. Three answers, never folded into two.</summary>
public enum RemoteAccessAnswer
{
    /// <summary>The read succeeded and the account has remote access.</summary>
    Granted,

    /// <summary>The read succeeded and the account has none: a refusal of the Cockpit and the phone app, and only of
    /// them. Its Director is untouched.</summary>
    NotGranted,

    /// <summary>What would have decided it could not be read. Never a refusal and never a grant: the caller answers
    /// "ask again".</summary>
    Unknown,
}

/// <summary>
/// THE ONE ANSWER TO "MAY THIS ACCOUNT USE THE COCKPIT AND THE PHONE APP" (Pricing Change mission, B2 and B4). Asked
/// in two places - where a Cockpit or phone key is minted (<see cref="Api.HostedEnrollmentEndpoint"/>) and when the
/// access lease is read for the request path (<see cref="HostedAccessLeaseService"/>) - and both ask this one
/// function, so the two can never disagree about a plan, about the grace or about a team.
///
/// THREE THINGS GRANT IT, asked in this order, and nothing else does:
///  1. the account's own plan (<see cref="EntitlementScopes.GrantsRemoteAccess"/>);
///  2. the pre-launch grace (<see cref="PreLaunchGrace.Covers"/>);
///  3. where Teams is released, being a member of a team whose bill is running
///     (<see cref="Teams.TeamRemoteAccess"/>), because a person's Cockpit and phone key is their own account's.
/// The team read is made only when neither of the first two grants, so an account with a plan costs no extra read.
///
/// A hosted Gateway only. A self-hosted Gateway has no entitlement registry, never builds this rule and is not gated
/// (decision D5).
/// </summary>
public sealed class RemoteAccessRule
{
    private readonly Teams.TeamRemoteAccess? _teams;

    /// <param name="grace">The pre-launch grace.</param>
    /// <param name="teams">The team half, or null where Teams is not released: there are then no teams, and the rule
    /// has no third answer to give.</param>
    public RemoteAccessRule(PreLaunchGrace grace, Teams.TeamRemoteAccess? teams = null)
    {
        Grace = grace ?? throw new ArgumentNullException(nameof(grace));
        _teams = teams;
    }

    /// <summary>The pre-launch grace this rule applies.</summary>
    public PreLaunchGrace Grace { get; }

    /// <summary>
    /// Does an ENTITLED account on <paramref name="tier"/> have remote access at <paramref name="nowUtc"/>?
    /// </summary>
    /// <param name="tier">The tier of an Entitled decision, from the same read the caller is acting on.</param>
    /// <param name="accountSubject">The verified account subject, asked of the team half. Never logged.</param>
    /// <param name="tenantCreatedAtUtc">When the account's tenant row was minted, or null when it has none.</param>
    /// <param name="nowUtc">The moment to judge against.</param>
    public RemoteAccessAnswer Decide(string? tier, string accountSubject, DateTime? tenantCreatedAtUtc, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("An account subject is required.", nameof(accountSubject));

        var tierForLog = string.IsNullOrWhiteSpace(tier) ? "<none>" : tier;
        if (EntitlementScopes.GrantsRemoteAccess(tier))
        {
            FileLog.Write($"[RemoteAccessRule] Decide: tier={tierForLog} -> granted by the plan");
            return RemoteAccessAnswer.Granted;
        }
        if (Grace.Covers(tenantCreatedAtUtc, nowUtc))
        {
            FileLog.Write($"[RemoteAccessRule] Decide: tier={tierForLog} -> granted by the pre-launch grace");
            return RemoteAccessAnswer.Granted;
        }
        if (_teams is null)
        {
            FileLog.Write($"[RemoteAccessRule] Decide: tier={tierForLog} -> not granted (no plan, no grace; Teams is not released)");
            return RemoteAccessAnswer.NotGranted;
        }

        var byTeam = _teams.MemberOfATeamWithARunningBill(accountSubject, nowUtc);
        var answer = byTeam switch
        {
            true => RemoteAccessAnswer.Granted,
            false => RemoteAccessAnswer.NotGranted,
            null => RemoteAccessAnswer.Unknown,
        };
        FileLog.Write($"[RemoteAccessRule] Decide: tier={tierForLog} -> {answer} by the team read (no plan, no grace)");
        return answer;
    }

    /// <summary>
    /// Is a device key of this type a Cockpit's or a phone's? True exactly when
    /// <see cref="SessionOriginSurfaces.FromDeviceType"/> does not answer unknown - the types <c>browser</c> and
    /// <c>phone</c>, which the Gateway itself writes on a key at the Cockpit and phone door. Every other caller - a
    /// Director's key, a session key, a service token - is not.
    /// </summary>
    public static bool IsCockpitOrPhoneDevice(string? deviceType)
        => SessionOriginSurfaces.FromDeviceType(deviceType) != SessionOriginSurfaces.Unknown;
}

/// <summary>
/// The refusal a Cockpit or phone meets when its account has no remote access (Pricing Change mission, B2). ONE body,
/// answered alike where a key is minted and on a request, and the clients render it and decide nothing: HTTP 402 with
/// <c>{ error, code, message, subscribeUrl }</c>. The second Developer's phone screen reads <c>code</c>
/// (<see cref="EntitlementRegistry.RemotePlanRequiredCode"/>), so the field names and the code do not change without
/// that screen changing with them.
/// </summary>
public static class RemoteAccessRefusal
{
    /// <summary>The body, as the object every writer serialises.</summary>
    public static object Body { get; } = new
    {
        error = EntitlementRegistry.RemotePlanRequiredError,
        code = EntitlementRegistry.RemotePlanRequiredCode,
        message = EntitlementRegistry.RemotePlanRequiredMessage,
        subscribeUrl = EntitlementRegistry.SubscribeUrl,
    };

    /// <summary>The refusal as a route's result.</summary>
    public static IResult ToResult() => Results.Json(Body, statusCode: StatusCodes.Status402PaymentRequired);

    /// <summary>Write the refusal as a middleware's answer.</summary>
    public static Task WriteAsync(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ctx.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        return ctx.Response.WriteAsJsonAsync(Body);
    }
}
