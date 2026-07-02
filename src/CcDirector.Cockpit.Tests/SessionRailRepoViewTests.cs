using Bunit;
using CcDirector.Cockpit.Components;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Issue #219: the Cockpit session rail's third view ("repo") groups every session by repository
/// across machines/Directors, headers alphabetical (case-insensitive), "(no repo)" last; rows hold
/// desktop order (no reshuffle on color change) and each shows its owning Director short-id. These
/// bUnit tests render the real SessionRail and assert the rendered markup. Mirrors the #237
/// SessionRailPortLabelTests harness, including the screenshot-proof emitter.
/// </summary>
public sealed class SessionRailRepoViewTests : TestContext
{
    private const string Guid7884 = "a3a971fa-1111-2222-3333-444455556666";
    private const string Guid7886 = "b7c44ee0-aaaa-bbbb-cccc-ddddeeeeffff";

    private static DirectorDto Director(string id, string controlEndpoint, string machine = "SOREN_NORTH") => new()
    {
        DirectorId = id,
        MachineName = machine,
        Version = "0.6.23",
        StartedAt = DateTime.UtcNow.AddMinutes(-12),
        ControlEndpoint = controlEndpoint,
    };

    private static SessionDto Session(string id, string name, string repoPath, string color = "blue",
        int sortOrder = 0, string remoteRepo = "", string directorId = Guid7884, string machine = "SOREN_NORTH") => new()
    {
        SessionId = id,
        DirectorId = directorId,
        MachineName = machine,
        Name = name,
        RepoPath = repoPath,
        RemoteRepo = remoteRepo,
        SortOrder = sortOrder,
        StatusColor = color,
        ActivityState = "Idle",    };

    private static List<DirectorDto> TwoDirectors() => new()
    {
        Director(Guid7884, "http://127.0.0.1:7884", "MACHINE_A"),
        Director(Guid7886, "http://127.0.0.1:7886", "MACHINE_B"),
    };

    [Fact]
    public void RepoView_OneHeaderPerRepo_Alphabetical()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "repo")
            .Add(c => c.Directors, TwoDirectors())
            .Add(c => c.Sessions, new List<SessionDto>
            {
                Session("s1", "z-sess", @"D:\repos\zebra"),
                Session("s2", "a-sess", @"D:\repos\apple"),
                Session("s3", "b-sess", @"D:\repos\banana"),
            }));

        var headers = cut.FindAll(".repo-name").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "apple", "banana", "zebra" }, headers);
    }

    [Fact]
    public void RepoView_NoRepoGroup_RenderedLast()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "repo")
            .Add(c => c.Directors, TwoDirectors())
            .Add(c => c.Sessions, new List<SessionDto>
            {
                Session("none", "no-repo-sess", repoPath: ""),
                Session("named", "named-sess", @"D:\repos\alpha"),
            }));

        var headers = cut.FindAll(".repo-name").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "alpha", SessionOrdering.NoRepoGroup }, headers);
    }

    [Fact]
    public void RepoView_EachRow_ShowsDirectorShortId()
    {
        // sess-dir must be present AND visible (the .rail.repo CSS shows it, unlike tree).
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "repo")
            .Add(c => c.Directors, TwoDirectors())
            .Add(c => c.Sessions, new List<SessionDto>
            {
                Session("s1", "one", @"D:\repos\alpha", directorId: Guid7886, machine: "MACHINE_B"),
            }));

        var dir = cut.Find(".sess-dir");
        Assert.Equal(":7886", dir.TextContent.Trim());
        Assert.Equal(Guid7886, dir.GetAttribute("title"));
        // The rail carries the "repo" class so the .rail.repo .sess-dir rule applies.
        Assert.Contains("repo", cut.Find(".rail").GetAttribute("class"));
    }

    [Fact]
    public void RepoView_SameRepoAcrossMachines_UnderOneHeader()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "repo")
            .Add(c => c.Directors, TwoDirectors())
            .Add(c => c.Sessions, new List<SessionDto>
            {
                Session("onA", "agent-a", @"D:\ReposFred\cc-director",
                    remoteRepo: "thefrederiksen/cc-director.git", directorId: Guid7884, machine: "MACHINE_A"),
                Session("onB", "agent-b", @"C:\src\cc-director",
                    remoteRepo: "thefrederiksen/cc-director", directorId: Guid7886, machine: "MACHINE_B"),
            }));

        // Exactly one header, both sessions under it.
        var headers = cut.FindAll(".repo-name").Select(h => h.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "cc-director" }, headers);

        var group = cut.Find(".repo-grp");
        Assert.Equal(2, group.QuerySelectorAll(".sess").Length);
        Assert.Equal("2", cut.Find(".repo-count").TextContent.Trim());
    }

    [Fact]
    public void RepoView_RowOrder_IsDesktopOrder_NotAffectedByColor()
    {
        // Lower SortOrder renders first even though it is the "louder" red row; a color change on
        // the lower-SortOrder session must not lift it above the higher-priority slot. Desktop order
        // (SortOrder) is the only positional signal.
        var blue = Session("low", "low-order", @"D:\repos\shared", color: "blue", sortOrder: 1);
        var red = Session("high", "high-order", @"D:\repos\shared", color: "red", sortOrder: 2);

        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "repo")
            .Add(c => c.Directors, TwoDirectors())
            .Add(c => c.Sessions, new List<SessionDto> { red, blue }));

        var names = cut.FindAll(".repo-grp .sess-name").Select(n => n.TextContent.Trim()).ToList();
        // SortOrder 1 ("low-order") before SortOrder 2 ("high-order"), regardless of red vs blue.
        Assert.Equal(new[] { "low-order", "high-order" }, names);
    }

    /// <summary>
    /// Emits a standalone HTML proof artifact (the real rendered repo-view rail wrapped in the real
    /// app.css) when CC219_PROOF_DIR is set. Not an assertion test - it lets the Developer Agent
    /// screenshot the genuine compiled markup for the issue's visual proof. Skipped in the normal
    /// suite (env var unset). Mirrors the #237 EmitProofArtifact pattern.
    /// </summary>
    [Fact]
    public void EmitProofArtifact_WhenProofDirSet()
    {
        var proofDir = Environment.GetEnvironmentVariable("CC219_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(proofDir)) return; // no-op in the normal suite

        var directors = new List<DirectorDto>
        {
            Director(Guid7884, "http://127.0.0.1:7884", "MACHINE_A"),
            Director(Guid7886, "http://127.0.0.1:7886", "MACHINE_B"),
        };

        // Demonstrates: alphabetical headers (apple < cc-director), a repo with two sessions on TWO
        // machines coalesced (cc-director, onA/MACHINE_A + onB/MACHINE_B), a desktop-ordered pair
        // where the lower-SortOrder blue stays above the higher-SortOrder red, and a "(no repo)"
        // group last.
        var redOrBlue = Environment.GetEnvironmentVariable("CC219_PROOF_FLIP") == "1";
        var sessions = new List<SessionDto>
        {
            Session("a1", "apple-impl", @"D:\repos\apple", color: "blue", sortOrder: 0, directorId: Guid7884, machine: "MACHINE_A"),
            Session("c1", "cc-director-A", @"D:\ReposFred\cc-director", color: "blue", sortOrder: 1,
                remoteRepo: "thefrederiksen/cc-director.git", directorId: Guid7884, machine: "MACHINE_A"),
            // The "flip" row: blue by default, red when CC219_PROOF_FLIP=1 - proves the color change
            // does not move it out of its SortOrder=2 slot (renders BELOW cc-director-A either way).
            Session("c2", "cc-director-B", @"C:\src\cc-director", color: redOrBlue ? "red" : "blue", sortOrder: 2,
                remoteRepo: "thefrederiksen/cc-director", directorId: Guid7886, machine: "MACHINE_B"),
            Session("n1", "scratch-session", repoPath: "", color: "blue", directorId: Guid7884, machine: "MACHINE_A"),
        };

        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "repo")
            .Add(c => c.Directors, directors)
            .Add(c => c.Sessions, sessions));

        var railHtml = cut.Markup;

        var here = AppContext.BaseDirectory;
        var cssPath = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..",
            "CcDirector.Cockpit", "wwwroot", "app.css"));
        var css = File.Exists(cssPath) ? File.ReadAllText(cssPath) : "";

        var html =
            "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>\n" +
            "body{background:#1E1E1E;margin:0;padding:16px;font-family:'Segoe UI',sans-serif}\n" +
            ".proof-wrap{max-width:360px}\n" +
            css +
            "\n</style></head><body><div class=\"proof-wrap\">" +
            railHtml +
            "</div></body></html>";

        Directory.CreateDirectory(proofDir);
        var fileName = redOrBlue ? "rail-rendered-repo-flipped.html" : "rail-rendered-repo.html";
        File.WriteAllText(Path.Combine(proofDir, fileName), html);
    }
}
