using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// What the entitlement read actually established. THREE outcomes, not two, and keeping them apart is the
/// whole point of this type.
///
/// A boolean would collapse "I looked and there is no entitlement" together with "I could not look", and
/// those demand OPPOSITE answers: the first is a refusal the caller must act on, the second is a temporary
/// condition the caller must retry. Collapsing them either locks out a paying customer when the database
/// hiccups, or - far worse - hands the product away free the moment a read fails. An unresolvable state and
/// a negative state are different answers and must never share a code path.
/// </summary>
public enum EntitlementOutcome
{
    /// <summary>The read succeeded and the account holds a currently-valid entitlement.</summary>
    Entitled,

    /// <summary>
    /// The read SUCCEEDED and the account holds no currently-valid entitlement - either no row at all, or a
    /// row whose state or period says it is not valid now. This is knowledge, and it justifies a refusal.
    /// </summary>
    NotEntitled,

    /// <summary>
    /// The read FAILED - connection, timeout, permission, malformed data, anything at all. We do not KNOW.
    /// This must never be answered as a refusal and must never be answered as a grant: the only honest reply
    /// is "ask again".
    /// </summary>
    Unknown,
}

/// <summary>
/// The full result of one entitlement read: the three-way <see cref="EntitlementOutcome"/> and the plan
/// <see cref="Tier"/> the row carries (<c>hosted</c>, <c>pro</c>, or null on an older row / a read that found
/// or read nothing).
///
/// The two fields answer DIFFERENT questions and must be read together. <see cref="Outcome"/> answers "may
/// this account reach hosted at all" - the enrollment gate's question, which is tier-agnostic. <see cref="Tier"/>
/// answers "which plan" - a capability question (the wingman) - and it is meaningful ONLY when paired with an
/// <see cref="EntitlementOutcome.Entitled"/> outcome: a tier next to <see cref="EntitlementOutcome.NotEntitled"/>
/// is the plan of a non-granting row and grants nothing, and a tier is always null on
/// <see cref="EntitlementOutcome.Unknown"/> because a failed read establishes nothing.
/// </summary>
/// <param name="Outcome">The three-way entitlement outcome. Never fold the three into two.</param>
/// <param name="Tier">The plan tier the row records (hosted|pro), or null. Never gates enrollment.</param>
/// <param name="CurrentPeriodEnd">The paid-period boundary from the row, present only on an
/// <see cref="EntitlementOutcome.Entitled"/> outcome. The cancellation cutoff (MTR-15) CLIPS a positive lease
/// to this instant (<c>expiry = min(now + ttl, CurrentPeriodEnd)</c>) so caching can never extend access one
/// moment past the paid boundary. Null on NotEntitled/Unknown, and null on an Entitled row that recorded no
/// period end (an active row need not carry one; the lease then falls back to the plain ttl).</param>
public sealed record EntitlementDecision(EntitlementOutcome Outcome, string? Tier, DateTime? CurrentPeriodEnd = null);

/// <summary>
/// Reads the paid-entitlement record for a hosted account, at enrollment time.
///
/// This is the gate that makes hosted a paid product: without it, enrolling is free and the billing side
/// sells what anyone can take for nothing. It sits between subject-verification and tenant-mint in the
/// hosted enrollment path, so an account with no entitlement never gets a tenant and never gets a device
/// key - and with no device key there is no tunnel, no cockpit and no mobile. That is the literal meaning
/// of "approval before you can use a hosted tenant".
///
/// THE POLICY, stated rather than implied. An account is entitled when its record says <c>active</c>, OR
/// when it says <c>past_due</c> and the paid period has not yet ended - the payment provider retries a
/// failed payment for a while, and cutting a customer off during that window would refuse someone who has
/// paid and simply had a card decline. A <c>past_due</c> record whose period HAS ended is not entitled, so
/// the grace window is finite rather than open-ended. Any other state - including one this code does not
/// recognise - is NOT entitled, because an unknown state is not an entitled one.
///
/// THE READ IS NEVER A VERDICT WHEN IT FAILS. Every failure path returns <see cref="EntitlementOutcome.Unknown"/>,
/// and the caller is responsible for turning that into a retry rather than a refusal. This type deliberately
/// does not throw: a caller that has to catch will eventually catch in the wrong place and turn ignorance
/// into a denial. The failure is logged LOUD, because a persistent inability to read this table means
/// nobody can enroll and that must not be silent.
///
/// THE FREE TRIAL IS FOLDED IN HERE, AND ONLY HERE (issue #2117). The public pricing page promises every new
/// account 14 days of Pro, and that promise has to be kept at the same read every other entitlement question
/// is answered at - not bolted onto the enrollment endpoint - or the trial would let a member IN at
/// enrollment and then be invisible to the ongoing request-path cutoff, which is where a trial has to EXPIRE.
/// One place decides, so the enrollment gate, the hosted access lease and the 60-second sweep can never
/// disagree about whether an account may use hosted today. See <see cref="TrialRegistry"/> for who is granted
/// a trial; this type only READS the ledger and never grants.
///
/// Nothing personally identifying is logged here - not the subject, not the subscription reference.
/// </summary>
public sealed class EntitlementRegistry
{
    private readonly GatewayDatabase _db;
    private readonly TrialRegistry? _trials;

    /// <param name="db">The Gateway database. The entitlement table is read through the UNSCOPED context:
    /// it is keyed by account subject and is read BEFORE any tenant exists, so scoping it to a tenant would
    /// be circular - the same reason the tenant mapping table is unscoped.</param>
    /// <param name="requireLivemode">
    /// Whether a live-mode subscription is required. Defaults to the hosted deployment signal, so production
    /// hosted demands real money and nothing else has to remember to. A test may pass false to exercise the
    /// rest of the policy without a live row.
    /// </param>
    /// <param name="trials">
    /// The free-trial ledger (issue #2117). NULL means no trial concept at all - the behaviour before trials
    /// existed, byte-for-byte, which is what self-host and the existing tests get. When supplied, an account
    /// with no valid PAID entitlement is entitled while its trial is running, at Pro tier and expiring at the
    /// trial's end instant.
    /// </param>
    public EntitlementRegistry(GatewayDatabase db, bool? requireLivemode = null, TrialRegistry? trials = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _requireLivemode = requireLivemode ?? GatewayHostedMode.IsHosted;
        _trials = trials;
    }

    private readonly bool _requireLivemode;

    /// <summary>
    /// Look up whether a verified account subject may enroll. See <see cref="EntitlementOutcome"/> for why
    /// the answer has three states; the caller MUST handle all three and must not collapse them.
    ///
    /// This is the enrollment gate's read, and it is deliberately TIER-AGNOSTIC: a live, paid entitlement of
    /// EITHER tier (hosted or pro) enrolls, because enrolling is "may this account reach a hosted tenant at
    /// all", which both plans grant. The tier is read and exposed by <see cref="Evaluate"/> for the capability
    /// that cares about it (the wingman); it never changes this outcome. This method returns only the outcome
    /// so an enrollment caller cannot accidentally start gating on tier.
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="nowUtc">The moment to judge the paid period against. Injected so the grace-window
    /// boundary is testable at an exact instant rather than only in whichever direction the clock happens
    /// to be pointing when the suite runs.</param>
    public EntitlementOutcome LookupBySubject(string accountSubject, DateTime nowUtc)
        => Evaluate(accountSubject, nowUtc).Outcome;

    /// <summary>
    /// The full entitlement read: the three-way <see cref="EntitlementOutcome"/> AND the plan
    /// <see cref="EntitlementDecision.Tier"/> the row carries. One read, one place - the enrollment gate takes
    /// the outcome (tier-agnostic) and the wingman capability takes the tier, so the two can never disagree
    /// about the same row.
    ///
    /// The tier is exposed as the row records it, and is meaningful ONLY paired with the outcome: a tier
    /// alongside <see cref="EntitlementOutcome.NotEntitled"/> is the plan of a NON-granting row and confers
    /// nothing, and a failed read carries no tier at all (null) because we read nothing.
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="nowUtc">The moment to judge the paid period against, injected for exact-boundary tests.</param>
    public EntitlementDecision Evaluate(string accountSubject, DateTime nowUtc)
    {
        var paid = EvaluatePaid(accountSubject, nowUtc);

        // No trial ledger wired means trials do not exist here at all - the paid answer IS the answer, exactly
        // as it was before trials. That is the self-host case and the control the trial tests revert against.
        if (_trials is null)
            return paid;

        // A VALID PAID ENTITLEMENT ALWAYS WINS, and it is checked first. That is what makes "subscribes on day
        // seven" seamless: the moment the payment side writes an active row, this returns the PAID decision -
        // paid tier, paid period end, no trial expiry clipped onto it - and the member's access simply carries
        // on. There is no gap because the trial was never removed, and no double-grant because the trial is
        // not consulted at all while a paid row is valid.
        if (paid.Outcome == EntitlementOutcome.Entitled)
            return paid;

        // The paid answer is NotEntitled (no row, cancelled, elapsed, test-mode) or Unknown (the read failed).
        // Either way a running trial entitles this account on its own, so the trial is read in BOTH cases: a
        // failed PAID read is not a reason to deny a member who is inside their free window.
        var trial = _trials.Evaluate(accountSubject, nowUtc);

        if (trial.Outcome == TrialOutcome.Active)
        {
            // THE TRIAL GRANT. Pro tier - the trial grants exactly the Pro entitlement set the pricing page
            // promises - and the period end is the trial's own end instant, so the hosted access lease clips
            // to it and caching can never extend a trial one moment past its end. This is where "expiry is
            // enforced at the entitlement read, not just displayed" actually happens: the instant the trial
            // ends this stops returning Entitled, and the same read that lets a member in is the one that
            // stops letting them in.
            FileLog.Write("[EntitlementRegistry] Evaluate: ENTITLED by the free Pro trial (no paid entitlement; the trial has not ended)");
            return new EntitlementDecision(EntitlementOutcome.Entitled, TierPro, trial.ExpiresAtUtc);
        }

        if (trial.Outcome == TrialOutcome.Unknown)
        {
            // The trial read FAILED. We cannot say whether a trial covers this account, so we may not deny -
            // that would lock out a member inside their free window because the database hiccuped - and we may
            // not grant. Ignorance about EITHER source is ignorance about the answer.
            FileLog.Write("[EntitlementRegistry] Evaluate: UNKNOWN - the trial read failed, so no verdict can be given (must be retried, never treated as unpaid or as paid)");
            return new EntitlementDecision(EntitlementOutcome.Unknown, null);
        }

        // BOTH READS SUCCEEDED, AND NEITHER GRANTS. This is the FREE plan, and it is the one branch here that
        // is a grant rather than a refusal.
        //
        // WHY A REFUSAL BECAME A GRANT. Until now this returned the paid NotEntitled, and that answer travelled:
        // enrolment refused with a 402 and minted nothing, and - worse - an account that already held a tenant
        // was REVOKED, its device credentials tombstoned and its connections dropped. So the end of a trial did
        // not quieten the product, it broke the machine. A member who had connected four computers watched all
        // four fall off on day fifteen. The owner's decision (2026-09-02) is that a lapsed trial lands on a free
        // plan instead: the sessions keep running, and only the artificial-intelligence features go quiet.
        //
        // THIS DOES NOT GIVE ANYTHING AWAY THAT COSTS US A MODEL. Free is a TIER like any other, so what it
        // grants is decided in the one table that owns that question - see EntitlementScopes, where free carries
        // the hosted-gateway scope and NONE of the three artificial-intelligence scopes. Returning Entitled here
        // is therefore not "entitled to everything"; it is "entitled to whatever free grants", which is
        // orchestration and nothing else.
        //
        // THE TRIAL IS NOT SHORT-CIRCUITED BY THIS. A brand-new account reaches this line too, and if it were
        // simply enrolled on free it would never be handed the fourteen days it is owed. The grant lives at the
        // enrolment door rather than here, and that door reads the free tier as "no paid entitlement" exactly as
        // it used to read NotEntitled - see HostedEnrollmentEndpoint. Anything that learns to act on this
        // outcome must make the same distinction: Entitled-on-free is NOT a paying account.
        if (paid.Outcome == EntitlementOutcome.NotEntitled)
        {
            FileLog.Write("[EntitlementRegistry] Evaluate: ENTITLED on the FREE plan (no paid entitlement and no running trial; orchestration only - no artificial-intelligence scopes)");
            return new EntitlementDecision(EntitlementOutcome.Entitled, TierFree);
        }

        // The PAID read FAILED. Ignorance is still never a verdict, and it is not downgraded to free either: we
        // cannot tell a lapsed account from a paying one whose row we could not read, and quietly serving the
        // paying one a plan without its artificial intelligence is a silent wrong answer. Retry.
        return paid;
    }

    /// <summary>
    /// The PAID half of the decision: the payment side's row and nothing else. Split out from
    /// <see cref="Evaluate"/> so the paid policy - active, the past-due grace window, live-money-only, and the
    /// three outcomes - is one unbroken piece of reasoning that the free trial sits beside rather than inside.
    /// </summary>
    private EntitlementDecision EvaluatePaid(string accountSubject, DateTime nowUtc)
    {
        // A blank subject is not a failed read - it is a caller error, and answering Unknown would invite a
        // retry loop that can never succeed. There is no entitlement for nobody, and no tier.
        if (string.IsNullOrWhiteSpace(accountSubject))
        {
            FileLog.Write("[EntitlementRegistry] Evaluate: NOT ENTITLED - no account subject was supplied");
            return new EntitlementDecision(EntitlementOutcome.NotEntitled, null);
        }

        var subject = accountSubject.Trim();

        Data.Entities.EntitlementEntity? row;
        try
        {
            using var ctx = _db.CreateUnscopedContext();
            row = ctx.Entitlements.AsNoTracking().FirstOrDefault(e => e.Subject == subject);
        }
        catch (Exception ex)
        {
            // IGNORANCE, NOT ABSENCE. Every failure lands here - connection refused, timeout, the scoped
            // role losing its SELECT grant, a malformed row. None of them tell us the account is unpaid, so
            // none of them may deny, and none of them may grant. Logged loud and by TYPE, because a
            // persistent failure here stops every enrollment on the box and must not be quiet. No tier: we
            // read nothing, so there is nothing to expose.
            FileLog.Write($"[EntitlementRegistry] Evaluate: READ FAILED ({ex.GetType().Name}) - answering UNKNOWN, " +
                          "which must be retried and must NEVER be treated as unpaid or as paid");

            // DIAGNOSTIC. On a PostgreSQL failure, add the two fields that let the server's OWN error be read
            // from the log - the SQLSTATE code and the server's message text - whether the PostgresException
            // arrived directly or wrapped by EF. These are the difference between "the box is down" and "the
            // read reached the server but the row/column/relation was not what the query expected" (for
            // example SQLSTATE 42P01 undefined_table, 42703 undefined_column, or 42804 datatype_mismatch).
            // Without them the generic type name alone cannot tell those apart. NEITHER field carries PII for
            // a schema, relation or column error - no subject, no email, no row data - so the
            // never-log-the-subject rule is preserved: the subject is still never written here.
            var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
            if (pg is not null)
                FileLog.Write($"[EntitlementRegistry] Evaluate: PostgreSQL error SqlState={pg.SqlState} MessageText={pg.MessageText}");

            return new EntitlementDecision(EntitlementOutcome.Unknown, null);
        }

        // From here the read SUCCEEDED, so whatever we conclude is knowledge.
        if (row is null)
        {
            FileLog.Write("[EntitlementRegistry] Evaluate: NOT ENTITLED - the read succeeded and the account has no entitlement record");
            return new EntitlementDecision(EntitlementOutcome.NotEntitled, null);
        }

        // The plan the row records (hosted|pro), normalized to null-if-blank. Exposed with WHATEVER outcome
        // follows - it is the row's data. It never influences the outcome below.
        var tier = string.IsNullOrWhiteSpace(row.Tier) ? null : row.Tier!.Trim();

        // LIVE MONEY ONLY on the production hosted Gateway. A payment-provider TEST-mode subscription costs
        // nothing to create, so honouring one is a paywall bypass in the deny-OPEN direction - the expensive
        // direction, because it is silent. A NULL is refused exactly as a false is: a row written before this
        // column existed, or by a webhook that forgot it, arrives null, and "we did not record whether this
        // was real money" is not evidence that it was.
        //
        // This composes with the three outcomes rather than adding a fourth: the read SUCCEEDED, so a
        // non-live row is a successful read that returned no VALID entitlement - absence, not ignorance. It
        // earns the 402 and it mints nothing. Keyed off the same hosted signal as everything else, so
        // self-host - which has no billing at all - is untouched.
        if (_requireLivemode && row.Livemode != true)
        {
            FileLog.Write("[EntitlementRegistry] Evaluate: NOT ENTITLED - the entitlement is not a live-mode subscription (a test-mode or unrecorded one is not an entitlement)");
            return new EntitlementDecision(EntitlementOutcome.NotEntitled, tier);
        }

        var status = (row.Status ?? "").Trim();

        if (string.Equals(status, StatusActive, StringComparison.OrdinalIgnoreCase))
            return new EntitlementDecision(EntitlementOutcome.Entitled, tier, row.CurrentPeriodEnd);

        // The dunning grace window: a payment that failed is being retried, and the customer has paid for
        // the period already. Entitled until that period ends, and not one moment after.
        //
        // The comparison is STRICTLY LESS THAN, so the end instant itself is already outside the window. At
        // exactly CurrentPeriodEnd the paid period HAS ended - that is what the field means - and an
        // inclusive comparison would grant on an expired entitlement. It is one tick of access, but it is a
        // grant in the deny-OPEN direction, and this gate's whole job is to never guess in the paying
        // direction. Boundaries are where a policy is decided, so this one is pinned by its own test.
        if (string.Equals(status, StatusPastDue, StringComparison.OrdinalIgnoreCase)
            && row.CurrentPeriodEnd is { } periodEnd
            && nowUtc < periodEnd)
        {
            FileLog.Write("[EntitlementRegistry] Evaluate: ENTITLED within the payment-retry grace window (payment is past due, the paid period has not ended)");
            return new EntitlementDecision(EntitlementOutcome.Entitled, tier, periodEnd);
        }

        // Canceled, past_due with the period ended or with no period recorded, or any state this code does
        // not recognise. An unrecognised state is NOT entitled - we do not guess in the paying direction.
        FileLog.Write($"[EntitlementRegistry] Evaluate: NOT ENTITLED - the read succeeded and the record's state does not grant access (state='{status}')");
        return new EntitlementDecision(EntitlementOutcome.NotEntitled, tier);
    }

    /// <summary>
    /// The paid-features decision for one person in a PERSONAL tenant: exactly <see cref="Evaluate"/> on the
    /// person's own subject - their own entitlement row and their trial, unchanged. No team row is read, so no team
    /// bill can change a personal tenant's answer.
    ///
    /// This and <see cref="EvaluateTeamTenant"/> are TWO methods on purpose: the kind of tenant is a choice the
    /// caller makes by name, never something inferred from whether a membership lookup happened to return a value.
    /// A caller in a team tenant cannot reach a person's own row by passing nothing.
    /// </summary>
    /// <param name="accountSubject">The verified subject of the person.</param>
    /// <param name="nowUtc">The moment to judge against, injected for exact tests.</param>
    public EntitlementDecision EvaluatePersonalTenant(string accountSubject, DateTime nowUtc)
        => Evaluate(accountSubject, nowUtc);

    /// <summary>
    /// The decision for one person in a TEAM tenant (#2299). Reads the TEAM's bill and the person's membership
    /// there - never the person's own subject, so a personal Pro or a trial never grants inside a team (owner,
    /// 3 Oct 2026: "If you have a seat on one team, it doesn't give you a personal seat. It doesn't give you a seat
    /// on another team."), and a team seat never grants outside it.
    ///
    /// A MEMBER IS NEVER REFUSED HERE. The owner's rulings are that a plan without paid features keeps the Gateway
    /// ("Free keeps the Gateway", 2 and 22 Sep 2026 - only the paid artificial intelligence stops) and that a
    /// Collaborator works in the team. So for a member the answer is one of three, and NotEntitled is not one of
    /// them - which is what keeps a member's request from ever reaching the access lease's revoke branch, which
    /// tombstones the whole team tenant's device credentials:
    ///  - Entitled at <see cref="TierTeam"/> (the Pro scopes): a paid seat (Owner, Manager, Developer) on a team
    ///    whose bill grants (see <see cref="EvaluateTeam"/>).
    ///  - Entitled at <see cref="TierFree"/> (hosted access, NO paid scopes): a Collaborator or any role this code
    ///    does not recognise, whatever the bill; or a paid seat on a team whose bill does not grant - no bill yet
    ///    (the Owner has not finished checkout), canceled, an unrecognised state, or a test-mode row on
    ///    production. The same pattern as the personal free plan: Entitled means "entitled to whatever the tier
    ///    grants", and free grants orchestration and nothing else.
    ///  - Unknown: a paid seat whose team bill could not be read. Never a grant, never a refusal. A Collaborator's
    ///    answer does not depend on the bill, so it is never Unknown.
    ///
    /// A NON-MEMBER is a refusal of a different kind, and it is NOT an entitlement verdict:
    /// <see cref="TeamTenantDecision.IsMember"/> is false and there is no <see cref="TeamTenantDecision.Entitlement"/>
    /// at all. The caller reports it as "not a member of this team" (a 403-kind refusal of the PERSON) and must never
    /// treat it as the team being unpaid or feed it to the lease's revoke branch - one stranger's request is not a
    /// reason to cut off the team.
    /// </summary>
    /// <param name="teamId">The team id, which is the tenant id.</param>
    /// <param name="membership">The person's membership in that team, from the membership table (#2300). Required:
    /// <see cref="TeamMembership.NotAMember"/> is how "no membership" is said. A FAILED membership lookup has no
    /// value here on purpose - the caller must answer it as Unknown itself, never as NotAMember.</param>
    /// <param name="nowUtc">The moment to judge against.</param>
    public TeamTenantDecision EvaluateTeamTenant(string teamId, TeamMembership membership, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required", nameof(teamId));
        ArgumentNullException.ThrowIfNull(membership);

        var teamLog = new Core.Tenancy.TenantId(teamId.Trim()).ToLogString();

        if (!membership.IsMember)
        {
            FileLog.Write($"[EntitlementRegistry] EvaluateTeamTenant: team={teamLog} NOT A MEMBER - the person is refused as a non-member; this is not an entitlement verdict and must not revoke the team");
            return TeamTenantDecision.NotAMember;
        }

        if (!TeamSeatRoles.IsPaidSeat(membership.Role))
        {
            FileLog.Write($"[EntitlementRegistry] EvaluateTeamTenant: team={teamLog} ENTITLED on the FREE tier - the member's role is not a paid seat (a Collaborator keeps the team's Gateway, with no paid scopes)");
            return TeamTenantDecision.ForMember(new EntitlementDecision(EntitlementOutcome.Entitled, TierFree));
        }

        var bill = EvaluateTeam(teamId, nowUtc);
        if (bill.Outcome == EntitlementOutcome.NotEntitled)
        {
            // No bill yet, canceled, unrecognised, or not live money: the member keeps the team's Gateway and
            // loses only the paid scopes - never a refusal, so never a revocation.
            FileLog.Write($"[EntitlementRegistry] EvaluateTeamTenant: team={teamLog} ENTITLED on the FREE tier - the team's bill grants no paid features (no bill yet, canceled, or not live money)");
            return TeamTenantDecision.ForMember(new EntitlementDecision(EntitlementOutcome.Entitled, TierFree));
        }

        // Entitled at the team tier, or Unknown (the bill could not be read): passed through as they are.
        return TeamTenantDecision.ForMember(bill);
    }

    /// <summary>
    /// The TEAM's bill: the three-way outcome for one team id, read from the team row the payment side writes
    /// (#2299). On <see cref="EntitlementOutcome.Entitled"/> the tier is <see cref="TierTeam"/>, which
    /// <see cref="EntitlementScopes"/> maps to exactly the Pro scopes.
    ///
    /// THE TEAM POLICY, and how it deliberately differs from the personal one in <see cref="EvaluatePaid"/>:
    ///  - <c>active</c> grants.
    ///  - <c>past_due</c> grants with NO period cut-off. The personal row's past-due grace is FINITE (it ends at
    ///    the paid period's end); a team's is not. Owner, 3 Oct 2026, on a failed team payment: "Nothing stops
    ///    for anyone" - the Owner is told and it is handled by hand. This is a decision for the first version of
    ///    Teams, not an oversight; a later version may add a cut-off, and it would land on this line.
    ///  - <c>canceled</c>, any state this code does not recognise, or no row at all: NotEntitled.
    ///
    /// THIS IS THE BILL, NOT A PERSON'S ANSWER. Its NotEntitled means "the team's bill grants no paid features";
    /// it must never be handed to the access lease, whose NotEntitled revokes the tenant. A person's answer in a
    /// team tenant comes only from <see cref="EvaluateTeamTenant"/>, which turns this NotEntitled into the free
    /// tier for a member.
    ///  - A failed read: Unknown - never a grant, never a refusal.
    ///  - On the production hosted Gateway the row must be live money (<c>livemode</c> true; false and null are
    ///    both refused), exactly as for the personal row.
    ///
    /// THE TRIAL LEDGER IS NEVER CONSULTED FOR A TEAM. Owner, 3 Oct 2026: "We do not do trials for teams." A team
    /// with no bill is a team with no paid features; it does not fall through to a trial. (For a MEMBER it lands on
    /// the free tier - see <see cref="EvaluateTeamTenant"/> - which grants no paid scopes.)
    /// </summary>
    /// <param name="teamId">The team id, which is the tenant id. Logged only in the hashed tenant form.</param>
    /// <param name="nowUtc">The moment to judge against. A team row has no period cut-off, so this does not
    /// change the outcome today; it is taken so the signature matches every other entitlement read and a future
    /// cut-off is one line.</param>
    public EntitlementDecision EvaluateTeam(string teamId, DateTime nowUtc)
    {
        // A blank team id is a caller error, not a failed read - answering Unknown would invite a retry loop that
        // can never succeed.
        if (string.IsNullOrWhiteSpace(teamId))
        {
            FileLog.Write("[EntitlementRegistry] EvaluateTeam: NOT ENTITLED - no team id was supplied");
            return new EntitlementDecision(EntitlementOutcome.NotEntitled, null);
        }

        var id = teamId.Trim();
        var teamLog = new Core.Tenancy.TenantId(id).ToLogString();

        Data.Entities.TeamEntitlementEntity? row;
        try
        {
            using var ctx = _db.CreateUnscopedContext();
            row = ctx.TeamEntitlements.AsNoTracking().FirstOrDefault(e => e.TeamId == id);
        }
        catch (Exception ex)
        {
            // IGNORANCE, NOT ABSENCE - the same rule, and the same loud log, as the personal read.
            FileLog.Write($"[EntitlementRegistry] EvaluateTeam: team={teamLog} READ FAILED ({ex.GetType().Name}) - answering UNKNOWN, " +
                          "which must be retried and must NEVER be treated as unpaid or as paid");
            var pg = ex as Npgsql.PostgresException ?? ex.InnerException as Npgsql.PostgresException;
            if (pg is not null)
                FileLog.Write($"[EntitlementRegistry] EvaluateTeam: team={teamLog} PostgreSQL error SqlState={pg.SqlState} MessageText={pg.MessageText}");
            return new EntitlementDecision(EntitlementOutcome.Unknown, null);
        }

        if (row is null)
        {
            FileLog.Write($"[EntitlementRegistry] EvaluateTeam: team={teamLog} NOT ENTITLED - the read succeeded and the team has no bill (no team entitlement record)");
            return new EntitlementDecision(EntitlementOutcome.NotEntitled, null);
        }

        if (_requireLivemode && row.Livemode != true)
        {
            FileLog.Write($"[EntitlementRegistry] EvaluateTeam: team={teamLog} NOT ENTITLED - the team's subscription is not a live-mode one (a test-mode or unrecorded one is not an entitlement)");
            return new EntitlementDecision(EntitlementOutcome.NotEntitled, null);
        }

        var status = (row.Status ?? "").Trim();

        if (string.Equals(status, StatusActive, StringComparison.OrdinalIgnoreCase))
            return new EntitlementDecision(EntitlementOutcome.Entitled, TierTeam, row.CurrentPeriodEnd);

        if (string.Equals(status, StatusPastDue, StringComparison.OrdinalIgnoreCase))
        {
            // NO PERIOD CUT-OFF, by the owner's decision (see the method remarks). No period end is returned
            // either: the hosted access lease clips a positive lease to CurrentPeriodEnd, and clipping to a
            // boundary that ends nothing would only force extra reads that all answer Entitled.
            FileLog.Write($"[EntitlementRegistry] EvaluateTeam: team={teamLog} ENTITLED - the team's payment is past due, and a failed team payment stops nothing (owner, 3 Oct 2026)");
            return new EntitlementDecision(EntitlementOutcome.Entitled, TierTeam);
        }

        FileLog.Write($"[EntitlementRegistry] EvaluateTeam: team={teamLog} NOT ENTITLED - the read succeeded and the team's bill does not grant access (state='{status}')");
        return new EntitlementDecision(EntitlementOutcome.NotEntitled, null);
    }

    /// <summary>
    /// The billed seat count on a team's row, for the seat-convergence check ONLY (<see cref="TeamSeatSync"/>).
    /// Three-way like every other read here: <see cref="TeamBilledSeats.Known"/> false means the read FAILED.
    /// Never used to decide access.
    /// </summary>
    /// <param name="teamId">The team id, which is the tenant id. Logged only in the hashed tenant form.</param>
    public TeamBilledSeats ReadTeamBilledSeats(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            throw new ArgumentException("A team id is required", nameof(teamId));

        var id = teamId.Trim();
        try
        {
            using var ctx = _db.CreateUnscopedContext();
            var row = ctx.TeamEntitlements.AsNoTracking().FirstOrDefault(e => e.TeamId == id);
            if (row is null)
                return new TeamBilledSeats(Known: true, HasBill: false, Status: null, Seats: null);
            return new TeamBilledSeats(Known: true, HasBill: true, Status: (row.Status ?? "").Trim(), Seats: row.Seats);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[EntitlementRegistry] ReadTeamBilledSeats: team={new Core.Tenancy.TenantId(id).ToLogString()} READ FAILED ({ex.GetType().Name}) - the seat count is unknown; convergence will retry");
            return new TeamBilledSeats(Known: false, HasBill: false, Status: null, Seats: null);
        }
    }

    /// <summary>The state meaning a live, paid subscription.</summary>
    public const string StatusActive = "active";

    /// <summary>The state meaning a payment failed and is being retried - entitled only until the paid period ends.</summary>
    public const string StatusPastDue = "past_due";

    /// <summary>The state meaning the subscription has ended. Not entitled; listed so the seat-convergence check
    /// can name it rather than repeat the string.</summary>
    public const string StatusCanceled = "canceled";

    /// <summary>The hosted plan tier.</summary>
    public const string TierHosted = "hosted";

    /// <summary>The pro plan tier.</summary>
    public const string TierPro = "pro";

    /// <summary>
    /// The FREE plan tier: hosted gateway capacity and nothing else. It is what an account holds when it has no
    /// paid row and no running trial, and it is the reason the end of a trial is a downgrade rather than a
    /// cutoff (owner decision, 2026-09-02).
    ///
    /// It is a real tier and not a null, absence, or sentinel, deliberately. Every capability question in this
    /// Gateway is answered by asking <see cref="EntitlementScopes"/> what a TIER grants; a free plan expressed
    /// as "no tier" would have to be special-cased at each of those call sites, and a call site that forgot
    /// would fail in the granting direction. As a tier string it flows through the same allowlist as every
    /// other plan and is refused everything it is not explicitly given.
    ///
    /// The payment side never writes this value - there is no free row in Stripe - so it originates here and
    /// only here. It nonetheless carries no period end, because a free plan has no paid period to expire.
    /// </summary>
    public const string TierFree = "free";

    /// <summary>
    /// Is this the free plan? Asked wherever code needs "this account is entitled, but it is not PAYING" - the
    /// distinction the enrolment door makes before handing out a trial. Stated once so no call site re-derives
    /// it by comparing strings.
    /// </summary>
    public static bool IsFree(string? tier) =>
        string.Equals((tier ?? "").Trim(), TierFree, StringComparison.Ordinal);

    /// <summary>
    /// Where a member goes to subscribe. Stated ONCE so every refusal the Gateway writes points at the same
    /// place: an entitlement denial has to tell the member what to do about it, and a refusal that only says
    /// "not entitled" is a raw error, not an answer (issue #2117). The client is dumb - the Gateway hands it
    /// the finished message and this address, and the client only renders them.
    /// </summary>
    public const string SubscribeUrl = "https://devthrottle.com/pricing";

    /// <summary>
    /// The human sentence that accompanies every hosted entitlement refusal, paired with
    /// <see cref="SubscribeUrl"/>. One string, so the wording cannot drift between the enrollment refusal and
    /// the request-path cutoff.
    /// </summary>
    public const string SubscribeMessage =
        "Your DevThrottle Pro access has ended. Subscribe to keep using the wingman, dictation and spoken replies.";

    /// <summary>
    /// The pro SELF-HOST plan tier: the artificial-intelligence features only, on a Gateway the customer runs
    /// themselves. This reader treats it exactly like any other tier - it is passed through unexamined, because
    /// this type answers "is there live money", not "what does the plan include". What it grants (and, load
    /// bearing, what it does NOT) lives in <see cref="EntitlementScopes"/>.
    /// </summary>
    public const string TierProSelfHost = "pro_selfhost";

    /// <summary>
    /// The TEAM seat tier (#2299). The payment side never writes it - a team row has no tier column - so it
    /// originates here, on an Entitled team read, exactly as <see cref="TierFree"/> does. What it grants is
    /// decided in <see cref="EntitlementScopes"/>: the same scopes as Pro.
    /// </summary>
    public const string TierTeam = "team";
}

/// <summary>
/// A person's membership in the TEAM tenant a request is in, as the membership table (#2300) records it. The only
/// two values are <see cref="Member"/> (with the role) and <see cref="NotAMember"/>; there is no null and no
/// "unknown", so "the lookup found nothing" can only be said as <see cref="NotAMember"/>, and a FAILED lookup has
/// no value at all - the caller answers it as Unknown before calling
/// <see cref="EntitlementRegistry.EvaluateTeamTenant"/>.
/// </summary>
public sealed class TeamMembership
{
    private TeamMembership(bool isMember, string? role)
    {
        IsMember = isMember;
        Role = role;
    }

    /// <summary>True when the person is a member of the team (any role, Collaborator included).</summary>
    public bool IsMember { get; }

    /// <summary>The member's role (owner, manager, developer, collaborator). Null for a non-member.</summary>
    public string? Role { get; }

    /// <summary>The person is a member, with this role.</summary>
    public static TeamMembership Member(string? role) => new(true, role);

    /// <summary>The lookup succeeded and the person is not a member of this team.</summary>
    public static TeamMembership NotAMember { get; } = new(false, null);
}

/// <summary>
/// The answer for one person in a team tenant. <see cref="IsMember"/> false is a refusal of the PERSON ("not a
/// member of this team") and carries no <see cref="Entitlement"/>; it must never be read as the team being unpaid
/// and never revokes the tenant. For a member, <see cref="Entitlement"/> is Entitled (team or free tier) or
/// Unknown - never NotEntitled.
/// </summary>
/// <param name="IsMember">Whether the person is a member of the team.</param>
/// <param name="Entitlement">The member's entitlement; null for a non-member.</param>
public sealed record TeamTenantDecision(bool IsMember, EntitlementDecision? Entitlement)
{
    /// <summary>The person is not a member of the team.</summary>
    public static TeamTenantDecision NotAMember { get; } = new(false, null);

    /// <summary>The person is a member; this is their entitlement.</summary>
    public static TeamTenantDecision ForMember(EntitlementDecision entitlement) => new(true, entitlement);
}

/// <summary>
/// What a team row says about its billed seats, for the seat-convergence check. <see cref="Known"/> false means
/// the read failed (nothing else on the record is meaningful); <see cref="HasBill"/> false means the read
/// succeeded and the team has no bill yet.
/// </summary>
public sealed record TeamBilledSeats(bool Known, bool HasBill, string? Status, int? Seats);

/// <summary>
/// THE ONE PLACE that says which team roles are PAID SEATS (#2299, owner 3 Oct 2026: "Every team member pays.
/// Except for the collaborators."). The access decision and the seat count both ask this, so "who pays" and "who
/// gets paid features" can never disagree.
///
/// An allowlist, ordinal and exact, like every other plan question here: a role this code does not recognise is
/// NOT a paid seat, so it gets no paid scopes and is not counted.
/// </summary>
public static class TeamSeatRoles
{
    /// <summary>The team's Owner - pays for the team and holds a paid seat themselves.</summary>
    public const string Owner = "owner";

    /// <summary>A Manager - a paid seat.</summary>
    public const string Manager = "manager";

    /// <summary>A Developer - a paid seat.</summary>
    public const string Developer = "developer";

    /// <summary>A Collaborator - never a paid seat, never on the bill, never given paid scopes.</summary>
    public const string Collaborator = "collaborator";

    /// <summary>Is this role a paid seat? Owner, Manager and Developer are; anything else is not.</summary>
    public static bool IsPaidSeat(string? role) =>
        role is not null && (string.Equals(role.Trim(), Owner, StringComparison.Ordinal)
                             || string.Equals(role.Trim(), Manager, StringComparison.Ordinal)
                             || string.Equals(role.Trim(), Developer, StringComparison.Ordinal));
}
