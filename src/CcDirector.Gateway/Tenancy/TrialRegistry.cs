using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Tenancy;

/// <summary>
/// What a trial read established. THREE outcomes, for exactly the reason
/// <see cref="EntitlementOutcome"/> has three: "I looked and there is no live trial" and "I could not look"
/// demand opposite answers, and folding them together either denies a member who is inside their free window
/// or grants the product to someone who is not.
/// </summary>
public enum TrialOutcome
{
    /// <summary>The read succeeded and a trial is running right now.</summary>
    Active,

    /// <summary>
    /// The read SUCCEEDED and no trial covers this account now - either no trial was ever granted, or the one
    /// that was has ended. This is knowledge, and it justifies falling back to the paid answer.
    /// </summary>
    None,

    /// <summary>
    /// The read FAILED - connection, timeout, permission, anything. We do not KNOW whether a trial covers this
    /// account, so this must never be answered as a grant and never as a refusal.
    /// </summary>
    Unknown,
}

/// <summary>
/// What a trial read established for a surface that DESCRIBES the trial to the member, rather than deciding
/// entitlement from it. Four values, because the two questions need different resolutions.
///
/// <see cref="TrialOutcome"/> has three because that is exactly what an ACCESS decision can act on: grant,
/// refuse, retry. "Never had a trial" and "the trial ended" both mean refuse, so folding them is correct
/// there. A SENTENCE shown to a member cannot fold them: "your Pro trial ended on 17 August" told to someone
/// who never had one is false, and so is "you have no trial" told to someone whose fourteen days finished
/// yesterday. So this splits the refusal in two and leaves the other two alone.
///
/// THE SAFETY PARTITION IS STILL THREE, and it is the one that must never be collapsed: ACTIVE /
/// KNOWN-NOT-ACTIVE (<see cref="Expired"/> and <see cref="NeverGranted"/>) / <see cref="Unreadable"/>.
/// Unreadable is ignorance and may never be answered as either of the others - telling a member they have no
/// trial because a database read failed is telling someone with twelve days left that they have nothing.
/// </summary>
public enum TrialStatusKind
{
    /// <summary>The read succeeded and a trial is running right now.</summary>
    Active,

    /// <summary>The read succeeded, a trial WAS granted to this account, and its window has closed. It is
    /// never extended or re-granted, so this state is permanent.</summary>
    Expired,

    /// <summary>The read succeeded and this account was never granted a trial at all.</summary>
    NeverGranted,

    /// <summary>The read FAILED. We do not KNOW - this must never be answered as any of the other three.</summary>
    Unreadable,
}

/// <summary>One descriptive trial read: the four-way status and the row's instants when there was a row.</summary>
/// <param name="Kind">The four-way status. The Unreadable value may never be folded into the others.</param>
/// <param name="StartedAtUtc">When the trial was granted; present whenever a row was read.</param>
/// <param name="ExpiresAtUtc">When the trial ends or ended; present whenever a row was read. Unlike
/// <see cref="TrialDecision.ExpiresAtUtc"/> this is populated on <see cref="TrialStatusKind.Expired"/> too,
/// because a surface saying "your trial ended" has to name the date. It is NOT an entitlement period end and
/// nothing clips a lease to it.</param>
public sealed record TrialStatus(TrialStatusKind Kind, DateTime? StartedAtUtc = null, DateTime? ExpiresAtUtc = null);

/// <summary>The result of one trial read: the three-way outcome and, on <see cref="TrialOutcome.Active"/>,
/// the exact instant the trial ends.</summary>
/// <param name="Outcome">The three-way outcome. Never fold the three into two.</param>
/// <param name="ExpiresAtUtc">The trial's end instant, present only on <see cref="TrialOutcome.Active"/>.
/// The entitlement decision carries it forward as the period end, so the hosted access lease clips to it and
/// caching can never extend a trial one moment past its end.</param>
public sealed record TrialDecision(TrialOutcome Outcome, DateTime? ExpiresAtUtc = null);

/// <summary>
/// What an administrator's attempt to extend a trial did. FIVE values, kept apart for the same reason
/// <see cref="TrialStatusKind"/> keeps four: the person on the other end has to be told a DIFFERENT sentence
/// for each, and a caller handed a single boolean would have to say "that did not work" to four different
/// situations, three of which are not failures at all.
/// </summary>
public enum TrialExtensionOutcome
{
    /// <summary>Applied. The trial now ends later and the ledger has a row saying who did it and why.</summary>
    Extended,

    /// <summary>The read succeeded and this account has no trial row, so there is nothing to extend. NOT a
    /// failure and NOT something to retry - a trial cannot be created from here.</summary>
    NoTrial,

    /// <summary>The proposed end is at or before the trial's current end. Refused rather than applied: a tool
    /// that can quietly cut somebody's free window short is worse than no tool, because the person it happens
    /// to has no way to see it and no reason to look. Equal is refused too - re-applying the same date is not
    /// an extension, and letting it through would write a ledger row claiming a change that did not happen.
    /// </summary>
    NotLater,

    /// <summary>The proposed end is beyond the ceiling, so a mistyped year cannot hand somebody a decade of
    /// paid product.</summary>
    TooFar,

    /// <summary>The read or the write FAILED. We do not know which side of it we are on, so this must never be
    /// reported as a refusal - the caller has to say "I could not confirm this, go and look".</summary>
    Unknown,
}

/// <summary>The result of one extension attempt: the outcome and, whenever a row was read, the instants that
/// let the caller say what the trial now does.</summary>
/// <param name="Outcome">The five-way outcome. Never fold <see cref="TrialExtensionOutcome.Unknown"/> into any
/// of the others.</param>
/// <param name="StartedAtUtc">When the trial began; present whenever a row was read.</param>
/// <param name="PreviousExpiresAtUtc">The end instant before this attempt; present whenever a row was read.</param>
/// <param name="ExpiresAtUtc">The end instant the trial carries now. On
/// <see cref="TrialExtensionOutcome.Extended"/> this is the new one; on the refusals it is the unchanged one,
/// so the caller can state what is actually true rather than only what was refused.</param>
/// <param name="MaxExpiryUtc">The ceiling, present only on <see cref="TrialExtensionOutcome.TooFar"/> so the
/// caller can name the limit that was hit rather than merely reporting a limit.</param>
public sealed record TrialExtension(
    TrialExtensionOutcome Outcome,
    DateTime? StartedAtUtc = null,
    DateTime? PreviousExpiresAtUtc = null,
    DateTime? ExpiresAtUtc = null,
    DateTime? MaxExpiryUtc = null);

/// <summary>
/// The free-trial ledger: the Gateway's own record of which accounts were granted a 14-day Pro trial, and when
/// each trial ends.
///
/// NO NEW TRIAL IS GRANTED (Pricing Change mission, B3, owner decision D1 of 11 October 2026: "There is no more 14
/// day trial"). The hosted enrollment path used to grant one at an account's first arrival and no longer calls
/// <see cref="GrantIfFirstArrival"/>; nothing in the Gateway does. THE LEDGER IS STILL READ: a trial that was
/// already running is honoured to its end by <see cref="Evaluate"/>, through the entitlement read, and an
/// administrator can still extend one (<see cref="ExtendIfLater"/>). No row and no table was removed. What follows
/// describes the grant as it was made, because it is what the rows in the ledger mean.
///
/// WHY THE GATEWAY OWNS THIS AND NOT THE PAYMENT SIDE. The paid entitlement table belongs to the website's
/// payment webhook, which writes it as the service role while this Gateway holds SELECT and nothing more. A
/// trial is not a payment - no webhook produces one - so the Gateway cannot grant it by writing there. It
/// records the grant on its own side of the boundary, in <c>account_trials</c>, and the entitlement decision
/// reads BOTH.
///
/// WHO GETS A TRIAL, stated rather than implied. A trial is granted at an account's FIRST ARRIVAL at the
/// hosted Gateway and nowhere else: the account presents a verified token, holds no paid entitlement, has
/// never been granted a trial, and has no tenant - meaning this Gateway has never seen it before. An account
/// that already holds a tenant was already using hosted before this feature existed and is NOT granted a
/// trial; that is the owner's conservative rollout rule (issue #2117) expressed as something the Gateway can
/// actually observe, since account creation happens on the website and the Gateway never sees it. The
/// residual is stated plainly rather than hidden: an account that signed up before rollout and never once
/// reached the hosted Gateway is indistinguishable here from a brand-new one, and will be granted a trial on
/// its first arrival. It is granted nothing silently - only when the member actively tries to use hosted for
/// the first time - and no account that was already using hosted is granted anything at all.
///
/// ONE TRIAL PER ACCOUNT, EVER. <see cref="GrantIfFirstArrival"/> is idempotent: an existing row is returned
/// as it stands and is NEVER extended or re-stamped, so an expired trial row is what stops a member from
/// restarting the free window by re-enrolling.
///
/// THE ONE EXCEPTION IS A HUMAN, AND IT IS NOT AN EXCEPTION TO THAT RULE. <see cref="ExtendIfLater"/> moves an
/// existing trial's end date later because an administrator decided to, and writes a ledger row saying who
/// and why. It creates no trial and re-grants none, so the rule above is untouched: what stops a member
/// restarting their free window is that no automatic path can produce a second row, and none can. Nothing
/// scheduled, inferred, or self-triggering may ever call it.
///
/// THE READ IS NEVER A VERDICT WHEN IT FAILS. Every failure path returns <see cref="TrialOutcome.Unknown"/>,
/// and the caller turns that into a retry rather than a refusal or a grant. This type deliberately does not
/// throw. Failures are logged LOUD, because an unreadable trial ledger stops every new member from starting.
///
/// Nothing personally identifying is logged here - the subject never reaches the log.
/// </summary>
public sealed class TrialRegistry
{
    /// <summary>
    /// The length of the free trial: 14 days, the number the public pricing page states. It is a constant
    /// rather than a setting because the page says fourteen days and the two must not be able to disagree.
    /// </summary>
    public static readonly TimeSpan TrialLength = TimeSpan.FromDays(14);

    private readonly GatewayDatabase _db;
    private readonly object _writeLock = new();

    /// <param name="db">The Gateway database. The trial ledger is read and written through the UNSCOPED
    /// context: it is keyed by account subject and is read BEFORE any tenant exists, so scoping it to a
    /// tenant would be circular - the same reason the tenant mapping and entitlement tables are unscoped.</param>
    public TrialRegistry(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Read whether a trial covers this account at <paramref name="nowUtc"/>. READ-ONLY - it never grants,
    /// so the hot paths (the hosted access lease and its sweep) can call it on every check without the read
    /// itself creating an entitlement.
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="nowUtc">The moment to judge the trial window against. Injected so the expiry boundary is
    /// testable at an exact instant.</param>
    public TrialDecision Evaluate(string accountSubject, DateTime nowUtc)
    {
        // DELEGATES to the single read rather than performing its own (issue #1243). There is exactly ONE
        // place that loads the row and ONE expiry comparison in this type, so the access decision and the
        // sentence shown to the member can never disagree about whether a trial is running - which they could
        // the moment a second method repeated the boundary rule and one of the two was later edited.
        var status = ReadStatus(accountSubject, nowUtc);

        return status.Kind switch
        {
            // The period end the entitlement decision carries forward, so the hosted access lease clips to it.
            TrialStatusKind.Active => new TrialDecision(TrialOutcome.Active, status.ExpiresAtUtc),

            // IGNORANCE, NOT ABSENCE. Never a grant and never a refusal - the caller turns this into a retry.
            TrialStatusKind.Unreadable => new TrialDecision(TrialOutcome.Unknown),

            // Expired and NeverGranted are both KNOWLEDGE that no trial covers this account now, and an access
            // decision has one answer for both. Only a sentence shown to a member needs them apart.
            _ => new TrialDecision(TrialOutcome.None),
        };
    }

    /// <summary>
    /// Read what covers this account at <paramref name="nowUtc"/>, keeping "never had one" and "it ended"
    /// apart (issue #1243). READ-ONLY, like <see cref="Evaluate"/>, and the ONLY place this type loads the
    /// trial row - <see cref="Evaluate"/> is a fold over this.
    ///
    /// This exists because the trial was granted and never mentioned anywhere: no endpoint returned it, so no
    /// screen could show it, and a promise kept silently is indistinguishable from a promise broken. A surface
    /// that tells a member about their trial needs to name the date it ends or ended, and needs to say
    /// "I cannot tell you" out loud when the read fails rather than picking the comfortable answer.
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="nowUtc">The moment to judge the trial window against.</param>
    public TrialStatus ReadStatus(string accountSubject, DateTime nowUtc)
    {
        // A blank subject is a caller error, not a failed read. Answering Unreadable would invite a retry loop
        // that can never succeed. There is no trial for nobody.
        if (string.IsNullOrWhiteSpace(accountSubject))
        {
            FileLog.Write("[TrialRegistry] ReadStatus: NO TRIAL - no account subject was supplied");
            return new TrialStatus(TrialStatusKind.NeverGranted);
        }

        var subject = accountSubject.Trim();

        Data.Entities.AccountTrialEntity? row;
        try
        {
            using var ctx = _db.CreateUnscopedContext();
            row = ctx.AccountTrials.AsNoTracking().FirstOrDefault(t => t.Subject == subject);
        }
        catch (Exception ex)
        {
            // IGNORANCE, NOT ABSENCE. A failed read does not tell us the account has no trial, so it may not
            // deny and it may not grant. Logged loud and by type: a persistent failure here stops every new
            // member from starting a trial and must not be quiet.
            FileLog.Write($"[TrialRegistry] ReadStatus: READ FAILED ({ex.GetType().Name}) - answering UNREADABLE, " +
                          "which must be retried and must NEVER be treated as no-trial or as a live trial");
            return new TrialStatus(TrialStatusKind.Unreadable);
        }

        if (row is null)
            return new TrialStatus(TrialStatusKind.NeverGranted);

        // STRICTLY LESS THAN, so the expiry instant itself is already outside the window - at exactly
        // ExpiresAtUtc the trial HAS ended, which is what the field means. An inclusive comparison would grant
        // on an expired trial: one tick, but a tick in the give-it-away direction, and boundaries are where a
        // policy is decided, so this one is pinned by its own test.
        if (nowUtc < row.ExpiresAtUtc)
            return new TrialStatus(TrialStatusKind.Active, row.StartedAtUtc, row.ExpiresAtUtc);

        FileLog.Write("[TrialRegistry] ReadStatus: NO TRIAL - this account's trial has ended (it is never extended or re-granted)");
        return new TrialStatus(TrialStatusKind.Expired, row.StartedAtUtc, row.ExpiresAtUtc);
    }

    /// <summary>
    /// Grant the 14-day Pro trial to an account arriving at the hosted Gateway for the FIRST time, and return
    /// what now covers it.
    ///
    /// This is the ONLY place a trial is created, and SINCE THE PRICING CHANGE MISSION (B3) NO PART OF THE GATEWAY
    /// CALLS IT: the hosted enrollment path, its one caller, stopped granting trials. It is kept because it is the
    /// one writer of a ledger that is still read, and granting trials again would be a decision to call it, not
    /// code to write.
    ///
    /// IDEMPOTENT AND NEVER EXTENDING. An account that already has a trial row gets that row back exactly as
    /// it stands - live or expired - and nothing is written. That is what makes one trial per account a
    /// property of the code rather than a hope: re-enrolling after the window has closed re-reads the same
    /// expired row and is refused.
    /// </summary>
    /// <param name="accountSubject">The verified account subject. The caller must have validated the token
    /// this came from; this method does not re-verify it.</param>
    /// <param name="alreadyKnownToGateway">True when this Gateway has seen this account before (it already
    /// holds a tenant). Such an account was using hosted before the trial existed and is granted NOTHING -
    /// the owner's conservative rollout rule. The caller supplies this because the tenant mapping is the
    /// tenant registry's table, not this one's.</param>
    /// <param name="nowUtc">The grant instant; the trial ends at this plus <see cref="TrialLength"/>.</param>
    public TrialDecision GrantIfFirstArrival(string accountSubject, bool alreadyKnownToGateway, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
        {
            FileLog.Write("[TrialRegistry] GrantIfFirstArrival: NO TRIAL - no account subject was supplied");
            return new TrialDecision(TrialOutcome.None);
        }

        // An existing trial - live or expired - always wins over a new grant, and it is checked FIRST so the
        // never-extend rule holds no matter what the caller believes about the account.
        var existing = Evaluate(accountSubject, nowUtc);
        if (existing.Outcome != TrialOutcome.None)
            return existing;

        // Evaluate answered None. That is either "no row at all" or "the row has expired", and only the first
        // may be granted. Re-read the row itself rather than inferring, so an expired trial is never re-granted.
        var subject = accountSubject.Trim();

        lock (_writeLock)
        {
            try
            {
                using var ctx = _db.CreateUnscopedContext();

                if (ctx.AccountTrials.AsNoTracking().Any(t => t.Subject == subject))
                {
                    FileLog.Write("[TrialRegistry] GrantIfFirstArrival: NO TRIAL - this account already had its trial and it has ended (never re-granted)");
                    return new TrialDecision(TrialOutcome.None);
                }

                if (alreadyKnownToGateway)
                {
                    // The conservative rollout rule (issue #2117): an account that already holds a tenant was
                    // using hosted before the trial existed, so it is granted nothing. A goodwill window for
                    // those accounts is a later, deliberate, one-time write - never something that happens by
                    // itself the first time they come back.
                    FileLog.Write("[TrialRegistry] GrantIfFirstArrival: NO TRIAL - this account was already known to the hosted Gateway before the trial existed");
                    return new TrialDecision(TrialOutcome.None);
                }

                var expires = nowUtc + TrialLength;
                ctx.AccountTrials.Add(new Data.Entities.AccountTrialEntity
                {
                    Subject = subject,
                    StartedAtUtc = nowUtc,
                    ExpiresAtUtc = expires,
                });
                ctx.SaveChanges();

                FileLog.Write($"[TrialRegistry] GrantIfFirstArrival: GRANTED a {TrialLength.TotalDays:0}-day Pro trial to a first-seen account (no subject logged), ending {expires:O}");
                return new TrialDecision(TrialOutcome.Active, expires);
            }
            catch (DbUpdateException)
            {
                // A competing grant for the SAME subject won the primary key (a second instance, or a future
                // non-single-writer deployment - the in-process lock above does not cover those). One trial per
                // account is the rule, so the loser adopts the winner's row: re-read what the key enforced.
                // This is not a fallback that hides a problem - the primary key is the source of truth and we
                // are reading back the value it enforced. A still-absent row would be a genuine failure, so it
                // re-throws rather than granting again.
                var winner = Evaluate(subject, nowUtc);
                if (winner.Outcome != TrialOutcome.None)
                {
                    FileLog.Write("[TrialRegistry] GrantIfFirstArrival: lost a grant race, adopting the trial the winner wrote");
                    return winner;
                }
                throw;
            }
            catch (Exception ex)
            {
                // The write failed for anything else. We did not grant, and we do not know what covers this
                // account - answering None here would refuse a brand-new member because the database hiccuped.
                FileLog.Write($"[TrialRegistry] GrantIfFirstArrival: WRITE FAILED ({ex.GetType().Name}) - answering UNKNOWN, which must be retried");
                return new TrialDecision(TrialOutcome.Unknown);
            }
        }
    }

    /// <summary>
    /// How far ahead of NOW an administrator may set a trial's end. A CEILING, so a mistyped year cannot hand
    /// somebody a decade of paid product: one year is far beyond any goodwill window we would actually give
    /// and far short of a typo. Measured from the moment of the decision rather than from the trial's start,
    /// so it means the same thing regardless of how old the trial is.
    /// </summary>
    public static readonly TimeSpan MaxExtensionAhead = TimeSpan.FromDays(365);

    /// <summary>
    /// Move ONE account's trial end date LATER, because a human decided to, and record that decision.
    ///
    /// THIS IS THE DELIBERATE, AUDITED, HUMAN-INITIATED WRITE THIS TYPE'S OWN COMMENTS ANTICIPATED ("a
    /// goodwill window ... is a later, deliberate, one-time write - never something that happens by itself").
    /// It does not weaken the never-extend rule that matters: <see cref="GrantIfFirstArrival"/> is untouched
    /// and still returns an existing row exactly as it stands, so an expired trial still stops a member
    /// restarting their free window by re-enrolling. NO AUTOMATIC PATH MAY CALL THIS.
    ///
    /// WHY IT LIVES IN THE GATEWAY AT ALL. We promise things about trials - a customer blocked for days by
    /// our own bugs was offered four weeks instead of fourteen days - and nothing in the product could
    /// deliver any of it. The trial is a row in this Gateway's own table, which only this Gateway's database
    /// role may write, so every promise previously ended at somebody hand-editing production. Handing the
    /// website a direct grant on the table would have put the capability where the data is not, and the
    /// permission to use it outside either system's migrations, where a rebuild of this schema would silently
    /// take it away. The capability belongs with the data.
    ///
    /// WHAT IT DELIBERATELY DOES NOT DO. It cannot SHORTEN a trial, it cannot CREATE one (an account with no
    /// row is reported as having none - granting a trial to someone who never had one is a different decision
    /// with different consequences), and it cannot reach any account but the one named: the only input is a
    /// subject, matched for equality against the primary key, with no wildcard and no value meaning "all".
    ///
    /// It does NOT decide who may call it. That is the calling surface's administrator check. This is the
    /// capability; it is not the gate.
    /// </summary>
    /// <param name="accountSubject">The account whose trial moves. Matched byte-exactly against the primary
    /// key - no trimming beyond the outer whitespace the caller may have left, no case folding.</param>
    /// <param name="newExpiryUtc">The end instant the trial should now carry. Must be strictly later than the
    /// current one and no further ahead than <see cref="MaxExtensionAhead"/>.</param>
    /// <param name="actor">Who decided. Required - "who did this" is the question the ledger exists for.</param>
    /// <param name="reason">Why. Required by the CAPABILITY, not merely by a screen: a rule enforced only in
    /// a user interface is enforced only until the next caller.</param>
    /// <param name="memberEmail">The member's address for human eyes in the ledger. Optional, and never used
    /// to find anything.</param>
    /// <param name="nowUtc">The moment of the decision. Injected so the ceiling and the ledger's timestamp are
    /// testable at an exact instant.</param>
    /// <exception cref="ArgumentException">A blank subject, actor or reason. These are CALLER ERRORS, not
    /// outcomes: there is no account to report on and no state of the world that produced them, so returning
    /// a tidy result would let a broken caller read "no trial" and tell an administrator this member has
    /// none.</exception>
    public TrialExtension ExtendIfLater(
        string accountSubject, DateTime newExpiryUtc, string actor, string reason,
        string? memberEmail, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("a subject is required to extend a trial", nameof(accountSubject));
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("an actor is required: a trial extension must record who made it", nameof(actor));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("a reason is required: a trial extension must record why it was made", nameof(reason));

        var subject = accountSubject.Trim();
        // Both providers store these as UTC instants and Npgsql REFUSES a non-UTC DateTime against
        // `timestamp with time zone`. Normalising here rather than trusting the caller means a local-kind
        // instant arriving from a future caller is a converted value, not a runtime failure at the write.
        var newExpiry = ToUtc(newExpiryUtc);
        var now = ToUtc(nowUtc);
        var ceiling = now + MaxExtensionAhead;

        lock (_writeLock)
        {
            try
            {
                using var ctx = _db.CreateUnscopedContext();
                using var tx = ctx.Database.BeginTransaction();

                var row = ctx.AccountTrials.AsNoTracking().FirstOrDefault(t => t.Subject == subject);

                if (row is null)
                {
                    // KNOWLEDGE: the read succeeded and there is no trial row. This is not an extension that
                    // failed; it is a member who has nothing to extend, and the caller must say so rather
                    // than offering to try again.
                    FileLog.Write("[TrialRegistry] ExtendIfLater: NO TRIAL - this account has no trial row to extend (no subject logged)");
                    return new TrialExtension(TrialExtensionOutcome.NoTrial);
                }

                // STRICTLY LATER, checked before the ceiling so the more specific refusal wins: an
                // administrator who typed a date in the past is told that, not that it is too far ahead.
                if (newExpiry <= row.ExpiresAtUtc)
                {
                    FileLog.Write("[TrialRegistry] ExtendIfLater: REFUSED - the proposed end is not later than the trial's current end (a trial is never shortened)");
                    return new TrialExtension(TrialExtensionOutcome.NotLater,
                        row.StartedAtUtc, row.ExpiresAtUtc, row.ExpiresAtUtc);
                }

                if (newExpiry > ceiling)
                {
                    FileLog.Write($"[TrialRegistry] ExtendIfLater: REFUSED - the proposed end is beyond the {MaxExtensionAhead.TotalDays:0}-day ceiling");
                    return new TrialExtension(TrialExtensionOutcome.TooFar,
                        row.StartedAtUtc, row.ExpiresAtUtc, row.ExpiresAtUtc, ceiling);
                }

                // COMPARE AND SET. The update carries the expiry we just READ in its predicate, so a
                // competing writer that moved the row between the read and the write cannot have its change
                // silently discarded - the update matches nothing instead. The in-process lock above does not
                // cover a second instance, and a lost update here would leave the ledger recording an
                // extension that no longer exists on the row: an audit that lies.
                var observed = row.ExpiresAtUtc;
                var affected = ctx.AccountTrials
                    .Where(t => t.Subject == subject && t.ExpiresAtUtc == observed)
                    .ExecuteUpdate(s => s.SetProperty(t => t.ExpiresAtUtc, newExpiry));

                if (affected != 1)
                {
                    // We do not know what the row now says, and we did not apply our change. Ignorance, so it
                    // resolves to Unknown rather than to a refusal - the caller re-reads rather than telling
                    // an administrator the extension was rejected.
                    tx.Rollback();
                    FileLog.Write($"[TrialRegistry] ExtendIfLater: the trial row changed under us (matched {affected} rows, expected 1) - answering UNKNOWN, which must be re-read and never reported as a refusal");
                    return new TrialExtension(TrialExtensionOutcome.Unknown);
                }

                // ONE TRANSACTION. The ledger row is written here rather than by the caller so that an
                // extension without a record of it is not a thing that can exist: a caller that crashed
                // between the two would otherwise leave free product handed out and nothing saying who did it.
                ctx.TrialExtensions.Add(new Data.Entities.TrialExtensionEntity
                {
                    Subject = subject,
                    MemberEmail = string.IsNullOrWhiteSpace(memberEmail) ? null : memberEmail.Trim(),
                    StartedAtUtc = row.StartedAtUtc,
                    PreviousExpiresAtUtc = observed,
                    NewExpiresAtUtc = newExpiry,
                    Actor = actor.Trim(),
                    Reason = reason.Trim(),
                    RecordedUtc = now,
                });
                ctx.SaveChanges();
                tx.Commit();

                FileLog.Write($"[TrialRegistry] ExtendIfLater: EXTENDED a trial (no subject logged) from {observed:O} to {newExpiry:O}");
                return new TrialExtension(TrialExtensionOutcome.Extended,
                    row.StartedAtUtc, observed, newExpiry);
            }
            catch (Exception ex)
            {
                // We do not know whether the change landed - a failure after the update but before the commit
                // looks the same from here as one before it. Answering anything definite would be a guess in
                // the expensive direction: told it failed, an administrator tries again, and the second
                // attempt is refused as NotLater by an extension that had in fact already succeeded. They then
                // believe the customer has nothing.
                FileLog.Write($"[TrialRegistry] ExtendIfLater: FAILED ({ex.GetType().Name}) - answering UNKNOWN, which must be re-read and never reported as a refusal");
                return new TrialExtension(TrialExtensionOutcome.Unknown);
            }
        }
    }

    /// <summary>The instant as UTC. Unspecified kind is TREATED as UTC (every instant in this type already is);
    /// a local one is converted rather than reinterpreted, so a wrong-kind caller loses no time.</summary>
    private static DateTime ToUtc(DateTime instant) => instant.Kind switch
    {
        DateTimeKind.Utc => instant,
        DateTimeKind.Local => instant.ToUniversalTime(),
        _ => DateTime.SpecifyKind(instant, DateTimeKind.Utc),
    };
}
