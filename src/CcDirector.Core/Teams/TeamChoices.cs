namespace CcDirector.Core.Teams;

/// <summary>
/// One team as the hosted Gateway lists it on <c>GET /devices/enroll-hosted/teams</c>: only teams where the
/// person may run sessions (Owner, Manager, Developer). The personal account is never in the list.
/// </summary>
public sealed class HostedTeam
{
    /// <summary>The team's id, sent back as <c>teamId</c> when enrolling into it.</summary>
    public string TeamId { get; set; } = "";

    /// <summary>The team's name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The person's role in the team: "owner", "manager" or "developer".</summary>
    public string Role { get; set; } = "";

    /// <summary>How many people are in the team.</summary>
    public int MemberCount { get; set; }
}

/// <summary>The body of <c>GET /devices/enroll-hosted/teams</c>.</summary>
public sealed class HostedTeamsReply
{
    /// <summary>The teams where the person may run sessions.</summary>
    public List<HostedTeam> Teams { get; set; } = new();
}

/// <summary>
/// What the hosted Gateway said about the signed-in person's teams.
/// </summary>
/// <param name="TeamsReleased">False when the Gateway answered 404: it has no teams, so the Director behaves
/// exactly as before Teams - no question, no chip.</param>
/// <param name="Teams">The teams where the person may run sessions; empty when there are none.</param>
public sealed record HostedTeamsAnswer(bool TeamsReleased, IReadOnlyList<HostedTeam> Teams);

/// <summary>One option in "Which team is this Director for?" (screen D1) and in the Settings move list (D3).</summary>
/// <param name="TeamId">The team id, or null for the personal account.</param>
/// <param name="Name">The option's title - the team name, or "Personal".</param>
/// <param name="Detail">The line under it - "You are the Owner, 5 people, you pay", or "Just you".</param>
public sealed record TeamChoice(string? TeamId, string Name, string Detail)
{
    /// <summary>True for the personal account.</summary>
    public bool IsPersonal => TeamId is null;

    /// <summary>The team this option stands for, as the Director records it.</summary>
    public DirectorTeam ToTeam() => new(TeamId, Name);
}

/// <summary>The question D1 asks: the options, in order, the first one selected.</summary>
/// <param name="Choices">The Gateway's teams in its order, then the personal account last.</param>
/// <param name="MachineName">This computer's name, which starts the suggested Director name.</param>
public sealed record TeamQuestion(IReadOnlyList<TeamChoice> Choices, string MachineName);

/// <summary>What the person answered on D1.</summary>
/// <param name="Choice">The team chosen.</param>
/// <param name="DirectorName">What they named this Director.</param>
public sealed record TeamAnswer(TeamChoice Choice, string DirectorName);

/// <summary>
/// Builds the options for D1 and D3 from the Gateway's list. Pure, so every rule here is tested directly.
/// </summary>
public static class TeamChoices
{
    /// <summary>
    /// The options: every listed team, in the Gateway's order, then the personal account ("Just you"). With no
    /// listed team there is exactly one option, which is the signal that the question is not asked at all.
    /// </summary>
    public static IReadOnlyList<TeamChoice> Build(IReadOnlyList<HostedTeam> teams)
    {
        ArgumentNullException.ThrowIfNull(teams);
        var choices = new List<TeamChoice>(teams.Count + 1);
        foreach (var team in teams)
        {
            if (string.IsNullOrWhiteSpace(team.TeamId))
                throw new InvalidDataException("The Gateway listed a team with no id.");
            choices.Add(new TeamChoice(team.TeamId, team.Name, Describe(team)));
        }
        choices.Add(new TeamChoice(null, DirectorTeam.PersonalName, "Just you"));
        return choices;
    }

    /// <summary>True when the person must be asked: there is more than the personal account to choose from.</summary>
    public static bool MustAsk(IReadOnlyList<TeamChoice> choices) => choices.Count > 1;

    /// <summary>
    /// The line under a team's name: the person's role, the team's size, and "you pay" for the Owner, whose
    /// card the team's bill goes to.
    /// </summary>
    public static string Describe(HostedTeam team)
    {
        ArgumentNullException.ThrowIfNull(team);
        var people = team.MemberCount == 1 ? "1 person" : $"{team.MemberCount} people";
        return team.Role.Trim().ToLowerInvariant() switch
        {
            "owner" => $"You are the Owner, {people}, you pay",
            "manager" => $"You are a Manager, {people}",
            "developer" => $"You are a Developer, {people}",
            _ => throw new InvalidDataException(
                $"The Gateway listed team {team.TeamId} with the role \"{team.Role}\", which cannot run sessions."),
        };
    }

    /// <summary>
    /// The name the "Name this Director" box starts with: "SOREN_NORTH - DevThrottle", or "SOREN_NORTH - Personal".
    /// The computer's name, not the person's: the Director does not know the person's name.
    /// </summary>
    public static string SuggestDirectorName(string machineName, TeamChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (string.IsNullOrWhiteSpace(machineName))
            throw new ArgumentException("machineName is required", nameof(machineName));
        return $"{machineName.Trim()} - {choice.Name}";
    }
}
