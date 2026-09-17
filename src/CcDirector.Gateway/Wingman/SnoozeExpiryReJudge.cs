using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>What one snooze expiry came to (the Wingman-on-every-turn mission, slice F, ruling 10).</summary>
public enum SnoozeExpiryOutcome
{
    /// <summary>
    /// A VERDICT FOR THIS SESSION IS ALREADY BEING FORMED. The answer is on its way, so the expiry neither calls
    /// the row calm nor asks a second time - and it RECORDS THAT, with its own cause word. An expiry spends its
    /// one edge whatever it decides, and an edge that is spent without a ledger row is an expiry that cannot be
    /// accounted for afterwards (the inspector's second finding on pull request 2899, upheld by the Architect).
    /// </summary>
    ReadInFlight,

    /// <summary>No turn ended while the snooze ran, so the row comes back calm and nobody is asked anything.</summary>
    NothingNew,

    /// <summary>A turn ended while the snooze ran and its verdict is on the row, so that verdict rules - calm or red.</summary>
    VerdictRules,

    /// <summary>A turn ended while the snooze ran and no verdict covers it, so the judge is asked now. Red stands.</summary>
    ReadRequested,
}

/// <summary>
/// THE DECISION ONE SNOOZE EXPIRY MAKES, as a pure function over facts (ruling 10). Separated from the memory
/// below so its cases can be read, and tested, without a fold, a clock or a store.
/// </summary>
public static class SnoozeExpiryDecision
{
    /// <summary>
    /// What to do at the moment a snooze's clock is first seen elapsed.
    /// </summary>
    /// <param name="armedAtUtc">When this Gateway first saw THIS CLOCK armed - the snooze's set time, as
    /// observed. NULL WHEN THAT MOMENT IS NOT KNOWN, and then there is no stretch of time to compare anything
    /// against, so the expiry ASKS: unknown is red, and the judge is put to the current screen. See the arm below
    /// for the three ways the moment goes missing.</param>
    /// <param name="latest">The row's latest verdict - accepted or refused - or null when it carries none.</param>
    /// <param name="verdictState">The row's <see cref="SessionDto.VerdictState"/>.</param>
    /// <param name="turnEndsSinceSnoozeSet">THE SWITCHING DESIGN'S OWN FACT: how many turns ended since the
    /// snooze was set. It takes precedence when it is present, because it is the detector's count rather than
    /// an inference from what happened to be stored. IT IS NOT LIVE - the switching build has not landed, so
    /// production passes null today and the verdict comparison below is the path that runs. Written now, with
    /// its own tests, so the switching build has one line to change rather than a rule to re-derive.</param>
    public static SnoozeExpiryOutcome AtExpiry(
        DateTime? armedAtUtc,
        TurnVerdictDto? latest,
        string? verdictState,
        int? turnEndsSinceSnoozeSet)
    {
        // A read is already in flight for this stop: an answer is coming, so this must neither call the row calm
        // nor ask a second time. The reading stamp is the one row state that carries no verdict of its own, and
        // it gets its OWN word so it can carry its own ledger row - an expiry spends its one edge whatever it
        // decides, and every outcome here records exactly once.
        if (string.Equals(verdictState, VerdictStates.Reading, StringComparison.Ordinal))
            return SnoozeExpiryOutcome.ReadInFlight;

        // UNKNOWN IS RED, and an unknown arming moment therefore ASKS (the Architect's ruling, 2026-09-16).
        //
        // "Nothing happened while the snooze ran" is a claim about a stretch of time, and without its start there
        // is no stretch. So this must never answer calm. What it does instead is the inversion that matters: it
        // asks the judge about the current screen, which is the one answer that is true whatever happened in the
        // part this Gateway could not see.
        //
        // WHAT REACHES THIS ARM, now that a partial view may not prune (see PruneToRoster). THREE things, and
        // the first is the one that cannot be closed from inside this class:
        //
        //   A GATEWAY RESTART. The arming observation lives in PROCESS MEMORY, so a Gateway that restarts while a
        //   snooze runs has no record of when that clock started. The watch is re-armed when the session next
        //   folds, and its expiry asks.
        //
        //   A SESSION THAT GENUINELY LEFT THE ACCOUNT AND CAME BACK. Its entry was pruned because the account
        //   really did not have it, and a returning session is a clock nobody watched start.
        //
        //   A NEW CLOCK NOBODY SAW ARMED. A re-snooze that runs out with no fold in between is a deadline this
        //   memory never observed - the snooze endpoint's display push is BEST EFFORT, so nothing guarantees a
        //   fold between arming a clock and its running out, and a short re-snooze needs only one missed push.
        //   The entry found then belongs to the PREVIOUS clock, so it says nothing about this one.
        //
        // What no longer reaches it is a session that merely dropped out of somebody's VIEW - a filtered read, a
        // Director's own push, a machine that went quiet. Those prune nothing now, and that was the path where
        // this mattered most, because it could silently turn a real ask into calm.
        //
        // It is still written as a property of NOT KNOWING rather than of a restart, because the rule is about
        // the evidence and not about the cause: anything that loses the moment gets the safe answer without
        // anybody having to remember to add a case. What it replaced was an expiry that answered CALM when it
        // could not see the start of the quiet - a quietened question, which this file's own words call the worst
        // thing the mission can do.
        //
        // THE REAL FIX IS DEFERRED, with its cost named: the arming moment belongs ON THE SNOOZE, stored where
        // the deadline already is, so it survives a restart instead of being re-derived from whoever looked
        // first. That needs a schema change and its migration, which is why it is a follow-up and not this
        // slice; until it lands, a restart costs one read per snoozed session at its expiry.
        //
        // The cost is bounded by the same things every automatic trigger is bounded by: the edge fires once per
        // expiry, the account's judge switch gates it, and its ceiling caps it.
        if (armedAtUtc is not DateTime armedAt)
            return SnoozeExpiryOutcome.ReadRequested;

        var newTurnEnd = turnEndsSinceSnoozeSet is int counted
            ? counted > 0
            : latest is not null && latest.TurnEndObservedAtUtc > armedAt;
        var accepted = latest is { Failed: false }
                       && string.Equals(verdictState, VerdictStates.Judged, StringComparison.Ordinal);

        // A turn ended while the snooze ran and the row carries the verdict for it: that verdict rules, calm or
        // red, and this slice adds nothing. A refused answer covers nothing, and an accepted verdict from before
        // the snooze does not cover a stop that happened after it.
        if (accepted && latest!.TurnEndObservedAtUtc > armedAt) return SnoozeExpiryOutcome.VerdictRules;

        // A turn ended and nothing on the row says what it means. Ask now; the row keeps the red it has.
        if (newTurnEnd) return SnoozeExpiryOutcome.ReadRequested;

        // NOTHING NEW HAPPENED - but an ACCEPTED VERDICT FROM BEFORE THE SNOOZE STILL RULES THE ROW. A clock
        // running out does not answer a question: if the Wingman judged that stop an ask, the session is still
        // sitting on that ask and the owner parked it precisely so it would come BACK. Quietening it here would
        // turn the snooze from an alarm clock into a delete button, and a quietened question is the worst thing
        // this mission can do. Where the verdict was calm, it is the calm arm above that says so, in the
        // Wingman's own words, which are better than these.
        //
        // RULING 10 PREDATES UNIVERSAL JUDGING, AND THIS LINE IS THE RECONCILIATION. It was written when a
        // snoozed row mostly carried no verdict at all; slice C then made EVERY owned stop judged, so read
        // literally today "no new turn end -> calm" would quieten almost every snoozed row - and would kill the
        // ruling's own last sentence, "only needed-you brings it back red", by leaving nothing that can.
        if (accepted) return SnoozeExpiryOutcome.VerdictRules;

        // THE WHOLE POINT OF RULING 10: nothing happened and nothing had judged this stop, so the red this row
        // would otherwise show is the clock's own and not the agent's. A snooze expiry must not manufacture one.
        return SnoozeExpiryOutcome.NothingNew;
    }
}

/// <summary>
/// A SNOOZE EXPIRY RE-JUDGES (the Wingman-on-every-turn mission, slice F, ruling 10).
///
/// EXPIRY IS NOT AN EVENT. <c>SnoozeRegistry.IsExpired</c> is a pure computation the fold stamps onto the row as
/// <see cref="SessionDto.SnoozeExpired"/> every time it folds, so there is nothing to subscribe to. The trigger
/// is therefore an EDGE IN THE FOLD: expired now for a session that was not expired on the previous fold. This
/// keeps its per-session answer across folds exactly as <see cref="Briefing.NeedsYouClock"/> keeps its stamp -
/// in memory, keyed by (tenant, session), re-derived after a restart - because one kind of memory in the fold is
/// enough and a second would be a second authority.
///
/// THE CASES ARE IN <see cref="SnoozeExpiryDecision"/>. What lives here is WHEN they are asked, and that is
/// the part with teeth: the edge fires ONCE. A session that stays expired across a thousand folds asks the judge
/// on the first of them and on none of the others - a fold runs on every roster poll, every display sweep and
/// every accepted Director push, so a condition rather than an edge would be a paid model call per poll.
///
/// THE CALM STAMP IS HELD, NOT LATCHED. While the expiry stands, every fold re-asks whether nothing has happened
/// yet, and stops saying so the moment something has. A latch would keep saying "nothing new" about a session
/// that had since taken a turn, which is the stale-answer defect this mission exists to end. Re-asking is free:
/// it reads the row the fold has already stamped, and it never asks the judge again.
///
/// THE ACCOUNT'S COLOUR SWITCH GATES IT, so an account in shadow sees nothing of this on the wire (ruling 4).
/// NOT PROVEN, AND NAMED AS A GAP: a shadow account therefore does not re-judge on a snooze expiry either, so its
/// stored verdicts are not in every case what the product would have shown. The carrying-on clock was ruled the
/// other way for exactly that reason (the slice D ruling), and the difference is that the clock reads the store
/// while this reads the row the stamp wrote, which is empty in shadow by design.
/// </summary>
public sealed class SnoozeExpiryReJudge
{
    /// <summary>
    /// What the previous fold saw for one session. <see cref="ArmedUntilUtc"/> identifies WHICH snooze was seen
    /// armed, so a re-snooze is a new clock and takes a new observation rather than inheriting the old one's.
    ///
    /// A CLASS, NOT A RECORD, AND THAT IS LOAD-BEARING. It is the compare value of a compare-and-swap, and
    /// <c>ConcurrentDictionary.TryUpdate</c> compares with the default equality comparer - which for a record is
    /// VALUE equality. Two folds holding two equal-but-different snapshots would then both win the swap, and both
    /// would fire the edge. Reference equality makes the swap mean what a swap has to mean: exactly one winner.
    /// </summary>
    private sealed class Watch
    {
        public Watch(bool expired, DateTime? armedSeenAtUtc, DateTime? armedUntilUtc, bool nothingNew)
        {
            Expired = expired;
            ArmedSeenAtUtc = armedSeenAtUtc;
            ArmedUntilUtc = armedUntilUtc;
            NothingNew = nothingNew;
        }

        public bool Expired { get; }
        public DateTime? ArmedSeenAtUtc { get; }
        public DateTime? ArmedUntilUtc { get; }
        public bool NothingNew { get; }
    }

    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), Watch> _watch = new();
    private readonly Func<TenantId, string, string, bool>? _requestRead;
    private readonly Action<TurnVerdictRecord>? _record;

    /// <param name="requestRead">Ask the seat to judge this session's CURRENT screen now (tenant, director id,
    /// session id). The JUDGEMENT is fire and forget - the fold is the hot path and waits for no model call - but
    /// the ANSWER TO THIS CALL is not: it says whether the Wingman is now READING this session, which the seat
    /// decides synchronously, on this thread, before it reads anything. That answer is what lets this fold paint
    /// the row yellow instead of letting it go out red one last time (the inspector's third finding on pull
    /// request 2899). False means the seat will not judge this stop at all, and then the row keeps its red.</param>
    /// <param name="record">The ledger, so an expiry's ruling is answerable by query rather than by reading a
    /// log file.</param>
    public SnoozeExpiryReJudge(
        Func<TenantId, string, string, bool>? requestRead = null,
        Action<TurnVerdictRecord>? record = null)
    {
        _requestRead = requestRead;
        _record = record;
    }

    /// <summary>How many sessions this is watching. Diagnostics and tests; the fold does not read it.</summary>
    public int Watching => _watch.Count;

    /// <summary>
    /// One fold's pass over one account's rows: observe every session's snooze, fire the edge where there is one,
    /// and assign <see cref="SessionDto.SnoozeEndedNothingNew"/> on EVERY row in both directions - the roster
    /// re-serves rows a previous fold stamped, so a stamp that was only ever set would outlive its reason.
    /// </summary>
    /// <param name="rosterSessionIds">THE ACCOUNT'S WHOLE ROSTER, as session ids, or NULL FROM A CALLER THAT IS
    /// LOOKING AT ONLY PART OF THE ACCOUNT. A caller that can name the whole roster gets a prune; a caller that
    /// cannot gets none. It is never inferred from the rows, because a fold's rows are not the account - the
    /// display push carries one Director's sessions and a filtered roster read carries one machine's.</param>
    public void Observe(
        TenantId tenant,
        IReadOnlyList<SessionDto> rows,
        Snooze.SnoozeHoldSnapshot holds,
        DateTime nowUtc,
        IReadOnlyCollection<string>? rosterSessionIds = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(holds);

        // A PARTIAL VIEW OBSERVES BUT DOES NOT DROP. Null is a caller saying "I am not looking at the whole
        // account", and the answer to that is to prune nothing at all.
        if (rosterSessionIds is not null) PruneToRoster(tenant, rosterSessionIds);

        foreach (var s in rows)
        {
            s.SnoozeEndedNothingNew = false;
            if (string.IsNullOrEmpty(s.SessionId)) continue;

            var key = (tenant, s.SessionId);
            var expired = holds.IsExpired(s.SessionId, nowUtc);
            var until = holds.SnoozeUntilFor(s.SessionId);

            if (!expired)
            {
                // Armed and still running: remember WHEN this clock was first seen, which is this Gateway's
                // observation of the moment the snooze was set. A deferred hold has no clock yet and a cleared
                // one has none any more; both forget, so the next arming is observed afresh.
                if (until is DateTime deadline && deadline > nowUtc)
                {
                    // THE FIRST FOLD TO SEE THIS CLOCK IS THE ONE THAT DATES IT. A concurrent fold must not
                    // re-stamp the observation to its own later moment, so the entry is only added or swapped
                    // when this caller genuinely has something new to say: a clock nobody had seen yet.
                    var previous = _watch.TryGetValue(key, out var seen) ? seen : null;
                    var sameClock = previous is { Expired: false, ArmedUntilUtc: DateTime was } && was == deadline;
                    if (!sameClock)
                    {
                        var armed = new Watch(false, nowUtc, deadline, false);
                        if (previous is null) _watch.TryAdd(key, armed);
                        else _watch.TryUpdate(key, armed, previous);
                    }
                }
                else
                {
                    _watch.TryRemove(key, out _);
                }
                continue;
            }

            StampExpired(tenant, key, s, until);
        }
    }

    /// <summary>
    /// One session whose snooze has elapsed: take the edge if it is still there to take, otherwise hold.
    ///
    /// THE EDGE IS WON, NOT OBSERVED. The roster, the single-session read and every accepted Director push all
    /// fold through here, concurrently, over ONE shared memory - so "was it expired last time?" read and then
    /// written is two steps a second fold can slip between, and both would ask the judge about the same stop.
    /// The transition is a compare-and-swap instead: whoever swaps the session's entry from not-expired to
    /// expired is the one caller that acts, and everybody else goes round and takes the hold path. This is the
    /// same defect, and the same fix, as the one-stop-raised-twice finding on slice E.
    /// </summary>
    /// <summary>
    /// What <see cref="Observe"/> would stamp on <see cref="SessionDto.SnoozeEndedNothingNew"/>, WITHOUT arming,
    /// pruning, spending an expiry edge, writing a ledger line or asking for a read. For a fold that records what the
    /// display push shows (the Wingman inspector's trace colour), which must not move the product.
    ///
    /// An expiry the watch has already taken answers exactly what Observe answers on every later fold. An expiry no fold
    /// has taken yet answers what the first fold would stamp - except that a re-judge it would REQUEST is not requested
    /// here, so that row is not marked as being read. That one moment, the fold that spends the edge, is the only place
    /// the two can differ, and it lasts until the next display push.
    /// </summary>
    public void Peek(TenantId tenant, IReadOnlyList<SessionDto> rows, Snooze.SnoozeHoldSnapshot holds, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(holds);
        foreach (var s in rows)
        {
            s.SnoozeEndedNothingNew = false;
            if (string.IsNullOrEmpty(s.SessionId) || !holds.IsExpired(s.SessionId, nowUtc)) continue;
            var until = holds.SnoozeUntilFor(s.SessionId);
            var watch = _watch.TryGetValue((tenant, s.SessionId), out var held) ? held : null;
            var sameClock = watch is not null && Nullable.Equals(watch.ArmedUntilUtc, until);
            var armedAt = sameClock ? watch!.ArmedSeenAtUtc : null;
            var outcome = SnoozeExpiryDecision.AtExpiry(armedAt, s.TurnVerdict, s.VerdictState, turnEndsSinceSnoozeSet: null);
            s.SnoozeEndedNothingNew = watch is { Expired: true } && sameClock
                ? watch.NothingNew && outcome == SnoozeExpiryOutcome.NothingNew
                : outcome == SnoozeExpiryOutcome.NothingNew;
        }
    }

    private void StampExpired(TenantId tenant, (TenantId, string) key, SessionDto s, DateTime? until)
    {
        while (true)
        {
            var watch = _watch.TryGetValue(key, out var held) ? held : null;
            // The observation only counts for the clock that actually elapsed. A snooze this Gateway watched
            // being armed, then re-armed elsewhere, is a different stretch of quiet.
            // WHICH CLOCK IS THIS WATCH ABOUT? The entry holds the deadline it was made for, so a re-snooze is a
            // DIFFERENT clock even though it is the same session - and everything below turns on telling those
            // apart. Compared with Nullable.Equals so that two unknown deadlines match each other: an entry made
            // without one must not read as a new clock on every fold, which would ask the judge on every poll.
            var sameClock = watch is not null && Nullable.Equals(watch.ArmedUntilUtc, until);
            var armedAt = sameClock ? watch!.ArmedSeenAtUtc : null;
            var outcome = SnoozeExpiryDecision.AtExpiry(armedAt, s.TurnVerdict, s.VerdictState, turnEndsSinceSnoozeSet: null);

            // THE HOLD PATH BELONGS TO THE CLOCK THE EDGE FIRED FOR, AND ONLY TO IT.
            //
            // A SECOND SNOOZE THAT RUNS OUT WITH NO FOLD IN BETWEEN used to land here and be swallowed: the entry
            // still said "expired" from the FIRST clock, so this took the hold path, asked nothing and recorded
            // nothing, and that expiry was never ruled on at all. It is reachable because the snooze endpoint's
            // display push is BEST EFFORT - nothing guarantees a fold between arming a clock and its running out
            // - and a short re-snooze needs only one missed push. Measured before this line existed: the second
            // expiry produced no read and no ledger row, and the only row in the ledger belonged to the first.
            //
            // A new clock is a new edge, so it falls through to the swap below, where its unknown arming moment
            // makes it ASK - the same answer the other two ways of losing that moment get.
            if (watch is { Expired: true } && sameClock)
            {
                // ALREADY EXPIRED WHEN SOMEBODY LOOKED LAST, and it is the same clock: the edge has fired, so
                // nothing is asked and nothing is recorded. The calm stamp is re-decided rather than remembered,
                // and only ever downward - this can stop saying "nothing new", and can never start. A lost swap
                // here changes nothing worth retrying for: the other caller computed the same answer from the
                // same row.
                var stillNothingNew = watch.NothingNew && outcome == SnoozeExpiryOutcome.NothingNew;
                s.SnoozeEndedNothingNew = stillNothingNew;
                if (stillNothingNew != watch.NothingNew)
                    _watch.TryUpdate(key, new Watch(true, watch.ArmedSeenAtUtc, watch.ArmedUntilUtc, stillNothingNew), watch);
                return;
            }

            // THE EDGE. Nobody acts until the swap is won.
            var next = new Watch(true, armedAt, until, outcome == SnoozeExpiryOutcome.NothingNew);
            var won = watch is null ? _watch.TryAdd(key, next) : _watch.TryUpdate(key, next, watch);
            if (!won) continue;   // another fold took this expiry; go round and hold with whatever it decided

            s.SnoozeEndedNothingNew = outcome == SnoozeExpiryOutcome.NothingNew;
            // EVERY EXPIRY RECORDS, with no exception left. The one outcome that used to write nothing was the
            // expiry that knew nothing, and that outcome no longer exists - not knowing is now a reason to ASK,
            // and an ask says so like every other ruling. The event's contract of exactly one row per expiry is
            // therefore true of every path through here rather than of all but one.
            FileLog.Write($"[SnoozeExpiryReJudge] sid={s.SessionId} tenant={tenant.ToLogString()} snooze ended: {outcome}");
            _record?.Invoke(new TurnVerdictRecord(tenant, s.DirectorId ?? "", s.SessionId,
                ActivityEventTypes.TurnVerdictSnoozeExpiry, CauseOf(outcome),
                $"verdictState={s.VerdictState}"));

            if (outcome == SnoozeExpiryOutcome.ReadRequested)
            {
                // NO RED FRAME BEFORE THE WINGMAN READS (the owner's slice E ruling, which is LATER than this
                // slice's own "red stands until it answers" wording and wins over it). The seat stamps "reading"
                // synchronously, before it reads the screen, and answers here whether it did - so a stop that
                // WILL be judged turns yellow on THIS fold rather than going out red one last time.
                //
                // THE ROW HAS TO BE RE-STAMPED, and that is not a second colour authority. TurnVerdictRowStamp
                // ran over this row before this stamp did, so the row in hand still carries the verdict the read
                // is about to replace; the re-stamp is the SAME rule, applied again to the fact that changed
                // underneath it, through the one method that owns what "reading" looks like on a row.
                //
                // A STOP THE SEAT WILL NOT JUDGE KEEPS ITS RED - the judge switch is off for this account, its
                // ceiling is full, or the free checks refuse the session. Painting that row yellow would trade
                // one lie for another: nothing is reading it, and nothing is going to.
                if (_requestRead?.Invoke(tenant, s.DirectorId ?? "", s.SessionId) == true)
                    TurnVerdictRowStamp.Reading(s);
            }
            return;
        }
    }

    /// <summary>The cause word for one outcome. EVERY case is named: a fall-through arm would silently give a new
    /// outcome somebody else's word, which is exactly the shape of defect this file has already been caught by.
    ///
    /// AN EXPIRY WITH NO KNOWN ARMING MOMENT IS A RE-JUDGE AND CARRIES THE RE-JUDGE'S WORD, deliberately. It is
    /// not a fifth cause: the ledger says WHAT WAS DONE about the expiry, and what was done is that the judge was
    /// asked about the current screen - the same thing, for the same reason, as any other stop nothing had
    /// judged. Why the arming moment was missing is the fold's business, not the owner's.</summary>
    private static string CauseOf(SnoozeExpiryOutcome outcome) => outcome switch
    {
        SnoozeExpiryOutcome.NothingNew => ActivityCauses.SnoozeNothingNew,
        SnoozeExpiryOutcome.VerdictRules => ActivityCauses.SnoozeVerdictRules,
        SnoozeExpiryOutcome.ReadRequested => ActivityCauses.SnoozeReJudgeRequested,
        SnoozeExpiryOutcome.ReadInFlight => ActivityCauses.SnoozeReadInFlight,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome,
            "A snooze expiry outcome with no cause word reached the ledger."),
    };

    /// <summary>
    /// PRUNE TO THE ROSTER, UNCONDITIONALLY - ONCE A CALLER HAS NAMED ONE. A watch entry for a session the
    /// account does not have has nothing to watch, so it goes, whatever else is or is not in that roster. This
    /// memory is then bounded by the account's sessions and never by how long the Gateway has been up; a session
    /// that vanished while its snooze was expired used to leave one entry behind for the life of the process, and
    /// those accumulate (the inspector's second note on pull request 2899).
    ///
    /// AN EMPTY ROSTER IS A ROSTER, and it is the case worth naming because it is the one an earlier version got
    /// wrong. "The account has no sessions" prunes everything for the account. There is no early return for it,
    /// no floor of one session per Director, and nothing kept because the Director it belonged to happens to be
    /// absent too - each of those was a condition that let the memory grow, which is exactly what this exists to
    /// stop.
    ///
    /// A PARTIAL VIEW NEVER REACHES HERE, and that is the other half of the same principle. A roster read
    /// filtered by machine or by Director, and any other caller looking at part of the account, passes no roster
    /// at all and prunes nothing: such a view cannot tell "this session is gone" from "this session is not in the
    /// part I am looking at", and a destructive decision may not be taken on a distinction the caller cannot
    /// make. The whole-account callers - the unfiltered roster read, the single-session read, and the display
    /// push, which names the account's roster from the pushed-session store rather than from its own rows - are
    /// the only ones that prune.
    ///
    /// WHAT IT COSTS WHEN A SESSION TRULY LEAVES THE ACCOUNT AND COMES BACK. Its entry went with it, so nothing
    /// says when its snooze was seen armed - and UNKNOWN IS RED (see <see cref="SnoozeExpiryDecision.AtExpiry"/>),
    /// so the expiry ASKS rather than claiming anything about a stretch of quiet it cannot see the start of. One
    /// extra read, at the colour the row already had, and the edge then settles: never a quietened question. The
    /// DIRECTION of that failure is the whole point - a bounded memory is worth an occasional repeated read, and
    /// is not worth a real ask going silent.
    ///
    /// AND THE ARMING MOMENT ITSELF LIVES IN PROCESS MEMORY, which is the one gap this class cannot close. With
    /// a partial view no longer pruning, THREE things can leave an expiry without it, and all three answer the
    /// same way - by asking:
    ///
    ///   A GATEWAY RESTART. The arming observation lives in PROCESS MEMORY, so a Gateway that restarts while a
    ///   snooze runs has no record of when that clock started.
    ///
    ///   A SESSION THAT GENUINELY LEFT THE ACCOUNT AND CAME BACK. Its entry was pruned because the account
    ///   really did not have it, and a returning session is a clock nobody watched start.
    ///
    ///   A NEW CLOCK NOBODY SAW ARMED. A re-snooze that runs out with no fold in between - the snooze endpoint's
    ///   display push is BEST EFFORT, so nothing guarantees one - is a deadline this memory never observed, and
    ///   the entry it finds belongs to the previous clock.
    ///
    /// None of them can quieten a real ask, which is the property that matters; each costs one read at the colour
    /// the row already had.
    ///
    /// THE FIX IS DEFERRED, with its cost named: the arming moment belongs ON THE SNOOZE, beside the deadline,
    /// where it survives a restart instead of being re-derived from whoever looked first. That is a schema change
    /// and a migration, which is why it is a follow-up of this mission and not this slice. Until it lands, a
    /// restart costs one read per snoozed session at its expiry.
    ///
    /// <see cref="SnoozeExpiryReJudgeTests.ASessionThatTrulyLeavesTheAccount_AndComesBack_IsAskedOnceMore_NeverCalmed"/>
    /// and <see cref="SnoozeExpiryReJudgeTests.AnAbsenceDuringTheSnooze_MakesNoDifferenceToWhatTheExpiryDoes"/>
    /// are the assertions of this paragraph. The sentences and the tests point at each other on purpose: a claim
    /// written beside code that nothing checks is where the next reader stops being sceptical.
    /// </summary>
    private void PruneToRoster(TenantId tenant, IReadOnlyCollection<string> rosterSessionIds)
    {
        foreach (var key in _watch.Keys)
        {
            if (key.Tenant != tenant || rosterSessionIds.Contains(key.SessionId)) continue;
            _watch.TryRemove(key, out _);
        }
    }

}

/// <summary>
/// Stamps <see cref="SessionDto.SnoozeEndedNothingNew"/> onto a roster, for the fold in
/// <c>GatewayEndpoints.StampFleetRolesAndFold</c>. The shape of <see cref="TurnVerdictRowStamp"/>, and for the
/// same reason: every field is assigned on every row, and an account with nothing on the wire is stamped false
/// rather than left alone.
/// </summary>
public static class SnoozeExpiryRowStamp
{
    /// <param name="rows">The rows to stamp.</param>
    /// <param name="watch">The fold's snooze memory. Null (a diagnostics page, an older test) stamps false.</param>
    /// <param name="verdictsOnTheWire">Whether <see cref="TurnVerdictRowStamp"/> put this account's verdicts on
    /// these rows. False means the colour switch is off, and then this slice says nothing at all: its inputs are
    /// the stamped verdicts, and its output is a colour, and neither belongs to an account in shadow.</param>
    /// <param name="holds">The fold's ONE snooze snapshot. Never a second read.</param>
    /// <param name="tenant">The account the rows belong to. Null or invalid stamps false.</param>
    /// <param name="nowUtc">The fold's single moment.</param>
    /// <param name="rosterSessionIds">The ACCOUNT'S whole roster as session ids, or null from a caller looking at
    /// only part of the account - and then nothing is pruned. See <see cref="SnoozeExpiryReJudge.Observe"/>.</param>
    public static void Stamp(
        IReadOnlyList<SessionDto> rows,
        SnoozeExpiryReJudge? watch,
        bool verdictsOnTheWire,
        Snooze.SnoozeHoldSnapshot holds,
        TenantId? tenant,
        DateTime nowUtc,
        IReadOnlyCollection<string>? rosterSessionIds = null,
        bool writes = true)
    {
        ArgumentNullException.ThrowIfNull(rows);

        if (watch is null || !verdictsOnTheWire || tenant is not { IsValid: true } account)
        {
            foreach (var s in rows) s.SnoozeEndedNothingNew = false;
            return;
        }

        if (writes) watch.Observe(account, rows, holds, nowUtc, rosterSessionIds);
        else watch.Peek(account, rows, holds, nowUtc);
    }
}
