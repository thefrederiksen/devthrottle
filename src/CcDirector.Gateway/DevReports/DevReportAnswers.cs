using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// A SESSION READS THE OWNER'S STORED ANSWERS TO A REPORT, AND SAYS WHEN IT HAS ACTED ON THEM (Factories Run Alone,
/// <c>cc-dev-reports answers</c>). The owner's ruling, 26 October 2026: "The run writes the item to one place you read
/// when you like, then closes. Your answer is stored, and the NEXT run reads it and acts."
///
/// A report is keyed on (session, file path), and a factory run closes when it is done, so by the time the owner answers,
/// the session that published the report is gone. The answer cannot be typed into it; it is stored
/// (<see cref="DevReportItemStates.SavedState"/>) and the next run - a different session - reads it here.
///
/// HOW THE NEXT RUN FINDS THE REPORT. By its id, always; or by its KEY, the report file's path. A key read from a
/// factory seat is narrowed to reports from that same factory and seat. Scheduled sessions carry that identity in
/// session history; older scheduled sessions can still be resolved through their run record. A key read returns the
/// newest matching report plus every older one still holding an item no session has marked acted on.
///
/// WHO MAY READ. A session of the same account - any session, not only the one that published, which is the point. In a
/// team's tenant a report is a person's (<see cref="DevReportAuthor"/>), so a session reads only the reports of the person
/// behind it, and a session nobody can be named behind reads nothing. Another account's report, or a team mate's, is not
/// found: 404, and its existence does not leak. Only the account owner's own notes and answers are read and marked; a team
/// member's answer and its words take their own path (<see cref="DevReportPersonComments"/>) and are never shown here.
/// </summary>
internal sealed class DevReportAnswers
{
    /// <summary>The most item ids one acted-on mark may name.</summary>
    public const int MaxAckItems = 500;

    private readonly DevReportStore _store;
    private readonly Func<TenantId, string, DevReportAuthorAnswer> _author;
    private readonly Func<TenantId, string, DevReportFactorySeatIdentity?> _factorySeatOf;
    private readonly Func<TenantId, string, DevReportSessionLiveness> _sessionLiveness;
    private readonly Func<DateTime> _nowUtc;

    /// <param name="author">Who the calling session's Director belongs to in this tenant - <see cref="DevReportAuthor.Resolve"/>.</param>
    /// <param name="factorySeatOf">The stable factory and seat of a session, or null when it is not a seat.</param>
    /// <param name="sessionLiveness">The Gateway's current reach to a session, used to take over claims only after it ended.</param>
    public DevReportAnswers(DevReportStore store, Func<TenantId, string, DevReportAuthorAnswer> author,
        Func<TenantId, string, DevReportFactorySeatIdentity?> factorySeatOf,
        Func<TenantId, string, DevReportSessionLiveness> sessionLiveness, Func<DateTime>? nowUtc = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _author = author ?? throw new ArgumentNullException(nameof(author));
        _factorySeatOf = factorySeatOf ?? throw new ArgumentNullException(nameof(factorySeatOf));
        _sessionLiveness = sessionLiveness ?? throw new ArgumentNullException(nameof(sessionLiveness));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>The owner's answers to one report (<paramref name="reportId"/>) or to the reports under one key.</summary>
    public IResult Read(TenantId tenant, string callerSessionId, string callerDirectorId, string? reportId, string? key)
    {
        FileLog.Write($"[DevReportAnswers] Read: tenant={tenant.ToLogString()} sid={callerSessionId} report={reportId ?? "(none)"} key={key ?? "(none)"}");
        var hasReport = !string.IsNullOrWhiteSpace(reportId);
        var hasKey = !string.IsNullOrWhiteSpace(key);
        if (hasReport == hasKey)
            return Error(400, "bad_query", "Name exactly one of report (a report id) or key (the report file's full path).");
        if (Person(tenant, callerDirectorId) is not { } person) return UnknownPerson();

        List<DevReportEntity> reports;
        if (hasReport)
        {
            var id = reportId ?? "";
            if (Find(tenant, id, person.Subject) is not { } report) return NotFound(id);
            reports = [report];
        }
        else
        {
            var seat = _factorySeatOf(tenant, callerSessionId);
            var matching = _store.ListByKey(tenant, key ?? "", person.Subject, seat);
            if (seat is not null)
                matching = matching.Where(r => MatchesSeat(tenant, r, seat)).ToList();
            reports = Select(tenant, matching);
        }

        var views = reports.Select(r => View(tenant, r, _store.Items(tenant, r.Id))).ToList();
        FileLog.Write($"[DevReportAnswers] Read: sid={callerSessionId} reports={views.Count}");
        return Results.Json(new { count = views.Count, reports = views });
    }

    /// <summary>Claim the report's currently unacted-on owner items before the calling session acts on them.</summary>
    public IResult Claim(TenantId tenant, string callerSessionId, string callerDirectorId, string reportId,
        IReadOnlyList<string>? itemIds)
    {
        FileLog.Write($"[DevReportAnswers] Claim: tenant={tenant.ToLogString()} sid={callerSessionId} report={reportId} items={itemIds?.Count.ToString() ?? "(all)"}");
        if (itemIds is { Count: > MaxAckItems })
            return Error(400, "too_many_items", $"An action claim may name at most {MaxAckItems} items; it named {itemIds.Count}.");
        if (Person(tenant, callerDirectorId) is not { } person) return UnknownPerson();
        if (Find(tenant, reportId, person.Subject) is not { } report) return NotFound(reportId);

        var view = View(tenant, report, _store.Items(tenant, report.Id));
        var eligible = UnactedItemIds(view);
        if (itemIds is not null)
            eligible = eligible.Where(id => itemIds.Contains(id)).ToList();

        var endedClaimers = new List<string>();
        foreach (var claimedBy in _store.ActionClaimSessions(tenant, report.Id, eligible, callerSessionId))
            if (_sessionLiveness(tenant, claimedBy).Reach == DevReportSessionReach.Ended)
                endedClaimers.Add(claimedBy);

        var claimed = _store.ClaimUnactedItems(tenant, report.Id, eligible, callerSessionId, endedClaimers, _nowUtc());
        view = View(tenant, report, _store.Items(tenant, report.Id));
        FileLog.Write($"[DevReportAnswers] Claim: report={report.Id} sid={callerSessionId} claimed={claimed.Count}");
        return Results.Json(new
        {
            claimed = claimed.Select(i => new { itemId = i.ClientItemId, kind = i.Kind, questionId = i.Kind == DevReportItem.Answer ? i.QuestionId : null }).ToList(),
            count = 1,
            reports = new[] { view },
        });
    }

    /// <summary>
    /// Mark the owner's items on a report acted on by the calling session: every one not yet marked, or only
    /// <paramref name="itemIds"/>. Answers what this call marked and the report as it now reads.
    /// </summary>
    public IResult Ack(TenantId tenant, string callerSessionId, string callerDirectorId, string reportId, IReadOnlyList<string>? itemIds)
    {
        FileLog.Write($"[DevReportAnswers] Ack: tenant={tenant.ToLogString()} sid={callerSessionId} report={reportId} items={itemIds?.Count.ToString() ?? "(all)"}");
        if (itemIds is { Count: > MaxAckItems })
            return Error(400, "too_many_items", $"An acted-on mark may name at most {MaxAckItems} items; it named {itemIds.Count}.");
        if (Person(tenant, callerDirectorId) is not { } person) return UnknownPerson();
        if (Find(tenant, reportId, person.Subject) is not { } report) return NotFound(reportId);

        var eligible = UnactedItemIds(View(tenant, report, _store.Items(tenant, report.Id)));
        if (itemIds is not null)
            eligible = eligible.Where(id => itemIds.Contains(id)).ToList();
        var marked = _store.MarkActedOn(tenant, report.Id, eligible, callerSessionId, _nowUtc());
        var view = View(tenant, report, _store.Items(tenant, report.Id));
        FileLog.Write($"[DevReportAnswers] Ack: report={report.Id} sid={callerSessionId} marked={marked.Count}");
        return Results.Json(new
        {
            marked = marked.Select(i => new { itemId = i.ClientItemId, kind = i.Kind, questionId = i.Kind == DevReportItem.Answer ? i.QuestionId : null }).ToList(),
            count = 1,
            reports = new[] { view },
        });
    }

    // ---------------------------------------------------------------- the fold

    /// <summary>
    /// One report as a session reads it: every question in its newest version with the owner's latest answer or "not
    /// answered", then any answered question a newer version dropped, then the owner's notes. Each answer and note says
    /// whether a session has acted on it.
    /// </summary>
    internal ReportAnswersView View(TenantId tenant, DevReportEntity report, IReadOnlyList<DevReportItemEntity> items)
    {
        var latest = _store.GetVersion(tenant, report.Id, null)
            ?? throw new InvalidOperationException($"report {report.Id} has no stored version");
        var asked = DevReportQuestions.Read(latest.Html);

        var owners = items.Where(i => i.AnswererSubject is null).ToList();
        var answers = owners.Where(i => i.Kind == DevReportItem.Answer)
            .GroupBy(i => i.QuestionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.Sequence).First(), StringComparer.Ordinal);

        var questions = new List<QuestionAnswerView>();
        foreach (var q in asked)
            questions.Add(Question(q.Id, q.Text, inLatestVersion: true, answers.GetValueOrDefault(q.Id)));
        foreach (var (questionId, answer) in answers.Where(a => asked.All(q => q.Id != a.Key)).OrderBy(a => a.Value.Sequence))
            questions.Add(Question(questionId, answer.Question, inLatestVersion: false, answer));

        var notes = owners.Where(i => i.Kind == DevReportItem.Note).OrderBy(i => i.Sequence)
            .Select(n => new NoteView(n.ClientItemId, n.Text, Quote(n.AnchorJson), n.SentAtUtc, n.Status, n.StatusLabel,
                n.ActedOnAtUtc, n.ActedOnSessionId, n.ActionClaimedAtUtc, n.ActionClaimedSessionId))
            .ToList();

        var notActedOn = questions.Count(q => q.Answered && q.ActedOnAtUtc is null) + notes.Count(n => n.ActedOnAtUtc is null);
        return new ReportAnswersView(report.Id.ToString("D"), report.Title, report.Key, report.SessionId, report.Version,
            report.UpdatedAtUtc, questions.Count(q => q.Answered), questions.Count(q => !q.Answered), notes.Count, notActedOn,
            questions, notes);
    }

    private static QuestionAnswerView Question(string id, string text, bool inLatestVersion, DevReportItemEntity? answer)
        => answer is null
            ? new QuestionAnswerView(id, text, inLatestVersion, false, null, null, null, null, null, null, null, null, null, null, null)
            : new QuestionAnswerView(id, text, inLatestVersion, true, answer.ClientItemId, answer.OptionValue, answer.OptionLabel,
                answer.Comment, answer.SentAtUtc, answer.Status, answer.StatusLabel, answer.ActedOnAtUtc, answer.ActedOnSessionId,
                answer.ActionClaimedAtUtc, answer.ActionClaimedSessionId);

    private static List<string> UnactedItemIds(ReportAnswersView view)
    {
        var ids = view.Questions.Where(q => q.Answered && q.ActedOnAtUtc is null)
            .Select(q => q.ItemId)
            .Concat(view.Notes.Where(n => n.ActedOnAtUtc is null).Select(n => n.ItemId));
        return ids.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
    }

    private static string? Quote(string? anchorJson)
    {
        if (anchorJson is null) return null;
        using var doc = JsonDocument.Parse(anchorJson);
        return doc.RootElement.TryGetProperty("quote", out var q) ? q.GetString() : null;
    }

    /// <summary>The newest report under a key, then every older one still holding an item no session has acted on.</summary>
    private List<DevReportEntity> Select(TenantId tenant, IReadOnlyList<DevReportEntity> underKey)
    {
        var chosen = new List<DevReportEntity>();
        for (var i = 0; i < underKey.Count; i++)
        {
            var report = underKey[i];
            if (i == 0 || View(tenant, report, _store.Items(tenant, report.Id)).NotActedOn > 0)
                chosen.Add(report);
        }
        return chosen;
    }

    // ---------------------------------------------------------------- who may read

    /// <summary>The person whose reports the caller reads: <c>Subject</c> null in a personal account (the whole account), a
    /// person's subject in a team's tenant. Null when the caller is a team's session nobody can be named behind.</summary>
    private Reader? Person(TenantId tenant, string directorId)
    {
        var author = _author(tenant, directorId);
        if (author.Refused) return null;
        return new Reader(author.InTeam ? author.Subject : null);
    }

    private DevReportEntity? Find(TenantId tenant, string reportId, string? person)
    {
        if (!Guid.TryParse(reportId, out var id)) return null;
        var report = _store.Get(tenant, id);
        if (report is null) return null;
        if (person is not null && !string.Equals(report.AuthorSubject, person, StringComparison.Ordinal)) return null;
        return report;
    }

    private bool MatchesSeat(TenantId tenant, DevReportEntity report, DevReportFactorySeatIdentity expected)
    {
        if (report.Factory is null && report.FactorySeat is null)
            return expected.Matches(_factorySeatOf(tenant, report.SessionId));
        return string.Equals(report.Factory, expected.Factory, StringComparison.OrdinalIgnoreCase)
               && string.Equals(report.FactorySeat, expected.Seat, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult UnknownPerson()
        => Error(403, "report_author_unknown",
            "DevThrottle cannot tell which member of the team this session belongs to, so it reads no report's answers inside the team.");

    private static IResult NotFound(string reportId)
        => Error(404, "report_not_found", $"There is no dev report {reportId} here.");

    private static IResult Error(int status, string code, string error)
        => Results.Json(new { error, code }, statusCode: status);

    private sealed record Reader(string? Subject);
}

/// <summary>One report's answers as a session reads them. Names are the JSON the command line prints.</summary>
internal sealed record ReportAnswersView(
    string Id, string Title, string Key, string SessionId, int Version, DateTime UpdatedAtUtc,
    int Answered, int NotAnswered, int NoteCount, int NotActedOn,
    IReadOnlyList<QuestionAnswerView> Questions, IReadOnlyList<NoteView> Notes);

/// <summary>One question and the owner's latest answer to it, or <c>Answered</c> false.</summary>
internal sealed record QuestionAnswerView(
    string Id, string Question, bool InLatestVersion, bool Answered, string? ItemId, string? OptionValue, string? OptionLabel,
    string? Comment, DateTime? AnsweredAtUtc, string? Status, string? StatusLabel, DateTime? ActedOnAtUtc,
    string? ActedOnSessionId, DateTime? ActionClaimedAtUtc, string? ActionClaimedSessionId);

/// <summary>One of the owner's notes on the report, with the words it was anchored to.</summary>
internal sealed record NoteView(
    string ItemId, string Text, string? Quote, DateTime AtUtc, string Status, string StatusLabel,
    DateTime? ActedOnAtUtc, string? ActedOnSessionId, DateTime? ActionClaimedAtUtc, string? ActionClaimedSessionId);

/// <summary>The stable identity shared by runs of one factory seat.</summary>
internal sealed record DevReportFactorySeatIdentity(string Factory, string Seat)
{
    public bool Matches(DevReportFactorySeatIdentity? other)
        => other is not null
           && string.Equals(Factory, other.Factory, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Seat, other.Seat, StringComparison.OrdinalIgnoreCase);
}
