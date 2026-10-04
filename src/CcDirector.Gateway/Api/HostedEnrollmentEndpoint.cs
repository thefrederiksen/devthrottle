using System;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Teams;
using CcDirector.Core.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Hosted device enrollment (Hosted Multi-Tenancy increment 1): a REMOTE Director enrolls with the HOSTED
/// Gateway by presenting its OWN verified DevThrottle (Supabase) account token, and receives a per-device
/// key bound to that account's tenant. This is the hosted counterpart to the loopback-only, single-owner
/// <see cref="SignedInEnrollmentEndpoint"/>: on the hosted Gateway there is NO single signed-in Gateway
/// account, so the caller's OWN account token is the authorization, and each distinct account (subject) maps
/// to its own tenant.
///
///   POST /devices/enroll-hosted
///     Authorization: Bearer &lt;the caller's Supabase access token&gt;
///     { deviceId, machineName, platform, deviceType }
///   -&gt; 200 { deviceKey, ... }   issue this device's key (a fresh one if it is already enrolled), bound to the tenant
///   -&gt; 400                       deviceId missing
///   -&gt; 401                       missing / invalid / expired account token (no verified subject to bind)
///
/// TEAMS (devthrottle_internal#2311), only where Teams is released (<see cref="Teams.TeamsReleaseSwitch"/>):
///   GET  /devices/enroll-hosted/teams   the teams this person may set a Director up for
///   POST /devices/enroll-hosted         with { teamId }: the key is bound to that team's tenant, for this person
///   POST /devices/enroll-hosted/move    { deviceKey, teamId }: move one Director to another team or back home
/// The whole contract, every refusal included, is docs/proof/teams-2311/gateway-contract.md.
///
/// The account token is validated (signature + expiry + audience + issuer) and its stable subject extracted;
/// the subject maps (mint-or-lookup) to a tenant; the device is registered and bound to (subject, tenant), so
/// the tunnel can later resolve the tenant from the SAME per-device key. The route is in the AuthMiddleware
/// public set because it carries its OWN authorization (the account token) - a fresh remote Director has no
/// Gateway device key yet. Security: the subject and email are personally identifying, so NEITHER is logged.
/// </summary>
internal static class HostedEnrollmentEndpoint
{
    public const string Path = "/devices/enroll-hosted";

    /// <summary>The outcome of <see cref="Enroll"/>, extracted so the enrollment logic is unit-tested without a
    /// web host. <see cref="Response"/> is set only on <see cref="Status"/> 200.</summary>
    public sealed record EnrollResult(int Status, DeviceRegistrationResponse? Response, string Error);

    /// <param name="entitlements">
    /// The paid-entitlement gate. NULL means no gate - that is the self-host case, where there is no billing
    /// and no boundary, and enrollment behaves exactly as it always has. On hosted this is REQUIRED, and the
    /// gate is what makes hosted a paid product rather than a free one.
    /// </param>
    /// <param name="trials">
    /// The free-trial ledger (issue #2117). NULL means no trial concept - the behaviour before trials existed.
    /// When supplied, an account with no paid entitlement that this Gateway has never seen before is GRANTED
    /// the 14-day Pro trial here, at its first arrival, and enrolls on it.
    /// </param>
    /// <param name="teams">
    /// The Teams half (devthrottle_internal#2311), or null where Teams is not released. Null: the two team routes are
    /// not mapped (a request to either is answered as for any route that does not exist) and an enrollment naming a
    /// team is refused - so the dark Gateway's enrollment is exactly what it was.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, DeviceRegistry devices,
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator,
        Tenancy.EntitlementRegistry? entitlements = null, Tenancy.TrialRegistry? trials = null,
        TeamEnrollment? teams = null)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));
        if (tenants is null) throw new ArgumentNullException(nameof(tenants));
        if (accountTokenValidator is null) throw new ArgumentNullException(nameof(accountTokenValidator));

        if (teams is not null)
        {
            app.MapGet(TeamsPath, (HttpContext ctx) =>
            {
                var result = ListTeams(BearerToken.Read(ctx), accountTokenValidator, teams.Teams, teams.Access);
                return result.Status == StatusCodes.Status200OK
                    ? Results.Json(result.Response, statusCode: StatusCodes.Status200OK)
                    : Results.Json(new { error = result.Error }, statusCode: result.Status);
            });
            app.MapPost(MovePath, (MoveDirectorRequest req, HttpContext ctx) =>
                Answer(Move(BearerToken.Read(ctx), req, devices, tenants, accountTokenValidator, teams, entitlements, DateTime.UtcNow, trials)));
        }

        app.MapPost(Path, (EnrollSignedInRequest req, HttpContext ctx) =>
            Answer(Enroll(BearerToken.Read(ctx), req, devices, tenants, accountTokenValidator, entitlements, DateTime.UtcNow, trials,
                teams?.Access)));
    }

    /// <summary>The HTTP answer for an enrollment or a move: the key on 200, the payment sentence on 402, and the
    /// plain error shape otherwise.</summary>
    private static IResult Answer(EnrollResult result)
    {
        if (result.Status == StatusCodes.Status200OK)
            return Results.Json(result.Response, statusCode: StatusCodes.Status200OK);

        // A payment refusal has to say what to do about it, not just that it happened (issue #2117): the
        // member is told, in one finished sentence the Gateway owns, that access has ended and where to
        // subscribe. Every other status keeps its plain error shape.
        if (result.Status == StatusCodes.Status402PaymentRequired)
        {
            return Results.Json(new
            {
                error = result.Error,
                message = Tenancy.EntitlementRegistry.SubscribeMessage,
                subscribeUrl = Tenancy.EntitlementRegistry.SubscribeUrl,
            }, statusCode: result.Status);
        }

        return Results.Json(new { error = result.Error }, statusCode: result.Status);
    }

    /// <summary>
    /// The enrollment decision as a pure function (Hosted Multi-Tenancy increment 1), so the security-relevant
    /// steps - validate the account token, extract the subject, map it to a tenant, bind the device - are
    /// unit-tested without a web host. Validates the account token fully (signature + expiry + audience +
    /// issuer); a token that is not authorization-valid or carries no subject is a 401 (no verified account to
    /// bind). The email is display metadata only, never the mapping key. Nothing personally identifying is
    /// logged.
    /// </summary>
    /// <param name="teamAccess">The team permission check, or null on a Gateway where Teams is not released - and
    /// then a request naming a team is refused (devthrottle_internal#2311).</param>
    public static EnrollResult Enroll(string? bearer, EnrollSignedInRequest? req, DeviceRegistry devices,
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator,
        Tenancy.EntitlementRegistry? entitlements = null, DateTime? nowUtc = null,
        Tenancy.TrialRegistry? trials = null, TeamAccess? teamAccess = null)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.DeviceId))
            return new EnrollResult(StatusCodes.Status400BadRequest, null, "deviceId is required");

        if (bearer is null)
            return new EnrollResult(StatusCodes.Status401Unauthorized, null, "an account access token is required");

        var validation = accountTokenValidator.ValidateForAuthorization(bearer);
        if (!validation.IsValid || string.IsNullOrEmpty(validation.Subject))
        {
            FileLog.Write("[HostedEnrollment] REJECTED: the account token is not authorization-valid (no subject to bind)");
            return new EnrollResult(StatusCodes.Status401Unauthorized, null, "the account token is not valid");
        }

        var teamId = string.IsNullOrWhiteSpace(req.TeamId) ? null : req.TeamId.Trim();
        if (teamId is not null)
            return EnrollIntoTeam(validation.Subject, teamId, req, devices, teamAccess);

        var refusal = PersonalAccountGate(validation.Subject, tenants, entitlements, nowUtc, trials);
        if (refusal is not null)
            return refusal;

        // The email is DISPLAY METADATA only (never the mapping key). Read it from the same verified token.
        var email = JwtIdentityReader.Read(bearer)?.Email;

        // Mint-or-lookup the tenant for this verified subject (same account -> same tenant).
        var tenant = tenants.MintOrLookupBySubject(validation.Subject, email);

        // The device id is CLIENT-supplied, so it must NEVER be the registry key on its own: two different
        // accounts presenting the same deviceId would otherwise collide on ONE registry entry, and enrolling
        // would hand one account a working key on the OTHER's record (which SetAccountBinding then rebinds to
        // the new tenant) - letting a pre-enroller take over the victim's device entry. Namespacing the registry
        // id with a ONE-WAY HASH of the resolved tenant makes cross-account collision impossible (different
        // accounts -> different tenants -> different hashes -> different device spaces) while staying
        // idempotent for one account (same tenant -> same hash). The hash - not the subject and not the raw
        // tenant id, both of which must never be logged - is what the device registry logs as the device id.
        var scopedDeviceId = NamespaceHash(tenant.Value) + "|" + req.DeviceId;
        var response = devices.RegisterForTenant(
            tenant,
            validation.Subject,
            scopedDeviceId,
            req.MachineName,
            req.Platform,
            req.DeviceType);

        // Where Teams is released a Director may have been set up for a team before; set up again for the person's
        // own account, its team key goes (devthrottle_internal#2311). A Director holds one working key, in one place.
        // Dark, no other key of this Director can be live, and nothing is asked.
        if (teamAccess is not null)
            devices.RevokeOtherKeysOfDirector(validation.Subject, req.DeviceId, scopedDeviceId, SetUpAgainReason);

        // RegisterForTenant counts inside the same tenant-bound transaction, so the response never exposes
        // the hosted fleet-wide count.

        FileLog.Write($"[HostedEnrollment] enrolled deviceId={req.DeviceId}, machine={req.MachineName} " +
                      $"-> bound to its account tenant (no subject/email logged), deviceCount={response.DeviceCount}");
        return new EnrollResult(StatusCodes.Status200OK, response, "");
    }

    /// <summary>A one-way SHA-256 hex hash used to namespace the device registry id per tenant WITHOUT ever
    /// putting the subject or the raw tenant id into a value the device registry logs.</summary>
    private static string NamespaceHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// THE PERSONAL ACCOUNT'S PAID GATE: whether this account may hold a hosted tenant of its own today - granting the
    /// free trial on a first arrival. Null means it may; otherwise the refusal to return. Asked by enrollment for the
    /// person's own account and by a move back to it (devthrottle_internal#2311), so both doors are one door. NEVER
    /// asked for a team: a team has no trial, and a member is never refused for the team's bill here (the team's bill
    /// is the team's, read by the request-path lease).
    /// </summary>
    private static EnrollResult? PersonalAccountGate(string subject, Tenancy.TenantRegistry tenants,
        Tenancy.EntitlementRegistry? entitlements, DateTime? nowUtc, Tenancy.TrialRegistry? trials)
    {
        // THE PAID GATE, between subject-verification and tenant-mint. It sits here, and not later, because
        // minting a tenant is itself giving something away: an unpaid account must leave no trace and hold no
        // key. No device key means no tunnel, no cockpit and no mobile, which is the whole meaning of
        // "approval before you can use a hosted tenant".
        //
        // THREE OUTCOMES, and keeping them apart is the entire correctness of this gate:
        //  - Entitled     -> fall through and enroll.
        //  - NotEntitled  -> 402. We LOOKED and the account has no valid entitlement. This is knowledge.
        //  - Unknown      -> 503 and RETRY. The read FAILED, so we do not know. It must not deny and it must
        //                    not grant.
        //
        // The asymmetry matters in both directions and they are not equally bad. A false 402 locks out a
        // customer who has paid. A false MINT gives the product away for nothing - and that one is silent,
        // because a successful enrollment looks exactly like a correct one. So the mint happens ONLY on a
        // confirmed entitlement: ignorance mints nothing, denies nothing, and says try again.
        //
        // Null entitlements is the SELF-HOST case - no gate at all, unchanged behaviour. That is the control.
        if (entitlements is not null)
        {
            var now = nowUtc ?? DateTime.UtcNow;

            // Evaluate rather than LookupBySubject, because the PLAN gate below needs the tier from the SAME
            // read the outcome came from - one read, one row, so the two questions can never disagree. Evaluate
            // already folds a RUNNING trial (returning the Pro tier and the trial's end instant); the block
            // below is the separate act of CREATING one on a first arrival.
            var decision = entitlements.Evaluate(subject, now);
            var outcome = decision.Outcome;
            var tier = decision.Tier;

            // THE FREE TRIAL GRANT (issue #2117), and this is the ONLY place a trial is created. The public
            // pricing page promises every new account 14 days of Pro with no card, so an account that the
            // entitlement read just refused gets one more question asked of it: is this its FIRST arrival at
            // the hosted Gateway? If it is, the trial is granted here and the account enrolls on it.
            //
            // It sits AFTER the paid read, so a paying account never reaches it and can never be handed a
            // trial it does not need. It reads NotEntitled only - an Unknown falls through to the 503 below
            // untouched, because granting a trial on a failed paid read would hand a free window to an account
            // whose subscription we simply could not see.
            //
            // "Never seen before" is the tenant mapping: an account that already holds a tenant was using
            // hosted before the trial existed, and the owner's rollout rule grants it nothing (issue #2117).
            // TrialRegistry re-checks its own ledger, so an account whose trial has already ended is refused
            // here rather than being handed a second one.
            // "NOT PAYING" IS THE CONDITION, AND IT IS NOT THE SAME AS "NOT ENTITLED" ANY MORE. Since the free
            // plan arrived (2026-09-02) an account with no paid row and no running trial comes back ENTITLED, on
            // tier free - so a test for NotEntitled alone would silently stop handing out trials, and every new
            // member would be enrolled straight onto free and quietly lose the fourteen days they are owed.
            // Nothing would look broken: enrolment would simply succeed. The question this gate has always been
            // asking is "is this account PAYING", so it now asks exactly that.
            //
            // Unknown is still excluded, for the reason stated above: a trial granted on a failed read hands a
            // free window to an account whose subscription we could not see.
            var notPaying = outcome == Tenancy.EntitlementOutcome.NotEntitled
                            || (outcome == Tenancy.EntitlementOutcome.Entitled
                                && Tenancy.EntitlementRegistry.IsFree(tier));

            if (notPaying && trials is not null)
            {
                var alreadyKnown = tenants.LookupBySubject(subject) is not null;
                var trial = trials.GrantIfFirstArrival(subject, alreadyKnown, now);

                if (trial.Outcome == Tenancy.TrialOutcome.Active)
                {
                    FileLog.Write("[HostedEnrollment] enrolling on the free Pro trial (no paid entitlement; the account is inside its trial window)");
                    outcome = Tenancy.EntitlementOutcome.Entitled;

                    // The trial grants the PRO plan - the same tier Evaluate returns for a running trial on
                    // every later read - so the plan gate below sees the trial for what it is rather than
                    // judging it on the absent paid row's null tier. Stated explicitly here so a freshly
                    // granted trial and an already-running one are ruled on identically.
                    tier = Tenancy.EntitlementRegistry.TierPro;
                }
                else if (trial.Outcome == Tenancy.TrialOutcome.Unknown)
                {
                    // The trial ledger could not be read or written. We do not know what covers this account,
                    // so this is the SAME temporary condition as an unreadable entitlement table: retry, never
                    // refuse, never grant.
                    outcome = Tenancy.EntitlementOutcome.Unknown;
                }
            }

            if (outcome == Tenancy.EntitlementOutcome.Unknown)
            {
                FileLog.Write("[HostedEnrollment] RETRY: the entitlement read failed, so entitlement is UNKNOWN - " +
                              "not enrolling and NOT refusing (no tenant minted, no device key issued)");
                return new EnrollResult(StatusCodes.Status503ServiceUnavailable, null,
                    "the entitlement service is temporarily unavailable; please try again");
            }

            // Reachable only where the free plan is not in play - a Gateway wired with no trial ledger at all,
            // which is the self-host control. On the hosted service a successful pair of reads now yields free
            // rather than a refusal, which is the whole point of the change: the door below is the plan gate,
            // not a paywall.
            if (outcome == Tenancy.EntitlementOutcome.NotEntitled)
            {
                FileLog.Write("[HostedEnrollment] REFUSED: the entitlement read succeeded and this account has no active entitlement (no tenant minted, no device key issued)");
                return new EnrollResult(StatusCodes.Status402PaymentRequired, null,
                    "this account does not have an active hosted subscription");
            }

            // THE PLAN GATE, and it is a SECOND question the first one cannot answer. Above we established
            // that this account pays. Here we establish that what it pays for INCLUDES hosted capacity - which
            // a self-host plan deliberately does not. Both refusals are a 402, but for different reasons: the
            // first is "you have not paid", this one is "your plan does not include this".
            //
            // Without this, every paid plan would provision hosted capacity, because the entitlement read is
            // tier-agnostic on purpose and passes the plan through unexamined. A self-host subscriber would
            // then obtain a tenant and a device key - the tunnel, the cockpit, the mobile application - on a
            // plan priced to exclude exactly that. Nothing would look wrong: enrolment would simply succeed.
            //
            // The verdict is not computed here. It is read from the ONE table that owns what a plan grants
            // (EntitlementScopes), so adding a plan is one edit there and never a new branch at a call site.
            if (!Tenancy.EntitlementScopes.GrantsHostedGateway(tier))
            {
                FileLog.Write("[HostedEnrollment] REFUSED: the account pays, but its plan does not include hosted " +
                              "gateway capacity (no tenant minted, no device key issued)");
                return new EnrollResult(StatusCodes.Status402PaymentRequired, null,
                    "this plan does not include the hosted gateway");
            }
        }

        return null;
    }

    // ---- Teams (devthrottle_internal#2311): a Director set up for a team -------------------------------------------

    /// <summary>The route that lists the teams a person may set a Director up for. Mapped only when Teams is released.</summary>
    public const string TeamsPath = Path + "/teams";

    /// <summary>The route that moves an enrolled Director to another team or back to the person's own account. Mapped
    /// only when Teams is released.</summary>
    public const string MovePath = Path + "/move";

    /// <summary>The revocation reason on the key a Director held before it moved.</summary>
    public const string MovedReason = "director_moved_to_another_team";

    /// <summary>The revocation reason on a Director's other keys when it is set up again somewhere else.</summary>
    public const string SetUpAgainReason = "director_set_up_again";

    /// <summary>What a request naming a team is told on a Gateway where Teams is not released.</summary>
    public const string TeamsNotReleasedRefusal =
        "This DevThrottle service does not offer teams yet, so a Director cannot be set up for one. Leave the team out to set it up for your own account.";

    /// <summary>The first sentence of every refusal to set a Director up for a team, or move one into it. The role
    /// table's own sentence follows it.</summary>
    public const string CannotRunSessionsInTeamLead = "You cannot run sessions in that team, so a Director cannot be set up for it.";

    /// <summary>What a move is told while the Director still has sessions on the Gateway.</summary>
    public const string MoveWithSessionsRefusal =
        "This Director still has sessions open. Close every session on it, then change its team. Nothing was changed.";

    /// <summary>What a move to the team (or account) the Director is already set up for is told.</summary>
    public const string MoveToSameTeamRefusal = "This Director is already set up for that team. Nothing was changed.";

    /// <summary>What a move naming a Director this account has no working key for is told.</summary>
    public const string MoveNoSuchDirectorRefusal =
        "This account has no Director set up with that id, or its key is no longer active, so it cannot be moved. Set the Director up again.";

    /// <summary>What a move naming a Director set up by another person is told.</summary>
    public const string MoveSomeoneElsesKeyRefusal =
        "That Director was set up by a different account, so it cannot be moved from yours. Sign in with the account that set it up.";

    /// <summary>What a move is told when one Director id has more than one working key for this person - a state
    /// enrollment no longer leaves behind.</summary>
    public const string MoveAmbiguousRefusal =
        "This Director is set up in more than one place, so DevThrottle cannot tell which one to move. Set the Director up again.";

    /// <summary>The Teams half of the enrollment routes: present only on a Gateway where Teams is released.</summary>
    /// <param name="RegisteredSessions">How many sessions a Director has registered on the Gateway, by tenant and
    /// Director id. A move is refused while it is above nought.</param>
    /// <param name="Connections">The live Director tunnels, so a moved Director's old tunnel is cut.</param>
    public sealed record TeamEnrollment(
        TeamRegistry Teams,
        TeamAccess Access,
        Func<TenantId, string, int> RegisteredSessions,
        Streaming.DirectorConnectionRegistry Connections);

    /// <summary>The outcome of <see cref="ListTeams"/>. <see cref="Response"/> is set only on 200.</summary>
    public sealed record TeamsResult(int Status, EnrollHostedTeamsResponse? Response, string Error);

    /// <summary>
    /// Set a Director up for a TEAM: the key is bound to the team's tenant, for this person. Refused unless Teams is
    /// released and the role table lets this person run sessions on their own computers in that team - not a member
    /// and a Collaborator are both refused, with one answer whether or not the team exists. The personal trial and
    /// paid gate are never reached: a team has no trial, and the team's bill is not read here.
    /// </summary>
    private static EnrollResult EnrollIntoTeam(string subject, string teamId, EnrollSignedInRequest req,
        DeviceRegistry devices, TeamAccess? teamAccess)
    {
        var refusal = TeamRefusal(subject, teamId, teamAccess);
        if (refusal is not null)
            return refusal;

        var team = new TenantId(teamId);
        var scopedDeviceId = TeamScopedDeviceId(teamId, subject, req.DeviceId);
        var response = devices.RegisterForTenant(team, subject, scopedDeviceId, req.MachineName, req.Platform, req.DeviceType);
        devices.RevokeOtherKeysOfDirector(subject, req.DeviceId, scopedDeviceId, SetUpAgainReason);
        FileLog.Write($"[HostedEnrollment] enrolled deviceId={req.DeviceId}, machine={req.MachineName} -> bound to team " +
                      $"{team.ToLogString()} for its member (no subject/email logged), deviceCount={response.DeviceCount}");
        return new EnrollResult(StatusCodes.Status200OK, response, "");
    }

    /// <summary>
    /// The one permission question for putting a Director in a team - enrollment and a move ask it alike: null when
    /// the person may run sessions in the team, otherwise the refusal.
    /// </summary>
    private static EnrollResult? TeamRefusal(string subject, string teamId, TeamAccess? teamAccess)
    {
        if (teamAccess is null)
        {
            FileLog.Write("[HostedEnrollment] REFUSED: a team was named, but Teams is not released on this Gateway");
            return new EnrollResult(StatusCodes.Status400BadRequest, null, TeamsNotReleasedRefusal);
        }

        var decision = teamAccess.Decide(teamId, subject, TeamAction.RunSessionsOnOwnComputers);
        if (decision.Allowed)
            return null;

        FileLog.Write($"[HostedEnrollment] REFUSED: team {new TenantId(teamId).ToLogString()} - the person may not run sessions there (member={decision.IsMember}, role={decision.Role?.ToString() ?? "<none>"})");
        return new EnrollResult(StatusCodes.Status403Forbidden, null, CannotRunSessionsInTeamLead + " " + decision.Refusal);
    }

    /// <summary>
    /// A team-bound device row's id. Namespaced by the team AND the person, one way, so two members presenting the same
    /// device id land on two rows - a member can never take over another member's row by enrolling its id first - and
    /// one person's Director on two teams is two rows. A personal key keeps its own namespace, unchanged.
    /// </summary>
    internal static string TeamScopedDeviceId(string teamId, string subject, string deviceId) =>
        NamespaceHash("team" + "\n" + teamId + "\n" + subject) + "|" + deviceId;

    /// <summary>
    /// The teams the signed-in person may set a Director up for: every team where the role table lets them run
    /// sessions on their own computers. Never a Collaborator's team; never the personal account. Empty for a person in
    /// no such team. Same bearer as enrollment, because a Director being set up has no device key yet.
    /// </summary>
    public static TeamsResult ListTeams(string? bearer, JwtAccessTokenValidator accountTokenValidator, TeamRegistry teams, TeamAccess access)
    {
        ArgumentNullException.ThrowIfNull(accountTokenValidator);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(access);

        if (bearer is null)
            return new TeamsResult(StatusCodes.Status401Unauthorized, null, "an account access token is required");
        var validation = accountTokenValidator.ValidateForAuthorization(bearer);
        if (!validation.IsValid || string.IsNullOrEmpty(validation.Subject))
        {
            FileLog.Write("[HostedEnrollment] ListTeams REJECTED: the account token is not authorization-valid");
            return new TeamsResult(StatusCodes.Status401Unauthorized, null, "the account token is not valid");
        }

        var offered = teams.ListTeamsFor(validation.Subject)
            .Where(t => access.Decide(t.TeamId, validation.Subject, TeamAction.RunSessionsOnOwnComputers).Allowed)
            .Select(t => new EnrollHostedTeam
            {
                TeamId = t.TeamId,
                Name = t.Name,
                Role = TeamRoles.ToStored(t.Role),
                MemberCount = t.MemberCount,
            })
            .ToList();
        FileLog.Write($"[HostedEnrollment] ListTeams: offering {offered.Count} team(s)");
        return new TeamsResult(StatusCodes.Status200OK, new EnrollHostedTeamsResponse { Teams = offered }, "");
    }

    /// <summary>
    /// MOVE ONE ENROLLED DIRECTOR to another team the person may run sessions in, or back to their own account. The
    /// Director is named by its own id (the device id it was set up with); the person by their account token, and the
    /// Director's working key must have been issued to that same account. Refused while the Director has any session
    /// registered on the Gateway. On success the old key is revoked FIRST - so it never works again, and a failure
    /// after that point leaves the Director with no working key (it is set up again), never with two - its old tunnel
    /// is cut, and a new key bound to the new team is returned. The permission check is enrollment's:
    /// <see cref="TeamRefusal"/> for a team, <see cref="PersonalAccountGate"/> for the person's own account.
    /// </summary>
    public static EnrollResult Move(string? bearer, MoveDirectorRequest? req, DeviceRegistry devices,
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator, TeamEnrollment teams,
        Tenancy.EntitlementRegistry? entitlements = null, DateTime? nowUtc = null, Tenancy.TrialRegistry? trials = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(accountTokenValidator);
        ArgumentNullException.ThrowIfNull(teams);

        if (req is null || string.IsNullOrWhiteSpace(req.DeviceId))
            return new EnrollResult(StatusCodes.Status400BadRequest, null, "deviceId is required");
        if (bearer is null)
            return new EnrollResult(StatusCodes.Status401Unauthorized, null, "an account access token is required");
        var validation = accountTokenValidator.ValidateForAuthorization(bearer);
        if (!validation.IsValid || string.IsNullOrEmpty(validation.Subject))
        {
            FileLog.Write("[HostedEnrollment] Move REJECTED: the account token is not authorization-valid");
            return new EnrollResult(StatusCodes.Status401Unauthorized, null, "the account token is not valid");
        }
        var subject = validation.Subject;
        var directorId = req.DeviceId.Trim();

        // The Director's working key, by its own id. Only this person's counts; a working key of the same id issued to
        // someone else is the one case that says "not yours" rather than "no such Director".
        var keys = devices.ActiveKeysOfDirector(directorId);
        var mine = keys.Where(k => string.Equals(k.AccountSubject, subject, StringComparison.Ordinal)).ToList();
        if (mine.Count == 0)
        {
            var someoneElses = keys.Count > 0;
            FileLog.Write($"[HostedEnrollment] Move REFUSED: director={directorId} has no working key of this account (another account's: {someoneElses})");
            return someoneElses
                ? new EnrollResult(StatusCodes.Status403Forbidden, null, MoveSomeoneElsesKeyRefusal)
                : new EnrollResult(StatusCodes.Status404NotFound, null, MoveNoSuchDirectorRefusal);
        }
        if (mine.Count > 1 || mine[0].TenantId is null)
        {
            FileLog.Write($"[HostedEnrollment] Move REFUSED: director={directorId} has {mine.Count} working keys of this account");
            return new EnrollResult(StatusCodes.Status409Conflict, null, MoveAmbiguousRefusal);
        }
        var current = mine[0];
        var from = new TenantId(current.TenantId!);

        // Where to: the team named, after the same question enrollment asks, or the person's own account, after its
        // paid gate.
        var teamId = string.IsNullOrWhiteSpace(req.TeamId) ? null : req.TeamId.Trim();
        TenantId to;
        string newDeviceId;
        if (teamId is not null)
        {
            var refusal = TeamRefusal(subject, teamId, teams.Access);
            if (refusal is not null)
                return refusal;
            to = new TenantId(teamId);
            newDeviceId = TeamScopedDeviceId(teamId, subject, directorId);
        }
        else
        {
            var refusal = PersonalAccountGate(subject, tenants, entitlements, nowUtc, trials);
            if (refusal is not null)
                return refusal;
            to = tenants.MintOrLookupBySubject(subject, JwtIdentityReader.Read(bearer)?.Email);
            newDeviceId = NamespaceHash(to.Value) + "|" + directorId;
        }

        if (to == from)
        {
            FileLog.Write($"[HostedEnrollment] Move REFUSED: director={directorId} is already in tenant {to.ToLogString()}");
            return new EnrollResult(StatusCodes.Status409Conflict, null, MoveToSameTeamRefusal);
        }

        var sessions = teams.RegisteredSessions(from, directorId);
        if (sessions > 0)
        {
            FileLog.Write($"[HostedEnrollment] Move REFUSED: director={directorId} has {sessions} session(s) registered on the Gateway");
            return new EnrollResult(StatusCodes.Status409Conflict, null, MoveWithSessionsRefusal);
        }

        var display = devices.DisplayOfDevice(current.DeviceId);
        if (!devices.RevokeDevice(current.DeviceId, MovedReason))
        {
            // Revoked between the lookup above and here, by someone else: there is no longer a working key to move.
            FileLog.Write($"[HostedEnrollment] Move REFUSED: director={directorId} - its key was revoked while the move was being made");
            return new EnrollResult(StatusCodes.Status404NotFound, null, MoveNoSuchDirectorRefusal);
        }
        teams.Connections.AbortForDirector(from, directorId, MovedReason);

        var response = devices.RegisterForTenant(to, subject, newDeviceId,
            display?.MachineName ?? "", display?.Platform, display?.DeviceType);
        FileLog.Write($"[HostedEnrollment] Move: director={directorId} moved from tenant {from.ToLogString()} to {to.ToLogString()} " +
                      "- the old key is revoked and its tunnel cut, a new key issued");
        return new EnrollResult(StatusCodes.Status200OK, response, "");
    }
}
