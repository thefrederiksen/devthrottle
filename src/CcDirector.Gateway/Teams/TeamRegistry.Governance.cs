using System.Globalization;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;

namespace CcDirector.Gateway.Teams;

/// <summary>
/// The team's Governance tab (Teams v1, the owner, 8 Oct 2026: "under the team, we really need a new tab called
/// Governance"): the rules the team sets for how its members work, and the record of every change to them.
///
/// WHO MAY DO WHAT IS ASKED OF <see cref="TeamAccess"/>. Reading is the row "see the team's governance rules" (the Owner,
/// Managers and Developers); every change is "change the team's governance rules" (the Owner and Managers).
///
/// THE GATEWAY DECIDES EVERY VERDICT (product rule 7). The sections, every row's words, whether the caller may change
/// them, the sentence for one who may not, the "who may read what" answers and each line of the record are finished here;
/// the tab renders them.
///
/// This version SAVES and SHOWS the rules and records every change. Nothing on a member's machine enforces them yet -
/// enforcement comes rule by rule, later, as separate decisions.
/// </summary>
public sealed partial class TeamRegistry
{
    /// <summary>What a member who may not change the rules reads under them.</summary>
    public const string OwnerAndManagersChangeGovernance = "Only the team's Owner and Managers can change these rules.";

    /// <summary>The line under the required and suggested skills and workflows.</summary>
    public const string GovernanceLibraryNote = "Chosen from the team's own skills and workflows, for every member's Directors.";

    /// <summary>The line an item shows once it has left the team's library.</summary>
    public const string GovernanceItemGone = "No longer in the team's library.";

    /// <summary>
    /// The Governance tab for <paramref name="callerSubject"/>: not found for someone who is not a member, forbidden with the
    /// role table's sentence for a role that may not see it. <paramref name="library"/> is the team's own skills and
    /// workflows, read by the caller inside the team's tenant.
    /// </summary>
    public TeamGovernanceViewResult DescribeTeamGovernance(string teamId, string callerSubject, IReadOnlyList<TeamGovernanceLibraryEntry> library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] DescribeTeamGovernance: team {LogTeam(teamId)}");

        var see = _access.Decide(teamId ?? "", caller, TeamAction.SeeTeamGovernance);
        if (!see.IsMember)
            return TeamGovernanceViewResult.NotFound;
        if (!see.Allowed)
        {
            FileLog.Write($"[TeamRegistry] DescribeTeamGovernance: REFUSED - a {see.Role} may not see the team's governance rules");
            return TeamGovernanceViewResult.Forbidden(see.Refusal!);
        }

        var mayChange = _access.Decide(teamId!, caller, TeamAction.ChangeTeamGovernance).Allowed;
        var view = BuildGovernanceView(teamId!.Trim(), caller, mayChange, library);
        FileLog.Write($"[TeamRegistry] DescribeTeamGovernance: team {LogTeam(teamId)} canChange={mayChange} items={view.Library.Items.Count} changes={view.Changes.Count}");
        return TeamGovernanceViewResult.Found(view);
    }

    /// <summary>
    /// CHANGE THE TEAM'S GOVERNANCE, as the Owner or a Manager: only what <paramref name="request"/> names changes, and every
    /// change is recorded with who made it. Not found for someone who is not a member, forbidden for a role that may not.
    /// </summary>
    public TeamGovernanceChangeResult ChangeTeamGovernance(string teamId, string callerSubject, TeamGovernanceChangeRequest request,
        IReadOnlyList<TeamGovernanceLibraryEntry> library)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(library);
        var caller = RequireSubject(callerSubject);
        FileLog.Write($"[TeamRegistry] ChangeTeamGovernance: team {LogTeam(teamId)}");

        var decision = _access.Decide(teamId ?? "", caller, TeamAction.ChangeTeamGovernance);
        if (!decision.IsMember)
        {
            FileLog.Write("[TeamRegistry] ChangeTeamGovernance: no such team for this caller");
            return new TeamGovernanceChangeResult(TeamGovernanceChangeOutcome.NotFound, TeamRefusals.NoSuchTeam);
        }
        if (!decision.Allowed)
        {
            FileLog.Write($"[TeamRegistry] ChangeTeamGovernance: REFUSED - a {decision.Role} may not change the team's governance rules");
            return new TeamGovernanceChangeResult(TeamGovernanceChangeOutcome.Forbidden, decision.Refusal);
        }

        var id = teamId!.Trim();
        var result = _governance.Apply(id, TeamLibraryEndpoints.MemberReference(id, caller), request, library);
        FileLog.Write($"[TeamRegistry] ChangeTeamGovernance: team {LogTeam(id)} outcome={result.Outcome} changes={result.Changes}");
        return result;
    }

    /// <summary>The tab's model for one team, for a caller who may see it.</summary>
    private TeamGovernanceView BuildGovernanceView(string teamId, string caller, bool mayChange, IReadOnlyList<TeamGovernanceLibraryEntry> library)
    {
        var snapshot = _governance.Read(teamId);
        var members = ListMembers(teamId, caller);
        var names = members.Members.ToDictionary(
            m => TeamLibraryEndpoints.MemberReference(teamId, m.AccountSubject), TeamEndpoints.MemberName, StringComparer.Ordinal);
        var teamName = members.Team?.Name ?? "";

        static TeamGovernanceSwitchView Switch(TeamGovernanceSwitch rule, Data.Entities.TeamGovernanceEntity rules) =>
            new(rule.Id, rule.Label, rule.Detail, rule.Read(rules));

        var items = snapshot.Items.Select(i =>
        {
            var current = library.FirstOrDefault(e => e.Kind == i.Kind && e.Id == i.ItemId);
            return new TeamGovernanceItemView(i.Kind, i.ItemId, current?.Name ?? i.Name, i.Level, current is null ? GovernanceItemGone : null);
        }).ToList();
        var choices = mayChange
            ? library.Where(e => !snapshot.Items.Any(i => i.Kind == e.Kind && i.ItemId == e.Id))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Kind, StringComparer.Ordinal)
                .Select(e => new TeamGovernanceChoiceView(e.Kind, e.Id, e.Name))
                .ToList()
            : new List<TeamGovernanceChoiceView>();

        return new TeamGovernanceView(
            TeamName: teamName,
            Summary: $"{teamName} - the rules every member's sessions work under. Only the Owner and Managers change them.",
            CanChange: mayChange,
            Note: mayChange ? null : OwnerAndManagersChangeGovernance,
            Review: TeamGovernanceCatalog.Review.Select(r => Switch(r, snapshot.Rules)).ToList(),
            Library: new TeamGovernanceLibraryView(GovernanceLibraryNote, items, choices,
                library.Count == 0 ? "The team has no skills or workflows of its own yet. Add them on the Skills and workflows page." : null),
            Agents: TeamGovernanceCatalog.Agents.Select(a => Switch(a, snapshot.Rules)).ToList(),
            ReadAccess: ReadAccessRows(),
            Limits: TeamGovernanceCatalog.Limits.Select(l =>
            {
                var value = l.Read(snapshot.Rules);
                return new TeamGovernanceLimitView(l.Id, l.Label, l.Detail, value, TeamGovernanceCatalog.LimitDisplay(l, value), l.Min, l.Max);
            }).ToList(),
            Changes: snapshot.Changes.Select(c => new TeamGovernanceChangeLine(
                c.Id,
                $"{TeamLibraryEndpoints.ChangedBy(c.ChangedBy, names)} {c.What}",
                When(c.CreatedAtUtc))).ToList());
    }

    /// <summary>
    /// "Who may read what" - READ FROM THE ROLE TABLE, never written by hand, so the tab cannot claim a rule the product does
    /// not enforce: the cells that decide each answer are the cells <see cref="TeamAccess"/> checks.
    /// </summary>
    internal static IReadOnlyList<TeamGovernanceReadAccessView> ReadAccessRows()
    {
        var mentorOthers = RolesAllowed(TeamAction.ReadMentorPageAboutEachPerson);
        return new[]
        {
            new TeamGovernanceReadAccessView("A member's sessions and transcripts",
                RolesAllowed(TeamAction.JoinOrWatchSomeoneElsesSession) is { Count: > 0 } watchers
                    ? "That member, " + RoleWords(watchers)
                    : "Only that member"),
            new TeamGovernanceReadAccessView("Prompts quoted on a Mentor page", RoleWords(RolesAllowed(TeamAction.ReadPromptsQuotedOnMentorPage))),
            new TeamGovernanceReadAccessView("Each member's Mentor page",
                mentorOthers.Count > 0 ? "The member, " + RoleWords(mentorOthers) : "Only that member"),
            // TeamFleetMap carries a Director's name and status and nothing else.
            new TeamGovernanceReadAccessView("Fleet Map", "Names and status only"),
        };
    }

    private static List<TeamRole> RolesAllowed(TeamAction action) =>
        new[] { TeamRole.Owner, TeamRole.Manager, TeamRole.Developer, TeamRole.Collaborator }
            .Where(r => TeamPermissions.Allows(r, action)).ToList();

    /// <summary>"Owner and Managers", "Owner, Managers and Developers"; "Nobody" for none.</summary>
    private static string RoleWords(IReadOnlyList<TeamRole> roles)
    {
        var words = roles.Select(r => r switch
        {
            TeamRole.Owner => "Owner",
            TeamRole.Manager => "Managers",
            TeamRole.Developer => "Developers",
            TeamRole.Collaborator => "Collaborators",
            _ => throw new ArgumentOutOfRangeException(nameof(roles), r, "Not one of the four team roles."),
        }).ToList();
        return words.Count switch
        {
            0 => "Nobody",
            1 => words[0],
            _ => string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1],
        };
    }

    /// <summary>When a change was made, as the record shows it: "Mon 6 Oct 2026, 09:14 UTC".</summary>
    internal static string When(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>The Governance tab, finished for display (rule 7): the client renders it and decides nothing.</summary>
public sealed record TeamGovernanceView(
    string TeamName,
    string Summary,
    bool CanChange,
    string? Note,
    IReadOnlyList<TeamGovernanceSwitchView> Review,
    TeamGovernanceLibraryView Library,
    IReadOnlyList<TeamGovernanceSwitchView> Agents,
    IReadOnlyList<TeamGovernanceReadAccessView> ReadAccess,
    IReadOnlyList<TeamGovernanceLimitView> Limits,
    IReadOnlyList<TeamGovernanceChangeLine> Changes);

/// <summary>One on/off rule.</summary>
public sealed record TeamGovernanceSwitchView(string Id, string Label, string? Detail, bool On);

/// <summary>The required and suggested skills and workflows, what the caller may add (empty when they may not change
/// them), and a line when the team has no library to choose from.</summary>
public sealed record TeamGovernanceLibraryView(string Note, IReadOnlyList<TeamGovernanceItemView> Items,
    IReadOnlyList<TeamGovernanceChoiceView> Choices, string? EmptyLibraryNote);

/// <summary>One named item. <see cref="Gone"/> is set when it has left the team's library.</summary>
public sealed record TeamGovernanceItemView(string Kind, string Id, string Name, string Level, string? Gone);

/// <summary>An item of the team's library that is not on the list yet.</summary>
public sealed record TeamGovernanceChoiceView(string Kind, string Id, string Name);

/// <summary>One "who may read what" row: the thing, and who may read it.</summary>
public sealed record TeamGovernanceReadAccessView(string Label, string Who);

/// <summary>One limit: its value (null for no limit), how it shows, and the range a change must keep to.</summary>
public sealed record TeamGovernanceLimitView(string Id, string Label, string? Detail, int? Value, string Display, int Min, int Max);

/// <summary>One line of the record of changes: "priya@acme.example switched on ...", and when.</summary>
public sealed record TeamGovernanceChangeLine(string Id, string Sentence, string When);

/// <summary>The outcome of reading the Governance tab.</summary>
public enum TeamGovernanceViewOutcome
{
    /// <summary>The tab's model.</summary>
    Found,

    /// <summary>No such team for this caller.</summary>
    NotFound,

    /// <summary>The caller's role has no Governance tab.</summary>
    Forbidden,
}

/// <summary>The Governance tab for a caller, or why they get none.</summary>
public sealed record TeamGovernanceViewResult(TeamGovernanceViewOutcome Outcome, TeamGovernanceView? View, string? Refusal)
{
    /// <summary>No such team for this caller.</summary>
    public static readonly TeamGovernanceViewResult NotFound = new(TeamGovernanceViewOutcome.NotFound, null, TeamRefusals.NoSuchTeam);

    /// <summary>The caller's role has no Governance tab.</summary>
    public static TeamGovernanceViewResult Forbidden(string refusal) => new(TeamGovernanceViewOutcome.Forbidden, null, refusal);

    /// <summary>The tab's model.</summary>
    public static TeamGovernanceViewResult Found(TeamGovernanceView view) => new(TeamGovernanceViewOutcome.Found, view, null);
}
