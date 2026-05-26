using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

public class RosterParserTests
{
    [Fact]
    public void Parse_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(RosterParser.Parse(""));
        Assert.Empty(RosterParser.Parse("   "));
    }

    [Fact]
    public void Parse_ValidArray_MapsFieldsCaseInsensitively()
    {
        var json = """
        [
          {
            "sessionId": "11111111-1111-1111-1111-111111111111",
            "name": "alpha",
            "repoPath": "C:\\repos\\alpha",
            "status": "Running",
            "activityState": "WaitingForInput",
            "statusColor": "red",
            "lastStatusReason": "pending question",
            "tailnetEndpoint": "https://host.ts.net/",
            "machineName": "soren-north",
            "voiceMode": true
          }
        ]
        """;

        var roster = RosterParser.Parse(json);

        var s = Assert.Single(roster);
        Assert.Equal("alpha", s.Name);
        Assert.Equal("red", s.StatusColor);
        Assert.Equal("pending question", s.LastStatusReason);
        // Trailing slash is trimmed so endpoint concatenation is clean.
        Assert.Equal("https://host.ts.net", s.TailnetEndpoint);
        Assert.Equal("soren-north", s.MachineName);
        Assert.True(s.VoiceMode);
    }

    [Fact]
    public void Parse_MissingVoiceMode_DefaultsToFalse()
    {
        var json = """[ { "sessionId": "a", "activityState": "Idle", "status": "Running" } ]""";
        var s = Assert.Single(RosterParser.Parse(json));
        Assert.False(s.VoiceMode);
    }

    [Fact]
    public void Parse_DropsExitedAndFailedSessions()
    {
        var json = """
        [
          { "sessionId": "a", "activityState": "Exited", "status": "Exited", "statusColor": "unknown" },
          { "sessionId": "b", "activityState": "Idle", "status": "Failed", "statusColor": "unknown" },
          { "sessionId": "c", "activityState": "Working", "status": "Running", "statusColor": "blue" }
        ]
        """;

        var roster = RosterParser.Parse(json);

        var s = Assert.Single(roster);
        Assert.Equal("c", s.SessionId);
    }

    [Fact]
    public void Parse_MissingStatusColor_DefaultsToUnknown()
    {
        var json = """[ { "sessionId": "a", "activityState": "Idle", "status": "Running" } ]""";
        var s = Assert.Single(RosterParser.Parse(json));
        Assert.Equal("unknown", s.StatusColor);
    }

    [Fact]
    public void DisplayName_PrefersNameThenRepoFolderThenId()
    {
        Assert.Equal("nice", new SessionInfo { Name = "nice", RepoPath = "C:\\x\\y" }.DisplayName);
        Assert.Equal("y", new SessionInfo { RepoPath = "C:\\x\\y" }.DisplayName);
        Assert.Equal("the-id", new SessionInfo { SessionId = "the-id" }.DisplayName);
    }
}
