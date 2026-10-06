using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.DevReports;

/// <summary>
/// "WORDS GO TO A PERSON" - THE ONE PATH BY WHICH A PERSON'S OWN WORDS ON A DEV REPORT REACH ANOTHER PERSON, AND NEVER AN
/// AGENT (devthrottle_internal#2309). The owner's rule for the whole Collaborator track: a Collaborator's own words never
/// reach an agent. A comment written here is stored and read back by two people only - the person it goes to (the
/// report's author) and the person who wrote it - through their own account routes.
///
/// What keeps it from an agent is that nothing that talks to a session reads this table: <see cref="DevReportDelivery"/>,
/// <see cref="DevReportPromptFold"/> and the session's own dev report routes read the agent's conversation
/// (<c>dev_report_items</c> and <c>dev_report_replies</c>) and never this one. The Collaborator's Questions
/// (devthrottle_internal#2307) sends the comment that comes with an answer down this same path, with the question it is
/// about; the answer's choice goes to the session, and its comment only here.
///
/// Every operation takes the team's tenant and reads through a context scoped to it. The words are never logged.
/// </summary>
internal sealed class DevReportPersonComments
{
    /// <summary>The longest comment, in characters - the same measure and limit as a dev report note.</summary>
    public const int MaxLength = 20000;

    private readonly GatewayDatabase _db;

    public DevReportPersonComments(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Store <paramref name="text"/>, written by <paramref name="fromSubject"/> on the report, for
    /// <paramref name="toSubject"/>. The text is kept exactly as written. Who may write to whom is decided before this.
    /// </summary>
    /// <param name="questionId">The question the comment was written beside, when it came with an answer on the Questions
    /// page (devthrottle_internal#2307); null for a comment on the whole report.</param>
    /// <exception cref="ArgumentException">The text is empty, only whitespace, or longer than <see cref="MaxLength"/>.</exception>
    public DevReportCommentEntity Add(TenantId team, Guid reportId, string fromSubject, string toSubject, string text, DateTime nowUtc,
        string? questionId = null)
    {
        var row = NewRow(team, reportId, fromSubject, toSubject, text, nowUtc, questionId);
        using var ctx = _db.CreateContext(team);
        ctx.DevReportComments.Add(row);
        ctx.SaveChanges();
        FileLog.Write($"[DevReportPersonComments] Add: tenant={team.ToLogString()} report={reportId} comment={row.Id} chars={text.Length}");
        return row;
    }

    /// <summary>
    /// A comment row, checked, not yet stored - the one place a comment's rules are applied, so the comment that comes
    /// with an answer (<see cref="DevReportStore.AddMemberAnswer"/>, stored in the answer's own transaction) is held to
    /// exactly the rules of <see cref="Add"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The text is empty, only whitespace, or longer than <see cref="MaxLength"/>.</exception>
    internal static DevReportCommentEntity NewRow(TenantId team, Guid reportId, string fromSubject, string toSubject, string text,
        DateTime nowUtc, string? questionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromSubject);
        ArgumentException.ThrowIfNullOrWhiteSpace(toSubject);
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("A comment has words in it.", nameof(text));
        if (text.Length > MaxLength)
            throw new ArgumentException($"A comment is at most {MaxLength} characters; this one is {text.Length}.", nameof(text));
        return new DevReportCommentEntity
        {
            TenantId = team.Value,
            ReportId = reportId,
            FromSubject = fromSubject,
            ToSubject = toSubject,
            Text = text,
            AtUtc = nowUtc,
            QuestionId = questionId,
        };
    }

    /// <summary>The comments on the report that go to <paramref name="toSubject"/>, oldest first - the author's read.</summary>
    public IReadOnlyList<DevReportCommentEntity> To(TenantId team, Guid reportId, string toSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toSubject);
        using var ctx = _db.CreateContext(team);
        return Ordered(ctx.DevReportComments.AsNoTracking().Where(c => c.ReportId == reportId && c.ToSubject == toSubject));
    }

    /// <summary>The comments <paramref name="fromSubject"/> wrote on the report, oldest first - the writer's own read.</summary>
    public IReadOnlyList<DevReportCommentEntity> From(TenantId team, Guid reportId, string fromSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromSubject);
        using var ctx = _db.CreateContext(team);
        return Ordered(ctx.DevReportComments.AsNoTracking().Where(c => c.ReportId == reportId && c.FromSubject == fromSubject));
    }

    /// <summary>How many comments go to <paramref name="toSubject"/> on each report, for a list. None is absent.</summary>
    public IReadOnlyDictionary<Guid, int> CountsTo(TenantId team, string toSubject, IReadOnlyCollection<Guid> reportIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toSubject);
        ArgumentNullException.ThrowIfNull(reportIds);
        if (reportIds.Count == 0) return new Dictionary<Guid, int>();
        using var ctx = _db.CreateContext(team);
        return ctx.DevReportComments.AsNoTracking()
            .Where(c => c.ToSubject == toSubject && reportIds.Contains(c.ReportId))
            .GroupBy(c => c.ReportId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
    }

    private static List<DevReportCommentEntity> Ordered(IQueryable<DevReportCommentEntity> rows) =>
        rows.ToList().OrderBy(c => c.AtUtc).ThenBy(c => c.Id).ToList();
}
