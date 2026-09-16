using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// "This verdict was wrong" - the rules (the Wingman-on-every-turn mission, slice G).
///
/// WHAT THIS IS FOR. The Wingman's verdicts are graded against a labelled corpus, and until now every label in
/// that corpus came from two reviewers reading a record afterwards. The one reader who KNOWS is the person whose
/// session it was, and he had nowhere to say so: a wrong verdict was something he noticed, shrugged at, and
/// answered. This route is the shortest path from "that was wrong" to a label the grader counts, and the label it
/// writes outranks two reviewers agreeing, because agreement between readers is evidence and the owner's own
/// reading of his own session is closer to fact than that.
///
/// THE RULES, AND WHY EACH ONE IS HERE:
///
///  - THE VERDICT MUST BE ONE OF THE SESSION IN THE PATH. The store finds a verdict by id inside the account; the
///    join to the session is made HERE, and made explicitly, so it can be tested and so it can be removed in a
///    revert proof and watched to fail. Without it, a caller holding one account's session and any verdict id in
///    that account could attach a correction to a stop it has nothing to do with, and the corpus would then hold
///    a label about a screen nobody looked at.
///  - A VERDICT ALREADY SUPERSEDED IS STILL REPORTABLE, and that is the ordinary case rather than an edge. The
///    owner answers a red row, the answer puts the session to work, the working transition supersedes the verdict
///    - and only THEN does he think "that was never a question, it was telling me it was done". A rule that only
///    accepted a live verdict would refuse almost every real report.
///  - THE WORD MUST BE FROM THE SHARED VOCABULARY. A label the grader cannot compare is worse than no label: the
///    whole reason the verdict is one closed word rather than a sentence is that two readings have to be
///    comparable mechanically. An unknown word is refused, never stored and never mapped to a near one.
///  - A SECOND REPORT REPLACES THE FIRST. One person correcting one stop twice means the second one. Two rows
///    would make the corpus decide which, and a corpus that decides is a corpus that guesses.
///
/// WHAT IT DELIBERATELY DOES NOT DO. It does not judge whether the owner is right, does not touch the verdict it
/// is about, and does not change any colour on any screen. The verdict that was wrong stays exactly as it was
/// stored - the record of what the Wingman said is evidence, and evidence that gets edited when somebody
/// disagrees with it is not evidence any more.
/// </summary>
public sealed class TurnVerdictFeedbackService
{
    private readonly TurnVerdictStore _store;
    private readonly Func<DateTime> _now;

    /// <param name="store">The durable record. Taken directly rather than behind a seam: every rule below is
    /// about rows, so a test that used a fake store would be testing the fake.</param>
    /// <param name="now">The clock, so a test can name the reported-at moment it expects.</param>
    public TurnVerdictFeedbackService(TurnVerdictStore store, Func<DateTime>? now = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Record that the verdict named in <paramref name="request"/> was wrong about the stop of
    /// <paramref name="sessionId"/>, or say why it was not recorded. Exactly one row is written on an accepted
    /// report, and nothing at all is written on a refused one.
    /// </summary>
    public TurnVerdictFeedbackOutcome Report(TenantId tenant, string sessionId, TurnVerdictFeedbackRequest? request)
    {
        var verdictId = request?.VerdictId?.Trim() ?? "";
        var word = request?.CorrectVerdict?.Trim() ?? "";

        if (request is null || verdictId.Length == 0)
            return Refuse(sessionId, verdictId, StatusCodes400, TurnVerdictFeedbackCodes.Malformed,
                "The report named no verdict, so nothing was recorded.");

        if (word.Length == 0)
            return Refuse(sessionId, verdictId, StatusCodes400, TurnVerdictFeedbackCodes.Malformed,
                "The report named no verdict word, so nothing was recorded.");

        // THE VOCABULARY IS CHECKED BEFORE THE LOOKUP, so a caller probing verdict ids with a nonsense word
        // learns nothing about which ids exist.
        if (!TurnVerdictVocabulary.AllVerdicts.Contains(word, StringComparer.Ordinal))
            return Refuse(sessionId, verdictId, StatusCodes400, TurnVerdictFeedbackCodes.UnknownVerdict,
                $"\"{word}\" is not one of the words a verdict can be, so nothing was recorded. The words are: "
                + string.Join(", ", TurnVerdictVocabulary.AllVerdicts) + ".");

        var located = _store.FindById(tenant, verdictId);

        // THE JOIN. One answer for "this account holds no such verdict" and "it belongs to another of its
        // sessions": telling the two apart would say which verdict ids exist on sessions the caller is not
        // looking at, and the correction is meaningless either way.
        if (located is null || !string.Equals(located.SessionId, sessionId, StringComparison.Ordinal))
            return Refuse(sessionId, verdictId, StatusCodes404, TurnVerdictFeedbackCodes.VerdictNotFound,
                "That verdict is not one of this session's, so nothing was recorded.");

        var replaced = _store.FeedbackFor(tenant, verdictId) is not null;
        _store.RecordFeedback(tenant, verdictId, sessionId, located.Verdict.TurnEndObservedAtUtc, word,
            string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(), _now());

        FileLog.Write(
            $"[TurnVerdictFeedbackService] recorded: sid={sessionId} verdict={verdictId} corrected={word} "
            + $"replaced={replaced}");

        return new TurnVerdictFeedbackOutcome(true, StatusCodes200, TurnVerdictFeedbackCodes.Recorded,
            replaced
                ? "Your earlier report on this verdict was replaced by this one."
                : "Recorded. This stop will be graded against your word, not the Wingman's.",
            verdictId, replaced);
    }

    private static TurnVerdictFeedbackOutcome Refuse(
        string sessionId, string verdictId, int status, string code, string reason)
    {
        FileLog.Write($"[TurnVerdictFeedbackService] refused: sid={sessionId} verdict={verdictId} code={code}");
        return new TurnVerdictFeedbackOutcome(false, status, code, reason, verdictId, Replaced: false);
    }

    // The three status codes this service chooses between, named rather than repeated as bare numbers. It does
    // not reference ASP.NET Core's StatusCodes: this type is pure rules over a store, and a test drives it with
    // no web stack present at all.
    private const int StatusCodes200 = 200;
    private const int StatusCodes400 = 400;
    private const int StatusCodes404 = 404;
}

/// <summary>How a report ended: whether it was stored, the status the route answers with, the closed outcome
/// word, the sentence the owner is shown, the verdict it was about, and whether it replaced an earlier
/// report.</summary>
public sealed record TurnVerdictFeedbackOutcome(
    bool Accepted, int StatusCode, string Code, string Reason, string VerdictId, bool Replaced);
