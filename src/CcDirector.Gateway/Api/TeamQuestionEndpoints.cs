using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE QUESTIONS WAITING ON A MEMBER OF THE TEAM (Teams 9, devthrottle_internal#2307, screen S8). A question is in a dev
/// report its author sent to named members (devthrottle_internal#2309); it waits on exactly those members, and on nobody
/// else. A member answers by PICKING ONE OF ITS OPTIONS.
///
/// <list type="bullet">
/// <item><c>GET  /teams/{teamId}/questions</c> - the questions waiting on the caller, and the ones they answered.</item>
/// <item><c>POST /teams/{teamId}/questions/{reportId}/{questionId}/answer</c> - <c>{version, optionValue, comment}</c>.</item>
/// </list>
///
/// THE CHOICE GOES TO THE SESSION THAT ASKED; THE WORDS GO TO A PERSON. The chosen option goes through the one answer
/// delivery (<see cref="DevReportDelivery"/>) exactly as the owner's does - held while the session works, delivered when
/// it is idle, at most once - as an answer that carries no words of the member's own. Their comment, if they wrote one,
/// goes to the report's AUTHOR - the person behind the session that published it, recorded at publish through the one
/// resolver (<see cref="TeamCallerOwnership"/>) - through the person-only path (<see cref="DevReportPersonComments"/>),
/// which nothing on an agent's path reads. The rule from 18 September: a Collaborator's own words never reach an agent.
///
/// THE GATEWAY READS THE QUESTION. Its words and the chosen option's label come from the version the member was sent
/// (<see cref="DevReportQuestions"/>), never from the page, so an answer cannot carry words the report did not hold.
///
/// WHO MAY DO WHAT is decided by <see cref="TeamEndpointGate"/> from <see cref="TeamEndpointRules"/> before a route runs:
/// answering questions is every role's row. What the routes narrow themselves: a question is answered only by a person
/// the report was sent to - anyone else gets the same not-found, whether or not the report exists.
///
/// Mapped only while Teams is released (<see cref="TeamsReleaseSwitch"/>). The address is under <c>/teams/{teamId}/</c>,
/// the only place a Collaborator may call (devthrottle_internal#2306).
/// </summary>
internal static class TeamQuestionEndpoints
{
    /// <summary>The group every route here lives under.</summary>
    public const string GroupPath = TeamEndpoints.Path + "/{teamId}/questions";

    /// <summary>Maps the routes listed in the class comment.</summary>
    public static void Map(IEndpointRouteBuilder app, TeamQuestions questions, HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);

        var group = app.MapGroup(GroupPath);
        group.AddEndpointFilter(async (filterCtx, next) =>
        {
            var http = filterCtx.HttpContext;
            var (teamId, callerSubject, denial) = TeamLibraryEndpoints.AdmitIntoTeam(http, boundary, tenants);
            if (denial is not null)
                return denial;
            http.Items[TeamReportEndpoints.CallerItemKey] = callerSubject;
            using (boundary.EnterScope(new TenantId(teamId!)))
            {
                try
                {
                    return await next(filterCtx).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[TeamQuestionEndpoints] {http.Request.Method} {http.Request.Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                    return Results.Json(new { error = "DevThrottle could not do this just now because of a fault. Try again shortly." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }
        });

        group.MapGet("", (HttpContext ctx, string teamId) => questions.List(teamId, TeamReportEndpoints.Caller(ctx)));
        group.MapPost("/{reportId}/{questionId}/answer", async (HttpContext ctx, string teamId, string reportId, string questionId) =>
        {
            var (body, bad) = await TeamReportEndpoints.ReadObject(ctx).ConfigureAwait(false);
            if (bad is not null) return bad;
            var (version, badVersion) = TeamReportEndpoints.Version(body!.Value);
            if (badVersion is not null) return badVersion;
            if (!body.Value.TryGetProperty("optionValue", out var option) || option.ValueKind != JsonValueKind.String)
                return TeamReportEndpoints.BadRequest("bad_request_body", "The body must have an \"optionValue\": the option chosen.");
            var comment = "";
            if (body.Value.TryGetProperty("comment", out var c) && c.ValueKind != JsonValueKind.Null)
            {
                if (c.ValueKind != JsonValueKind.String)
                    return TeamReportEndpoints.BadRequest("bad_request_body", "\"comment\" must be a string when it is sent.");
                comment = c.GetString()!;
            }
            return await questions.AnswerAsync(teamId, TeamReportEndpoints.Caller(ctx), reportId, questionId, version,
                option.GetString()!, comment, AuthMiddleware.IdentityKind(ctx), ctx.RequestAborted).ConfigureAwait(false);
        });

        FileLog.Write($"[TeamQuestionEndpoints] mapped {GroupPath} (list, answer)");
    }
}

/// <summary>
/// What the Questions routes answer, once the gate has let the request in and the caller is known. Separate from the
/// mapping so every branch is tested without a host. Every sentence and flag the page shows is decided here (rule 7).
/// </summary>
internal sealed class TeamQuestions
{
    /// <summary>The page when nothing waits on the caller.</summary>
    internal const string NothingWaiting = "No questions waiting on you.";

    /// <summary>A question in a report not sent to the caller, or not in the version they hold - one answer for both.</summary>
    internal const string NoSuchQuestion = "There is no question with that id in a report sent to you.";

    /// <summary>An answer to a version the caller no longer holds: the author sent a newer one while the page showed this.</summary>
    internal const string VersionNotHeld =
        "A newer version of this report was sent to you while this page showed the older one, so your answer was not sent. Read the question again.";

    /// <summary>A second answer from the same person to the same question.</summary>
    internal const string AlreadyAnswered = "You already answered this question. Your answer was not sent again.";

    /// <summary>An answer to a question whose session can no longer take one.</summary>
    internal const string SessionEnded = "The session that asked this question has ended, so your answer was not sent.";

    /// <summary>A comment when the person who asked can no longer read comments in this team.</summary>
    internal const string CommentClosed =
        "The person who asked can no longer read comments in this team. Only your choice can be sent.";

    /// <summary>The comment box's placeholder - the words of the mockup.</summary>
    internal const string CommentPlaceholder = "Anything to add (optional)";

    /// <summary>Said beside the send button: where the words go, and that only the choice reaches the agent (S8).</summary>
    internal static string WordsGoTo(string authorName) => $"Your words go to {authorName}. Only your choice reaches the agent.";

    /// <summary>The line on a report sent to a person, when questions in it wait on them (review F11). Null when none do.</summary>
    internal static string? WaitingLabel(int waiting) => waiting switch
    {
        0 => null,
        1 => "1 question waiting on you - answer it on Questions",
        _ => $"{waiting} questions waiting on you - answer them on Questions",
    };

    /// <summary>What the page is for, under its title when nothing waits; the empty state alone says nothing does.</summary>
    internal const string PagePurpose = "Questions your team is waiting on you to answer.";

    /// <summary>The page's line under its title. With nothing waiting it says what the page is for, because the empty
    /// state below already says nothing is waiting and the page would otherwise say it twice.</summary>
    internal static string Subtitle(int waiting) => waiting switch
    {
        0 => PagePurpose,
        1 => "One question is waiting on you.",
        _ => $"{waiting} questions are waiting on you.",
    };

    /// <summary>What the author reads beside a comment that came with an answer.</summary>
    internal static string AboutLabel(string question, string optionLabel) => $"About \"{question}\" - chose \"{optionLabel}\"";

    private readonly DevReportStore _store;
    private readonly DevReportRecipients _recipients;
    private readonly DevReportPersonComments _comments;
    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly DevReportDelivery _delivery;
    private readonly Func<DateTime> _utcNow;

    public TeamQuestions(DevReportStore store, DevReportRecipients recipients, DevReportPersonComments comments,
        TeamRegistry teams, TeamAccess access, DevReportDelivery delivery, Func<DateTime>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _comments = comments ?? throw new ArgumentNullException(nameof(comments));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// The questions waiting on the caller - in the version of each report they were sent, not yet answered by them, and
    /// whose session can still take an answer - newest sent first; then the ones they answered, with what became of each.
    /// </summary>
    public IResult List(string teamId, string caller)
    {
        var team = new TenantId(teamId);
        var names = TeamReports.MemberNames(_teams, teamId, caller);
        var cards = Cards(teamId, caller, _recipients.SentTo(team, caller), names);
        var waiting = cards.Where(c => c.Answer is null).ToList();
        var answered = cards.Where(c => c.Answer is not null).ToList();
        FileLog.Write($"[TeamQuestions] List: team {team.ToLogString()} waiting={waiting.Count} answered={answered.Count}");
        return Results.Json(new
        {
            teamId,
            count = waiting.Count,
            subtitle = Subtitle(waiting.Count),
            emptyText = NothingWaiting,
            waiting = waiting.Select(Shape).ToList(),
            answered = answered.Select(Shape).ToList(),
            answeredHeading = "Answered",
        });
    }

    /// <summary>How many questions wait on the caller in each of <paramref name="received"/>, by report. A report with
    /// none is absent.</summary>
    public IReadOnlyDictionary<Guid, int> WaitingByReport(string teamId, string caller, IReadOnlyList<DevReportReceived> received)
    {
        ArgumentNullException.ThrowIfNull(received);
        var names = TeamReports.MemberNames(_teams, teamId, caller);
        return Cards(teamId, caller, received, names)
            .Where(c => c.Answer is null)
            .GroupBy(c => c.ReportId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// The caller answers a question in a report sent to them. The choice goes to the session that asked through
    /// <see cref="DevReportDelivery"/>; the comment, when there is one, goes to the report's author person and is never
    /// part of what the session receives. Nothing is stored when the answer is refused.
    /// </summary>
    public async Task<IResult> AnswerAsync(string teamId, string caller, string reportId, string questionId, int version,
        string optionValue, string comment, string senderKind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(optionValue);
        ArgumentNullException.ThrowIfNull(comment);
        var team = new TenantId(teamId);
        FileLog.Write($"[TeamQuestions] AnswerAsync: team {team.ToLogString()} report={reportId} question={questionId} version={version} commented={comment.Trim().Length > 0}");

        if (!Guid.TryParse(reportId, out var id) || _recipients.RowFor(team, id, caller) is not { } row
            || _store.Get(team, id) is not { } report)
        {
            FileLog.Write($"[TeamQuestions] AnswerAsync: report={reportId} was not sent to the caller - REFUSED");
            return TeamReportEndpoints.NotFound(NoSuchQuestion);
        }
        if (version != row.SentVersion)
        {
            FileLog.Write($"[TeamQuestions] AnswerAsync: report={report.Id} page showed version {version}, the caller holds {row.SentVersion} - REFUSED");
            return Conflict("version_not_held", VersionNotHeld);
        }
        var held = _store.GetVersion(team, report.Id, row.SentVersion)
                   ?? throw new InvalidOperationException($"Report {report.Id} was sent at version {row.SentVersion}, which is not stored.");
        var question = DevReportQuestions.Read(held.Html).FirstOrDefault(q => string.Equals(q.Id, questionId, StringComparison.Ordinal));
        if (question is null)
            return TeamReportEndpoints.NotFound(NoSuchQuestion);
        var option = question.Option(optionValue);
        if (option is null)
            return TeamReportEndpoints.BadRequest("option_unknown", "That is not one of this question's options. Pick one of the options shown.");

        var words = comment.Trim().Length == 0 ? "" : comment;
        if (words.Length > DevReportPersonComments.MaxLength)
            return TeamReportEndpoints.BadRequest("comment_too_long",
                $"The comment is {words.Length} characters; the limit is {DevReportPersonComments.MaxLength}.");
        if (words.Length > 0 && !TeamReports.AuthorCanReceive(_access, teamId, report))
        {
            FileLog.Write($"[TeamQuestions] AnswerAsync: report={report.Id} has a comment, and its author can no longer read comments - REFUSED");
            return Conflict("author_cannot_receive", CommentClosed);
        }

        if (_delivery.Liveness(team, report.SessionId).Reach == DevReportSessionReach.Ended)
        {
            FileLog.Write($"[TeamQuestions] AnswerAsync: report={report.Id} session has ended - REFUSED, nothing stored");
            return Conflict("session_ended", SessionEnded);
        }

        // THE CHOICE AND THE WORDS, STORED TOGETHER IN ONE TRANSACTION that also decides, in the database, that the caller
        // still holds this version and has not answered this question (review F2, F3, F4). The words go to the person who
        // asked, by the person-only table; the choice is stored held, with no words in it.
        var stored = _store.AddMemberAnswer(team, report, row.SentVersion, ChoiceItem(question, option), caller, words,
            report.AuthorSubject ?? "", senderKind, _utcNow());
        switch (stored)
        {
            case MemberAnswerOutcome.VersionNotHeld:
                return Conflict("version_not_held", VersionNotHeld);
            case MemberAnswerOutcome.AlreadyAnswered:
                return Conflict("already_answered", AlreadyAnswered);
            case MemberAnswerOutcome.Stored:
                break;
            default:
                throw new InvalidOperationException($"An answer outcome this route does not know: {stored}");
        }

        // THE SEND: the one settle pass - held while the session works, delivered when it is idle, never twice. The answer
        // and the words are already stored, so a send that fails here loses nothing: the delivery has recorded what became
        // of the choice (held again, or sent and not confirmed), and the card below shows that state. The fault is logged
        // and not repeated to the caller, who could do nothing with it - their answer IS taken.
        try
        {
            await _delivery.SettleAsync(team, report.SessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TeamQuestions] AnswerAsync: report={report.Id} the answer is stored; settling its session FAILED ({ex.GetType().Name}): {ex.Message}");
        }

        var names = TeamReports.MemberNames(_teams, teamId, caller);
        var card = Cards(teamId, caller, [new DevReportReceived(row, held.Title, held.Status)], names)
            .Single(c => string.Equals(c.QuestionId, question.Id, StringComparison.Ordinal));
        return Results.Json(new { question = Shape(card) });
    }

    /// <summary>
    /// THE ITEM A MEMBER'S ANSWER BECOMES - THE CHOICE, AND NOTHING ELSE: the question's words and the option's label from
    /// the stored version, a fresh id, and no text and no comment. The member's own words never enter it. The one place
    /// it is built, so the delivery proofs send exactly what the route sends.
    /// </summary>
    internal static DevReportItem ChoiceItem(DevReportQuestion question, DevReportQuestionOption option)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(option);
        return new DevReportItem("a-" + Guid.NewGuid().ToString("N"), DevReportItem.Answer, "", null,
            question.Id, question.Text, option.Value, option.Label, "");
    }

    /// <summary>A comment's question, as its author reads it beside the comment; null for a comment on the whole report.</summary>
    public string? AboutLabelFor(TenantId team, DevReportCommentEntity comment)
    {
        ArgumentNullException.ThrowIfNull(comment);
        if (comment.QuestionId is null) return null;
        return _store.AnswersBy(team, comment.FromSubject, [comment.ReportId]).TryGetValue((comment.ReportId, comment.QuestionId), out var answer)
            ? AboutLabel(answer.Question, answer.OptionLabel)
            : null;
    }

    // ---------------------------------------------------------------- shapes

    private sealed record Card(
        Guid ReportId, int Version, string ReportTitle, string QuestionId, string Question,
        IReadOnlyList<DevReportQuestionOption> Options, string AskedBy, DateTime AskedAtUtc,
        bool CanComment, string CommentNote, AnswerShape? Answer);

    private sealed record AnswerShape(string OptionValue, string OptionLabel, string StatusLabel, DateTime AtUtc, string? YourComment);

    private List<Card> Cards(string teamId, string caller, IReadOnlyList<DevReportReceived> received, IReadOnlyDictionary<string, string> names)
    {
        var team = new TenantId(teamId);
        var answers = _store.AnswersBy(team, caller, received.Select(r => r.Row.ReportId).ToList());
        var cards = new List<Card>();
        foreach (var r in received.OrderByDescending(r => r.Row.SentAtUtc))
        {
            var report = _store.Get(team, r.Row.ReportId)
                         ?? throw new InvalidOperationException($"Report {r.Row.ReportId} was sent to a member and is not stored.");
            var held = _store.GetVersion(team, report.Id, r.Row.SentVersion)
                       ?? throw new InvalidOperationException($"Report {report.Id} was sent at version {r.Row.SentVersion}, which is not stored.");
            var questions = DevReportQuestions.Read(held.Html);
            if (questions.Count == 0) continue;

            var author = TeamReports.NameOf(report.AuthorSubject, names);
            var canComment = TeamReports.AuthorCanReceive(_access, teamId, report);
            var commentNote = canComment ? WordsGoTo(author) : CommentClosed;
            var ended = _delivery.Liveness(team, report.SessionId).Reach == DevReportSessionReach.Ended;
            var mine = questions.Any(q => answers.ContainsKey((report.Id, q.Id)))
                ? _comments.From(team, report.Id, caller)
                : [];
            foreach (var q in questions)
            {
                AnswerShape? answer = null;
                if (answers.TryGetValue((report.Id, q.Id), out var given))
                {
                    var said = mine.LastOrDefault(c => string.Equals(c.QuestionId, q.Id, StringComparison.Ordinal))?.Text;
                    answer = new AnswerShape(given.OptionValue, given.OptionLabel, given.StatusLabel, given.SentAtUtc, said);
                }
                else if (ended)
                {
                    // Nobody waits on an answer a session can no longer take.
                    continue;
                }
                cards.Add(new Card(report.Id, r.Row.SentVersion, held.Title, q.Id, q.Text, q.Options, author, r.Row.SentAtUtc,
                    canComment, commentNote, answer));
            }
        }
        return cards;
    }

    private static object Shape(Card c) => new
    {
        reportId = c.ReportId.ToString("D"),
        version = c.Version,
        reportTitle = c.ReportTitle,
        questionId = c.QuestionId,
        question = c.Question,
        options = c.Options.Select(o => new { value = o.Value, label = o.Label, recommended = o.Recommended }).ToList(),
        recommendedLabel = "recommended",
        askedBy = c.AskedBy,
        askedAtUtc = c.AskedAtUtc,
        canAnswer = c.Answer is null,
        canComment = c.Answer is null && c.CanComment,
        commentPlaceholder = CommentPlaceholder,
        commentNote = c.CommentNote,
        sendLabel = "Send answer",
        answer = c.Answer is null ? null : new
        {
            optionValue = c.Answer.OptionValue,
            optionLabel = c.Answer.OptionLabel,
            chosenLabel = $"You chose \"{c.Answer.OptionLabel}\"",
            statusLabel = c.Answer.StatusLabel,
            atUtc = c.Answer.AtUtc,
            yourComment = c.Answer.YourComment,
            yourCommentLabel = c.Answer.YourComment is null ? null : $"Your words went to {c.AskedBy}, not to the agent",
        },
    };

    private static IResult Conflict(string code, string sentence) =>
        Results.Json(new { error = sentence, code }, statusCode: StatusCodes.Status409Conflict);
}
