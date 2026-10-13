using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Tenancy;

/// <summary>What <see cref="TenantAiGate"/> concluded for one model call.</summary>
public enum TenantAiVerdict
{
    /// <summary>The tenant's plan includes the scope, or this Gateway has no billing gate (self-host). Make the call.</summary>
    Allowed,

    /// <summary>
    /// The plan was read and does not include the scope (the free plan, the remote plan, a plan this code does not
    /// know, an account with no tenant mapping, or a person who is not a member of the team). No model call. A
    /// request answers 402 <c>subscription_required</c>; a background job skips the tenant and logs it. A refusal is
    /// ONLY a refusal: nothing is revoked, tombstoned or touched on the tenant's devices.
    /// </summary>
    Refused,

    /// <summary>
    /// The plan could not be read (the database failed). We do not know, so the call is NOT made and the tenant is
    /// NOT told it needs a plan: a request answers 503 with Retry-After, a background job skips this pass.
    /// </summary>
    Unknown,
}

/// <summary>
/// One answer from <see cref="TenantAiGate"/>: the verdict, the scope it was asked about, the plan tier that decided
/// it (null when nothing was read), and plain words for the log.
/// </summary>
public sealed record TenantAiDecision(TenantAiVerdict Verdict, string Scope, string? Tier, string Reason)
{
    /// <summary>Whether the model call may be made.</summary>
    public bool IsAllowed => Verdict == TenantAiVerdict.Allowed;
}

/// <summary>
/// Thrown by a model-call factory when <see cref="TenantAiGate"/> did not allow the call. It exists so that a call
/// site that forgot to ask the gate first still cannot reach the model: the factory refuses to build the client.
/// A request entry point turns it into its HTTP answer (<see cref="HostedAi.HostedAiHttp.TenantAiRefusal"/>); a
/// background entry point catches it, logs it and moves on.
/// </summary>
public sealed class TenantAiRefusedException : Exception
{
    public TenantAiRefusedException(TenantAiDecision decision)
        : base($"The account's plan does not allow this AI call (scope={decision.Scope}, verdict={decision.Verdict}): {decision.Reason}")
    {
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));
    }

    /// <summary>The gate's answer that refused the call.</summary>
    public TenantAiDecision Decision { get; }
}

/// <summary>
/// THE ONE PLACE THAT DECIDES WHETHER THE HOSTED GATEWAY MAY MAKE AN AI CALL FOR A TENANT (Pricing Change mission, B1).
///
/// The hosted Gateway calls the models with ONE shared key for every tenant, so before this nothing checked, per
/// member, that the plan included what was being paid for. Every model call the hosted Gateway makes on a tenant's
/// behalf - transcription and the dictation judge, text to speech, the Wingman's judgement, narration, voice and the
/// Mentor - now asks this gate first, naming the scope it needs (<see cref="EntitlementScopes.Dictation"/>,
/// <see cref="EntitlementScopes.Tts"/>, <see cref="EntitlementScopes.Wingman"/>).
///
/// NOTHING HERE DERIVES A TIER. The plan is read through the existing <see cref="EntitlementRegistry"/> - so a
/// running trial counts as Pro exactly as it does everywhere else, a lapsed account reads as the free tier, and a
/// team seat is answered by the existing team path (<see cref="Teams.TeamMemberEntitlement"/>) - and what the plan
/// grants is asked of <see cref="EntitlementScopes.Grants"/>, the one table that owns that question.
///
/// SELF-HOST IS NOT GATED (decision D5). A self-hosted Gateway has no entitlement registry wired into this gate, and
/// with none every call is allowed, unchanged.
///
/// A REFUSAL IS ONLY A REFUSAL. This type never revokes, never tombstones, and never touches a device key or a lease
/// (<see cref="HostedAccessLeaseService"/>): a Free or Remote member keeps their Director, their tunnel and their
/// sessions, and only the AI call is not made.
///
/// COST. Each question is one tenant-mapping read and one entitlement read (plus the trial ledger read when there is
/// no paid row), all small keyed reads on the Gateway database. There is no cache: one question is asked per model
/// call, and a model call takes seconds where these reads take milliseconds.
/// </summary>
public sealed class TenantAiGate
{
    private readonly EntitlementRegistry? _entitlements;
    private readonly TenantRegistry? _tenants;
    private readonly Teams.TeamMemberEntitlement? _teams;
    private readonly Func<TenantId, string, string?>? _personOfSession;
    private readonly Func<TenantId, DeviceCredentialIdentity?, SessionCredentialIdentity?, string?>? _personOfCredential;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;

    /// <summary>The gate a self-hosted Gateway uses: no entitlement registry, so every call is allowed.</summary>
    public static TenantAiGate SelfHost { get; } = new(null, null);

    /// <param name="entitlements">The entitlement read. NULL means this Gateway has no billing gate - the self-host
    /// path - and every call is allowed (decision D5).</param>
    /// <param name="tenants">Maps a personal tenant to its account subject. Required when
    /// <paramref name="entitlements"/> is given.</param>
    /// <param name="teams">The team path. Null where Teams is not released: every tenant is then a personal one.</param>
    /// <param name="personOfSession">Whose a session is inside a team's tenant
    /// (<see cref="Teams.TeamCallerOwnership.PersonOfSession"/>). Used by <see cref="ForSession"/>.</param>
    /// <param name="personOfCredential">Who a request's credential names inside a team's tenant
    /// (<see cref="Teams.TeamCallerOwnership.PersonOf"/>). Used by <see cref="ForRequest"/>.</param>
    /// <param name="clock">The clock the entitlement read is judged against.</param>
    /// <param name="log">Where the gate's log lines go; the Gateway log when null. Injected so a test can count them.</param>
    public TenantAiGate(
        EntitlementRegistry? entitlements,
        TenantRegistry? tenants,
        Teams.TeamMemberEntitlement? teams = null,
        Func<TenantId, string, string?>? personOfSession = null,
        Func<TenantId, DeviceCredentialIdentity?, SessionCredentialIdentity?, string?>? personOfCredential = null,
        TimeProvider? clock = null,
        Action<string>? log = null)
    {
        if (entitlements is not null && tenants is null)
            throw new ArgumentNullException(nameof(tenants), "A gate that reads entitlements needs the tenant registry to find the account.");
        _entitlements = entitlements;
        _tenants = tenants;
        _teams = teams;
        _personOfSession = personOfSession;
        _personOfCredential = personOfCredential;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? FileLog.Write;
    }

    /// <summary>Whether this gate checks anything at all. False on a self-hosted Gateway.</summary>
    public bool IsGating => _entitlements is not null;

    /// <summary>
    /// The answer for a call made on a tenant's behalf where no particular person is known - a background job over a
    /// tenant, or a model factory that is told only the tenant. In a personal tenant that is the account's own plan.
    /// In a team's tenant it is the TEAM's bill (<see cref="EntitlementRegistry.EvaluateTeam"/>): the team tier when
    /// the bill grants, nothing when it does not. Prefer <see cref="ForSession"/>, <see cref="ForPerson"/> or
    /// <see cref="ForRequest"/> wherever the person is known, because they answer for that person's seat.
    /// </summary>
    public TenantAiDecision ForTenant(TenantId? tenant, string scope)
        => Decide(tenant, scope, person: null, personKnown: false, via: "tenant");

    /// <summary>
    /// <see cref="ForTenant"/> without the gate's own line for the answer, for a caller that asks the SAME question
    /// on a timer and writes one line itself when the answer changes (the daily dictionary scan asks every ten
    /// minutes for every tenant that is due; review finding 3). The answer is identical. A read that FAILS is still
    /// logged here: that is a fault, not a repeated answer.
    /// </summary>
    public TenantAiDecision ForTenantOnATimer(TenantId? tenant, string scope)
        => Decide(tenant, scope, person: null, personKnown: false, via: "tenant", logTheAnswer: false);

    /// <summary>The answer for a call about one session: in a team's tenant, for the session's owner there.</summary>
    public TenantAiDecision ForSession(TenantId tenant, string sessionId, string scope)
        => Decide(tenant, scope, () => _personOfSession?.Invoke(tenant, sessionId), personKnown: true, via: "session");

    /// <summary>The answer for a call about one named person: in a team's tenant, for that person's seat there.</summary>
    public TenantAiDecision ForPerson(TenantId tenant, string? accountSubject, string scope)
        => Decide(tenant, scope, () => accountSubject, personKnown: true, via: "person");

    /// <summary>
    /// The answer for a call a request asked for: in a team's tenant, for the person the request's credential names -
    /// the same answer the access lease uses for that request.
    /// </summary>
    public TenantAiDecision ForRequest(HttpContext ctx, TenantId? tenant, string scope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return Decide(tenant, scope,
            () => tenant is { } t
                ? _personOfCredential?.Invoke(t, Util.AuthMiddleware.AuthenticatedDevice(ctx), Util.AuthMiddleware.CallingSession(ctx))
                : null,
            personKnown: true, via: "request");
    }

    /// <summary><see cref="ForTenant"/>, throwing <see cref="TenantAiRefusedException"/> when the call is not
    /// allowed. For a model factory, which can only build a client or refuse to.</summary>
    public void EnsureAllowedForTenant(TenantId? tenant, string scope)
    {
        var decision = ForTenant(tenant, scope);
        if (!decision.IsAllowed)
            throw new TenantAiRefusedException(decision);
    }

    // THE DECISION. Every public question lands here, so there is one reading of the plan for every model call.
    private TenantAiDecision Decide(TenantId? tenant, string scope, Func<string?>? person, bool personKnown, string via,
        bool logTheAnswer = true)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("A scope name is required", nameof(scope));

        if (_entitlements is null)
            return new TenantAiDecision(TenantAiVerdict.Allowed, scope, null, "self-hosted Gateway: not gated");

        // An AI call on the hosted Gateway with no account to charge it to is not one we can check, so it is not made.
        // The self-host single tenant and the reserved system tenant name no account here.
        if (tenant is not { IsValid: true } t || t.IsLocal || t.IsSystem)
            return Log(new TenantAiDecision(TenantAiVerdict.Refused, scope, null, "no account tenant to check the plan of"), tenant, via, logTheAnswer);

        // THE READ CAN FAIL, AND A FAILED READ IS NOT A VERDICT. The tenant mapping and the team table are database
        // reads; when one throws we do not know the plan, so the answer is Unknown - never a refusal (it would tell a
        // paying member to subscribe) and never a grant (it would hand the model away). This is the same three-way
        // rule the entitlement read itself keeps, applied to the reads around it.
        try
        {
            return Log(DecideHosted(t, scope, person, personKnown), t, via, logTheAnswer);
        }
        catch (Exception ex)
        {
            _log($"[TenantAiGate] Decide FAILED: tenant={t.ToLogString()} scope={scope} via={via}: {ex.GetType().Name}: {ex.Message} - the plan is Unknown, no call is made");
            return new TenantAiDecision(TenantAiVerdict.Unknown, scope, null, "the plan could not be read");
        }
    }

    private TenantAiDecision DecideHosted(TenantId tenant, string scope, Func<string?>? person, bool personKnown)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        if (_teams is not null && _teams.IsTeam(tenant))
        {
            if (!personKnown)
                return FromEntitlement(_entitlements!.EvaluateTeam(tenant.Value, now), scope, "the team's bill");

            // NOBODY'S CALL IS NOBODY'S PLAN. A call in a team that names no person (a session no Director claims), or
            // names someone who is not a member, has no seat to read - so it is not made, and nothing is said about a
            // plan, exactly as the Wingman's narration has always answered it (NarrationPlanRule.DecideForTeamSession).
            // A request never gets this far without a member: the access lease refuses a non-member with a 403 first.
            var who = person?.Invoke();
            if (string.IsNullOrWhiteSpace(who))
                return new TenantAiDecision(TenantAiVerdict.Unknown, scope, null, "the call names no person in this team, so there is no seat to read");

            if (_teams.DecideOrUnknown(tenant, who, now) is not { } teamDecision)
                return new TenantAiDecision(TenantAiVerdict.Unknown, scope, null, "the team membership could not be read");
            if (!teamDecision.IsMember || teamDecision.Entitlement is not { } seat)
                return new TenantAiDecision(TenantAiVerdict.Unknown, scope, null, "the person is not a member of this team, so there is no seat to read");
            return FromEntitlement(seat, scope, "the person's seat in the team");
        }

        var subject = _tenants!.SubjectForTenant(tenant);
        if (string.IsNullOrWhiteSpace(subject))
            return new TenantAiDecision(TenantAiVerdict.Refused, scope, null, "the tenant maps to no account");

        return FromEntitlement(_entitlements!.Evaluate(subject, now), scope, "the account's plan");
    }

    private static TenantAiDecision FromEntitlement(EntitlementDecision decision, string scope, string whose) => decision.Outcome switch
    {
        EntitlementOutcome.Entitled when EntitlementScopes.Grants(decision.Tier, scope)
            => new TenantAiDecision(TenantAiVerdict.Allowed, scope, decision.Tier, $"{whose} includes {scope}"),
        EntitlementOutcome.Entitled or EntitlementOutcome.NotEntitled
            => new TenantAiDecision(TenantAiVerdict.Refused, scope, decision.Tier, $"{whose} does not include {scope}"),
        _ => new TenantAiDecision(TenantAiVerdict.Unknown, scope, null, $"{whose} could not be read"),
    };

    private TenantAiDecision Log(TenantAiDecision decision, TenantId? tenant, string via, bool logTheAnswer)
    {
        if (!logTheAnswer) return decision;
        _log($"[TenantAiGate] tenant={tenant?.ToLogString() ?? "none"} scope={decision.Scope} via={via} " +
                      $"verdict={decision.Verdict} tier={decision.Tier ?? "none"}: {decision.Reason}");
        return decision;
    }
}
