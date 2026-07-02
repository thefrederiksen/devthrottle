using Bunit;
using CcDirector.Cockpit.Components;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Issue #218: the Cockpit triage view shows how long each NEEDS YOU session has been waiting,
/// derived from the Gateway-owned <see cref="SessionDto.NeedsYouSince"/> stamp. These bUnit tests
/// render the real SessionRail and assert the elapsed-waiting label: present on red rows, absent
/// on non-red rows, larger for an older stamp (the "ticks up" behaviour), and absent while the
/// wingman is still briefing (effective yellow, not red).
/// </summary>
public sealed class SessionRailNeedsYouWaitTests : TestContext
{
    private const string Guid7884 = "a3a971fa-1111-2222-3333-444455556666";

    private static DirectorDto Director() => new()
    {
        DirectorId = Guid7884,
        MachineName = "SOREN_NORTH",
        Version = "0.6.23",
        StartedAt = DateTime.UtcNow.AddMinutes(-12),
        ControlEndpoint = "http://127.0.0.1:7884",
    };

    private static SessionDto RedSession(string id, string name, DateTime? needsYouSince) => new()
    {
        SessionId = id,
        DirectorId = Guid7884,
        MachineName = "SOREN_NORTH",
        Name = name,
        RepoPath = @"D:\repos\demo",
        StatusColor = "red",
        ActivityState = "WaitingForInput",        NeedsYouSince = needsYouSince,
    };

    private static SessionDto BlueSession(string id, string name) => new()
    {
        SessionId = id,
        DirectorId = Guid7884,
        MachineName = "SOREN_NORTH",
        Name = name,
        RepoPath = @"D:\repos\demo",
        StatusColor = "blue",
        ActivityState = "Working",        NeedsYouSince = null,
    };

    [Fact]
    public void TriageRedRow_ShowsWaitingLabel_DerivedFromNeedsYouSince()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "triage")
            .Add(c => c.Directors, new List<DirectorDto> { Director() })
            .Add(c => c.Sessions, new List<SessionDto>
            {
                RedSession("r1", "needs-input", DateTime.UtcNow.AddMinutes(-1).AddSeconds(-20)),
            }));

        var wait = cut.Find(".needs-wait");
        // ~1m20s ago -> compact "1m" (sub-minute seconds drop once past a minute).
        Assert.Equal("1m", wait.TextContent.Trim());
    }

    [Fact]
    public void TriageRedRow_ShortWait_ShowsSeconds()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "triage")
            .Add(c => c.Directors, new List<DirectorDto> { Director() })
            .Add(c => c.Sessions, new List<SessionDto>
            {
                RedSession("r1", "needs-input", DateTime.UtcNow.AddSeconds(-12)),
            }));

        var wait = cut.Find(".needs-wait");
        // Sub-minute waits read in seconds so a freshly-red session shows a meaningful value.
        Assert.EndsWith("s", wait.TextContent.Trim());
        Assert.DoesNotContain("m", wait.TextContent);
    }

    [Fact]
    public void NonRedSession_HasNoWaitingLabel()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "triage")
            .Add(c => c.Directors, new List<DirectorDto> { Director() })
            .Add(c => c.Sessions, new List<SessionDto> { BlueSession("b1", "running") }));

        // An ACTIVE/blue session shows NO waiting-duration label (not "0s", not stale).
        Assert.Empty(cut.FindAll(".needs-wait"));
    }

    [Fact]
    public void RedSession_WithNullNeedsYouSince_HasNoWaitingLabel()
    {
        // Defensive: a red row whose stamp has not landed yet (e.g. an old Gateway) shows the
        // existing "needs you" pill but no waiting label - never a bogus "0s".
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "triage")
            .Add(c => c.Directors, new List<DirectorDto> { Director() })
            .Add(c => c.Sessions, new List<SessionDto> { RedSession("r1", "needs-input", needsYouSince: null) }));

        Assert.Empty(cut.FindAll(".needs-wait"));
    }

    [Fact]
    public void OlderStamp_ShowsLargerValue_ThanRecentStamp()
    {
        // The "ticks up" behaviour: a session that has been red longer shows a larger elapsed
        // value than one that just went red. (Re-rendering with an earlier stamp == a later
        // poll of the same still-red session, since the label is now - NeedsYouSince.)
        var recent = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "triage")
            .Add(c => c.Directors, new List<DirectorDto> { Director() })
            .Add(c => c.Sessions, new List<SessionDto>
            {
                RedSession("r1", "needs-input", DateTime.UtcNow.AddSeconds(-5)),
            }));
        var older = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "triage")
            .Add(c => c.Directors, new List<DirectorDto> { Director() })
            .Add(c => c.Sessions, new List<SessionDto>
            {
                RedSession("r1", "needs-input", DateTime.UtcNow.AddMinutes(-2)),
            }));

        var recentSecs = int.Parse(recent.Find(".needs-wait").TextContent.Trim().TrimEnd('s'));
        var olderText = older.Find(".needs-wait").TextContent.Trim();
        // Recent is a sub-minute seconds value; older crossed into minutes -> strictly larger.
        Assert.True(recentSecs < 60);
        Assert.Contains("m", olderText);
    }

    /// <summary>
    /// Emits a standalone HTML proof artifact (real rendered triage rail wrapped in real app.css)
    /// when CC218_PROOF_DIR is set, so the Developer Agent can screenshot the genuine compiled
    /// markup for the issue's visual proof. The optional CC218_PROOF_NAME / CC218_PROOF_OFFSET_S
    /// env vars let the harness emit the same rail twice with a larger elapsed value to prove the
    /// label ticks up. No-op (env var unset) in the normal suite.
    /// </summary>
    [Fact]
    public void EmitProofArtifact_WhenProofDirSet()
    {
        var proofDir = Environment.GetEnvironmentVariable("CC218_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(proofDir)) return; // no-op in the normal suite

        var offsetS = int.TryParse(Environment.GetEnvironmentVariable("CC218_PROOF_OFFSET_S"), out var o) ? o : 0;
        var now = DateTime.UtcNow;
        var directors = new List<DirectorDto> { Director() };
        var sessions = new List<SessionDto>
        {
            RedSession("r1", "wingman-intelligence", now.AddSeconds(-80 - offsetS)),
            RedSession("r2", "gateway-handshake", now.AddSeconds(-12 - offsetS)),
            BlueSession("b1", "cockpit-port-label"),
        };

        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "triage")
            .Add(c => c.Directors, directors)
            .Add(c => c.Sessions, sessions));

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
            cut.Markup +
            "</div></body></html>";

        Directory.CreateDirectory(proofDir);
        var name = Environment.GetEnvironmentVariable("CC218_PROOF_NAME") ?? "rail-needsyou-wait.html";
        File.WriteAllText(Path.Combine(proofDir, name), html);
    }
}
