using System.Net;
using System.Net.Http.Json;
using Bunit;
using CcDirector.Cockpit.Components;
using CcDirector.Cockpit.Components.Pages;
using CcDirector.Cockpit.Services;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Issue #237: the Cockpit session rail identifies each Director by its port (short,
/// restart-stable, maps to the slot convention) rather than the meaningless 8-hex-char
/// per-process GUID prefix. These bUnit tests render the real SessionRail component and
/// assert the rendered markup, covering the tree header, the triage per-session span, the
/// unreachable rows, the full-GUID tooltip, and the GUID-prefix fallback when a port cannot
/// be resolved.
/// </summary>
public sealed class SessionRailPortLabelTests : TestContext
{
    // Two Directors on ONE machine with distinct ports, as the acceptance criteria require.
    private const string Guid7884 = "a3a971fa-1111-2222-3333-444455556666";
    private const string Guid7886 = "b7c44ee0-aaaa-bbbb-cccc-ddddeeeeffff";

    private static DirectorDto Director(string id, string controlEndpoint, string? tailnet = null) => new()
    {
        DirectorId = id,
        MachineName = "SOREN_NORTH",
        Version = "0.6.23",
        StartedAt = DateTime.UtcNow.AddMinutes(-12),
        ControlEndpoint = controlEndpoint,
        TailnetEndpoint = tailnet,
    };

    private static SessionDto Session(string id, string directorId, string name) => new()
    {
        SessionId = id,
        DirectorId = directorId,
        MachineName = "SOREN_NORTH",
        Name = name,
        RepoPath = @"D:\repos\demo",
        StatusColor = "green",
        ActivityState = "Idle",    };

    private static List<DirectorDto> TwoDirectors() => new()
    {
        Director(Guid7884, "http://127.0.0.1:7884"),
        Director(Guid7886, "http://127.0.0.1:7886"),
    };

    [Fact]
    public void TreeHeader_RendersPortLabel_NotGuidPrefix()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "tree")
            .Add(c => c.Directors, TwoDirectors())
            .Add(c => c.Sessions, new List<SessionDto>
            {
                Session("s1", Guid7884, "alpha"),
                Session("s2", Guid7886, "beta"),
            }));

        var headers = cut.FindAll(".dir-head-label");
        var headerText = string.Join(" | ", headers.Select(h => h.TextContent.Trim()));

        // Both ports show; neither GUID prefix shows as the label.
        Assert.Contains("director :7884", headerText);
        Assert.Contains("director :7886", headerText);
        Assert.DoesNotContain("a3a971fa", headerText);
        Assert.DoesNotContain("b7c44ee0", headerText);
    }

    [Fact]
    public void TreeHeader_CarriesFullGuid_InTitleTooltip()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "tree")
            .Add(c => c.Directors, TwoDirectors())
            .Add(c => c.Sessions, new List<SessionDto> { Session("s1", Guid7884, "alpha") }));

        var header = cut.Find(".dir-head-label");
        Assert.Equal(Guid7884, header.GetAttribute("title"));
    }

    [Fact]
    public void TriageSessionSpan_ShowsPort_ResolvedFromRegistry()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "triage")
            .Add(c => c.Directors, TwoDirectors())
            .Add(c => c.Sessions, new List<SessionDto> { Session("s1", Guid7886, "beta") }));

        var dir = cut.Find(".sess-dir");
        Assert.Equal(":7886", dir.TextContent.Trim());
        Assert.Equal(Guid7886, dir.GetAttribute("title"));
    }

    [Fact]
    public void UnreachableRow_ShowsPort_WhenDirectorStillInRegistry()
    {
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "tree")
            .Add(c => c.Directors, TwoDirectors())
            .Add(c => c.Sessions, new List<SessionDto>())
            .Add(c => c.Errors, new List<MachineErrorDto>
            {
                new() { DirectorId = Guid7884, MachineName = "SOREN_NORTH", Error = "timeout" },
            }));

        var row = cut.Find(".dir-error");
        Assert.Contains(":7884", row.TextContent);
        Assert.DoesNotContain("a3a971fa", row.TextContent);
    }

    [Fact]
    public void UnreachableRow_FallsBackToGuidPrefix_WhenDirectorGoneFromRegistry()
    {
        // Dead Director no longer in the registry: the lookup misses, so the row must fall
        // back to the GUID short-id (never blank, never ":?").
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "tree")
            .Add(c => c.Directors, new List<DirectorDto>())
            .Add(c => c.Sessions, new List<SessionDto>())
            .Add(c => c.Errors, new List<MachineErrorDto>
            {
                new() { DirectorId = Guid7884, MachineName = "SOREN_NORTH", Error = "gone" },
            }));

        var row = cut.Find(".dir-error");
        Assert.Contains("a3a971fa", row.TextContent);
        Assert.DoesNotContain(":?", row.TextContent);
    }

    [Fact]
    public void TreeHeader_FallsBackToTailnetPort_WhenControlEndpointEmpty()
    {
        // Cross-machine HTTP-registered Director: ControlEndpoint empty, port comes from the
        // tailnet endpoint instead.
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, "tree")
            .Add(c => c.Directors, new List<DirectorDto>
            {
                Director(Guid7884, "", "https://soren-north.taildb08ed.ts.net:7890"),
            })
            .Add(c => c.Sessions, new List<SessionDto> { Session("s1", Guid7884, "alpha") }));

        Assert.Contains("director :7890", cut.Find(".dir-head-label").TextContent);
    }

    /// <summary>
    /// Emits a standalone HTML proof artifact (the real rendered rail wrapped in the real
    /// app.css) when CC237_PROOF_DIR is set. Not an assertion test - it exists so the
    /// Developer Agent can screenshot the genuine compiled markup for the issue's visual
    /// proof. Skipped in the normal suite (env var unset).
    /// </summary>
    [Fact]
    public void EmitProofArtifact_WhenProofDirSet()
    {
        var proofDir = Environment.GetEnvironmentVariable("CC237_PROOF_DIR");
        if (string.IsNullOrWhiteSpace(proofDir)) return; // no-op in the normal suite

        var directors = new List<DirectorDto>
        {
            Director(Guid7884, "http://127.0.0.1:7884"),
            Director(Guid7886, "http://127.0.0.1:7886"),
        };
        var sessions = new List<SessionDto>
        {
            Session("s1", Guid7884, "wingman-intelligence"),
            Session("s2", Guid7884, "cockpit-port-label"),
            Session("s3", Guid7886, "gateway-handshake"),
        };
        var errors = new List<MachineErrorDto>
        {
            new() { DirectorId = "deadbeef-9999-0000-1111-222233334444", MachineName = "SOREN_NORTH", Error = "no heartbeat for 90s" },
        };

        var mode = Environment.GetEnvironmentVariable("CC237_PROOF_MODE") ?? "tree";
        var cut = RenderComponent<SessionRail>(p => p
            .Add(c => c.ViewMode, mode)
            .Add(c => c.Directors, directors)
            .Add(c => c.Sessions, sessions)
            .Add(c => c.Errors, errors));

        var railHtml = cut.Markup;

        // Locate the real app.css next to the Cockpit project so the proof looks like the app.
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
        var fileName = mode == "triage" ? "rail-rendered-triage.html" : "rail-rendered.html";
        File.WriteAllText(Path.Combine(proofDir, fileName), html);
    }

    // ----- Directors.razor page render -----

    /// <summary>Stub handler that answers the two Gateway reads the Directors page makes:
    /// GET directors and GET sessions?envelope=true with controlled JSON.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<DirectorDto> _directors;
        private readonly SessionsEnvelope _envelope;

        public StubHandler(List<DirectorDto> directors, SessionsEnvelope envelope)
        {
            _directors = directors;
            _envelope = envelope;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.PathAndQuery;
            object body = path.Contains("sessions") ? _envelope : _directors;
            return await Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body),
            });
        }
    }

    [Fact]
    public void DirectorsPage_RowLeadsWithPort_FullGuidInCellTitle()
    {
        var directors = new List<DirectorDto>
        {
            Director(Guid7884, "http://127.0.0.1:7884"),
            Director(Guid7886, "http://127.0.0.1:7886"),
        };
        var http = new HttpClient(new StubHandler(directors, new SessionsEnvelope()))
        {
            BaseAddress = new Uri("http://localhost:7878/"),
        };
        Services.AddSingleton(new GatewayClient(http, NullLogger<GatewayClient>.Instance));
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = RenderComponent<Directors>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count >= 2, TimeSpan.FromSeconds(5));

        // The Director cell (2nd column) leads with the port and keeps the full GUID in title.
        var dirCells = cut.FindAll("tbody tr td.mono[title]");
        var texts = string.Join(" | ", dirCells.Select(c => c.TextContent.Trim()));
        Assert.Contains(":7884", texts);
        Assert.Contains(":7886", texts);
        Assert.DoesNotContain("a3a971fa", texts);
        Assert.Contains(dirCells, c => c.GetAttribute("title") == Guid7884);

        var proofDir = Environment.GetEnvironmentVariable("CC237_PROOF_DIR");
        if (!string.IsNullOrWhiteSpace(proofDir))
        {
            Directory.CreateDirectory(proofDir);
            File.WriteAllText(Path.Combine(proofDir, "directors-page-markup.html"), cut.Markup);
        }
    }
}
