using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.Gateway;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Runner-level proof for the devops source adapter (issue #300, decision D-4 level (a)) against
/// the REAL production <see cref="GatewayHost"/> wiring, driven over real HTTP. A lightweight STUB
/// Director answers the two routes the runner uses (<c>POST /sessions</c> records the seed
/// PrePrompt; <c>GET /sessions/{sid}/buffer</c> serves the IMPL-LOOP-TERMINAL sentinel), simulating
/// the seeded session exactly the way the #274/#276 queue-runner proofs did. It proves:
///
///   1. A <c>source = devops</c> ref IS dispatched: a session-seed request is emitted with the
///      devops-mode seed prompt (<c>/implementation-loop --source devops &lt;id&gt;</c>).
///   2. The terminal sentinel is correlated by the WORK ITEM ID (source-agnostic contract) and the
///      terminal signal is recorded on the item result - the runner-side half of write-back (the
///      tracker-side half, az boards claim/state transitions, is the seeded session's job per D-2
///      and is proven live in the issue-300 az demonstration).
///   3. A <c>jira</c> ref still skips with the existing note (regression).
///   4. The <c>github</c> path is unchanged: plain seed prompt, same drain behavior (regression).
///
/// When CC300_PROOF_DIR is set it writes the Expected-vs-Actual HTML rows to that directory (the
/// Developer Agent commits it under docs/cencon/proof/issue-300/).
/// </summary>
public sealed class DevopsAdapterDispatchProofTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private StubDirector _stub = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-300-" + Guid.NewGuid().ToString("N"));

    private const string Token = "test-token-300";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");
        Environment.SetEnvironmentVariable("CC_TURNBRIEFS", "0");

        _stub = new StubDirector();
        await _stub.StartAsync();

        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: AllocateFreePort());
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {Token}");

        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = "dir-300",
            TailnetEndpoint = _stub.BaseUrl,
            MachineName = "MACHINE-300",
            Version = "1.0.0-test",
            StartedAt = DateTime.UtcNow,
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _stub.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    [Fact]
    public async Task DevopsRef_DispatchedSeededCorrelated_JiraSkipped_GithubUnchanged()
    {
        var report = new ProofReport();

        // One list carrying all three sources, in order: devops, github, jira.
        var create = await _http.PostAsJsonAsync("lists", new { name = "adapter-300" });
        create.EnsureSuccessStatusCode();
        foreach (var (source, id) in new[] { ("devops", "9001"), ("github", "262"), ("jira", "CCD-44") })
        {
            var add = await _http.PostAsJsonAsync("lists/adapter-300/items", new { source, id });
            add.EnsureSuccessStatusCode();
        }

        _stub.SetSignal("9001", "done", merged: "yes");
        _stub.SetSignal("262", "done", merged: "yes");

        var runResp = await _http.PostAsJsonAsync("lists/adapter-300/run",
            new { directorId = "dir-300", repoPath = TempRepo() });
        Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);
        var run = await runResp.Content.ReadFromJsonAsync<RunDto>(JsonOpts);
        Assert.NotNull(run);

        // (1) devops seed emitted in devops mode.
        report.Add(1, "source=devops ref is dispatched: session-seed request emitted with the devops-mode seed prompt '/implementation-loop --source devops 9001'.",
            $"seeds observed at the stub Director (real HTTP POST /sessions): {string.Join(" | ", _stub.Seeds)}",
            _stub.Seeds.Count == 2 && _stub.Seeds[0] == "/implementation-loop --source devops 9001");
        Assert.Equal("/implementation-loop --source devops 9001", _stub.Seeds[0]);

        // (2) sentinel correlated by work item id; terminal signal recorded on the item result.
        report.Add(2, "Terminal sentinel correlated by work item id (issue: 9001) and the terminal signal recorded on the devops item (outcome=Ran, signal=Done) - the runner-side write-back record.",
            $"devops item result: outcome={run!.Items[0].Outcome}, signal={run.Items[0].Signal}, sessionId={(string.IsNullOrEmpty(run.Items[0].SessionId) ? "none" : "present")}",
            run.Items[0].Outcome == "Ran" && run.Items[0].Signal == "Done" && !string.IsNullOrEmpty(run.Items[0].SessionId));
        Assert.Equal("Ran", run.Items[0].Outcome);
        Assert.Equal("Done", run.Items[0].Signal);

        // (3) jira regression: still skipped with the note, never started, left in the list.
        var listResp = await _http.GetAsync("lists/adapter-300");
        listResp.EnsureSuccessStatusCode();
        var list = JsonSerializer.Deserialize<WorkListDto>(await listResp.Content.ReadAsStringAsync(), JsonOpts);
        Assert.NotNull(list);
        report.Add(3, "jira ref still skips with the existing skip-note (no adapter), never started, left in the list.",
            $"jira item result: outcome={run.Items[2].Outcome}, note={run.Items[2].Note}; list still={string.Join(",", list!.Items.Select(i => i.Id))}",
            run.Items[2].Outcome == "SkippedNonGithub"
                && run.Items[2].Note.Contains("source 'jira' is not runnable", StringComparison.Ordinal)
                && list.Items.Select(i => i.Id).SequenceEqual(new[] { "9001", "262", "CCD-44" }));
        Assert.Equal("SkippedNonGithub", run.Items[2].Outcome);
        Assert.Contains("source 'jira' is not runnable", run.Items[2].Note, StringComparison.Ordinal);

        // (4) github regression: plain v1 seed prompt, drained to Done as before.
        report.Add(4, "github path unchanged: plain seed '/implementation-loop 262', item drained to Done exactly as pre-#300.",
            $"github seed={_stub.Seeds[1]}; github item result: outcome={run.Items[1].Outcome}, signal={run.Items[1].Signal}",
            _stub.Seeds[1] == "/implementation-loop 262" && run.Items[1].Outcome == "Ran" && run.Items[1].Signal == "Done");
        Assert.Equal("/implementation-loop 262", _stub.Seeds[1]);
        Assert.Equal("Done", run.Items[1].Signal);

        report.WriteIfRequested();
        Assert.True(report.AllPassed, "Not all proof rows passed - see report.");
    }

    // ===== helpers =====

    private static string TempRepo()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc300-repo");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int AllocateFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private sealed record RunDto(string ListName, string Consumer, bool ConsumerReleased, List<RunItemDto> Items);
    private sealed record RunItemDto(string Source, string Id, string? Area, string Outcome, string? Signal, string? SessionId, string Note);

    /// <summary>
    /// A minimal stub Director simulating the seeded implementation session (the same simulation the
    /// prior queue-runner proofs used): records each seed PrePrompt and serves a canned
    /// IMPL-LOOP-TERMINAL block correlated to the item id parsed from the seed's last token.
    /// </summary>
    private sealed class StubDirector
    {
        private WebApplication _app = null!;
        private readonly object _gate = new();
        private readonly Dictionary<string, (string signal, string merged)> _signals = new();

        public string BaseUrl { get; private set; } = "";
        public List<string> Seeds { get; } = new();

        public void SetSignal(string id, string signal, string merged = "no")
        {
            lock (_gate) _signals[id] = (signal, merged);
        }

        public async Task StartAsync()
        {
            var port = AllocateFreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            _app = builder.Build();
            _app.Urls.Add(BaseUrl);

            _app.MapPost("/sessions", async (HttpContext ctx) =>
            {
                var req = await JsonSerializer.DeserializeAsync<NewSessionRequest>(ctx.Request.Body, JsonOpts);
                var prePrompt = req?.PrePrompt ?? "";
                var tokens = prePrompt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var id = tokens.Length > 0 ? tokens[^1] : "";
                lock (_gate) Seeds.Add(prePrompt);
                return Results.Json(new SessionDto { SessionId = $"sid-{id}-{Guid.NewGuid():N}", ActivityState = "Working", Status = "Running" });
            });

            _app.MapGet("/sessions/{sid}/buffer", (string sid) =>
            {
                var parts = sid.Split('-');
                var id = parts.Length >= 2 ? parts[1] : sid;
                (string signal, string merged) s;
                lock (_gate) s = _signals.TryGetValue(id, out var v) ? v : ("done", "yes");
                var text = $"working on the item...\nIMPL-LOOP-TERMINAL\nissue: {id}\nsignal: {s.signal}\npr: none\nmerged: {s.merged}\nreason: stub session canned signal (issue-300 proof)\n";
                return Results.Json(new BufferResponse { SessionId = sid, Text = text, TotalBytes = text.Length, NewCursor = text.Length });
            });

            await _app.StartAsync();
        }

        public async Task StopAsync() => await _app.DisposeAsync();
    }

    /// <summary>Accumulates Expected/Actual rows and renders the HTML proof fragment (ASCII only).</summary>
    private sealed class ProofReport
    {
        private readonly List<(int n, string expected, string actual, bool pass)> _rows = new();
        public bool AllPassed => _rows.All(r => r.pass);

        public void Add(int n, string expected, string actual, bool pass) => _rows.Add((n, expected, actual, pass));

        public void WriteIfRequested()
        {
            var dir = Environment.GetEnvironmentVariable("CC300_PROOF_DIR");
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine($"<!-- generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC by DevopsAdapterDispatchProofTests -->");
            sb.AppendLine("<table><tr><th>#</th><th>Expected</th><th>Actual (observed over HTTP)</th><th>Result</th></tr>");
            foreach (var r in _rows.OrderBy(r => r.n))
                sb.AppendLine($"<tr><td>{r.n}</td><td>{Esc(r.expected)}</td><td><code>{Esc(r.actual)}</code></td><td class=\"{(r.pass ? "pass" : "fail")}\">{(r.pass ? "PASS" : "FAIL")}</td></tr>");
            sb.AppendLine("</table>");

            File.WriteAllText(Path.Combine(dir, "runner-dispatch-proof.html"), sb.ToString());
        }

        private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
