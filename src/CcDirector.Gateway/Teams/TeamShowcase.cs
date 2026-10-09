using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Teams.Mentor;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// FILLING A TEAM SO IT CAN BE SHOWN AS IT IS (owner, 8 Oct 2026): "One script, through a small administrator route,
/// puts the four people and all their pages into your team, every row marked with one tag so the whole lot can be
/// removed in one step. They cannot sign in; they exist on your screens."
///
/// <see cref="Write"/> puts into ONE named team: members who are not accounts (a display name, an email and a role on
/// the member row itself), the Mentor's blocks about them for the last closed week (and one on the Owner's own account),
/// reports sent to members (some with a question that has options and one recommended), and requests with their
/// history. EVERY row it writes carries the caller's tag in its <c>ShowcaseTag</c> column.
///
/// <see cref="Remove"/> deletes the rows that carry a tag, and the rows that hang off a tagged report or request (an
/// answer, a comment or a reply someone added to it afterwards) - and nothing else. Lean to keep: it never deletes by
/// "everything except", only by the tag it is given.
///
/// WHAT A MADE-UP MEMBER IS NOT: an account. It has no tenant row, so it cannot sign in, enrol a Director or be sent a
/// mail; it holds no paid seat (<see cref="TeamBillStore.PaidSeats"/>); and the Mentor's writer never visits it.
/// </summary>
public sealed class TeamShowcase
{
    /// <summary>The prefix of a made-up member's account subject. Never a real account's: real subjects come from the
    /// sign-in provider and carry no colon-separated prefix of ours.</summary>
    public const string SubjectPrefix = "showcase:";

    /// <summary>The longest a tag may be; the column is this wide.</summary>
    public const int MaxTagLength = 40;

    /// <summary>The longest a made-up member's display name may be; the column is this wide.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The words a tag may be: lower-case letters, digits and hyphens, 3 to 40 of them.</summary>
    private static readonly Regex TagShape = new("^[a-z0-9-]{3,40}$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions QuoteJson = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly TeamRegistry _teams;
    private readonly TenantRegistry _tenants;
    private readonly Func<TenantId, string> _timeZoneOf;
    private readonly Func<DateTime> _utcNow;

    public TeamShowcase(GatewayDatabase db, TeamRegistry teams, TenantRegistry tenants, Func<TenantId, string> timeZoneOf,
        Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _teams = teams ?? throw new ArgumentNullException(nameof(teams));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _timeZoneOf = timeZoneOf ?? throw new ArgumentNullException(nameof(timeZoneOf));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The account subject a made-up member is stored under in one tag.</summary>
    public static string SubjectFor(string tag, string key) => $"{SubjectPrefix}{tag}:{key}";

    /// <summary>
    /// Write the content into the team under the tag. Every row is written in one go per table group; nothing is written
    /// when the content is refused. Returns how many rows of each kind were written, or the refusal.
    /// </summary>
    public TeamShowcaseResult Write(string teamId, string tag, TeamShowcaseContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        FileLog.Write($"[TeamShowcase] Write: team {LogTeam(teamId)} tag={tag}");
        if (TagRefusal(tag) is { } badTag) return TeamShowcaseResult.Refused(badTag);
        if (string.IsNullOrWhiteSpace(teamId)) return TeamShowcaseResult.Refused("A team id is required.");

        var members = _teams.MembersOf(teamId);
        var owner = members.FirstOrDefault(m => m.Role == TeamRole.Owner);
        if (owner is null) return TeamShowcaseResult.Refused($"There is no team with the id {teamId} on this Gateway.");
        if (owner.ShowcaseTag is not null)
            throw new InvalidOperationException("A team's Owner is a made-up member, which the showcase never writes.");
        if (ContentRefusal(content) is { } badContent) return TeamShowcaseResult.Refused(badContent);

        lock (_gate)
        {
            if (TagInUse(teamId, owner.AccountSubject, tag))
                return TeamShowcaseResult.Refused(
                    $"The tag \"{tag}\" is already on rows in this team. Remove it first, or use another tag.");

            // Who each key in the content is: "owner" is the team's Owner; every other key is a made-up member.
            var who = content.Members.ToDictionary(m => m.Key, m => SubjectFor(tag, m.Key), StringComparer.Ordinal);
            who[TeamShowcaseContent.OwnerKey] = owner.AccountSubject;

            var now = _utcNow();
            var team = new TenantId(teamId);
            var teamZone = TimeZoneInfo.FindSystemTimeZoneById(_timeZoneOf(team));
            var teamWeek = MentorWeek.LastClosed(now, teamZone);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            using (var ctx = _db.CreateUnscopedContext())
            {
                if (!string.IsNullOrWhiteSpace(content.TeamName))
                {
                    var row = ctx.Teams.Single(t => t.Id == teamId);
                    row.Name = content.TeamName.Trim();
                }
                foreach (var m in content.Members)
                {
                    ctx.TeamMembers.Add(new TeamMemberEntity
                    {
                        TeamId = teamId,
                        AccountSubject = who[m.Key],
                        Role = RoleNamed(m.Role)!.Value,
                        JoinedAtUtc = now.AddDays(-m.JoinedDaysAgo),
                        DisplayName = m.Name.Trim(),
                        DisplayEmail = m.Email.Trim(),
                        ShowcaseTag = tag,
                    });
                }
                ctx.SaveChanges();
            }
            counts["members"] = content.Members.Count;

            using (var ctx = _db.CreateContext(team))
            {
                foreach (var block in content.TeamMentor)
                    ctx.TeamMentorBlocks.Add(Block(teamId, teamWeek, who[block.Person], block, now, tag));

                foreach (var report in content.Reports)
                    AddReport(ctx, teamId, report, who, now, tag);

                foreach (var request in content.Requests)
                    AddRequest(ctx, teamId, request, who, now, tag);
                ctx.SaveChanges();
            }
            counts["mentorBlocks"] = content.TeamMentor.Count;
            counts["reports"] = content.Reports.Count;
            counts["questions"] = content.Reports.Count(r => r.Question is not null);
            counts["requests"] = content.Requests.Count;

            if (content.OwnerPersonal is { } personal)
            {
                var own = _tenants.LookupBySubject(owner.AccountSubject)
                          ?? throw new InvalidOperationException("The team's Owner has no account of their own on this Gateway.");
                var ownWeek = MentorWeek.LastClosed(now, TimeZoneInfo.FindSystemTimeZoneById(_timeZoneOf(own)));
                using var ctx = _db.CreateContext(own);
                ctx.TeamMentorBlocks.Add(Block(own.Value, ownWeek, owner.AccountSubject, personal, now, tag));
                ctx.SaveChanges();
                counts["ownerMentorBlocks"] = 1;
            }

            FileLog.Write($"[TeamShowcase] Write: team {LogTeam(teamId)} tag={tag} wrote " +
                          string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));
            return TeamShowcaseResult.Done(counts);
        }
    }

    /// <summary>
    /// Delete every row in the team (and on its Owner's own account) that carries the tag, and the rows that hang off a
    /// tagged report or request. Rows without the tag are never touched. Returns how many rows of each kind went.
    /// </summary>
    public TeamShowcaseResult Remove(string teamId, string tag)
    {
        FileLog.Write($"[TeamShowcase] Remove: team {LogTeam(teamId)} tag={tag}");
        if (TagRefusal(tag) is { } badTag) return TeamShowcaseResult.Refused(badTag);
        if (string.IsNullOrWhiteSpace(teamId)) return TeamShowcaseResult.Refused("A team id is required.");
        var owner = _teams.MembersOf(teamId).FirstOrDefault(m => m.Role == TeamRole.Owner);
        if (owner is null) return TeamShowcaseResult.Refused($"There is no team with the id {teamId} on this Gateway.");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        lock (_gate)
        {
            using (var ctx = _db.CreateContext(new TenantId(teamId)))
            {
                RemoveTagged(ctx, tag, counts);
                ctx.SaveChanges();
            }
            if (_tenants.LookupBySubject(owner.AccountSubject) is { } own)
            {
                using var ctx = _db.CreateContext(own);
                RemoveTagged(ctx, tag, counts);
                ctx.SaveChanges();
            }
            using (var ctx = _db.CreateUnscopedContext())
            {
                var members = ctx.TeamMembers.Where(m => m.TeamId == teamId && m.ShowcaseTag == tag).ToList();
                ctx.TeamMembers.RemoveRange(members);
                Add(counts, "members", members.Count);
                ctx.SaveChanges();
            }
        }

        FileLog.Write($"[TeamShowcase] Remove: team {LogTeam(teamId)} tag={tag} removed " +
                      string.Join(", ", counts.Select(c => $"{c.Key}={c.Value}")));
        return TeamShowcaseResult.Done(counts);
    }

    // ---- writing -------------------------------------------------------------------------------------------------

    private static TeamMentorBlockEntity Block(string tenantId, MentorWeek week, string subject, TeamShowcaseMentorBlock b,
        DateTime now, string tag)
    {
        var quotes = b.Quote is null
            ? new List<MentorQuote>()
            : new List<MentorQuote> { new($"showcase-{Guid.NewGuid():N}", week.Start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(1).AddHours(9), b.Quote) };
        return new TeamMentorBlockEntity
        {
            TenantId = tenantId,
            Week = week.ToString(),
            PersonSubject = subject,
            Tone = b.Tone,
            WorkedOn = b.WorkedOn,
            HowItWent = b.HowItWent,
            WentBadlyAndWhy = b.WentBadlyAndWhy,
            OneThingToTry = b.OneThingToTry,
            QuotesJson = JsonSerializer.Serialize(quotes, QuoteJson),
            WrittenAtUtc = now,
            Model = "showcase",
            ShowcaseTag = tag,
        };
    }

    private static void AddReport(GatewayDbContext ctx, string teamId, TeamShowcaseReport r, IReadOnlyDictionary<string, string> who,
        DateTime now, string tag)
    {
        var sent = now.AddHours(-r.HoursAgo);
        var html = ReportHtml(r);
        var bytes = Encoding.UTF8.GetBytes(html);
        var report = new DevReportEntity
        {
            TenantId = teamId,
            // A session nobody runs: the report stands on its own, and an answer to its question is held for a session
            // that never comes - never typed anywhere.
            SessionId = Guid.NewGuid().ToString("D"),
            Key = $"showcase/{tag}/{Guid.NewGuid():N}.html",
            Title = r.Title,
            Status = r.Question is null ? "done" : "waiting-on-you",
            Version = 1,
            PublishedAtUtc = sent,
            UpdatedAtUtc = sent,
            AuthorSubject = who[r.From],
            ShowcaseTag = tag,
        };
        ctx.DevReports.Add(report);
        ctx.DevReportVersions.Add(new DevReportVersionEntity
        {
            TenantId = teamId,
            ReportId = report.Id,
            Version = 1,
            Html = html,
            ByteHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            ByteLength = bytes.Length,
            PublishedAtUtc = sent,
            Status = report.Status,
            Title = r.Title,
            ShowcaseTag = tag,
        });
        foreach (var to in r.SentTo)
        {
            ctx.DevReportRecipients.Add(new DevReportRecipientEntity
            {
                TenantId = teamId,
                ReportId = report.Id,
                RecipientSubject = who[to],
                SentBySubject = who[r.From],
                SentVersion = 1,
                SentAtUtc = sent,
                ReadAtUtc = r.ReadBy.Contains(to, StringComparer.Ordinal) ? sent.AddHours(1) : null,
                ShowcaseTag = tag,
            });
        }
    }

    /// <summary>The report's page: a header, the summary, and the question with its options when it has one - in the
    /// markup the Gateway reads a question from (<see cref="DevReports.DevReportQuestions"/>).</summary>
    internal static string ReportHtml(TeamShowcaseReport r)
    {
        static string E(string s) => System.Net.WebUtility.HtmlEncode(s);
        var status = r.Question is null ? "done" : "waiting-on-you";
        var html = new StringBuilder();
        html.Append($"<header data-dev-report=\"header\" data-dev-report-status=\"{status}\"><h1>{E(r.Title)}</h1></header>");
        html.Append($"<section data-dev-report=\"summary\"><p>{E(r.Summary)}</p></section>");
        // The full dev report shape: a questions section on EVERY report (the questions, or the marker that there are
        // none) and a detail section. The Questions and Reports pages read every report through
        // DevReportQuestions.Read, which treats a report without that shape as a fault - one such report made both
        // pages answer 500 for the whole team.
        html.Append("<section data-dev-report=\"questions\">");
        if (r.Question is { } q)
        {
            html.Append($"<div data-dev-report-question=\"{E(q.Id)}\" data-dev-report-question-text=\"{E(q.Text)}\">");
            foreach (var o in q.Options)
                html.Append($"<label><input type=\"radio\" name=\"{E(q.Id)}\" value=\"{E(o.Value)}\"{(o.Recommended ? " data-recommended" : "")}> {E(o.Label)}</label>");
            html.Append("</div>");
        }
        else
        {
            html.Append("<p data-dev-report-no-questions>No questions - nothing needed from you.</p>");
        }
        html.Append("</section>");
        html.Append($"<section data-dev-report=\"detail\"><p>{E(r.Summary)}</p></section>");
        return html.ToString();
    }

    private static void AddRequest(GatewayDbContext ctx, string teamId, TeamShowcaseRequest r, IReadOnlyDictionary<string, string> who,
        DateTime now, string tag)
    {
        var sent = now.AddDays(-r.DaysAgo);
        var state = r.Steps.Count == 0 ? TeamRequestStates.Sent : r.Steps[^1].State;
        var request = new TeamRequestEntity
        {
            TenantId = teamId,
            SenderSubject = who[r.From],
            Text = r.Text,
            State = state,
            SentAtUtc = sent,
            UpdatedAtUtc = r.Steps.Count == 0 ? sent : now.AddDays(-r.Steps[^1].DaysAgo),
            ShowcaseTag = tag,
        };
        ctx.TeamRequests.Add(request);
        ctx.TeamRequestChanges.Add(new TeamRequestChangeEntity
        {
            TenantId = teamId, RequestId = request.Id, State = TeamRequestStates.Sent, BySubject = who[r.From], AtUtc = sent,
            ShowcaseTag = tag,
        });
        foreach (var step in r.Steps)
        {
            ctx.TeamRequestChanges.Add(new TeamRequestChangeEntity
            {
                TenantId = teamId, RequestId = request.Id, State = step.State, BySubject = who[step.By],
                AtUtc = now.AddDays(-step.DaysAgo), Reason = step.Reason, ShowcaseTag = tag,
            });
        }
    }

    // ---- removing ------------------------------------------------------------------------------------------------

    private static void RemoveTagged(GatewayDbContext ctx, string tag, Dictionary<string, int> counts)
    {
        var reports = ctx.DevReports.Where(r => r.ShowcaseTag == tag).ToList();
        var reportIds = reports.Select(r => r.Id).ToList();
        // What hangs off a tagged report belongs to it: its versions and recipients (tagged when written), and an answer,
        // a comment or a reply somebody added to it on screen afterwards.
        Add(counts, "reportVersions", RemoveAll(ctx, ctx.DevReportVersions.Where(v => v.ShowcaseTag == tag || reportIds.Contains(v.ReportId))));
        Add(counts, "reportRecipients", RemoveAll(ctx, ctx.DevReportRecipients.Where(v => v.ShowcaseTag == tag || reportIds.Contains(v.ReportId))));
        Add(counts, "reportItems", RemoveAll(ctx, ctx.DevReportItems.Where(i => reportIds.Contains(i.ReportId))));
        Add(counts, "reportComments", RemoveAll(ctx, ctx.DevReportComments.Where(c => reportIds.Contains(c.ReportId))));
        Add(counts, "reportReplies", RemoveAll(ctx, ctx.DevReportReplies.Where(c => reportIds.Contains(c.ReportId))));
        ctx.DevReports.RemoveRange(reports);
        Add(counts, "reports", reports.Count);

        var requestIds = ctx.TeamRequests.Where(r => r.ShowcaseTag == tag).Select(r => r.Id).ToList();
        Add(counts, "requestChanges", RemoveAll(ctx, ctx.TeamRequestChanges.Where(c => c.ShowcaseTag == tag || requestIds.Contains(c.RequestId))));
        Add(counts, "requests", RemoveAll(ctx, ctx.TeamRequests.Where(r => r.ShowcaseTag == tag)));
        Add(counts, "mentorBlocks", RemoveAll(ctx, ctx.TeamMentorBlocks.Where(b => b.ShowcaseTag == tag)));
    }

    private static int RemoveAll<T>(GatewayDbContext ctx, IQueryable<T> rows) where T : class
    {
        var list = rows.ToList();
        ctx.RemoveRange(list);
        return list.Count;
    }

    private static void Add(Dictionary<string, int> counts, string key, int n) =>
        counts[key] = (counts.TryGetValue(key, out var had) ? had : 0) + n;

    // ---- checks --------------------------------------------------------------------------------------------------

    private bool TagInUse(string teamId, string ownerSubject, string tag)
    {
        using (var ctx = _db.CreateUnscopedContext())
            if (ctx.TeamMembers.Any(m => m.TeamId == teamId && m.ShowcaseTag == tag)) return true;
        using (var ctx = _db.CreateContext(new TenantId(teamId)))
            if (ctx.DevReports.Any(r => r.ShowcaseTag == tag) || ctx.TeamRequests.Any(r => r.ShowcaseTag == tag)
                || ctx.TeamMentorBlocks.Any(b => b.ShowcaseTag == tag)) return true;
        if (_tenants.LookupBySubject(ownerSubject) is { } own)
        {
            using var ctx = _db.CreateContext(own);
            if (ctx.TeamMentorBlocks.Any(b => b.ShowcaseTag == tag)) return true;
        }
        return false;
    }

    internal static string? TagRefusal(string? tag) =>
        tag is not null && TagShape.IsMatch(tag)
            ? null
            : "A tag is required: 3 to 40 lower-case letters, digits or hyphens, for example \"showcase-2026-10\".";

    internal static string? ContentRefusal(TeamShowcaseContent c)
    {
        if (c.TeamName is { } name && TeamRegistry.NameRefusal(name) is { } badName) return badName;
        var keys = new HashSet<string>(StringComparer.Ordinal) { TeamShowcaseContent.OwnerKey };
        foreach (var m in c.Members)
        {
            if (string.IsNullOrWhiteSpace(m.Key) || !TagShape.IsMatch(m.Key)) return $"A member's key \"{m.Key}\" is not 3 to 40 lower-case letters, digits or hyphens.";
            if (!keys.Add(m.Key)) return $"Two members have the key \"{m.Key}\" (or a member is called \"owner\").";
            if (string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Email)) return $"The member \"{m.Key}\" needs a name and an email.";
            if (m.Name.Trim().Length > MaxNameLength || m.Email.Trim().Length > TeamInvitationRules.MaxEmailLength)
                return $"The member \"{m.Key}\" has a name or an email that is too long.";
            if (RoleNamed(m.Role) is not { } role || role == TeamRole.Owner)
                return $"The member \"{m.Key}\" has the role \"{m.Role}\"; a made-up member is a Manager, a Developer or a Collaborator.";
            if (m.JoinedDaysAgo < 0) return $"The member \"{m.Key}\" joined in the future.";
        }

        string? Unknown(string key, string where) => keys.Contains(key) ? null : $"{where} names \"{key}\", who is not the owner or one of the members.";
        foreach (var b in c.TeamMentor)
            if ((Unknown(b.Person, "A Mentor block") ?? BlockRefusal(b)) is { } bad) return bad;
        if (c.TeamMentor.Select(b => b.Person).Distinct(StringComparer.Ordinal).Count() != c.TeamMentor.Count)
            return "Two Mentor blocks are about the same person; there is one per person per week.";
        if (c.OwnerPersonal is { } own && BlockRefusal(own) is { } badOwn) return badOwn;
        foreach (var r in c.Reports)
        {
            if (Unknown(r.From, $"The report \"{r.Title}\"") is { } bad) return bad;
            if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.Summary)) return "A report needs a title and a summary.";
            if (r.SentTo.Count == 0) return $"The report \"{r.Title}\" is sent to nobody.";
            foreach (var to in r.SentTo.Concat(r.ReadBy))
                if (Unknown(to, $"The report \"{r.Title}\"") is { } badTo) return badTo;
            if (r.SentTo.Contains(r.From, StringComparer.Ordinal)) return $"The report \"{r.Title}\" is sent to its own author.";
            if (r.HoursAgo < 0) return $"The report \"{r.Title}\" is sent in the future.";
            if (r.Question is { } q && (string.IsNullOrWhiteSpace(q.Id) || string.IsNullOrWhiteSpace(q.Text) || q.Options.Count < 2
                                        || q.Options.Count(o => o.Recommended) != 1))
                return $"The question in \"{r.Title}\" needs an id, its words, two or more options and exactly one recommended.";
        }
        foreach (var r in c.Requests)
        {
            if (Unknown(r.From, "A request") is { } bad) return bad;
            if (TeamRequestStates.TextRefusal(r.Text) is { } badText) return badText;
            foreach (var s in r.Steps)
            {
                if (Unknown(s.By, "A request's change") is { } badBy) return badBy;
                if (s.State is not (TeamRequestStates.Accepted or TeamRequestStates.Declined or TeamRequestStates.Done))
                    return $"A request's change is \"{s.State}\"; it is accepted, declined or done.";
                if (s.State == TeamRequestStates.Declined && string.IsNullOrWhiteSpace(s.Reason))
                    return "A declined request needs its reason.";
                if (s.DaysAgo > r.DaysAgo) return "A request's change comes before the request was sent.";
            }
        }
        return null;
    }

    /// <summary>The role a name says - Manager, Developer or Collaborator as the screens write them - or null.</summary>
    private static TeamRole? RoleNamed(string? name) =>
        Enum.GetValues<TeamRole>().Cast<TeamRole?>()
            .FirstOrDefault(r => string.Equals(TeamRoles.Label(r!.Value), name?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string? BlockRefusal(TeamShowcaseMentorBlock b)
    {
        if (!MentorTones.IsTone(b.Tone)) return $"A Mentor block's tone \"{b.Tone}\" is not good, mixed or hard.";
        if (string.IsNullOrWhiteSpace(b.WorkedOn) || string.IsNullOrWhiteSpace(b.OneThingToTry))
            return "A Mentor block needs what was worked on and one thing to try.";
        // The page shows a quoted prompt only beside where the week went badly, and that part always quotes one.
        if ((b.WentBadlyAndWhy is null) != (b.Quote is null))
            return "A Mentor block that says where the week went badly quotes one prompt, and one that does not quotes none.";
        return null;
    }

    private static string LogTeam(string teamId) => string.IsNullOrWhiteSpace(teamId) ? "(none)" : new TenantId(teamId.Trim()).ToLogString();
}

/// <summary>What the showcase route answers: the rows written or removed by kind, or the refusal.</summary>
public sealed record TeamShowcaseResult(IReadOnlyDictionary<string, int>? Counts, string? Refusal)
{
    public static TeamShowcaseResult Done(IReadOnlyDictionary<string, int> counts) => new(counts, null);
    public static TeamShowcaseResult Refused(string refusal) => new(null, refusal);
}

/// <summary>What the showcase writes into a team. Every person is named by a key: <see cref="OwnerKey"/> for the team's
/// Owner, or one of <see cref="Members"/>. Times are counted back from the moment the route runs.</summary>
public sealed record TeamShowcaseContent(
    [property: JsonPropertyName("teamName")] string? TeamName,
    [property: JsonPropertyName("members")] IReadOnlyList<TeamShowcaseMember> Members,
    [property: JsonPropertyName("ownerPersonal")] TeamShowcaseMentorBlock? OwnerPersonal,
    [property: JsonPropertyName("teamMentor")] IReadOnlyList<TeamShowcaseMentorBlock> TeamMentor,
    [property: JsonPropertyName("reports")] IReadOnlyList<TeamShowcaseReport> Reports,
    [property: JsonPropertyName("requests")] IReadOnlyList<TeamShowcaseRequest> Requests)
{
    /// <summary>The key that names the team's Owner.</summary>
    public const string OwnerKey = "owner";
}

public sealed record TeamShowcaseMember(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("joinedDaysAgo")] int JoinedDaysAgo);

public sealed record TeamShowcaseMentorBlock(
    [property: JsonPropertyName("person")] string Person,
    [property: JsonPropertyName("tone")] string Tone,
    [property: JsonPropertyName("workedOn")] string WorkedOn,
    [property: JsonPropertyName("howItWent")] string? HowItWent,
    [property: JsonPropertyName("wentBadlyAndWhy")] string? WentBadlyAndWhy,
    [property: JsonPropertyName("quote")] string? Quote,
    [property: JsonPropertyName("oneThingToTry")] string OneThingToTry);

public sealed record TeamShowcaseReport(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("hoursAgo")] double HoursAgo,
    [property: JsonPropertyName("sentTo")] IReadOnlyList<string> SentTo,
    [property: JsonPropertyName("readBy")] IReadOnlyList<string> ReadBy,
    [property: JsonPropertyName("question")] TeamShowcaseQuestion? Question);

public sealed record TeamShowcaseQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("options")] IReadOnlyList<TeamShowcaseOption> Options);

public sealed record TeamShowcaseOption(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("recommended")] bool Recommended);

public sealed record TeamShowcaseRequest(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("daysAgo")] double DaysAgo,
    [property: JsonPropertyName("steps")] IReadOnlyList<TeamShowcaseRequestStep> Steps);

public sealed record TeamShowcaseRequestStep(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("by")] string By,
    [property: JsonPropertyName("daysAgo")] double DaysAgo,
    [property: JsonPropertyName("reason")] string? Reason);
