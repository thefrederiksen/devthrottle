using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>A verdict found by its id, the session it belongs to, when the owner's answer to it was confirmed (null while
/// it is unanswered), and what that answer was.</summary>
public sealed record TurnVerdictLocated(string SessionId, TurnVerdictDto Verdict, DateTime? AnsweredAtUtc = null,
    TurnVerdictStoredAnswer? Answer = null);

/// <summary>
/// What the answer route wrote into a session for one verdict, stored with the verdict when the Director confirmed it:
/// the verdict it answered (its id and the moment its turn ended), the option positions chosen, in the order chosen,
/// and the words those options are - the options' keys joined in that order, or <see cref="TypedReplyWords"/> for the
/// confirm of a typed reply. The walkthrough records exactly this, never positions a client names.
/// </summary>
public sealed record TurnVerdictStoredAnswer(string VerdictId, DateTime TurnEndObservedAtUtc, IReadOnlyList<int> OptionIndexes,
    string Words)
{
    /// <summary>The words of the one answer that chooses no option: the confirm of a reply typed on the screen.</summary>
    public const string TypedReplyWords = "Sent the reply typed on the screen.";

    /// <summary>The stored answer for these positions of this verdict. The positions must already be a selection the
    /// verdict allows (<see cref="TurnVerdictActivation.Plan"/>).</summary>
    public static TurnVerdictStoredAnswer For(TurnVerdictDto verdict, IReadOnlyList<int> indexes)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(indexes);
        var words = indexes.Count == 0
            ? TypedReplyWords
            : string.Join(", ", indexes.Select(i => verdict.Options[i].Key));
        return new TurnVerdictStoredAnswer(verdict.VerdictId, verdict.TurnEndObservedAtUtc, indexes.ToList(), words);
    }
}

/// <summary>
/// One stored verdict of a session's history, with the moment the owner's answer to it was confirmed (null while it
/// is unanswered) and the answer itself.
///
/// WHY THE MOMENT DOES NOT RIDE ON <see cref="TurnVerdictDto"/>. That record is the JUDGE's answer, serialised when
/// the judge answered; being answered happens long afterwards and lives in the row's own column. A field on the
/// serialised answer would read null on every route that does not stamp it, which is indistinguishable from "nobody
/// has answered this" - so the fact travels beside the answer rather than inside it.
/// </summary>
/// <param name="Answer">What he answered, as the answer route stored it - null when this stop was not answered
/// through that route, which includes every reply typed straight into the session. There is ONE record of the
/// owner's answer and this is it; the Wingman tab reads it rather than keeping a second one of its own.</param>
public sealed record AnsweredTurnVerdict(TurnVerdictDto Verdict, DateTime? AnsweredAtUtc,
    TurnVerdictStoredAnswer? Answer = null);

/// <summary>
/// One located session's screen and keyboard, as the answer route needs them. The route binds it to the session in
/// its path (the owning Director, over the tunnel); a test binds it to a fake that records every write.
/// </summary>
public interface ITurnVerdictAnswerChannel
{
    /// <summary>Read the session's live screen grid. Null when it cannot be read.</summary>
    Task<ScreenGridResponse?> ReadScreenAsync(CancellationToken ct);

    /// <summary>The prompt route's menu guard, unchanged: true when a model-confirmed menu owns the screen, so a
    /// reply's trailing Enter would press whatever option the picker has highlighted.</summary>
    Task<bool> MenuOwnsScreenAsync(CancellationToken ct);

    /// <summary>Write into the session through the existing prompt send. <paramref name="appendEnter"/> false is the
    /// raw write: exactly <paramref name="text"/>, byte for byte, and nothing after it.</summary>
    Task<TurnVerdictAnswerWrite> WriteAsync(string text, bool appendEnter, CancellationToken ct);
}

/// <summary>What became of a write - the three outcomes the prompt send keeps apart.</summary>
public enum TurnVerdictAnswerWriteKind
{
    /// <summary>The Director confirmed the write.</summary>
    Accepted,
    /// <summary>Nothing left the Gateway: the owning Director is not connected.</summary>
    NeverLeftTheGateway,
    /// <summary>The write went out and the Director did not confirm it.</summary>
    Unanswered,
}

/// <summary>A write's outcome and the transport's own words for it.</summary>
public sealed record TurnVerdictAnswerWrite(TurnVerdictAnswerWriteKind Kind, string Detail);

/// <summary>Where the answer route finds verdicts and records what it did.</summary>
public interface ITurnVerdictAnswerRecords
{
    /// <summary>The verdict with this id in this tenant, and its session; null when there is none.</summary>
    TurnVerdictLocated? FindVerdict(TenantId tenant, string verdictId);

    /// <summary>This session's latest verdict in this tenant.</summary>
    TurnVerdictDto? Latest(TenantId tenant, string sessionId);

    /// <summary>Record that this verdict's answer was written and confirmed, and what it was. False when it was already
    /// answered or is not found.</summary>
    bool MarkAnswered(TenantId tenant, TurnVerdictStoredAnswer answer);

    /// <summary>One ledger line for one activation.</summary>
    void Record(TurnVerdictRecord record);
}

/// <summary>The production records: the verdict store, and the turn-verdict ledger writer.</summary>
public sealed class TurnVerdictAnswerRecords : ITurnVerdictAnswerRecords
{
    private readonly TurnVerdictStore _store;
    private readonly Action<TurnVerdictRecord> _record;

    public TurnVerdictAnswerRecords(TurnVerdictStore store, Action<TurnVerdictRecord> record)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public TurnVerdictLocated? FindVerdict(TenantId tenant, string verdictId) => _store.FindById(tenant, verdictId);

    public TurnVerdictDto? Latest(TenantId tenant, string sessionId) => _store.Latest(tenant, sessionId);

    public bool MarkAnswered(TenantId tenant, TurnVerdictStoredAnswer answer) => _store.MarkAnswered(tenant, answer, DateTime.UtcNow);

    public void Record(TurnVerdictRecord record) => _record(record);
}

/// <summary>How an answer ended: accepted or not, the HTTP status the route answers with, the closed cause word,
/// and the sentence the owner is shown.</summary>
public sealed record TurnVerdictAnswerOutcome(bool Accepted, int StatusCode, string Code, string Reason);

/// <summary>What an accepted selection writes: the bytes, whether the prompt send appends Enter, and whether the
/// menu guard applies (it does for a reply, exactly as it does on the prompt route).</summary>
public sealed record TurnVerdictActivationPlan(string Text, bool AppendEnter, bool IsReply);

/// <summary>
/// The pure half of an activation: given a verdict and the chosen option indexes, the exact bytes to write, or the
/// sentence saying why this selection is not one the verdict allows. No screen, no send, no clock - so every rule
/// of <c>inspections/RULING-slice-A-round3.md</c> is testable by value.
/// </summary>
public static class TurnVerdictActivation
{
    /// <summary>The one Enter a reply option is followed by. A keys answer never gets it beyond its own submit.</summary>
    public const string Enter = "\r";

    /// <summary>
    /// Plan the write, or refuse. Exactly one of the two results is non-null.
    ///
    /// - <c>reply</c>: exactly one index; the option's <c>send</c>, then one Enter through the prompt send.
    /// - <c>keys</c>: <c>single</c> takes exactly one index, <c>multiple</c> one or more; the chosen options'
    ///   <c>send</c> bytes in the order given, then <c>menu.submit</c>, as ONE raw write.
    /// - An EMPTY list only in the parked-reply shape: <c>keys</c>, a menu, <c>single</c>, <c>submit</c> "\r",
    ///   zero options. Then <c>submit</c> alone is written.
    /// - An index out of range or chosen twice refuses the whole selection.
    /// </summary>
    public static (TurnVerdictActivationPlan? Plan, string? Refusal) Plan(TurnVerdictDto verdict, IReadOnlyList<int> indexes)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        ArgumentNullException.ThrowIfNull(indexes);
        var options = verdict.Options ?? new List<TurnVerdictOptionDto>();

        var seen = new HashSet<int>();
        foreach (var index in indexes)
        {
            if (index < 0 || index >= options.Count)
                return (null, $"Option {index} is not one of this verdict's {options.Count} options, so nothing was sent.");
            if (!seen.Add(index))
                return (null, $"Option {index} was chosen twice, so nothing was sent.");
        }

        if (verdict.AnswerVia == "reply")
        {
            if (verdict.Menu is not null)
                return (null, "This verdict is a reply with a menu, which no answer can carry out, so nothing was sent.");
            if (indexes.Count != 1)
                return (null, "A reply takes exactly one option, so nothing was sent.");
            return (new TurnVerdictActivationPlan(options[indexes[0]].Send, AppendEnter: true, IsReply: true), null);
        }

        if (verdict.AnswerVia == "keys")
        {
            var menu = verdict.Menu;
            if (menu is null)
                return (null, "This verdict answers with keys and has no menu, so nothing was sent.");
            if (menu.Submit is not ("" or Enter))
                return (null, "This verdict's menu has a confirm key the answer route does not send, so nothing was sent.");

            if (indexes.Count == 0)
            {
                // THE PARKED REPLY, the one shape an empty list is accepted in: the person has typed a reply and
                // not sent it, so there is nothing to toggle and only something to confirm.
                if (options.Count == 0 && menu.SelectionMode == "single" && menu.Submit == Enter)
                    return (new TurnVerdictActivationPlan(Enter, AppendEnter: false, IsReply: false), null);
                return (null, "No option was chosen, so nothing was sent.");
            }

            switch (menu.SelectionMode)
            {
                case "single" when indexes.Count != 1:
                    return (null, "This menu takes exactly one option, so nothing was sent.");
                case "single":
                case "multiple":
                    break;
                default:
                    return (null, "This verdict's menu has a selection mode the answer route does not know, so nothing was sent.");
            }

            var text = string.Concat(indexes.Select(i => options[i].Send)) + menu.Submit;
            return (new TurnVerdictActivationPlan(text, AppendEnter: false, IsReply: false), null);
        }

        return (null, "This verdict's answer shape is not one the answer route can carry out, so nothing was sent.");
    }
}

/// <summary>
/// THE ONE SERVER-OWNED WRITE PATH FOR A VERDICT'S OPTIONS (the Wingman-on-every-turn mission, slice E; ruling 12).
/// An option button is the owner typing, not the Wingman: the Wingman never types, and this is the only place a
/// verdict's option bytes are written into a session.
///
/// What it binds, in order: the verdict to the session in the path (a verdict from another session in the same
/// account is refused); the verdict to its session's LATEST verdict; the selection to what the verdict allows; and,
/// under ONE lock per session, the verdict still unanswered, the live screen to the verdict's screen by the
/// canonical full-grid hash (ruling 14), then the write and the answered mark, which stores exactly what was chosen. ONE VERDICT, ONE ACTIVATION: an
/// answer that waited behind an accepted one finds the verdict answered and is refused before it reads the screen,
/// whether or not the first write has repainted it yet. A multiple-select is one write that its own first toggle
/// can never invalidate.
///
/// Every activation writes exactly one ledger line, accepted or refused. The line carries control flow only: the
/// verdict id, the answer shape, how many options - never the bytes and never the screen.
/// </summary>
public sealed class TurnVerdictAnswerService
{
    /// <summary>What the owner is shown when the bytes were written.</summary>
    public const string SentReason = "Sent to the session.";

    /// <summary>The refusal when the screen is not the one the verdict was formed on.</summary>
    public const string ScreenChangedReason =
        "The screen has changed since the Wingman read it, so nothing was sent. Look at the session again.";

    /// <summary>The refusal when the verdict has already been answered once.</summary>
    public const string AlreadyAnsweredReason =
        "That stop has already been answered, so nothing was sent. Look at the session again.";

    private readonly ITurnVerdictAnswerRecords _records;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public TurnVerdictAnswerService(ITurnVerdictAnswerRecords records)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
    }

    /// <summary>
    /// Record a refusal the route made before this service could be asked - an unreadable body, or a session that
    /// is not in the caller's account - so that every activation, whoever refused it, leaves its ledger line.
    /// </summary>
    public TurnVerdictAnswerOutcome RefuseBeforeLookup(TenantId tenant, string directorId, string sessionId,
        string verdictId, int statusCode, string cause, string reason)
    {
        FileLog.Write($"[TurnVerdictAnswerService] RefuseBeforeLookup: sid={sessionId} verdict={verdictId} cause={cause}");
        return Refuse(tenant, directorId, sessionId, verdictId, statusCode, cause, reason, "");
    }

    /// <summary>
    /// Record an activation that threw part-way. Whether anything was written is not known, so it is recorded as
    /// unconfirmed - never as a refusal, which would promise that nothing reached the session.
    /// </summary>
    public TurnVerdictAnswerOutcome RecordUnconfirmed(TenantId tenant, string directorId, string sessionId,
        string verdictId, string detail)
    {
        _records.Record(new TurnVerdictRecord(tenant, directorId, sessionId,
            ActivityEventTypes.TurnVerdictAnswerUnconfirmed, ActivityCauses.Unknown,
            $"verdict={(verdictId.Length == 0 ? "none" : verdictId)}"));
        FileLog.Write($"[TurnVerdictAnswerService] RecordUnconfirmed: sid={sessionId} verdict={verdictId}: {detail}");
        return new TurnVerdictAnswerOutcome(false, 502, ActivityCauses.Unknown,
            $"The answer failed part-way and it is not known whether anything reached the session: {detail}");
    }

    /// <summary>
    /// Answer a verdict. The caller has already located <paramref name="sessionId"/> inside
    /// <paramref name="tenant"/> and bound <paramref name="channel"/> to it.
    /// </summary>
    public async Task<TurnVerdictAnswerOutcome> AnswerAsync(TenantId tenant, string directorId, string sessionId,
        TurnVerdictAnswerRequest? request, ITurnVerdictAnswerChannel channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var verdictId = request?.VerdictId?.Trim() ?? "";
        FileLog.Write($"[TurnVerdictAnswerService] AnswerAsync: sid={sessionId} verdict={verdictId} " +
                      $"indexes={(request?.OptionIndexes is null ? "null" : request.OptionIndexes.Count.ToString())}");

        if (request is null || verdictId.Length == 0)
            return Refuse(tenant, directorId, sessionId, verdictId, 400, ActivityCauses.AnswerMalformed,
                "The answer did not say which verdict it answers, so nothing was sent.", "");
        if (request.OptionIndexes is null)
            return Refuse(tenant, directorId, sessionId, verdictId, 400, ActivityCauses.AnswerMalformed,
                "The answer carried no option list, so nothing was sent.", "");
        var indexes = request.OptionIndexes;

        // ---- THE JOIN: the verdict must be one of THIS session's ----
        var located = _records.FindVerdict(tenant, verdictId);
        if (located is null || !string.Equals(located.SessionId, sessionId, StringComparison.Ordinal))
            return Refuse(tenant, directorId, sessionId, verdictId, 404, ActivityCauses.AnswerVerdictNotFound,
                "That verdict is not one of this session's, so nothing was sent.", "");
        var verdict = located.Verdict;

        if (verdict.Failed)
            return Refuse(tenant, directorId, sessionId, verdictId, 409, ActivityCauses.AnswerVerdictFailed,
                "That verdict was refused when it was judged and has nothing to carry out, so nothing was sent.", Shape(verdict, indexes));

        var latest = _records.Latest(tenant, located.SessionId);
        if (latest is null || !string.Equals(latest.VerdictId, verdictId, StringComparison.Ordinal))
            return Refuse(tenant, directorId, sessionId, verdictId, 409, ActivityCauses.AnswerVerdictSuperseded,
                "A newer verdict has replaced that one, so nothing was sent. Look at the session again.", Shape(verdict, indexes));

        var (plan, refusal) = TurnVerdictActivation.Plan(verdict, indexes);
        if (plan is null)
            return Refuse(tenant, directorId, sessionId, verdictId, 400, ActivityCauses.AnswerSelectionRefused,
                refusal!, Shape(verdict, indexes));

        // ---- ONE LOCK: the screen compare and the write ----
        var gate = _locks.GetOrAdd($"{tenant}/{sessionId}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read again INSIDE the lock, from the stored record the accepted write marks. Checked before the screen,
            // because the screen is exactly what cannot be trusted here: the first write may not have repainted it.
            if (_records.FindVerdict(tenant, verdictId)?.AnsweredAtUtc is not null)
                return Refuse(tenant, directorId, sessionId, verdictId, 409, ActivityCauses.AnswerAlreadyAnswered,
                    AlreadyAnsweredReason, Shape(verdict, indexes));

            var grid = await channel.ReadScreenAsync(ct).ConfigureAwait(false);
            if (grid is null || !grid.HasGrid || grid.Rows is null || grid.Rows.Count == 0)
                return Refuse(tenant, directorId, sessionId, verdictId, 409, ActivityCauses.AnswerScreenUnreadable,
                    "The session's screen could not be read, so nothing was sent.", Shape(verdict, indexes));

            var hash = WingmanScreenVerdictCache.HashRows(grid.Rows);
            if (verdict.ScreenHash.Length == 0 || !string.Equals(verdict.ScreenHash, hash, StringComparison.Ordinal))
                return Refuse(tenant, directorId, sessionId, verdictId, 409, ActivityCauses.AnswerScreenChanged,
                    ScreenChangedReason, Shape(verdict, indexes));

            if (plan.IsReply && await channel.MenuOwnsScreenAsync(ct).ConfigureAwait(false))
                return Refuse(tenant, directorId, sessionId, verdictId, 409, ActivityCauses.MenuOwnsScreen,
                    "A menu owns the session's screen, so the reply was not typed and no Enter was pressed.", Shape(verdict, indexes));

            var write = await channel.WriteAsync(plan.Text, plan.AppendEnter, ct).ConfigureAwait(false);
            switch (write.Kind)
            {
                case TurnVerdictAnswerWriteKind.Accepted:
                    // Marked before the lock is released, so the next waiter reads it. A false here cannot come from
                    // a racing answer in this Gateway - they are all behind this lock - so it is logged, not hidden.
                    if (!_records.MarkAnswered(tenant, TurnVerdictStoredAnswer.For(verdict, indexes)))
                        FileLog.Write($"[TurnVerdictAnswerService] AnswerAsync: sid={sessionId} verdict={verdictId} was written but could not be marked answered (already marked or no longer stored)");
                    _records.Record(new TurnVerdictRecord(tenant, directorId, sessionId,
                        ActivityEventTypes.TurnVerdictAnswered, ActivityCauses.OwnerAnswered,
                        $"verdict={verdictId} {Shape(verdict, indexes)}"));
                    FileLog.Write($"[TurnVerdictAnswerService] AnswerAsync: sid={sessionId} verdict={verdictId} SENT {Shape(verdict, indexes)}");
                    return new TurnVerdictAnswerOutcome(true, 200, ActivityCauses.OwnerAnswered, SentReason);
                case TurnVerdictAnswerWriteKind.NeverLeftTheGateway:
                    return Refuse(tenant, directorId, sessionId, verdictId, 502, ActivityCauses.AnswerNeverSent,
                        "The session's Director is not connected, so nothing was sent.", Shape(verdict, indexes));
                default:
                    _records.Record(new TurnVerdictRecord(tenant, directorId, sessionId,
                        ActivityEventTypes.TurnVerdictAnswerUnconfirmed, ActivityCauses.AnswerUnanswered,
                        $"verdict={verdictId} {Shape(verdict, indexes)}"));
                    FileLog.Write($"[TurnVerdictAnswerService] AnswerAsync: sid={sessionId} verdict={verdictId} UNCONFIRMED: {write.Detail}");
                    return new TurnVerdictAnswerOutcome(false, 502, ActivityCauses.AnswerUnanswered,
                        $"The answer went to the session's Director and it did not confirm it: {write.Detail}");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private TurnVerdictAnswerOutcome Refuse(TenantId tenant, string directorId, string sessionId, string verdictId,
        int statusCode, string cause, string reason, string shape)
    {
        _records.Record(new TurnVerdictRecord(tenant, directorId, sessionId,
            ActivityEventTypes.TurnVerdictAnswerRefused, cause,
            $"verdict={(verdictId.Length == 0 ? "none" : verdictId)}{(shape.Length == 0 ? "" : " " + shape)}"));
        FileLog.Write($"[TurnVerdictAnswerService] refused: sid={sessionId} verdict={verdictId} cause={cause}");
        return new TurnVerdictAnswerOutcome(false, statusCode, cause, reason);
    }

    /// <summary>Control flow only, for the ledger: never the bytes.</summary>
    private static string Shape(TurnVerdictDto verdict, IReadOnlyList<int> indexes)
        => $"answerVia={verdict.AnswerVia} mode={verdict.Menu?.SelectionMode ?? "none"} chosen={indexes.Count}";
}
