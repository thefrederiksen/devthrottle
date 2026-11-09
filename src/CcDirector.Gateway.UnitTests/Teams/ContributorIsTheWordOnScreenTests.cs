using System.Text.RegularExpressions;
using CcDirector.Gateway.Teams;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Teams;

/// <summary>
/// THE FREE ROLE IS "CONTRIBUTOR" ON SCREEN (devthrottle_internal#2791, owner decision). A person reads "Contributor"
/// as a label and "contributor" in a sentence - never "Collaborator" - and the feature is never called "Teams" in
/// product text. Internally nothing moved: <see cref="TeamRole.Collaborator"/>, the stored word "collaborator" the
/// website reads, and every class, file and address keep their names.
///
/// The first tests pin the Gateway's words, because the Gateway sends every label (rule 7, the client is dumb). The
/// scans stop the old word coming back by hand: a string a person can read in the Gateway, the Cockpit, the phone app
/// or the shared client code. Comments, test files, code names and log lines are not read by a person and are not
/// scanned.
/// </summary>
public sealed class ContributorIsTheWordOnScreenTests
{
    [Fact]
    public void Label_Collaborator_IsContributor()
    {
        Assert.Equal("Contributor", TeamRoles.Label(TeamRole.Collaborator));
        Assert.Equal("contributor", TeamRoles.InText(TeamRole.Collaborator));
        Assert.Equal("contributors", TeamRoles.InTextPlural(TeamRole.Collaborator));
    }

    [Fact]
    public void ToStored_Collaborator_StaysTheWordTheWebsiteReads()
    {
        Assert.Equal("collaborator", TeamRoles.ToStored(TeamRole.Collaborator));
        Assert.Equal(TeamRole.Collaborator, TeamRoles.FromStored("collaborator"));
    }

    [Theory]
    [InlineData("Contributor")]
    [InlineData("contributor")]
    [InlineData("collaborator")]
    [InlineData(" Collaborator ")]
    public void Parse_TheLabelOrTheStoredWord_IsTheFreeRole(string value)
    {
        Assert.Equal(TeamRole.Collaborator, TeamRoles.Parse(value));
    }

    [Fact]
    public void Parse_EveryRole_RoundTripsThroughItsLabel()
    {
        foreach (var role in Enum.GetValues<TeamRole>())
            Assert.Equal(role, TeamRoles.Parse(TeamRoles.Label(role)));
        Assert.Null(TeamRoles.Parse("Admin"));
        Assert.Null(TeamRoles.Parse(" "));
    }

    [Fact]
    public void RoleRefusal_Contributor_SaysContributorInLowerCase()
    {
        var row = TeamPermissions.Row(TeamAction.InviteOrRemoveDevelopersAndCollaborators);
        Assert.Equal(
            "In this team you are a contributor, and a contributor may not invite or remove Developers and contributors.",
            TeamAccessDecision.RoleRefusal(TeamRole.Collaborator, row));
    }

    [Fact]
    public void NotAvailableTo_Contributor_SaysContributors()
    {
        Assert.Equal("This page is not available to contributors.", TeamApp.NotAvailableTo(TeamRole.Collaborator));
    }

    // A capitalised Collaborator or Collaborators as a whole word: what a person would read. The stored word
    // "collaborator" is lower case, and a code name such as InviteOrRemoveDevelopersAndCollaborators or
    // isCollaborator has a letter before it, so neither matches.
    private static readonly Regex OldWord = new(@"(?<![A-Za-z_.])Collaborators?\b", RegexOptions.Compiled);

    // "Teams" as the feature's name. A code name (TeamsSection, CC_GATEWAY_TEAMS, useTeams, Teams.Mentor) has a
    // letter, an underscore or a dot beside it and does not match.
    private static readonly Regex FeatureName = new(@"(?<![A-Za-z_.])Teams\b(?![A-Za-z_(.])", RegexOptions.Compiled);

    private static readonly Regex CSharpString = new(@"\$?@?""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);

    [Fact]
    public void GatewaySource_NoStringAPersonReads_SaysCollaboratorOrTeams()
    {
        var hits = new List<string>();
        foreach (var file in RepositorySourceIndex.CSharpFiles)
        {
            var rel = Rel(file);
            if (!rel.StartsWith("src/CcDirector.Gateway/", StringComparison.Ordinal)
                && !rel.StartsWith("src/CcDirector.Gateway.Contracts/", StringComparison.Ordinal))
                continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                    continue;
                // A log line, an exception's message and an attribute are for the developer, not the person on screen.
                if (line.Contains("FileLog.Write", StringComparison.Ordinal)
                    || line.Contains("Exception(", StringComparison.Ordinal)
                    || trimmed.StartsWith("[", StringComparison.Ordinal))
                    continue;
                foreach (Match literal in CSharpString.Matches(line))
                {
                    // A string that opens with "[ClassName]" is a log line split over several lines.
                    if (literal.Value.TrimStart('$', '@', '"').StartsWith("[", StringComparison.Ordinal))
                        continue;
                    if (OldWord.IsMatch(literal.Value) || FeatureName.IsMatch(literal.Value))
                        hits.Add($"{rel}:{i + 1}: {literal.Value}");
                }
            }
        }
        Assert.True(hits.Count == 0,
            "A Gateway string a person reads says \"Collaborator\" or \"Teams\". Say \"Contributor\" as a label, " +
            "\"contributor\" in a sentence (TeamRoles.Label / InText), and \"team\" for the feature " +
            "(devthrottle_internal#2791):\n" + string.Join("\n", hits));
    }

    [Fact]
    public void ClientSource_NoTextAPersonReads_SaysCollaboratorOrTeams()
    {
        var roots = new[] { "apps/cockpit/src/", "apps/mobile/src/", "packages/client-core/src/" };
        var scanned = 0;
        var hits = new List<string>();
        foreach (var file in RepositorySourceIndex.Files)
        {
            var rel = Rel(file);
            if (!roots.Any(r => rel.StartsWith(r, StringComparison.Ordinal)))
                continue;
            if (!(rel.EndsWith(".ts", StringComparison.Ordinal) || rel.EndsWith(".tsx", StringComparison.Ordinal)))
                continue;
            if (rel.Contains(".test.", StringComparison.Ordinal) || rel.Contains("/test/", StringComparison.Ordinal)
                || rel.Contains("__tests__", StringComparison.Ordinal))
                continue;
            scanned++;
            var text = StripComments(File.ReadAllText(file));
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith("import ", StringComparison.Ordinal))
                    continue;
                if (OldWord.IsMatch(line) || FeatureName.IsMatch(line))
                    hits.Add($"{rel}:{i + 1}: {line.Trim()}");
            }
        }
        // A scan that read nothing would pass on any tree.
        Assert.True(scanned > 100, $"The client scan read only {scanned} source files; the roots have moved.");
        Assert.True(hits.Count == 0,
            "Client text a person reads says \"Collaborator\" or \"Teams\". The Gateway sends role labels; a sentence " +
            "the client writes itself says \"contributor\" and \"team\" (devthrottle_internal#2791):\n" +
            string.Join("\n", hits));
    }

    // Removes // line comments, /* */ block comments and {/* */} JSX comments, keeping the line count so a hit names
    // its real line. A "//" inside a string (an address) is kept.
    private static string StripComments(string source)
    {
        var block = Regex.Replace(source, @"/\*.*?\*/", m => new string('\n', m.Value.Count(c => c == '\n')),
            RegexOptions.Singleline);
        return Regex.Replace(block, @"(?<![:""'`])//[^\n]*", "");
    }

    private static string Rel(string file) =>
        Path.GetRelativePath(RepositorySourceIndex.Root, file).Replace('\\', '/');
}
