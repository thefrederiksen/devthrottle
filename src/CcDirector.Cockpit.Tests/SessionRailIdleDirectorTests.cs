using Bunit;
using CcDirector.Cockpit.Components;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// The tree view must show EVERY reachable Director in the registry - including ones with
/// zero sessions - because the Director header's [+] is the only way to start the first
/// session on it. These bUnit tests render the real SessionRail in "tree" mode and assert
/// the idle Director surfaces with its [+] and a "no sessions" hint. Mirrors the
/// SessionRailRepoViewTests / SessionRailPortLabelTests harness.
/// </summary>
public sealed class SessionRailIdleDirectorTests : TestContext
{
    private const string GuidBusy = "a3a971fa-1111-2222-3333-444455556666";
    private const string GuidIdle = "b7c44ee0-aaaa-bbbb-cccc-ddddeeeeffff";

    private static DirectorDto Director(string id, string controlEndpoint, string machine = "SOREN_NORTH") => new()
    {
        DirectorId = id,
        MachineName = machine,
        Version = "0.6.25",
        StartedAt = DateTime.UtcNow.AddMinutes(-12),
        ControlEndpoint = controlEndpoint,
    };

    private static SessionDto Session(string id, string name, string directorId, string machine = "SOREN_NORTH") => new()
    {
        SessionId = id,
        DirectorId = directorId,
        MachineName = machine,
        Name = name,
        RepoPath = @"D:\repos\thing",
        SortOrder = 0,
        StatusColor = "blue",
        ActivityState = "Idle",
        Type = "Implement",
    };

    [Fact]
    public void Tree_IdleDirector_WithNoSessions_StillRenders_WithAddButton()
    {
        // One Director owns a session, a second Director (in the registry) has none. The idle
        // one must still get a header and its [+].
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "tree")
            .Add(c => c.Directors, new List<DirectorDto>
            {
                Director(GuidBusy, "http://127.0.0.1:7878"),
                Director(GuidIdle, "http://127.0.0.1:7879"),
            })
            .Add(c => c.Sessions, new List<SessionDto>
            {
                Session("s1", "busy-sess", GuidBusy),
            }));

        // Two Director headers (busy + idle), each carrying a [+].
        var labels = cut.FindAll(".dir-head-label").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "director :7878", "director :7879" }, labels);
        Assert.Equal(2, cut.FindAll(".dir-add").Count);
    }

    [Fact]
    public void Tree_IdleDirector_ShowsNoSessionsHint_AndFiresOnNewSession_WithItsId()
    {
        string? firedDirectorId = null;
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "tree")
            .Add(c => c.Directors, new List<DirectorDto> { Director(GuidIdle, "http://127.0.0.1:7879") })
            .Add(c => c.Sessions, new List<SessionDto>())
            .Add(c => c.OnNewSession, (string id) => firedDirectorId = id));

        // The idle Director reads as "no sessions", and the empty-roster message is suppressed.
        Assert.Equal("no sessions", cut.Find(".dir-idle").TextContent.Trim());
        Assert.Empty(cut.FindAll(".rail-empty"));

        // Its [+] starts a session on THAT Director.
        cut.Find(".dir-add").Click();
        Assert.Equal(GuidIdle, firedDirectorId);
    }

    [Fact]
    public void Tree_UnreachableDirector_NotRenderedAsEmptyHeader()
    {
        // A Director surfaced as unreachable gets the dedicated error row, never a bare [+] header.
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "tree")
            .Add(c => c.Directors, new List<DirectorDto> { Director(GuidIdle, "http://127.0.0.1:7879") })
            .Add(c => c.Sessions, new List<SessionDto>())
            .Add(c => c.Errors, new List<MachineErrorDto>
            {
                new() { DirectorId = GuidIdle, MachineName = "SOREN_NORTH", Error = "timeout" },
            }));

        Assert.Empty(cut.FindAll(".dir-head"));
        Assert.Single(cut.FindAll(".dir-error"));
    }

    [Fact]
    public void Tree_MachineWithOnlyIdleDirectors_StillAppears()
    {
        // The machine has no sessions and no errors - only a registry Director. It must still show.
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "tree")
            .Add(c => c.Directors, new List<DirectorDto> { Director(GuidIdle, "http://127.0.0.1:7879", "REMOTE_BOX") })
            .Add(c => c.Sessions, new List<SessionDto>()));

        var machines = cut.FindAll(".machine-name").Select(m => m.TextContent.Trim()).ToList();
        Assert.Contains("REMOTE_BOX", machines);
    }
}
