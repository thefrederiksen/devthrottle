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
///   POST /devices/enroll-hosted/move    { deviceId, teamId }: move one Director, named by its own id, to another
///                                       team or back home; the account token proves the person, and the
///                                       Director's working key must have been issued to that account
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

    /// <summary>
    /// WHICH DOOR <see cref="Enroll"/> WAS CALLED THROUGH (Pricing Change mission, B2). There are two, and the one
    /// mint is told which, because what an account may be handed differs: every plan may set up a Director, and only
    /// an account with remote access (<see cref="Tenancy.RemoteAccessRule"/>) may be handed a Cockpit's or a phone's
    /// key. The door is stated by the Gateway's own endpoint, never read from the request.
    /// </summary>
    public enum EnrollDoor
    {
        /// <summary><c>POST /devices/enroll-hosted</c>: a Director. Refuses a request that asks for the device type
        /// <c>phone</c> or <c>browser</c>, so this door is not a way round the other one.</summary>
        Director,

        /// <summary><c>POST /mobile/enroll</c> (and the old <c>/m/enroll</c>): the Cockpit and the phone app. The
        /// device type is decided by the Gateway from the platform, and the account needs remote access.</summary>
        CockpitOrPhone,
    }

    /// <summary>What the Director door tells a request that asks for a Cockpit's or a phone's key.</summary>
    public const string DirectorDoorDeviceTypeRefusal =
        "This route sets up a Director, so it does not issue a key for a browser or a phone. Sign in through the Cockpit or the phone app instead. Nothing was changed.";

    /// <summary>The machine-readable code beside <see cref="DirectorDoorDeviceTypeRefusal"/>.</summary>
    public const string DirectorDoorDeviceTypeCode = "device_type_not_allowed";

    /// <summary>The machine-readable code on the 503 a mint answers when the plan could not be read - the same code
    /// the request path answers.</summary>
    public const string EntitlementUnknownCode = "entitlement_unknown";

    /// <summary>How many seconds a caller is told to wait before asking again after a plan that could not be read.</summary>
    public const string RetryAfterSeconds = "10";

    /// <summary>The outcome of <see cref="Enroll"/>, extracted so the enrollment logic is unit-tested without a
    /// web host. <see cref="Response"/> is set only on <see cref="Status"/> 200.</summary>
    public sealed record EnrollResult(int Status, DeviceRegistrationResponse? Response, string Error, string? Code = null);

    /// <param name="entitlements">
    /// The paid-entitlement gate. NULL means no gate - that is the self-host case, where there is no billing
    /// and no boundary, and enrollment behaves exactly as it always has. On hosted this is REQUIRED, and the
    /// gate is what makes hosted a paid product rather than a free one.
    /// </param>
    /// <param name="teams">
    /// The Teams half (devthrottle_internal#2311), or null where Teams is not released. Null: the two team routes are
    /// not mapped (a request to either is answered as for any route that does not exist) and an enrollment naming a
    /// team is refused - so the dark Gateway's enrollment is exactly what it was.
    /// </param>
    /// <remarks>
    /// NO TRIAL IS GRANTED HERE ANY MORE (Pricing Change mission, B3). This route used to hand a 14-day Pro trial to
    /// an account arriving for the first time; it no longer does, for anyone. A trial that is already running is
    /// still honoured, because <see cref="Tenancy.EntitlementRegistry.Evaluate"/> reads the ledger itself.
    /// </remarks>
    public static void Map(IEndpointRouteBuilder app, DeviceRegistry devices,
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator,
        Tenancy.EntitlementRegistry? entitlements = null,
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
                Answer(ctx, Move(BearerToken.Read(ctx), req, devices, tenants, accountTokenValidator, teams, entitlements, DateTime.UtcNow)));
        }

        app.MapPost(Path, (EnrollSignedInRequest req, HttpContext ctx) =>
            Answer(ctx, Enroll(BearerToken.Read(ctx), req, devices, tenants, accountTokenValidator, entitlements, DateTime.UtcNow,
                teams, EnrollDoor.Director)));
    }

    /// <summary>The HTTP answer for an enrollment or a move: the key on 200, otherwise <see cref="Refusal"/>.</summary>
    private static IResult Answer(HttpContext ctx, EnrollResult result)
        => result.Status == StatusCodes.Status200OK
            ? Results.Json(result.Response, statusCode: StatusCodes.Status200OK)
            : Refusal(ctx, result);

    /// <summary>
    /// THE ONE HTTP ANSWER FOR A MINT THAT DID NOT ISSUE A KEY, written alike by the Director door and by the Cockpit
    /// and phone door: the remote access refusal, the payment sentence on any other 402, a 503 that says when to ask
    /// again, and the plain error shape otherwise.
    /// </summary>
    internal static IResult Refusal(HttpContext ctx, EnrollResult result)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(result);

        // The Cockpit or the phone app, on an account with no remote access (Pricing Change mission, B2): the same
        // body a request on such an account is answered with, so a client reads one refusal and not two.
        if (result.Status == StatusCodes.Status402PaymentRequired
            && string.Equals(result.Code, Tenancy.EntitlementRegistry.RemotePlanRequiredCode, StringComparison.Ordinal))
            return Tenancy.RemoteAccessRefusal.ToResult();

        // A plan that could not be read is a temporary condition: the caller is told when to ask again, exactly as
        // the request path tells it.
        if (result.Status == StatusCodes.Status503ServiceUnavailable)
            ctx.Response.Headers.RetryAfter = RetryAfterSeconds;

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

        if (result.Code is not null)
            return Results.Json(new { error = result.Error, code = result.Code }, statusCode: result.Status);
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
    /// <param name="teams">The Teams half, or null on a Gateway where Teams is not released - and then a request naming
    /// a team is refused and no other key of the Director is looked at (devthrottle_internal#2311).</param>
    /// <param name="door">Which door the request came through (<see cref="EnrollDoor"/>). The default is the Director
    /// door, which refuses a Cockpit's or a phone's device type - so a caller that does not say is never handed one.</param>
    /// <param name="remoteAccess">The remote access rule, asked on the Cockpit and phone door of a Gateway that has an
    /// entitlement gate. Required there.</param>
    public static EnrollResult Enroll(string? bearer, EnrollSignedInRequest? req, DeviceRegistry devices,
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator,
        Tenancy.EntitlementRegistry? entitlements = null, DateTime? nowUtc = null,
        TeamEnrollment? teams = null, EnrollDoor door = EnrollDoor.Director,
        Tenancy.RemoteAccessRule? remoteAccess = null)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.DeviceId))
            return new EnrollResult(StatusCodes.Status400BadRequest, null, "deviceId is required");
        var badDeviceId = DeviceIdRefusal(req.DeviceId);
        if (badDeviceId is not null)
            return badDeviceId;

        // THE DEVICE TYPE FOLLOWS THE DOOR (Pricing Change mission, B2). The type on a key is what tells every later
        // request apart - a Cockpit or phone request needs remote access, a Director's does not - so it must not be
        // something the Director door will write on request. Through the Cockpit and phone door the Gateway's own
        // endpoint decided the type from the platform; anything else there is a wiring defect, not a request to serve.
        var cockpitOrPhoneType = Tenancy.RemoteAccessRule.IsCockpitOrPhoneDevice(req.DeviceType);
        if (door == EnrollDoor.Director && cockpitOrPhoneType)
        {
            FileLog.Write("[HostedEnrollment] REFUSED: the Director door was asked for a browser's or a phone's key (no tenant minted, no device key issued)");
            return new EnrollResult(StatusCodes.Status400BadRequest, null, DirectorDoorDeviceTypeRefusal, DirectorDoorDeviceTypeCode);
        }
        if (door == EnrollDoor.CockpitOrPhone && !cockpitOrPhoneType)
            throw new InvalidOperationException(
                $"The Cockpit and phone door was given the device type '{req.DeviceType}', which is neither a browser nor a phone. " +
                "That door decides the type itself (MobileDeviceEnrollmentService.DeviceTypeForPlatform); it never takes one from a caller.");

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
            return EnrollIntoTeam(validation.Subject, teamId, req, devices, teams);

        var refusal = PersonalAccountGate(validation.Subject, tenants, entitlements, nowUtc, door, remoteAccess);
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

        // Where Teams is released a Director may have been set up for a team before. Set up again for the person's
        // own account, that is a MOVE, with the move's own rules (devthrottle_internal#2311): refused while it has
        // sessions registered there, and otherwise its team key goes and its tunnel there is cut. Dark, no other key
        // of this Director can be live, and nothing is asked.
        if (teams is not null)
        {
            var left = LeaveOtherPlaces(validation.Subject, req.DeviceId, tenant, scopedDeviceId, SetUpAgainReason, devices, teams);
            if (left.Refusal is not null)
                return left.Refusal;
        }

        var response = devices.RegisterForTenant(
            tenant,
            validation.Subject,
            scopedDeviceId,
            req.MachineName,
            req.Platform,
            req.DeviceType);

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
    /// THE PERSONAL ACCOUNT'S GATE: whether this account may be handed a key to a hosted tenant of its own today,
    /// through this door. Null means it may; otherwise the refusal to return. Asked by enrollment for the person's
    /// own account and by a move back to it (devthrottle_internal#2311), so both doors are one door. NEVER asked for a
    /// team: a member is never refused for the team's bill here (the team's bill is the team's, read by the
    /// request-path lease).
    ///
    /// NO TRIAL IS GRANTED HERE (Pricing Change mission, B3). Until that mission an account arriving for the first
    /// time was handed a 14-day Pro trial at this point. Nobody is any more. A trial already running is still
    /// honoured, to its end: <see cref="Tenancy.EntitlementRegistry.Evaluate"/> reads the ledger and answers such an
    /// account at the Pro tier, which is why nothing about a trial is asked here at all.
    /// </summary>
    private static EnrollResult? PersonalAccountGate(string subject, Tenancy.TenantRegistry tenants,
        Tenancy.EntitlementRegistry? entitlements, DateTime? nowUtc, EnrollDoor door, Tenancy.RemoteAccessRule? remoteAccess)
    {
        // THE GATE, between subject-verification and tenant-mint. It sits here, and not later, because minting a
        // tenant is itself giving something away: an account that is refused must leave no trace and hold no key.
        //
        // THREE OUTCOMES, and keeping them apart is the entire correctness of this gate:
        //  - Entitled     -> go on to the plan questions below.
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
        if (entitlements is null)
            return null;

        var now = nowUtc ?? DateTime.UtcNow;

        // Evaluate rather than LookupBySubject, because the plan questions below need the tier from the SAME read
        // the outcome came from - one read, one row, so they can never disagree. Evaluate folds a RUNNING trial
        // (the Pro tier, to the trial's end instant) and the free plan (an account with no paid row and no running
        // trial is Entitled on the free tier), so on the hosted Gateway a successful read is always Entitled.
        var decision = entitlements.Evaluate(subject, now);

        if (decision.Outcome == Tenancy.EntitlementOutcome.Unknown)
        {
            FileLog.Write("[HostedEnrollment] RETRY: the entitlement read failed, so entitlement is UNKNOWN - " +
                          "not enrolling and NOT refusing (no tenant minted, no device key issued)");
            return new EnrollResult(StatusCodes.Status503ServiceUnavailable, null,
                "the entitlement service is temporarily unavailable; please try again", EntitlementUnknownCode);
        }

        // Reachable only where the free plan is not in play - a Gateway wired with no trial ledger at all, which
        // answers an account with no paid row NotEntitled. On the hosted service a successful read yields the free
        // tier instead, and the doors below are plan questions, not a paywall.
        if (decision.Outcome == Tenancy.EntitlementOutcome.NotEntitled)
        {
            FileLog.Write("[HostedEnrollment] REFUSED: the entitlement read succeeded and this account has no active entitlement (no tenant minted, no device key issued)");
            return new EnrollResult(StatusCodes.Status402PaymentRequired, null,
                "this account does not have an active hosted subscription");
        }

        // THE PLAN GATE: does what the account holds INCLUDE a place on the hosted Gateway at all - which a self-host
        // plan deliberately does not. Without this, every paid plan would provision hosted capacity, because the
        // entitlement read is tier-agnostic on purpose and passes the plan through unexamined.
        //
        // The verdict is not computed here. It is read from the ONE table that owns what a plan grants
        // (EntitlementScopes), so adding a plan is one edit there and never a new branch at a call site.
        if (!Tenancy.EntitlementScopes.GrantsHostedGateway(decision.Tier))
        {
            FileLog.Write("[HostedEnrollment] REFUSED: the account pays, but its plan does not include hosted " +
                          "gateway capacity (no tenant minted, no device key issued)");
            return new EnrollResult(StatusCodes.Status402PaymentRequired, null,
                "this plan does not include the hosted gateway");
        }

        // THE REMOTE ACCESS GATE (Pricing Change mission, B2), asked ONLY through the Cockpit and phone door. Every
        // plan above may set up a Director; the Cockpit and the phone app need remote access, which is the plan's, the
        // pre-launch grace's, or a team's with a running bill. It is asked of the SAME rule the request path asks on every later request
        // (RemoteAccessRule, through the access lease), so a key this door issues is one the request path serves and
        // a key it refuses is one the request path would have refused. Refused BEFORE any tenant is minted or key
        // issued, and nothing the account already holds is touched: its Director's keys are as they were.
        if (door == EnrollDoor.CockpitOrPhone)
        {
            if (remoteAccess is null)
                throw new InvalidOperationException(
                    "The Cockpit and phone door was reached on a Gateway with an entitlement gate and no remote access rule. " +
                    "HostedEnrollDependencies carries the rule; an entry point that mints a browser's or a phone's key must pass it.");

            var remote = remoteAccess.Decide(decision.Tier, subject, tenants.CreatedAtUtcForSubject(subject), now);
            if (remote == Tenancy.RemoteAccessAnswer.Unknown)
            {
                // What would have decided it - the person's teams, or a team's bill - could not be read. The same
                // temporary condition as an unreadable plan: ask again, never a refusal and never a key.
                FileLog.Write("[HostedEnrollment] RETRY: remote access could not be read (no tenant minted, no device key issued)");
                return new EnrollResult(StatusCodes.Status503ServiceUnavailable, null,
                    "the entitlement service is temporarily unavailable; please try again", EntitlementUnknownCode);
            }
            if (remote == Tenancy.RemoteAccessAnswer.NotGranted)
            {
                FileLog.Write("[HostedEnrollment] REFUSED: the Cockpit or the phone app, on an account with no remote access " +
                              "(no tenant minted, no device key issued, nothing revoked)");
                return new EnrollResult(StatusCodes.Status402PaymentRequired, null,
                    Tenancy.EntitlementRegistry.RemotePlanRequiredError, Tenancy.EntitlementRegistry.RemotePlanRequiredCode);
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

    /// <summary>What setting a Director up for a team, or moving one into it, is told when another person in that team
    /// already set a Director up under the same id (#3552 review, S2-F1).</summary>
    public const string DirectorIdTakenInTeamRefusal =
        "Another member of this team already holds this Director id in this team, so it cannot be set up for the team under your account. Nothing was changed. Ask the team's Owner, or set this computer up as a new Director.";

    /// <summary>The machine-readable code beside <see cref="DirectorIdTakenInTeamRefusal"/>.</summary>
    public const string DirectorIdTakenInTeamCode = "director_id_taken_in_team";

    /// <summary>What setting a Director up, or moving one, is told when its device id holds a <c>|</c> or a control
    /// character (#3552 review, S2-F5).</summary>
    public const string DeviceIdNotAllowedRefusal =
        "This device id cannot be used: it contains a '|' or a control character, which no Director id ever does. Nothing was changed.";

    /// <summary>The machine-readable code beside <see cref="DeviceIdNotAllowedRefusal"/>.</summary>
    public const string DeviceIdNotAllowedCode = "device_id_not_allowed";

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
        Streaming.DirectorConnectionRegistry Connections)
    {
        /// <summary>The Gateway's own wiring, in one place so a test runs exactly it: a Director's registered sessions
        /// are its last known roster on this Gateway, read by the id it said Hello with - which, for a team key, is
        /// the id its key was enrolled for (review F2 and F3).</summary>
        public static TeamEnrollment Over(TeamRegistry teams, TeamAccess access,
            Streaming.PushedSessionStore sessions, Streaming.DirectorConnectionRegistry connections)
        {
            ArgumentNullException.ThrowIfNull(sessions);
            return new TeamEnrollment(teams, access,
                (tenant, directorId) => sessions.GetLastKnown(tenant, directorId).Sessions.Count,
                connections);
        }
    }

    /// <summary>The outcome of <see cref="ListTeams"/>. <see cref="Response"/> is set only on 200.</summary>
    public sealed record TeamsResult(int Status, EnrollHostedTeamsResponse? Response, string Error);

    /// <summary>
    /// Set a Director up for a TEAM: the key is bound to the team's tenant, for this person. Refused unless Teams is
    /// released and the role table lets this person run sessions on their own computers in that team - not a member
    /// and a Collaborator are both refused, with one answer whether or not the team exists. The personal account's
    /// gate is never reached: the team's bill is the team's and is not read here.
    /// </summary>
    private static EnrollResult EnrollIntoTeam(string subject, string teamId, EnrollSignedInRequest req,
        DeviceRegistry devices, TeamEnrollment? teams)
    {
        var refusal = TeamRefusal(subject, teamId, teams?.Access);
        if (refusal is not null || teams is null)
            return refusal!;

        var team = new TenantId(teamId);
        var taken = DirectorIdTakenRefusal(team, subject, req.DeviceId, devices);
        if (taken is not null)
            return taken;
        var scopedDeviceId = TeamScopedDeviceId(teamId, subject, req.DeviceId);
        var left = LeaveOtherPlaces(subject, req.DeviceId, team, scopedDeviceId, SetUpAgainReason, devices, teams);
        if (left.Refusal is not null)
            return left.Refusal;
        var response = devices.RegisterForTenant(team, subject, scopedDeviceId, req.MachineName, req.Platform, req.DeviceType);
        FileLog.Write($"[HostedEnrollment] enrolled deviceId={req.DeviceId}, machine={req.MachineName} -> bound to team " +
                      $"{team.ToLogString()} for its member (no subject/email logged), deviceCount={response.DeviceCount}");
        return new EnrollResult(StatusCodes.Status200OK, response, "");
    }

    /// <summary>The outcome of <see cref="LeaveOtherPlaces"/>: a refusal, or how many old keys were revoked.</summary>
    private readonly record struct LeaveResult(EnrollResult? Refusal, int Revoked);

    /// <summary>
    /// THE ONE "LEAVE WHERE IT WAS" STEP, shared by a move and by setting a Director up again (review F1,
    /// devthrottle_internal#2311). Putting one person's Director <paramref name="directorId"/> into tenant
    /// <paramref name="to"/>: every working key of that person for that Director in ANOTHER tenant is a place it is
    /// leaving. While the Director has any session registered in a place it is leaving, this refuses with the move's
    /// 409 and changes nothing - sessions started in one team never land in another. Otherwise every other key of that
    /// person for that Director is revoked (all but <paramref name="keepDeviceId"/>) and the Director's live tunnel in
    /// each place it left is cut, so the old tunnel cannot keep registering sessions there on a key that is gone.
    /// Setting a Director up again in the SAME tenant leaves nothing: no session check, no tunnel cut - today's
    /// behaviour.
    /// </summary>
    private static LeaveResult LeaveOtherPlaces(string subject, string directorId, TenantId to, string keepDeviceId,
        string reason, DeviceRegistry devices, TeamEnrollment teams)
    {
        var leaving = devices.ActiveKeysOfDirector(subject, directorId)
            .Where(k => k.TenantId is not null
                        && new TenantId(k.TenantId) != to)
            .Select(k => new TenantId(k.TenantId!))
            .Distinct()
            .ToList();

        foreach (var place in leaving)
        {
            var sessions = teams.RegisteredSessions(place, directorId);
            if (sessions > 0)
            {
                FileLog.Write($"[HostedEnrollment] REFUSED: director={directorId} has {sessions} session(s) registered in tenant {place.ToLogString()}, which it would leave");
                return new LeaveResult(new EnrollResult(StatusCodes.Status409Conflict, null, MoveWithSessionsRefusal), 0);
            }
        }

        var revoked = devices.RevokeOtherKeysOfDirector(subject, directorId, keepDeviceId, reason);
        foreach (var place in leaving)
            teams.Connections.AbortForDirector(place, directorId, reason);
        FileLog.Write($"[HostedEnrollment] LeaveOtherPlaces: director={directorId} left {leaving.Count} other tenant(s), revoked={revoked}");
        return new LeaveResult(null, revoked);
    }

    /// <summary>
    /// THE ONE ANSWER to "is this Director id another person's in this team", asked alike by setting a Director up for
    /// a team and by moving one into it (#3552 review, S2-F1). A Director id in a team belongs to one person for good:
    /// refused 409 <see cref="DirectorIdTakenInTeamCode"/> when another person has any key row for that id in the team,
    /// active or revoked. The same person setting the same id up again is unchanged. Null when allowed. No person or
    /// email is logged.
    /// </summary>
    private static EnrollResult? DirectorIdTakenRefusal(TenantId team, string subject, string directorId, DeviceRegistry devices)
    {
        if (!devices.AnotherPersonHasHeldDirectorInTenant(team, subject, directorId))
            return null;
        FileLog.Write($"[HostedEnrollment] REFUSED: director={directorId.Trim()} is already another person's in team {team.ToLogString()} (no subject/email logged)");
        return new EnrollResult(StatusCodes.Status409Conflict, null, DirectorIdTakenInTeamRefusal, DirectorIdTakenInTeamCode);
    }

    /// <summary>
    /// A DEVICE ID WITH A BAR OR A CONTROL CHARACTER IN IT IS REFUSED, by setting a Director up and by a move alike
    /// (#3552 review, S2-F5). The registry id is <c>&lt;namespace&gt;|&lt;device id&gt;</c>, and the Director a key may
    /// say Hello as is read back as the part after the LAST bar - so a device id <c>x|&lt;a colleague's id&gt;</c> would be
    /// stored as one id and read back as another. A Director's own id never holds either: it is a GUID, minted and
    /// re-read by <c>DirectorIdentitySlot.LoadOrCreate</c> (a file that does not parse as a GUID is replaced with a new
    /// one), and that is the id the desktop sends as its device id. Null when the id is allowed.
    /// </summary>
    internal static EnrollResult? DeviceIdRefusal(string deviceId)
    {
        if (!deviceId.Contains('|') && !deviceId.Any(char.IsControl))
            return null;
        FileLog.Write("[HostedEnrollment] REFUSED: the device id holds a '|' or a control character (the id is not logged)");
        return new EnrollResult(StatusCodes.Status400BadRequest, null, DeviceIdNotAllowedRefusal, DeviceIdNotAllowedCode);
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
    /// <see cref="TeamRefusal"/> for a team, <see cref="PersonalAccountGate"/> for the person's own account. Only a
    /// Director's key is moved: a browser's or a phone's is refused.
    /// </summary>
    public static EnrollResult Move(string? bearer, MoveDirectorRequest? req, DeviceRegistry devices,
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator, TeamEnrollment teams,
        Tenancy.EntitlementRegistry? entitlements = null, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(accountTokenValidator);
        ArgumentNullException.ThrowIfNull(teams);

        if (req is null || string.IsNullOrWhiteSpace(req.DeviceId))
            return new EnrollResult(StatusCodes.Status400BadRequest, null, "deviceId is required");
        var badDeviceId = DeviceIdRefusal(req.DeviceId);
        if (badDeviceId is not null)
            return badDeviceId;
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
        var mine = devices.ActiveKeysOfDirector(subject, directorId);
        if (mine.Count == 0)
        {
            var someoneElses = devices.AnotherPersonHasActiveKeyForDirector(subject, directorId);
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

        // A MOVE IS FOR A DIRECTOR (Pricing Change mission, B2). A browser's or a phone's key is found by the same
        // lookup, because the Cockpit and phone door registers it under its own device id, and moving one would
        // re-issue it without that door's remote access question. It is refused, as the Director door refuses to
        // issue one.
        var display = devices.DisplayOfDevice(current.DeviceId);
        if (Tenancy.RemoteAccessRule.IsCockpitOrPhoneDevice(display?.DeviceType))
        {
            FileLog.Write("[HostedEnrollment] Move REFUSED: the key named is a browser's or a phone's, not a Director's");
            return new EnrollResult(StatusCodes.Status400BadRequest, null, DirectorDoorDeviceTypeRefusal, DirectorDoorDeviceTypeCode);
        }

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
            var taken = DirectorIdTakenRefusal(to, subject, directorId, devices);
            if (taken is not null)
                return taken;
            newDeviceId = TeamScopedDeviceId(teamId, subject, directorId);
        }
        else
        {
            var refusal = PersonalAccountGate(subject, tenants, entitlements, nowUtc, EnrollDoor.Director, remoteAccess: null);
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

        // The same step setting a Director up again takes: refused while it has sessions where it is leaving, else
        // the old key revoked and its tunnel cut - BEFORE the new key is issued, so a failure after this point leaves
        // the Director with no working key, never with two.
        var left = LeaveOtherPlaces(subject, directorId, to, newDeviceId, MovedReason, devices, teams);
        if (left.Refusal is not null)
            return left.Refusal;
        if (left.Revoked == 0)
        {
            // Revoked between the lookup above and here, by someone else: there is no longer a working key to move.
            FileLog.Write($"[HostedEnrollment] Move REFUSED: director={directorId} - its key was revoked while the move was being made");
            return new EnrollResult(StatusCodes.Status404NotFound, null, MoveNoSuchDirectorRefusal);
        }

        var response = devices.RegisterForTenant(to, subject, newDeviceId,
            display?.MachineName ?? "", display?.Platform, display?.DeviceType);
        // Where the key this move revoked was working - this caller's one key for this Director, and nothing else - so
        // the Director's name follows only out of the team it was really in (review RM-F8). Null for the personal account.
        response.MovedFrom = new MovedFromPlace { TeamId = teams.Teams.IsTeam(from) ? from.Value : null };
        FileLog.Write($"[HostedEnrollment] Move: director={directorId} moved from tenant {from.ToLogString()} to {to.ToLogString()} " +
                      $"- the old key is revoked and its tunnel cut, a new key issued (left {(response.MovedFrom.TeamId is null ? "the personal account" : "a team")})");
        return new EnrollResult(StatusCodes.Status200OK, response, "");
    }
}
