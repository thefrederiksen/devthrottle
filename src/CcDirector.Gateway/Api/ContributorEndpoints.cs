using System.Globalization;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// A CONTRIBUTOR'S FACTORIES AND INBOX (Team implementation piece 4.1, devthrottle_internal#2798; the contributor screens
/// of docs/architecture/teams/mockup.html). A contributor is a customer following their project, or someone hired for a
/// task; what they see answers "what do you need from me, where is my project, what have I got so far" - and nothing else.
///
/// <list type="bullet">
/// <item><c>GET /teams/{teamId}/contributor/factories</c> - the factories sent to the caller in this team, as cards.</item>
/// <item><c>GET /teams/{teamId}/contributor/factories/{factory}</c> - one factory's customer page: needs you, where the
/// project is, today in your factory, what you have so far, sent to you, what you can do here.</item>
/// <item><c>GET /teams/{teamId}/contributor/inbox</c> - only what was sent to the caller, and their conversation with the
/// team's Owner.</item>
/// <item><c>GET /teams/{teamId}/contributor/factories/{factory}/watch?window=&amp;from=&amp;to=</c> - "Watch your factory": the
/// contributor audience of <see cref="FactoryWatchFold"/> for one factory sent to them (version one, piece 4), carrying the
/// explainer card until the person has seen it (piece 6).</item>
/// <item><c>POST /teams/{teamId}/contributor/factories/{factory}/watch/explainer-seen</c> - the person pressed the explainer
/// card's button: it is not shown to them again, on any team or surface (piece 6). The one write here.</item>
/// </list>
///
/// COMBINED ACROSS TEAMS, ONE ROUTE PER TEAM. A person on several teams sees one Factories page and one Inbox; the client
/// asks each of their teams (from <c>GET /teams</c>) and shows every item together. Each route stays under
/// <c>/teams/{teamId}/</c> so the team gate asks the role table for THAT team on every read (security review M2), a team
/// the person left answers "no such team" by the gate's own rule, and no new gate kind is needed for a person-level read.
/// The Gateway still owns the merge: every card and item carries its team's label and an <c>order</c> key, and the
/// client sorts the combined list by that key alone (rule 7).
///
/// AGENTS HIDDEN. Nothing here names a session, a seat, a computer, a folder, a model or an internal step. The only words
/// from a factory's activity that leave are the ones written FOR a contributor
/// (<see cref="Data.Entities.FactoryActivityEntity.ForContributor"/>), checked when they were written.
///
/// WHO SEES A FACTORY is decided once, by <see cref="ContributorFactoryAccess"/>; a factory it refuses is answered
/// exactly as one that does not exist.
/// </summary>
internal static class ContributorEndpoints
{
    /// <summary>The group every route here lives under.</summary>
    public const string GroupPath = TeamEndpoints.Path + "/{teamId}/contributor";

    /// <summary>The Factories list, as the route table writes it.</summary>
    public const string FactoriesPattern = GroupPath + "/factories";

    /// <summary>One factory's watch view, as the route table writes it.</summary>
    public const string WatchPattern = FactoriesPattern + "/{factory}/watch";

    /// <summary>"I have seen the explainer card", as the route table writes it.</summary>
    public const string ExplainerSeenPattern = WatchPattern + "/explainer-seen";

    /// <summary>The Inbox, as the route table writes it.</summary>
    public const string InboxPattern = GroupPath + "/inbox";

    internal const string CallerItemKey = "cc.teams.contributor.caller";

    /// <summary>Maps the five routes.</summary>
    public static void Map(IEndpointRouteBuilder app, ContributorPages pages, HostedTenantBoundary boundary, TenantRegistry tenants)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(pages);
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
                    FileLog.Write($"[ContributorEndpoints] {http.Request.Method} {http.Request.Path} FAILED ({ex.GetType().Name}): {ex.Message}");
                    return Results.Json(new { error = "DevThrottle could not do this just now because of a fault. Try again shortly." },
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }
        });

        group.MapGet("/factories", (HttpContext ctx, string teamId) => Results.Json(pages.Factories(teamId, Caller(ctx))));
        group.MapGet("/factories/{factory}", (HttpContext ctx, string teamId, string factory) =>
            pages.CustomerPage(teamId, Caller(ctx), factory) is { } page
                ? Results.Json(page)
                : NotSentToYou());
        group.MapGet("/factories/{factory}/watch", (HttpContext ctx, string teamId, string factory, string? window, DateTime? from, DateTime? to) =>
            Watch(pages, teamId, Caller(ctx), factory, window, from, to));
        group.MapPost("/factories/{factory}/watch/explainer-seen", (HttpContext ctx, string teamId, string factory) =>
            ExplainerSeen(pages, teamId, Caller(ctx), factory));
        group.MapGet("/inbox", (HttpContext ctx, string teamId) => Results.Json(pages.Inbox(teamId, Caller(ctx))));

        FileLog.Write($"[ContributorEndpoints] mapped {FactoriesPattern} (list, one), {WatchPattern}, {ExplainerSeenPattern} and {InboxPattern}");
    }

    /// <summary>The watch route's answer: the view; the customer page's own not-found for every refusal; or a 400 naming
    /// what is wrong with the window - asked only after the check allowed the factory, so a refused factory never
    /// answers anything but the not-found.</summary>
    internal static IResult Watch(ContributorPages pages, string teamId, string caller, string factory, string? window, DateTime? from, DateTime? to)
    {
        try
        {
            return pages.Watch(teamId, caller, factory, window, from, to) is { } view
                ? Results.Json(view)
                : NotSentToYou();
        }
        catch (FactoryViewValidationException ex)
        {
            FileLog.Write($"[ContributorEndpoints] GET watch: window refused: {ex.Message}");
            return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>The explainer-seen route's answer: 204 once recorded, or the customer page's own not-found for every refusal,
    /// so the route says nothing about a factory the person may not watch.</summary>
    internal static IResult ExplainerSeen(ContributorPages pages, string teamId, string caller, string factory) =>
        pages.MarkExplainerSeen(teamId, caller, factory) ? Results.NoContent() : NotSentToYou();

    /// <summary>THE one refusal a contributor reads for a factory, whichever condition failed: the customer page and the
    /// watch view answer it with the same bytes.</summary>
    internal static IResult NotSentToYou() =>
        Results.Json(new { error = ContributorFactoryAccess.NotSentToYou, code = ContributorPages.NotFoundCode },
            statusCode: StatusCodes.Status404NotFound);

    /// <summary>The person the filter admitted. Set on every request that reaches a route here.</summary>
    internal static string Caller(HttpContext ctx) =>
        ctx.Items.TryGetValue(CallerItemKey, out var value) && value is string subject && subject.Length > 0
            ? subject
            : throw new InvalidOperationException("A contributor route ran without the caller its filter admits.");
}

/// <summary>
/// What each contributor route answers, once the gate has let the request in and the caller is known. Separate from the
/// mapping so every branch is tested without a host. Every sentence, label, tone and order key a screen shows is decided
/// here (rule 7); the shapes are anonymous objects whose every field is listed, so a field never leaks in by being
/// added to a stored row.
/// </summary>
internal sealed class ContributorPages
{
    /// <summary>The error code on a customer page that is not the caller's to see.</summary>
    public const string NotFoundCode = "factory_not_found";

    internal const string FactoriesEmpty = "No factory is building something for you yet. Work sent to you arrives in your Inbox.";
    internal const string InboxEmpty = "Nothing has been sent to you yet.";
    internal const string FactoriesIntro = "The factories building something for you.";
    internal const string InboxIntro = "Only yours: what was sent to you, and your conversation with the team's Owner.";
    internal const string WriteLabel = "Write to the team's Owner";
    internal const string WritePlaceholder = "An idea, a change, a problem...";
    internal const string WriteNote = "Goes to the team's Owner only. They decide what reaches the factory.";
    internal const string NeedsYouEmpty = "Nothing right now.";
    internal const string TodayEmpty = "Nothing recorded today yet.";
    internal const string SoFarEmpty = "Nothing to show yet. Previews and the live site appear here once they are ready for you.";
    internal const string SentToYouEmpty = "Nothing yet.";
    internal const string CanDoText = "See where your project is - answer what is sent to you - look at previews - write to the team's Owner.";
    internal const string CanDoNote =
        "You cannot talk to the factory, start or stop anything, or see its settings. Everything you write goes to the team's Owner first.";
    internal const string StageNotSet = "Where your project is has not been set yet.";
    internal const string AnswerLabel = "Answer";

    /// <summary>What a contributor reads on a message the team's Owner forwarded (piece 4.3). It says that it happened and
    /// nothing about the factory: not which one, not who in it, not what it did.</summary>
    internal const string ForwardedNotice = "The team's Owner read this and forwarded it to the factory, in their name.";

    /// <summary>The five steps of a customer's project, in order (design.html: idea, plan, building, checking, live).</summary>
    internal static readonly IReadOnlyList<string> Stages = new[] { "Idea", "Plan", "Building", "Checking", "Live" };

    /// <summary>How many of a factory's newest activity rows are read for the words written for a contributor.</summary>
    internal const int ActivityRowsRead = 200;

    /// <summary>The most "today" lines a customer page shows.</summary>
    internal const int TodayLines = 20;

    private readonly ContributorFactoryAccess _access;
    private readonly FactoryContributors _shares;
    private readonly FactoryActivityRecord _activity;
    private readonly DevReportStore _reports;
    private readonly DevReportRecipients _recipients;
    private readonly TeamQuestions _questions;
    private readonly TeamRequestStore _requests;
    private readonly TeamRegistry _teams;
    private readonly ContributorWatchExplainer _explainer;
    private readonly Func<TenantId, string> _timeZoneOf;
    private readonly Func<DateTime> _utcNow;

    public ContributorPages(ContributorFactoryAccess access, FactoryContributors shares, FactoryActivityRecord activity, DevReportStore reports,
        DevReportRecipients recipients, TeamQuestions questions, TeamRequestStore requests, TeamRegistry teams,
        ContributorWatchExplainer explainer, Func<TenantId, string> timeZoneOf, Func<DateTime>? utcNow = null)
    {
        _explainer = explainer ?? throw new ArgumentNullException(nameof(explainer));
        _access = access ?? throw new ArgumentNullException(nameof(access));
        _shares = shares ?? throw new ArgumentNullException(nameof(shares));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _reports = reports ?? throw new ArgumentNullException(nameof(reports));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _questions = questions ?? throw new ArgumentNullException(nameof(questions));
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _timeZoneOf = timeZoneOf ?? throw new ArgumentNullException(nameof(timeZoneOf));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    // ---------------------------------------------------------------- Factories

    /// <summary>The factories sent to the caller in one team, as cards.</summary>
    public object Factories(string teamId, string caller)
    {
        var teamLabel = TeamLabel(teamId, caller);
        var received = ReceivedFromFactories(teamId, caller);
        var cards = _access.FactoriesFor(teamId, caller).Select(v =>
        {
            var f = v.Factory!;
            var mine = received.Where(r => r.Factory == f.Factory).ToList();
            var waiting = mine.Count(r => r.Waiting);
            var latest = ContributorLines(teamId, f.Factory).FirstOrDefault();
            var lastAt = new[] { latest?.AtUtc, mine.Select(r => (DateTime?)r.SentAtUtc).DefaultIfEmpty(null).Max(), v.Share!.SharedAtUtc }
                .Where(d => d is not null).Max()!.Value;
            return new
            {
                teamId,
                teamLabel,
                factory = f.Factory,
                name = f.Title,
                nowText = latest?.Text,
                needsYouLabel = NeedsYouLabel(waiting),
                watchLabel = ContributorWatchExplainer.WatchLabel,
                order = Order(waiting > 0, lastAt, f.Title, teamId),
            };
        }).ToList();
        FileLog.Write($"[ContributorPages] Factories: team {new TenantId(teamId).ToLogString()} count={cards.Count}");
        return new { teamId, teamLabel, count = cards.Count, intro = FactoriesIntro, emptyText = FactoriesEmpty, factories = cards };
    }

    /// <summary>One factory's customer page, or null when <see cref="ContributorFactoryAccess"/> refuses it - whichever
    /// condition failed, the route answers the same not-found.</summary>
    public object? CustomerPage(string teamId, string caller, string factory)
    {
        var verdict = _access.Check(teamId, caller, factory);
        if (!verdict.Allowed)
            return null;

        var f = verdict.Factory!;
        var mine = ReceivedFromFactories(teamId, caller).Where(r => r.Factory == f.Factory).ToList();
        var zone = TimeZoneInfo.FindSystemTimeZoneById(_timeZoneOf(new TenantId(teamId)));
        var now = _utcNow();
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(now, DateTimeKind.Utc), zone);
        var midnightUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified), zone);
        var today = ContributorLines(teamId, f.Factory)
            .Where(l => l.AtUtc >= midnightUtc)
            .Take(TodayLines)
            .Select(l => new
            {
                text = l.Text,
                atUtc = l.AtUtc,
                timeLabel = TimeZoneInfo.ConvertTimeFromUtc(l.AtUtc, zone).ToString("HH:mm", CultureInfo.InvariantCulture),
            })
            .ToList();

        FileLog.Write($"[ContributorPages] CustomerPage: team {new TenantId(teamId).ToLogString()} factory={f.Factory} sent={mine.Count} today={today.Count}");
        return new
        {
            teamId,
            teamLabel = verdict.TeamName,
            factory = f.Factory,
            name = f.Title,
            writeLabel = WriteLabel,
            needsYou = new
            {
                title = "Needs you",
                emptyText = NeedsYouEmpty,
                items = mine.Where(r => r.Waiting).Select(r => new { reportId = r.ReportId, title = r.Title, actionLabel = AnswerLabel }).ToList(),
            },
            stage = new
            {
                title = "Where your project is",
                // Nothing on main records a customer project's step yet, so none is current and the page says so.
                steps = Stages.Select(s => new { label = s, state = "later" }).ToList(),
                note = StageNotSet,
            },
            today = new { title = "Today in your factory", emptyText = TodayEmpty, items = today },
            soFar = new { title = "What you have so far", emptyText = SoFarEmpty, items = Array.Empty<object>() },
            sentToYou = new
            {
                title = "Sent to you",
                emptyText = SentToYouEmpty,
                items = mine.Select(r => new
                {
                    reportId = r.ReportId,
                    title = r.Title,
                    stateLabel = r.Waiting ? "Waiting on you" : "Done",
                    tone = r.Waiting ? "waiting" : "done",
                }).ToList(),
            },
            canDo = new { title = "What you can do here", text = CanDoText, note = CanDoNote },
        };
    }

    // ---------------------------------------------------------------- Watch your factory

    /// <summary>
    /// "Watch your factory" for a contributor (version one, piece 4): the CONTRIBUTOR audience of
    /// <see cref="FactoryWatchFold"/> for one factory, or null when <see cref="ContributorFactoryAccess"/> refuses it -
    /// whichever condition failed, the route answers the customer page's own not-found.
    ///
    /// The factory, its tenant and the rows come only from the check's verdict and the team the route names: the
    /// factory is the verdict's registered one (never looked up a second time), the rows are read in the team's tenant
    /// under the verdict's spelling, and the machine names are the team account's. What is the caller's own is
    /// version one's rule (A), decided in <see cref="ContributorWatchScope"/>.
    /// </summary>
    public FactoryWatchViewDto? Watch(string teamId, string caller, string factory, string? window, DateTime? fromUtc, DateTime? toUtc)
    {
        var verdict = _access.Check(teamId, caller, factory);
        if (!verdict.Allowed)
            return null;

        var team = new TenantId(teamId);
        var f = verdict.Factory!;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(_timeZoneOf(team));
        var w = FactoryAgentsFold.ResolveWindow(window, fromUtc, toUtc, _utcNow(), FactoryAgentsFold.WindowLast24h, zone);
        var (rows, truncated) = FactoryAgentsViewEndpoints.ReadAll(
            q => _activity.Query(team, q.Factory, q.Agent, q.Outcome, q.FromUtc, q.ToUtc, q.OldestFirst, q.Offset, q.Limit),
            new FactoryRecordQuery(f.Factory, null, null, w.FromUtc, w.ToUtc, true, 0, FactoryAgentsViewEndpoints.PageSize));
        var scope = ContributorWatchScope.For(_shares.ContributorsOf(team, f.Factory), caller, _activity.MachineNamesOf(team));

        var view = FactoryWatchFold.View(f, rows, truncated, w, zone, FactoryWatchAudience.Contributor, scope);
        view.Explainer = _explainer.For(caller);
        FileLog.Write($"[ContributorPages] Watch: team {team.ToLogString()} factory={f.Factory} window={w.Key} rows={rows.Count} " +
                      $"truncated={truncated} wholeFactory={scope.WholeFactory} steps={view.Steps.Count} explainer={view.Explainer is not null}");
        return view;
    }

    /// <summary>
    /// The person pressed the explainer card's button on one factory's watch view (piece 6). Asks the same check as the
    /// view first, so only someone who may watch this factory can record it, and a refusal is the same not-found; then
    /// records it on the person's own account, for every team and surface. False when the check refused.
    /// </summary>
    public bool MarkExplainerSeen(string teamId, string caller, string factory)
    {
        var verdict = _access.Check(teamId, caller, factory);
        if (!verdict.Allowed)
            return false;

        _explainer.MarkSeen(caller);
        FileLog.Write($"[ContributorPages] MarkExplainerSeen: team {new TenantId(teamId).ToLogString()} factory={verdict.Factory!.Factory}");
        return true;
    }

    // ---------------------------------------------------------------- Inbox

    /// <summary>Only what was sent to the caller in one team, and their conversation with the team's Owner.</summary>
    public object Inbox(string teamId, string caller)
    {
        var teamLabel = TeamLabel(teamId, caller);
        var team = new TenantId(teamId);
        var received = _recipients.SentTo(team, caller);
        var questions = _questions.ByReport(teamId, caller, received);
        var names = TeamReports.MemberNames(_teams, teamId, caller);

        var reports = received.Select(r =>
        {
            var asked = questions.TryGetValue(r.Row.ReportId, out var q) ? q : null;
            var isWaiting = asked is { Waiting: > 0 };
            var isRead = r.Row.ReadAtUtc is not null;
            var (label, tone) = isWaiting ? ("Waiting on you", "waiting") : isRead ? ("Read", "done") : ("New", "new");
            return new InboxItem(
                Kind: "report",
                Id: r.Row.ReportId.ToString("D"),
                Title: r.Title,
                Text: null,
                FromLabel: "Sent by " + TeamReports.NameOf(r.Row.SentBySubject, names),
                AtUtc: r.Row.SentAtUtc,
                StateLabel: label,
                Tone: tone,
                Trail: Array.Empty<object>(),
                Notice: null,
                Questions: asked is null ? Array.Empty<object>() : asked.Cards,
                Order: Order(isWaiting, r.Row.SentAtUtc, r.Title, teamId));
        });

        var mine = _requests.ListMine(teamId, caller);
        if (mine.Outcome != TeamRequestOutcome.Done)
            throw new InvalidOperationException($"A contributor's own requests could not be read after the gate allowed the inbox: {mine.Outcome}.");
        var messages = mine.Requests.Select(q => new InboxItem(
            Kind: "message",
            Id: q.Id,
            Title: "You wrote to the team's Owner",
            Text: q.Text,
            FromLabel: "From you",
            AtUtc: q.UpdatedAtUtc,
            StateLabel: q.StateLabel,
            Tone: "message",
            Trail: q.Trail.Select(s => (object)new { label = s.Label, by = s.By, atUtc = s.AtUtc, reason = s.Reason, sentence = s.Sentence }).ToList(),
            Notice: q.State == TeamRequestStates.Forwarded ? ForwardedNotice : null,
            Questions: Array.Empty<object>(),
            Order: Order(false, q.UpdatedAtUtc, q.Text, teamId)));

        var items = reports.Concat(messages).OrderBy(i => i.Order, StringComparer.Ordinal).Select(i => new
        {
            kind = i.Kind,
            id = i.Id,
            teamId,
            teamLabel,
            title = i.Title,
            text = i.Text,
            fromLabel = i.FromLabel,
            atUtc = i.AtUtc,
            stateLabel = i.StateLabel,
            tone = i.Tone,
            trail = i.Trail,
            notice = i.Notice,
            questions = i.Questions,
            order = i.Order,
        }).ToList();
        FileLog.Write($"[ContributorPages] Inbox: team {team.ToLogString()} count={items.Count}");
        return new
        {
            teamId,
            teamLabel,
            count = items.Count,
            emptyText = InboxEmpty,
            intro = InboxIntro,
            write = new { label = WriteLabel, placeholder = WritePlaceholder, sendLabel = "Send", note = WriteNote },
            items,
        };
    }

    private sealed record InboxItem(string Kind, string Id, string Title, string? Text, string FromLabel, DateTime AtUtc,
        string StateLabel, string Tone, IReadOnlyList<object> Trail, string? Notice, IReadOnlyList<object> Questions, string Order);

    // ---------------------------------------------------------------- helpers

    /// <summary>One report sent to the caller, with the factory that published it.</summary>
    private sealed record ReceivedReport(string ReportId, string Title, string? Factory, bool Waiting, DateTime SentAtUtc);

    private List<ReceivedReport> ReceivedFromFactories(string teamId, string caller)
    {
        var team = new TenantId(teamId);
        var received = _recipients.SentTo(team, caller);
        var waiting = _questions.WaitingByReport(teamId, caller, received);
        return received.Select(r => new ReceivedReport(
                r.Row.ReportId.ToString("D"),
                r.Title,
                _reports.Get(team, r.Row.ReportId)?.Factory,
                waiting.TryGetValue(r.Row.ReportId, out var n) && n > 0,
                r.Row.SentAtUtc))
            .ToList();
    }

    /// <summary>One line written for a contributor in a factory's activity record.</summary>
    private sealed record ContributorLine(string Text, DateTime AtUtc);

    /// <summary>The words written FOR a contributor in the factory's newest activity, newest first. Only that one field
    /// leaves the row - never what the factory agent did, who acted, the session, or the link.</summary>
    private List<ContributorLine> ContributorLines(string teamId, string factory) =>
        _activity.Query(new TenantId(teamId), factory: factory, limit: ActivityRowsRead).Rows
            .Where(r => !string.IsNullOrWhiteSpace(r.ForContributor))
            .Select(r => new ContributorLine(r.ForContributor!.Trim(), DateTime.SpecifyKind(r.OccurredUtc, DateTimeKind.Utc)))
            .ToList();

    private string TeamLabel(string teamId, string caller) =>
        _teams.ListTeamsFor(caller).FirstOrDefault(t => string.Equals(t.TeamId, teamId, StringComparison.Ordinal))?.Name
        ?? throw new InvalidOperationException("A contributor route ran for a caller who is not a member - the team gate was not run.");

    internal static string? NeedsYouLabel(int waiting) => waiting switch
    {
        0 => null,
        1 => "1 thing needs you",
        _ => $"{waiting} things need you",
    };

    /// <summary>
    /// THE ORDER OF THE COMBINED LIST, decided here (rule 7). The client sorts the items of every team together by this key
    /// alone, ordinal and ascending: what waits on the person first, then the newest, then by name and team so two
    /// teams' answers always interleave the same way.
    /// </summary>
    internal static string Order(bool waiting, DateTime atUtc, string name, string teamId)
    {
        var newestFirst = (DateTime.MaxValue.Ticks - DateTime.SpecifyKind(atUtc, DateTimeKind.Utc).Ticks).ToString("D19", CultureInfo.InvariantCulture);
        return $"{(waiting ? 0 : 1)}|{newestFirst}|{name.ToLowerInvariant()}|{teamId}";
    }
}
