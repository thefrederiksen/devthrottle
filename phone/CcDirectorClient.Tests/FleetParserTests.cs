using System.Text.Json;
using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

/// <summary>
/// Tests for the "start a new session" parsing + request-body logic (issue #245):
/// GET /directors, GET /directors/{id}/repos, and the POST body builder. All pure,
/// off-device.
/// </summary>
public class FleetParserTests
{
    // ===== ParseDirectors =====

    [Fact]
    public void ParseDirectors_MapsAllFields()
    {
        var json = """
        [{"directorId":"d1","machineName":"soren-north","tailnetEndpoint":"https://north.ts.net:7883/",
          "lastSeen":"2026-06-08T10:00:00Z","version":"0.6.17"}]
        """;

        var list = FleetParser.ParseDirectors(json);

        var d = Assert.Single(list);
        Assert.Equal("d1", d.DirectorId);
        Assert.Equal("soren-north", d.MachineName);
        Assert.Equal("https://north.ts.net:7883", d.TailnetEndpoint);   // trailing slash trimmed
        Assert.Equal("0.6.17", d.Version);
        Assert.NotNull(d.LastSeen);
    }

    [Fact]
    public void ParseDirectors_IsCaseInsensitiveOnFieldNames()
    {
        // The Gateway emits camelCase; stay robust if a field ever arrives PascalCase.
        var json = """[{"DirectorId":"d1","MachineName":"laptop"}]""";

        var d = Assert.Single(FleetParser.ParseDirectors(json));
        Assert.Equal("d1", d.DirectorId);
        Assert.Equal("laptop", d.MachineName);
    }

    [Fact]
    public void ParseDirectors_SortsMostRecentlySeenFirstThenByMachineName()
    {
        var json = """
        [{"directorId":"old","machineName":"zeta","lastSeen":"2026-06-01T00:00:00Z"},
         {"directorId":"new","machineName":"alpha","lastSeen":"2026-06-08T00:00:00Z"},
         {"directorId":"none","machineName":"beta"}]
        """;

        var list = FleetParser.ParseDirectors(json);

        Assert.Equal("new", list[0].DirectorId);    // most recent first -> default selection
        Assert.Equal("old", list[1].DirectorId);
        Assert.Equal("none", list[2].DirectorId);    // no lastSeen sorts last
    }

    [Fact]
    public void ParseDirectors_FallsBackToControlEndpointWhenNoTailnet()
    {
        // A local, file-discovered Director registers no tailnet URL; the Gateway uses
        // its control endpoint as the session endpoint, so the picker must too.
        var json = """
        [{"directorId":"local","machineName":"box","tailnetEndpoint":"","controlEndpoint":"http://127.0.0.1:7889/"},
         {"directorId":"remote","machineName":"laptop","tailnetEndpoint":"https://laptop.ts.net:7879","controlEndpoint":"https://laptop.ts.net:7879"}]
        """;

        var list = FleetParser.ParseDirectors(json);

        var local = Assert.Single(list, d => d.DirectorId == "local");
        Assert.Equal("http://127.0.0.1:7889", local.TailnetEndpoint);   // control endpoint, slash trimmed
        var remote = Assert.Single(list, d => d.DirectorId == "remote");
        Assert.Equal("https://laptop.ts.net:7879", remote.TailnetEndpoint);   // tailnet wins when present
    }

    [Fact]
    public void ParseDirectors_SkipsEntriesWithoutAnId()
    {
        var json = """[{"machineName":"ghost"},{"directorId":"real","machineName":"box"}]""";

        var d = Assert.Single(FleetParser.ParseDirectors(json));
        Assert.Equal("real", d.DirectorId);
    }

    [Fact]
    public void ParseDirectors_EmptyOrBlankIsEmptyList()
    {
        Assert.Empty(FleetParser.ParseDirectors("[]"));
        Assert.Empty(FleetParser.ParseDirectors(""));
        Assert.Empty(FleetParser.ParseDirectors("   "));
    }

    // ===== ParseRepos =====

    [Fact]
    public void ParseRepos_MapsFieldsAndSortsNewestUsedFirst()
    {
        var json = """
        [{"name":"old","path":"D:\\repos\\old","lastUsed":"2026-06-01T00:00:00Z"},
         {"name":"new","path":"D:\\repos\\new","lastUsed":"2026-06-08T00:00:00Z"}]
        """;

        var list = FleetParser.ParseRepos(json);

        Assert.Equal("new", list[0].Name);
        Assert.Equal("D:\\repos\\new", list[0].Path);
        Assert.Equal("old", list[1].Name);
    }

    [Fact]
    public void ParseRepos_TreatsMissingLastUsedAsOldest()
    {
        var json = """
        [{"name":"undated","path":"D:\\a"},
         {"name":"dated","path":"D:\\b","lastUsed":"2026-06-08T00:00:00Z"}]
        """;

        var list = FleetParser.ParseRepos(json);

        Assert.Equal("dated", list[0].Name);
        Assert.Equal("undated", list[1].Name);
    }

    [Fact]
    public void ParseRepos_SkipsEntriesWithoutAPath()
    {
        var json = """[{"name":"nopath"},{"name":"ok","path":"D:\\ok"}]""";

        var r = Assert.Single(FleetParser.ParseRepos(json));
        Assert.Equal("D:\\ok", r.Path);
    }

    [Fact]
    public void RepoInfo_DisplayNameFallsBackToFolderThenPath()
    {
        Assert.Equal("named", new RepoInfo { Name = "named", Path = "D:\\x\\y" }.DisplayName);
        Assert.Equal("y", new RepoInfo { Name = "", Path = "D:\\x\\y" }.DisplayName);
        Assert.Equal("y", new RepoInfo { Name = "  ", Path = "D:\\x\\y\\" }.DisplayName);
    }

    // ===== BuildCreateBody =====

    [Fact]
    public void BuildCreateBody_RequiresRepoPath()
    {
        Assert.Throws<ArgumentException>(() => FleetParser.BuildCreateBody(""));
        Assert.Throws<ArgumentException>(() => FleetParser.BuildCreateBody("   "));
    }

    [Fact]
    public void BuildCreateBody_DefaultsAgentToClaudeCodeAndOmitsOptionalFields()
    {
        var json = FleetParser.BuildCreateBody("D:\\repos\\cc-director");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("D:\\repos\\cc-director", root.GetProperty("repoPath").GetString());
        Assert.Equal("ClaudeCode", root.GetProperty("agent").GetString());
        Assert.False(root.GetProperty("wingmanEnabled").GetBoolean());
        Assert.False(root.TryGetProperty("type", out _));   // omitted when null -> server default
    }

    [Fact]
    public void BuildCreateBody_IncludesTypeAndWingmanWhenSet()
    {
        var json = FleetParser.BuildCreateBody(
            "D:\\repos\\cc-director", agent: "ClaudeCode", type: "BugReport", wingmanEnabled: true);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("BugReport", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("wingmanEnabled").GetBoolean());
    }

    [Fact]
    public void BuildCreateBody_TrimsRepoPath()
    {
        var json = FleetParser.BuildCreateBody("  D:\\repos\\x  ");
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("D:\\repos\\x", doc.RootElement.GetProperty("repoPath").GetString());
    }

    // ===== ParseOne (create response -> SessionInfo) =====

    [Fact]
    public void ParseOne_MapsACreateResponseIntoAUsableSession()
    {
        // What POST /directors/{id}/sessions returns (Director SessionDto): no
        // tailnetEndpoint (the Gateway stamps that only on the roster), so the caller
        // stamps it from the chosen Director.
        var json = """
        {"sessionId":"abc-123","name":"cc-director","repoPath":"D:\\repos\\cc-director",
         "activityState":"Idle","statusColor":"blue"}
        """;

        var s = RosterParser.ParseOne(json);

        Assert.Equal("abc-123", s.SessionId);
        Assert.Equal("cc-director", s.Name);
        Assert.Equal("D:\\repos\\cc-director", s.RepoPath);
        Assert.Equal("blue", s.StatusColor);
        Assert.Equal("", s.TailnetEndpoint);   // absent on a create response, stamped by the caller
    }

    [Fact]
    public void ParseOne_DoesNotDropAStartingSession()
    {
        // Unlike the roster Parse(), ParseOne must keep a just-created session even
        // before it reports a live state, so the caller can open it.
        var json = """{"sessionId":"x","activityState":"","status":""}""";

        var s = RosterParser.ParseOne(json);
        Assert.Equal("x", s.SessionId);
        Assert.Equal("unknown", s.StatusColor);
    }
}
