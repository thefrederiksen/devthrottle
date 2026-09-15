namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One judged stop: what the Wingman said this turn end MEANS, kept per tenant and per session (the
/// Wingman-on-every-turn mission, slice B). The judged answer itself is held as the serialized
/// <c>TurnVerdictDto</c> in <see cref="VerdictJson"/> - the bulky sub-document in one column, the same
/// shape the cron and session-rule tables use - and the few fields a query needs are columns beside it.
///
/// WHY (tenant, session, judged-at) IS THE KEY. A session is judged again at every stop, so the row is
/// one per STOP rather than one per session, and the history is what makes a wrong verdict answerable
/// afterwards. The tenant leads the key because the session id is caller-supplied: it arrives on the push
/// stream from a Director, so a key that did not lead with the tenant would let one account squat an id
/// for every other account.
///
/// <see cref="TurnEndObservedAtUtc"/> IS THE JOIN KEY, and it is not the same fact as
/// <see cref="JudgedAtUtc"/>. The first is when the detector SAW the turn end; the second is when the
/// judge answered, which can be many seconds later and varies with how busy the model is. The turn log
/// records the same observed moment (<c>turn_end_observed_at_utc</c> on its moment block), so a verdict
/// row and the captured turn it was formed on can be matched exactly - by (account, session, observed
/// moment) - rather than by looking for the nearest timestamp, which is a guess that gets worse the
/// slower the judge is.
///
/// A REFUSED ANSWER IS STILL A ROW. <see cref="Failed"/> with a <see cref="FailureReason"/> records that
/// the judge answered and the contract rejected it, which is a fact worth holding: it is how a broken
/// prompt or a drifting model is noticed. The session row stays exactly as the detector left it in that
/// case - silence is never a decision - and the reader must treat <see cref="VerdictJson"/> on a failed
/// row as carrying no verdict word.
/// </summary>
public sealed class TurnVerdictEntity : TenantScopedEntity
{
    /// <summary>The session this stop belongs to.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>When the judge answered (UTC). Part of the key, so a second judgement of the same session
    /// is a new row rather than an overwrite.</summary>
    public DateTime JudgedAtUtc { get; set; }

    /// <summary>This verdict's own identity, minted in code. Carried as a column as well as inside the
    /// serialized answer so an option activation can be bound to the exact verdict it came from without
    /// deserializing every row to find it.</summary>
    public string VerdictId { get; set; } = "";

    /// <summary>When the DETECTOR observed the turn end (UTC) - the moment being judged. See the note on
    /// the type: this is the join key into the turn log, not <see cref="JudgedAtUtc"/>.</summary>
    public DateTime TurnEndObservedAtUtc { get; set; }

    /// <summary>The full-grid fingerprint of the screen this verdict was formed on. A repaint changes it,
    /// which is what makes a stale option tap refusable.</summary>
    public string ScreenHash { get; set; } = "";

    /// <summary>True when the judge's answer was refused by the contract.</summary>
    public bool Failed { get; set; }

    /// <summary>Why the answer was refused, in plain words. Null when <see cref="Failed"/> is false.</summary>
    public string? FailureReason { get; set; }

    /// <summary>The whole answer as serialized <c>TurnVerdictDto</c> JSON. The clients render it verbatim,
    /// so it is stored whole rather than spread over columns that would have to be folded back together.</summary>
    public string VerdictJson { get; set; } = "";

    /// <summary>When the owner's answer to this verdict was written into the session and the Director confirmed
    /// it (UTC). Null until then. Set once, inside the answer route's per-session lock, so an answer that waited
    /// behind an accepted one finds the verdict answered and sends nothing: one verdict, one activation, whatever
    /// the screen does after the first write.</summary>
    public DateTime? AnsweredAtUtc { get; set; }
}

/// <summary>
/// One report that a verdict was WRONG, made by the person the verdict was about (slice G of the
/// Wingman-on-every-turn mission writes it; the table lands in slice B so there is one migration for the
/// pair rather than two).
///
/// It carries the same <see cref="TurnEndObservedAtUtc"/> join key as the verdict row, so a correction
/// reaches the labelled corpus as an owner label against the exact stop it is about.
///
/// KEYED ON (tenant, verdict id), so a second report about the same verdict REPLACES the first rather
/// than adding a second opinion from the same person about the same stop. Somebody who corrects a verdict
/// and then corrects the correction means the second one; two rows would make the corpus have to guess
/// which, and a corpus that guesses is worse than one that is smaller.
///
/// NOTHING WRITES THIS TABLE YET. That is deliberate and is stated here as a gap rather than left to be
/// inferred from an empty table: slice B creates the schema and nothing else, so an empty table in a
/// running Gateway is the expected state until slice G lands, not evidence that reporting is broken.
/// </summary>
public sealed class TurnVerdictFeedbackEntity : TenantScopedEntity
{
    /// <summary>The verdict being corrected.</summary>
    public string VerdictId { get; set; } = "";

    /// <summary>The session the corrected verdict was about.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The detector's observed moment for the stop being corrected - the join key into the turn
    /// log and the labelled corpus.</summary>
    public DateTime TurnEndObservedAtUtc { get; set; }

    /// <summary>When the correction was made (UTC).</summary>
    public DateTime ReportedAtUtc { get; set; }

    /// <summary>The verdict word the person says was right - one of the shared vocabulary's words. Held as
    /// the word rather than as a code so a corpus row and a live verdict read the same.</summary>
    public string CorrectedVerdict { get; set; } = "";

    /// <summary>What the person added in their own words, when they added anything. Null otherwise.</summary>
    public string? Note { get; set; }
}
