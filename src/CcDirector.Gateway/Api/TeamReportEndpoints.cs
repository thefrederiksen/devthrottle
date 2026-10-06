using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// A DEV REPORT SENT TO A MEMBER OF THE TEAM (Teams 11, devthrottle_internal#2309, screen S10). A report is published by
/// a session for itself; in a team's tenant it belongs to the person behind that session - its author
/// (<see cref="DevReportAuthor"/>). This lets the author send one of their reports to named members of the same team,
/// and gives each member the reports sent to them: newest first, from whom and when, read or not; open one, read it,
/// comment on it.
///
/// <list type="bullet">
/// <item><c>GET  /teams/{teamId}/reports/sent-to-me</c> - the reports sent to the caller.</item>
/// <item><c>GET  /teams/{teamId}/reports/sent-to-me/{reportId}</c> - one of them, with the caller's own comments.</item>
/// <item><c>GET  /teams/{teamId}/reports/sent-to-me/{reportId}/html</c> - the bytes of the VERSION SENT to the caller, and
/// no other, served exactly as the owner's are (<see cref="DevReportEndpoints.ServeVersion"/>), for the same host and the
/// same trust rules.</item>
/// <item><c>POST /teams/{teamId}/reports/sent-to-me/{reportId}/read</c> - the caller opened it.</item>
/// <item><c>POST /teams/{teamId}/reports/sent-to-me/{reportId}/comments</c> - a comment, for the report's author.</item>
/// <item><c>GET  /teams/{teamId}/reports/mine</c> - the caller's own reports in the team.</item>
/// <item><c>GET  /teams/{teamId}/reports/mine/{reportId}</c> - one, with who it was sent to, who it can still go to, and
/// the comments people wrote on it.</item>
/// <item><c>GET  /teams/{teamId}/reports/mine/{reportId}/html</c> - its bytes.</item>
/// <item><c>POST /teams/{teamId}/reports/mine/{reportId}/recipients</c> - send it to members, by their Team page ids.</item>
/// </list>
///
/// A COMMENT NEVER REACHES AN AGENT. It is stored by <see cref="DevReportPersonComments"/> for the author person and read
/// back only here, through the author's and the writer's own accounts. Nothing here touches
/// <see cref="DevReportDelivery"/> or the session's own report routes.
///
/// WHO MAY DO WHAT is not decided here. <see cref="TeamEndpointGate"/> decides every route from
/// <see cref="TeamEndpointRules"/> through <see cref="TeamAccess.Decide"/> before it runs: reading what was sent to you is
/// every role's; your own reports are a person who runs sessions, and one report is yours only when you wrote it
/// (<see cref="TeamCallerOwnership"/>). Each route enters the team only when the gate allowed this request in this very
/// team (<see cref="TeamLibraryEndpoints.AdmitIntoTeam"/>). What the routes narrow themselves: the sent-to-me routes
/// answer only a report sent to the caller - any other is not found, however it is asked for - and the mine list only the
/// caller's own. The recipients of a send are asked of the role table too: each must be a member whose role may read
/// reports sent to them.
///
/// Mapped only while Teams is released (<see cref="TeamsReleaseSwitch"/>). Every address is under
/// <c>/teams/{teamId}/</c>, the only place a Collaborator may call (devthrottle_internal#2306).
/// </summary>
internal static class TeamReportEndpoints
{
    /// <summary>The group every route here lives under.</summary>
    public const string GroupPath = TeamEndpoints.Path + "/{teamId}/reports";

    /// <summary>The recipient's routes, as the route table writes them.</summary>
    public const string SentToMePattern = GroupPath + "/sent-to-me";

    /// <summary>The author's list, as the route table writes it.</summary>
    public const string MinePattern = GroupPath + "/mine";

    /// <summary>One of the author's reports and everything under it, as the route table writes it.</summary>
    public const string MineReportPattern = MinePattern + "/{reportId}";

    /// <summary>The recipient's page when nothing was sent to them.</summary>
    internal const string SentToMeEmpty = "No reports sent to you yet.";

    /// <summary>The author's list when none of their sessions in the team has published a report.</summary>
    internal const string MineEmpty = "None of your sessions in this team has published a report yet.";

    /// <summary>A report not sent to the caller - the same answer whether it exists or not.</summary>
    internal const string NotSentToYou = "There is no report sent to you with that id.";

    /// <summary>One of the caller's own reports that is not there - their gate already refused anyone else's.</summary>
    internal const string NoSuchOwnReport = "There is no report of yours with that id in this team.";

    /// <summary>Who a sender is when they have left the team.</summary>
    internal const string FormerMember = "A former member of the team";

    /// <summary>Said beside the comment box, so the writer knows where the words go and where they never go.</summary>
    internal static string CommentsGoTo(string authorName) =>
        $"Your comments go to {authorName}, who wrote this report. They are never shown to an agent.";

    /// <summary>Said beside the author's send box.</summary>
    internal const string SendNote =
        "The people you send this report to can read it and comment on it. Their comments come to you here and are never shown to an agent.";

    /// <summary>A version of a report sent to the caller other than the one they were sent.</summary>
    internal const string VersionNotSentToYou =
        "That version of this report was not sent to you. You read the version its author sent you.";

    /// <summary>
    /// A send whose version is not the report's newest: the author was reading one version while the session published
    /// another, and what is sent must be what they read (delta review D2). Nothing is sent.
    /// </summary>
    internal const string NotTheNewestVersion =
        "This report has a newer version than the one on your screen, so nothing was sent. Read the newer version, then send it.";

    /// <summary>
    /// A read for a version the person no longer holds: the author sent them a newer one while the page showed the older.
    /// Nothing is marked, because the newer one has not been seen (delta review D5).
    /// </summary>
    internal const string ReadVersionNotHeld =
        "A newer version of this report was sent to you while this page showed the older one, so it was not marked read.";

    /// <summary>
    /// Said instead of where comments go, when the report's author can no longer read comments on it in this team - they
    /// left it, or their role no longer opens their own reports. A comment is then refused rather than kept for nobody.
    /// </summary>
    internal const string AuthorCannotReceive =
        "The person who wrote this report can no longer read comments on it in this team, so a comment cannot be sent.";

    /// <summary>The author's report when it has not been sent to anyone.</summary>
    internal const string NotSentYet = "Not sent to anyone yet.";

    internal const string ReadLabel = "Read";
    internal const string NewLabel = "New";

    /// <summary>Maps every route listed in the class comment.</summary>
    public static void Map(IEndpointRouteBuilder app, DevReportStore store, DevReportRecipients recipients,
        DevReportPersonComments comments, TeamRegistry teams, TeamAccess access, HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(recipients);
        ArgumentNullException.ThrowIfNull(comments);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(boundary);
        ArgumentNullException.ThrowIfNull(tenants);

        var group = app.MapGroup(GroupPath);
        group.AddEndpointFilter(async (filterCtx, next) =>
        {
            var http = filterCtx.HttpContext;
            var (teamId, callerSubject, denial) = TeamLibraryEndpoints.AdmitIntoTeam(http, boundary, tenants);
            if (denial is not null)
                return denial;
            http.Items[CallerItemKey] = callerSubject;
            using (boundary.EnterScope(new TenantId(teamId!)))
            {
                try
                {
                    return await next(filterCtx).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[TeamReportEndpoints] {http.Request.Method} {http.Request.Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                    return Results.Json(new { error = "DevThrottle could not do this just now because of a fault. Try again shortly." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }
        });

        var reports = new TeamReports(store, recipients, comments, teams, access);

        // ---------------------------------------------------------------- the recipient's routes

        group.MapGet("/sent-to-me", (HttpContext ctx, string teamId) => reports.SentToMe(teamId, Caller(ctx)));
        group.MapGet("/sent-to-me/{reportId}", (HttpContext ctx, string teamId, string reportId) =>
            reports.SentToMeDetail(teamId, Caller(ctx), reportId));
        group.MapGet("/sent-to-me/{reportId}/html", (HttpContext ctx, string teamId, string reportId) =>
            reports.SentToMeHtml(teamId, Caller(ctx), reportId, ctx));
        group.MapPost("/sent-to-me/{reportId}/read", async (HttpContext ctx, string teamId, string reportId) =>
        {
            var (body, bad) = await ReadObject(ctx).ConfigureAwait(false);
            if (bad is not null) return bad;
            var (version, badVersion) = Version(body!.Value);
            return badVersion ?? reports.MarkRead(teamId, Caller(ctx), reportId, version);
        });
        group.MapPost("/sent-to-me/{reportId}/comments", async (HttpContext ctx, string teamId, string reportId) =>
        {
            var (text, bad) = await ReadString(ctx, "text").ConfigureAwait(false);
            return bad ?? reports.Comment(teamId, Caller(ctx), reportId, text!);
        });

        // ---------------------------------------------------------------- the author's routes

        group.MapGet("/mine", (HttpContext ctx, string teamId) => reports.Mine(teamId, Caller(ctx)));
        group.MapGet("/mine/{reportId}", (HttpContext ctx, string teamId, string reportId) =>
            reports.MineDetail(teamId, Caller(ctx), reportId));
        group.MapGet("/mine/{reportId}/html", (HttpContext ctx, string teamId, string reportId) =>
            reports.OwnReport(teamId, Caller(ctx), reportId) is { } report
                ? DevReportEndpoints.ServeHtml(store, new TenantId(teamId), report, ctx)
                : NotFound(NoSuchOwnReport));
        group.MapPost("/mine/{reportId}/recipients", async (HttpContext ctx, string teamId, string reportId) =>
        {
            var (body, bad) = await ReadObject(ctx).ConfigureAwait(false);
            if (bad is not null) return bad;
            var (memberIds, badIds) = StringArray(body!.Value, "memberIds");
            if (badIds is not null) return badIds;
            var (version, badVersion) = Version(body.Value);
            return badVersion ?? reports.Send(teamId, Caller(ctx), reportId, memberIds!, version);
        });

        FileLog.Write($"[TeamReportEndpoints] mapped {SentToMePattern} (list, one, html, read, comments) and {MinePattern} (list, one, html, recipients)");
    }

    private const string CallerItemKey = "cc.teams.reports.caller";

    /// <summary>The person the filter admitted. Set on every request that reaches a route here.</summary>
    private static string Caller(HttpContext ctx) =>
        ctx.Items.TryGetValue(CallerItemKey, out var value) && value is string subject && subject.Length > 0
            ? subject
            : throw new InvalidOperationException("A team report route ran without the caller its filter admits.");

    internal static IResult NotFound(string sentence) => Results.Json(new { error = sentence, code = "report_not_found" }, statusCode: 404);

    internal static IResult BadRequest(string code, string sentence) => Results.Json(new { error = sentence, code }, statusCode: 400);

    private static async Task<(string? Value, IResult? Bad)> ReadString(HttpContext ctx, string name)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
                return (null, BadRequest("bad_request_body", $"The body must be an object with a \"{name}\" string."));
            return (v.GetString()!, null);
        }
        catch (JsonException ex)
        {
            return (null, BadRequest("bad_request_body", $"The body could not be read as JavaScript Object Notation: {ex.Message}"));
        }
    }

    /// <summary>The request body as a JSON object.</summary>
    private static async Task<(JsonElement? Value, IResult? Bad)> ReadObject(HttpContext ctx)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted).ConfigureAwait(false);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, BadRequest("bad_request_body", "The body must be a JavaScript Object Notation object."));
            return (doc.RootElement.Clone(), null);
        }
        catch (JsonException ex)
        {
            return (null, BadRequest("bad_request_body", $"The body could not be read as JavaScript Object Notation: {ex.Message}"));
        }
    }

    private static (IReadOnlyList<string>? Value, IResult? Bad) StringArray(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array
            || v.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
            return (null, BadRequest("bad_request_body", $"The body must have a \"{name}\" array of strings."));
        return (v.EnumerateArray().Select(e => e.GetString()!).ToList(), null);
    }

    /// <summary>The report version the page was showing - required, because what is sent or marked read is that one.</summary>
    private static (int Value, IResult? Bad) Version(JsonElement body)
    {
        if (!body.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out var version) || version < 1)
            return (0, BadRequest("bad_request_body", "The body must have a \"version\": the version of the report the page was showing."));
        return (version, null);
    }
}

/// <summary>
/// What each team report route answers, once the gate has let the request in and the caller is known. Separate from the
/// mapping so every branch is tested without a host. Every sentence and flag a screen shows is decided here (rule 7).
/// </summary>
internal sealed class TeamReports
{
    private readonly DevReportStore _store;
    private readonly DevReportRecipients _recipients;
    private readonly DevReportPersonComments _comments;
    private readonly TeamRegistry _teams;
    private readonly TeamAccess _access;
    private readonly Func<DateTime> _utcNow;

    public TeamReports(DevReportStore store, DevReportRecipients recipients, DevReportPersonComments comments,
        TeamRegistry teams, TeamAccess access, Func<DateTime>? utcNow = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _comments = comments ?? throw new ArgumentNullException(nameof(comments));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // ---------------------------------------------------------------- the recipient

    /// <summary>The reports sent to the caller, newest first, and whether the page also shows their own reports.</summary>
    public IResult SentToMe(string teamId, string caller)
    {
        var team = Team(teamId);
        var names = Names(teamId, caller);
        var list = _recipients.SentTo(team, caller).Select(r => Received(r, names)).ToList();
        FileLog.Write($"[TeamReports] SentToMe: team {team.ToLogString()} count={list.Count}");
        return Results.Json(new
        {
            teamId,
            count = list.Count,
            reports = list,
            emptyText = TeamReportEndpoints.SentToMeEmpty,
            showYourReports = _access.Decide(teamId, caller, TeamAction.RunSessionsOnOwnComputers).Allowed,
        });
    }

    /// <summary>
    /// One report sent to the caller - the version they were sent - with the comments they wrote on it, or not found. It
    /// says whether a comment can be sent and where it goes, and that the page's notes are off: a reader here has no
    /// agent conversation to send them into (review F2; the flag is the Gateway's, never the page's).
    /// </summary>
    public IResult SentToMeDetail(string teamId, string caller, string reportId)
    {
        var team = Team(teamId);
        if (Sent(teamId, caller, reportId) is not var (report, row))
            return TeamReportEndpoints.NotFound(TeamReportEndpoints.NotSentToYou);
        var held = _store.GetVersion(team, report.Id, row.SentVersion)
                   ?? throw new InvalidOperationException($"Report {report.Id} was sent at version {row.SentVersion}, which is not stored.");
        var names = Names(teamId, caller);
        var canComment = AuthorCanReceive(teamId, report);
        return Results.Json(new
        {
            report = Received(new DevReportReceived(row, held.Title, held.Status), names),
            comments = _comments.From(team, report.Id, caller).Select(c => new { id = c.Id.ToString("D"), text = c.Text, atUtc = c.AtUtc }).ToList(),
            canComment,
            commentsNote = canComment
                ? TeamReportEndpoints.CommentsGoTo(NameOf(report.AuthorSubject, names))
                : TeamReportEndpoints.AuthorCannotReceive,
            notesOpen = false,
            // The report's own answer controls: off, so a reader cannot pick an answer that goes nowhere (round-3
            // review R1). The Collaborator's Questions (devthrottle_internal#2307) turns this on for a question put to
            // them; the page follows this flag and nothing else.
            answersOpen = false,
        });
    }

    /// <summary>The report when it was sent to the caller; otherwise null, whether or not it exists.</summary>
    public DevReportEntity? SentToMeReport(string teamId, string caller, string reportId) =>
        Sent(teamId, caller, reportId)?.Report;

    /// <summary>
    /// The bytes of the version sent to the caller (Tech Lead ruling F1): a later version reaches them only when the author
    /// sends again, and an earlier one never. A <c>?version=</c> naming any other version is not found.
    /// </summary>
    public IResult SentToMeHtml(string teamId, string caller, string reportId, HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (Sent(teamId, caller, reportId) is not var (report, row))
            return TeamReportEndpoints.NotFound(TeamReportEndpoints.NotSentToYou);
        var raw = ctx.Request.Query["version"].ToString();
        if (raw.Length > 0 && (!int.TryParse(raw, out var asked) || asked != row.SentVersion))
        {
            FileLog.Write($"[TeamReports] SentToMeHtml: team {Team(teamId).ToLogString()} report={report.Id} asked a version other than the one sent ({row.SentVersion}) - REFUSED");
            return TeamReportEndpoints.NotFound(TeamReportEndpoints.VersionNotSentToYou);
        }
        return DevReportEndpoints.ServeVersion(_store, Team(teamId), report, row.SentVersion, ctx);
    }

    /// <summary>The report and the caller's row on it, when it was sent to them; otherwise null.</summary>
    private (DevReportEntity Report, DevReportRecipientEntity Row)? Sent(string teamId, string caller, string reportId)
    {
        var team = Team(teamId);
        if (!Guid.TryParse(reportId, out var id) || _recipients.RowFor(team, id, caller) is not { } row
            || _store.Get(team, id) is not { } report)
        {
            FileLog.Write($"[TeamReports] Sent: team {team.ToLogString()} report={reportId} - not sent to the caller, REFUSED");
            return null;
        }
        return (report, row);
    }

    /// <summary>
    /// Whether the report's author can still read comments on it here: a member of the team whose role opens their own
    /// reports - the same cell the author's routes are decided on (review F6).
    /// </summary>
    private bool AuthorCanReceive(string teamId, DevReportEntity report) =>
        !string.IsNullOrWhiteSpace(report.AuthorSubject)
        && _access.Decide(teamId, report.AuthorSubject, TeamAction.RunSessionsOnOwnComputers).Allowed;

    /// <summary>
    /// The caller opened <paramref name="version"/> of a report sent to them. Marked read only when that is the version
    /// they hold (delta review D5); otherwise 409 with the Gateway's sentence, and the page reads again.
    /// </summary>
    public IResult MarkRead(string teamId, string caller, string reportId, int version)
    {
        if (SentToMeReport(teamId, caller, reportId) is not { } report)
            return TeamReportEndpoints.NotFound(TeamReportEndpoints.NotSentToYou);
        return _recipients.MarkRead(Team(teamId), report.Id, caller, version, _utcNow()) switch
        {
            DevReportReadMark.Read => Results.Json(new { read = true, readLabel = TeamReportEndpoints.ReadLabel }),
            DevReportReadMark.VersionNotHeld => Results.Json(
                new { error = TeamReportEndpoints.ReadVersionNotHeld, code = "version_not_held" }, statusCode: StatusCodes.Status409Conflict),
            _ => TeamReportEndpoints.NotFound(TeamReportEndpoints.NotSentToYou),
        };
    }

    /// <summary>A comment on a report sent to the caller, stored for the report's author person.</summary>
    public IResult Comment(string teamId, string caller, string reportId, string text)
    {
        if (SentToMeReport(teamId, caller, reportId) is not { } report)
            return TeamReportEndpoints.NotFound(TeamReportEndpoints.NotSentToYou);
        if (string.IsNullOrWhiteSpace(text))
            return TeamReportEndpoints.BadRequest("comment_empty", "The comment is empty. Write what you want the author to read.");
        if (text.Length > DevReportPersonComments.MaxLength)
            return TeamReportEndpoints.BadRequest("comment_too_long",
                $"The comment is {text.Length} characters; the limit is {DevReportPersonComments.MaxLength}.");
        if (string.IsNullOrWhiteSpace(report.AuthorSubject))
            throw new InvalidOperationException($"Report {report.Id} was sent to a member but records no author.");
        if (!AuthorCanReceive(teamId, report))
        {
            FileLog.Write($"[TeamReports] Comment: team {Team(teamId).ToLogString()} report={report.Id} - the author can no longer read comments here, REFUSED");
            return Results.Json(new { error = TeamReportEndpoints.AuthorCannotReceive, code = "author_cannot_receive", canComment = false },
                statusCode: StatusCodes.Status409Conflict);
        }

        var row = _comments.Add(Team(teamId), report.Id, caller, report.AuthorSubject, text, _utcNow());
        return Results.Json(new { comment = new { id = row.Id.ToString("D"), text = row.Text, atUtc = row.AtUtc } });
    }

    // ---------------------------------------------------------------- the author

    /// <summary>The caller's own reports in the team, newest change first.</summary>
    public IResult Mine(string teamId, string caller)
    {
        var team = Team(teamId);
        var own = _store.ListByAuthor(team, caller);
        var ids = own.Select(r => r.Id).ToList();
        var sentTo = _recipients.RecipientCounts(team, ids);
        var commented = _comments.CountsTo(team, caller, ids);
        FileLog.Write($"[TeamReports] Mine: team {team.ToLogString()} count={own.Count}");
        return Results.Json(new
        {
            teamId,
            count = own.Count,
            emptyText = TeamReportEndpoints.MineEmpty,
            reports = own.Select(r =>
            {
                var people = sentTo.TryGetValue(r.Id, out var n) ? n : 0;
                var said = commented.TryGetValue(r.Id, out var c) ? c : 0;
                return new
                {
                    id = r.Id.ToString("D"),
                    title = r.Title,
                    status = r.Status,
                    version = r.Version,
                    updatedAtUtc = r.UpdatedAtUtc,
                    sentToLabel = people == 0 ? "Not sent to anyone yet" : people == 1 ? "Sent to 1 person" : $"Sent to {people} people",
                    commentsLabel = said == 0 ? null : said == 1 ? "1 comment" : $"{said} comments",
                };
            }).ToList(),
        });
    }

    /// <summary>One of the caller's reports when they wrote it; otherwise null.</summary>
    public DevReportEntity? OwnReport(string teamId, string caller, string reportId)
    {
        var team = Team(teamId);
        if (!Guid.TryParse(reportId, out var id) || _store.Get(team, id) is not { } report
            || !string.Equals(report.AuthorSubject, caller, StringComparison.Ordinal))
            return null;
        return report;
    }

    /// <summary>One of the caller's reports: who it was sent to, who it can still go to, and the comments people wrote.</summary>
    public IResult MineDetail(string teamId, string caller, string reportId)
    {
        if (OwnReport(teamId, caller, reportId) is not { } report)
            return TeamReportEndpoints.NotFound(TeamReportEndpoints.NoSuchOwnReport);
        return Results.Json(Describe(teamId, caller, report));
    }

    /// <summary>
    /// Send <paramref name="version"/> of one of the caller's reports - the version their page was showing - to members
    /// of the team, named by their Team page ids. A member who holds an earlier version is moved to this one (Tech Lead
    /// ruling F1). The version must be the report's newest: when the session published another while the author was
    /// reading, nothing is sent and the answer is 409 with the Gateway's sentence, so a person is never sent a version
    /// its author did not see (delta review D2). All or nothing: every id must be a member of THIS team whose role may
    /// read reports sent to them, and not the caller; otherwise nothing is recorded and the answer says which and why.
    /// </summary>
    public IResult Send(string teamId, string caller, string reportId, IReadOnlyList<string> memberIds, int version)
    {
        ArgumentNullException.ThrowIfNull(memberIds);
        if (OwnReport(teamId, caller, reportId) is not { } report)
            return TeamReportEndpoints.NotFound(TeamReportEndpoints.NoSuchOwnReport);
        if (memberIds.Count == 0)
            return TeamReportEndpoints.BadRequest("no_recipients", "Choose at least one person to send the report to.");
        // Refused early when it is already stale. The decision that counts is the recipients store's own conditional
        // write below, which is the moment the send takes effect (round-3 review R2).
        if (version != report.Version)
        {
            FileLog.Write($"[TeamReports] Send: team {Team(teamId).ToLogString()} report={report.Id} - page showed version {version}, newest is {report.Version}, REFUSED");
            return NotTheNewest();
        }

        var members = Members(teamId, caller);
        var subjects = new List<string>();
        foreach (var memberId in memberIds.Select(m => m.Trim()).Distinct(StringComparer.Ordinal))
        {
            var member = members.FirstOrDefault(m => string.Equals(TeamMemberIds.For(teamId, m.AccountSubject), memberId, StringComparison.Ordinal));
            if (member is null)
            {
                FileLog.Write($"[TeamReports] Send: team {Team(teamId).ToLogString()} report={report.Id} - a recipient is not a member, REFUSED");
                return Results.Json(new
                {
                    error = "One of the people chosen is not a member of this team, so the report was not sent to anyone.",
                    code = "not_a_member",
                }, statusCode: StatusCodes.Status400BadRequest);
            }
            if (string.Equals(member.AccountSubject, caller, StringComparison.Ordinal))
                return TeamReportEndpoints.BadRequest("not_to_yourself", "You wrote this report, so it is not sent to you. Nothing was sent.");
            var decision = _access.Decide(teamId, member.AccountSubject, TeamAction.AnswerQuestionsSendRequestsReadReports);
            if (!decision.Allowed)
                return Results.Json(new
                {
                    error = $"{TeamRegistry.DisplayName(member)} cannot read reports sent to them in this team, so the report was not sent to anyone.",
                    code = "recipient_may_not_read",
                }, statusCode: StatusCodes.Status403Forbidden);
            subjects.Add(member.AccountSubject);
        }

        if (!_recipients.Send(Team(teamId), report.Id, caller, subjects, version, _utcNow()).Sent)
        {
            FileLog.Write($"[TeamReports] Send: team {Team(teamId).ToLogString()} report={report.Id} - a newer version was published before the send took effect, REFUSED");
            return NotTheNewest();
        }
        return Results.Json(Describe(teamId, caller, report));
    }

    private static IResult NotTheNewest() => Results.Json(new
    {
        error = TeamReportEndpoints.NotTheNewestVersion,
        code = "version_not_newest",
    }, statusCode: StatusCodes.Status409Conflict);

    // ---------------------------------------------------------------- shapes and helpers

    private object Describe(string teamId, string caller, DevReportEntity report)
    {
        var team = Team(teamId);
        var members = Members(teamId, caller);
        var names = members.ToDictionary(m => m.AccountSubject, TeamRegistry.DisplayName, StringComparer.Ordinal);
        var sent = _recipients.RecipientsOf(team, report.Id);
        var held = sent.ToDictionary(r => r.RecipientSubject, r => r.SentVersion, StringComparer.Ordinal);
        return new
        {
            report = new
            {
                id = report.Id.ToString("D"),
                title = report.Title,
                status = report.Status,
                version = report.Version,
                publishedAtUtc = report.PublishedAtUtc,
                updatedAtUtc = report.UpdatedAtUtc,
            },
            recipients = sent.Select(r => new
            {
                memberId = TeamMemberIds.For(teamId, r.RecipientSubject),
                name = NameOf(r.RecipientSubject, names),
                sentAtUtc = r.SentAtUtc,
                sentVersion = r.SentVersion,
                versionLabel = r.SentVersion == report.Version ? null : $"Has version {r.SentVersion} of {report.Version}",
                read = r.ReadAtUtc is not null,
                readLabel = r.ReadAtUtc is null ? "Not read yet" : TeamReportEndpoints.ReadLabel,
            }).ToList(),
            recipientsEmptyText = TeamReportEndpoints.NotSentYet,
            // Who it can go to now: a member other than the author who may read reports sent to them - asked of the one
            // permission check the send is decided by (review F12) - and who does not already hold this version.
            choices = members
                .Where(m => !string.Equals(m.AccountSubject, caller, StringComparison.Ordinal)
                            && (!held.TryGetValue(m.AccountSubject, out var v) || v < report.Version)
                            && _access.Decide(teamId, m.AccountSubject, TeamAction.AnswerQuestionsSendRequestsReadReports).Allowed)
                .Select(m => new
                {
                    memberId = TeamMemberIds.For(teamId, m.AccountSubject),
                    name = TeamRegistry.DisplayName(m),
                    role = TeamRoles.Label(m.Role),
                    heldLabel = held.TryGetValue(m.AccountSubject, out var v)
                        ? $"Has version {v} - sending gives them version {report.Version}"
                        : null,
                })
                .ToList(),
            sendNote = TeamReportEndpoints.SendNote,
            notesOpen = false,
            // The author cannot answer their own agent here either (gap 2 in the proof README): off, so the report's own
            // answer controls are drawn disabled rather than live and lost.
            answersOpen = false,
            comments = _comments.To(team, report.Id, caller).Select(c => new
            {
                id = c.Id.ToString("D"),
                from = NameOf(c.FromSubject, names),
                text = c.Text,
                atUtc = c.AtUtc,
            }).ToList(),
            commentsEmptyText = "No comments yet. When someone you sent this report to comments on it, it appears here.",
        };
    }

    /// <summary>One report as its recipient sees it: the version they hold, its title and status.</summary>
    private static object Received(DevReportReceived received, IReadOnlyDictionary<string, string> names) => new
    {
        id = received.Row.ReportId.ToString("D"),
        title = received.Title,
        status = received.Status,
        version = received.Row.SentVersion,
        from = NameOf(received.Row.SentBySubject, names),
        sentAtUtc = received.Row.SentAtUtc,
        read = received.Row.ReadAtUtc is not null,
        readLabel = received.Row.ReadAtUtc is null ? TeamReportEndpoints.NewLabel : TeamReportEndpoints.ReadLabel,
    };

    private IReadOnlyList<TeamMember> Members(string teamId, string caller)
    {
        var result = _teams.ListMembers(teamId, caller);
        if (result.Outcome != TeamMembersOutcome.Found)
            throw new InvalidOperationException("A team report route ran for a caller who is not a member - the team gate was not run.");
        return result.Members;
    }

    private IReadOnlyDictionary<string, string> Names(string teamId, string caller) =>
        Members(teamId, caller).ToDictionary(m => m.AccountSubject, TeamRegistry.DisplayName, StringComparer.Ordinal);

    private static string NameOf(string? subject, IReadOnlyDictionary<string, string> names) =>
        subject is not null && names.TryGetValue(subject, out var name) ? name : TeamReportEndpoints.FormerMember;

    private static TenantId Team(string teamId) => new(teamId);
}
