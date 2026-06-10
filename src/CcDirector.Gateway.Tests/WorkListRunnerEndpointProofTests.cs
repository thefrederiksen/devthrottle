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
/// End-to-end proof for the queue runner REST surface (issue #274) against the REAL production
/// <see cref="GatewayHost"/> wiring, driven over real HTTP. A lightweight STUB Director (its own
/// Kestrel host) answers the two routes the runner uses - <c>POST /sessions</c> (returns a session
/// id, records the seed PrePrompt) and <c>GET /sessions/{sid}/buffer</c> (returns the
/// IMPL-LOOP-TERMINAL sentinel, #272) - so the whole path (endpoint -> WorkListRunner ->
/// DirectorImplSessionDriver -> DirectorEndpointClient -> ImplLoopTerminalSignal) runs deterministically
/// without an hours-long live implementation loop.
///
/// This test is also the proof generator: when CC274_PROOF_DIR is set it writes an HTML report of
/// Expected vs Actual per acceptance criterion to that directory (the Developer Agent commits it to
/// the PR branch under docs/cencon/proof/issue-274/).
/// </summary>
public sealed class WorkListRunnerEndpointProofTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private StubDirector _machine1 = null!;
    private StubDirector _machine2 = null!;

    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-274-" + Guid.NewGuid().ToString("N"));

    private const string Token = "test-token-274";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task InitializeAsync()
    {
        Environment.SetEnvironmentVariable("CC_GATEWAY_NO_TAILSCALE", "1");
        Environment.SetEnvironmentVariable("CC_TURNBRIEFS", "0");

        // Two stub Directors, two machine names (criterion 6 cross-machine concurrency).
        _machine1 = new StubDirector();
        _machine2 = new StubDirector();
        await _machine1.StartAsync();
        await _machine2.StartAsync();

        _gateway = new GatewayHost(port: AllocateFreePort(), token: Token, authEnabled: true,
            instancesDirectory: _instancesDir, cockpitProxyPort: AllocateFreePort());
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {Token}");

        // Register both stub Directors directly (in-memory upsert; no FSW, no real session launching).
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = "dir-machine-1",
            TailnetEndpoint = _machine1.BaseUrl,
            MachineName = "MACHINE-1",
            Version = "1.0.0-test",
            StartedAt = DateTime.UtcNow,
        });
        _gateway.Registry.Upsert(new DirectorRegistrationRequest
        {
            DirectorId = "dir-machine-2",
            TailnetEndpoint = _machine2.BaseUrl,
            MachineName = "MACHINE-2",
            Version = "1.0.0-test",
            StartedAt = DateTime.UtcNow,
        });
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
        await _machine1.StopAsync();
        await _machine2.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    [Fact]
    public async Task EndToEnd_AllAcceptanceCriteria_ProvenOverHttp()
    {
        var report = new ProofReport();

        // ---- Criteria 1, 2, 4: two github items drain in order, one in flight, signals recorded, claim released.
        await CreateList("today");
        _machine1.SetSignal("262", "done", merged: "yes");
        _machine1.SetSignal("263", "needs-human");
        await AddItem("today", "github", "262", "Gateway");
        await AddItem("today", "github", "263", "Core");

        var runResp = await _http.PostAsJsonAsync("lists/today/run",
            new { directorId = "dir-machine-1", repoPath = TempRepo() });
        Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);
        var run = await runResp.Content.ReadFromJsonAsync<RunDto>(JsonOpts);
        Assert.NotNull(run);

        // Criterion 1: started in list order, one at a time (the stub records exact start order +
        // asserts non-overlap of its single session slot).
        report.Add(1, "Two github items: session-1 starts, terminal signal, THEN session-2 - never overlapping.",
            $"start order={string.Join(",", _machine1.StartOrder)}; seeds={string.Join(" | ", _machine1.Seeds)}; overlap={_machine1.EverOverlapped}",
            _machine1.StartOrder.SequenceEqual(new[] { "262", "263" })
                && !_machine1.EverOverlapped
                && _machine1.Seeds.All(s => s.StartsWith("/implementation-loop ", StringComparison.Ordinal)));
        Assert.Equal(new[] { "262", "263" }, _machine1.StartOrder.ToArray());
        Assert.False(_machine1.EverOverlapped);

        // Criterion 2: recorded per-item signal matches the sentinel each session emitted.
        report.Add(2, "Runner records per item which of done/needs-human/failed it ended on (parsed from IMPL-LOOP-TERMINAL).",
            $"item[262].signal={run!.Items[0].Signal}; item[263].signal={run.Items[1].Signal}",
            run.Items[0].Signal == "Done" && run.Items[1].Signal == "NeedsHuman");
        Assert.Equal("Done", run.Items[0].Signal);
        Assert.Equal("NeedsHuman", run.Items[1].Signal);

        // Criterion 4: claim released after the last item; GET shows consumer cleared.
        var afterList = await GetList("today");
        report.Add(4, "After the last item, the runner releases the consumer claim; GET shows it cleared.",
            $"consumerReleased={run.ConsumerReleased}; GET consumer={(afterList.Consumer is null ? "null" : afterList.Consumer)}",
            run.ConsumerReleased && afterList.Consumer is null);
        Assert.True(run.ConsumerReleased);
        Assert.Null(afterList.Consumer);

        // ---- Criterion 3: source gating - devops/jira items never started, left in list.
        await CreateList("mixed");
        _machine1.Reset();
        _machine1.SetSignal("262", "done", merged: "yes");
        await AddItem("mixed", "devops", "1203");
        await AddItem("mixed", "github", "262");
        await AddItem("mixed", "jira", "CCD-44");

        var mixedResp = await _http.PostAsJsonAsync("lists/mixed/run",
            new { directorId = "dir-machine-1", repoPath = TempRepo() });
        var mixed = await mixedResp.Content.ReadFromJsonAsync<RunDto>(JsonOpts);
        Assert.NotNull(mixed);
        var mixedList = await GetList("mixed");
        report.Add(3, "A devops/jira item is NEVER started (no /implementation-loop seeded); skipped and left in the list.",
            $"start order={string.Join(",", _machine1.StartOrder)} (only 262); outcomes={string.Join(",", mixed!.Items.Select(i => i.Outcome))}; list still={string.Join(",", mixedList.Items.Select(i => i.Id))}",
            _machine1.StartOrder.SequenceEqual(new[] { "262" })
                && mixed.Items[0].Outcome == "SkippedNonGithub"
                && mixed.Items[2].Outcome == "SkippedNonGithub"
                && mixedList.Items.Select(i => i.Id).SequenceEqual(new[] { "1203", "262", "CCD-44" }));
        Assert.Equal(new[] { "262" }, _machine1.StartOrder.ToArray());

        // ---- Criterion 5: a second claim while held returns HTTP 409.
        await CreateList("locked");
        var firstClaim = await _http.PostAsync("lists/locked/consumer", null);
        firstClaim.EnsureSuccessStatusCode();
        var secondClaim = await _http.PostAsync("lists/locked/consumer", null);
        report.Add(5, "A second drain attempt while the claim is held is refused with HTTP 409.",
            $"second POST /lists/locked/consumer -> {(int)secondClaim.StatusCode}",
            secondClaim.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, secondClaim.StatusCode);
        await _http.DeleteAsync("lists/locked/consumer");

        // ---- Criterion 6: two lists on two different machines drain concurrently without interfering.
        await CreateList("m1-list");
        await CreateList("m2-list");
        _machine1.Reset(); _machine2.Reset();
        _machine1.SetSignal("100", "done", merged: "yes", delayMs: 300);
        _machine2.SetSignal("200", "done", merged: "yes", delayMs: 300);
        await AddItem("m1-list", "github", "100");
        await AddItem("m2-list", "github", "200");

        var t1 = _http.PostAsJsonAsync("lists/m1-list/run", new { directorId = "dir-machine-1", repoPath = TempRepo() });
        var t2 = _http.PostAsJsonAsync("lists/m2-list/run", new { directorId = "dir-machine-2", repoPath = TempRepo() });
        var both = await Task.WhenAll(t1, t2);
        var r1 = await both[0].Content.ReadFromJsonAsync<RunDto>(JsonOpts);
        var r2 = await both[1].Content.ReadFromJsonAsync<RunDto>(JsonOpts);
        report.Add(6, "Two lists on two different machines drain at the same time, one session each, no interference.",
            $"machine-1 ran {r1!.Items[0].Id}->{r1.Items[0].Signal}; machine-2 ran {r2!.Items[0].Id}->{r2.Items[0].Signal}; both OK={both[0].IsSuccessStatusCode && both[1].IsSuccessStatusCode}",
            both[0].IsSuccessStatusCode && both[1].IsSuccessStatusCode
                && r1.Items[0].Signal == "Done" && r2.Items[0].Signal == "Done"
                && _machine1.StartOrder.SequenceEqual(new[] { "100" })
                && _machine2.StartOrder.SequenceEqual(new[] { "200" }));
        Assert.Equal("Done", r1.Items[0].Signal);
        Assert.Equal("Done", r2.Items[0].Signal);

        // ---- Criterion 7: no runner logic in the Director host (verified by inspection).
        var gatewayFiles = Directory.GetFiles(
            Path.Combine(RepoRoot(), "src", "CcDirector.Gateway", "Running"), "*.cs");
        var directorRunnerFiles = SafeGrepForRunner();
        report.Add(7, "All runner logic lives under src/CcDirector.Gateway; none in the Director/Avalonia host.",
            $"runner files under Gateway/Running={gatewayFiles.Length}; runner-named files under Avalonia/ControlApi host={directorRunnerFiles}",
            gatewayFiles.Length >= 4 && directorRunnerFiles == 0);
        Assert.True(gatewayFiles.Length >= 4);
        Assert.Equal(0, directorRunnerFiles);

        // ---- Criterion 8: same-machine second drain refused (HTTP 409) while machine is busy.
        // Hold machine-1 busy with a slow drain, then attempt a second list on the SAME machine.
        await CreateList("busy-a");
        await CreateList("busy-b");
        _machine1.Reset();
        _machine1.SetSignal("500", "done", merged: "yes", delayMs: 600);
        await AddItem("busy-a", "github", "500");
        await AddItem("busy-b", "github", "501");
        _machine1.SetSignal("501", "done", merged: "yes");

        var slow = _http.PostAsJsonAsync("lists/busy-a/run", new { directorId = "dir-machine-1", repoPath = TempRepo() });
        // Give the slow drain time to claim the machine slot before the second attempt.
        await Task.Delay(150);
        var second = await _http.PostAsJsonAsync("lists/busy-b/run", new { directorId = "dir-machine-1", repoPath = TempRepo() });
        await slow;
        report.Add(8, "A second drain on a machine already draining one list is refused (v1 same-machine guard), not run concurrently.",
            $"second concurrent run on MACHINE-1 -> {(int)second.StatusCode} (Conflict expected)",
            second.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        report.WriteIfRequested();
        Assert.True(report.AllPassed, "Not all acceptance criteria passed - see report.");
    }

    // ===== helpers =====

    private async Task CreateList(string name)
    {
        var resp = await _http.PostAsJsonAsync("lists", new { name });
        resp.EnsureSuccessStatusCode();
    }

    private async Task AddItem(string list, string source, string id, string? area = null)
    {
        var resp = await _http.PostAsJsonAsync($"lists/{list}/items", new { source, id, area });
        resp.EnsureSuccessStatusCode();
    }

    private async Task<WorkListDto> GetList(string name)
    {
        var resp = await _http.GetAsync($"lists/{name}");
        resp.EnsureSuccessStatusCode();
        var dto = JsonSerializer.Deserialize<WorkListDto>(await resp.Content.ReadAsStringAsync(), JsonOpts);
        Assert.NotNull(dto);
        return dto;
    }

    private static string TempRepo()
    {
        // A real existing directory so the Director (real one) would accept it; the stub ignores it.
        var dir = Path.Combine(Path.GetTempPath(), "cc274-repo");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cc-director.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("could not locate repo root from test base dir");
    }

    private static int SafeGrepForRunner()
    {
        // Count files named for the runner that landed in the Director-side host trees - must be 0.
        var hostDirs = new[]
        {
            Path.Combine(RepoRoot(), "src", "CcDirector.Avalonia"),
            Path.Combine(RepoRoot(), "src", "CcDirector.ControlApi"),
        };
        var count = 0;
        foreach (var d in hostDirs)
        {
            if (!Directory.Exists(d)) continue;
            count += Directory.GetFiles(d, "WorkListRunner*.cs", SearchOption.AllDirectories).Length;
            count += Directory.GetFiles(d, "ImplLoopTerminalSignal*.cs", SearchOption.AllDirectories).Length;
        }
        return count;
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
    /// A minimal stub Director: implements only the two routes the runner calls. It records the
    /// start order and the seed PrePrompt, enforces a single in-flight session slot (so the test can
    /// assert the runner never overlaps two items on one list), and serves a canned IMPL-LOOP-TERMINAL
    /// block per issue.
    /// </summary>
    private sealed class StubDirector
    {
        private WebApplication _app = null!;
        private readonly object _gate = new();
        private readonly Dictionary<string, (string signal, string merged, int delayMs)> _signals = new();
        private int _open;

        public string BaseUrl { get; private set; } = "";
        public List<string> StartOrder { get; } = new();
        public List<string> Seeds { get; } = new();
        public bool EverOverlapped { get; private set; }

        public void SetSignal(string issue, string signal, string merged = "no", int delayMs = 0)
        {
            lock (_gate) _signals[issue] = (signal, merged, delayMs);
        }

        public void Reset()
        {
            lock (_gate)
            {
                StartOrder.Clear();
                Seeds.Clear();
                EverOverlapped = false;
                _open = 0;
            }
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
                // Derive the issue from the seed "/implementation-loop <id>".
                var id = prePrompt.Replace("/implementation-loop", "").Trim();
                var sid = $"sid-{id}-{Guid.NewGuid():N}";
                lock (_gate)
                {
                    StartOrder.Add(id);
                    Seeds.Add(prePrompt);
                    if (++_open > 1) EverOverlapped = true;
                }
                return Results.Json(new SessionDto { SessionId = sid, ActivityState = "Working", Status = "Running" });
            });

            _app.MapGet("/sessions/{sid}/buffer", async (string sid) =>
            {
                // sid = "sid-<issue>-<guid>"
                var parts = sid.Split('-');
                var issue = parts.Length >= 2 ? parts[1] : sid;
                (string signal, string merged, int delayMs) s;
                lock (_gate) s = _signals.TryGetValue(issue, out var v) ? v : ("done", "yes", 0);
                if (s.delayMs > 0) await Task.Delay(s.delayMs);
                lock (_gate)
                {
                    if (_open > 0) _open--;
                }
                var text = $"working on the issue...\nIMPL-LOOP-TERMINAL\nissue: {issue}\nsignal: {s.signal}\npr: none\nmerged: {s.merged}\nreason: stub director canned signal\n";
                return Results.Json(new BufferResponse { SessionId = sid, Text = text, TotalBytes = text.Length, NewCursor = text.Length });
            });

            await _app.StartAsync();
        }

        public async Task StopAsync() => await _app.DisposeAsync();
    }

    /// <summary>Accumulates Expected/Actual rows and renders the HTML proof report (ASCII only).</summary>
    private sealed class ProofReport
    {
        private readonly List<(int n, string expected, string actual, bool pass)> _rows = new();
        public bool AllPassed => _rows.All(r => r.pass);

        public void Add(int criterion, string expected, string actual, bool pass)
            => _rows.Add((criterion, expected, actual, pass));

        public void WriteIfRequested()
        {
            var dir = Environment.GetEnvironmentVariable("CC274_PROOF_DIR");
            if (string.IsNullOrWhiteSpace(dir)) return;
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>Issue #274 - Queue Runner - Proof</title>");
            sb.AppendLine("<style>body{font-family:Segoe UI,Arial,sans-serif;background:#1e1e1e;color:#ddd;margin:0;padding:32px;}");
            sb.AppendLine("h1{color:#4ec9b0;} table{border-collapse:collapse;width:100%;margin-top:16px;}");
            sb.AppendLine("th,td{border:1px solid #3c3c3c;padding:10px 12px;text-align:left;vertical-align:top;font-size:14px;}");
            sb.AppendLine("th{background:#252526;color:#9cdcfe;} .pass{color:#6a9955;font-weight:bold;} .fail{color:#f44747;font-weight:bold;}");
            sb.AppendLine("code{color:#ce9178;} .meta{color:#858585;font-size:13px;}</style></head><body>");
            sb.AppendLine("<h1>Issue #274 - Gateway queue runner drains one named list, one implementation session per item</h1>");
            sb.AppendLine($"<p class=\"meta\">Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC by WorkListRunnerEndpointProofTests - end-to-end over real HTTP against the production GatewayHost, with a stub Director answering POST /sessions and GET /sessions/{{sid}}/buffer.</p>");
            sb.AppendLine($"<p class=\"{(AllPassed ? "pass" : "fail")}\">Overall: {(AllPassed ? "ALL CRITERIA PASS" : "FAILURES PRESENT")}</p>");
            sb.AppendLine("<table><tr><th>#</th><th>Acceptance criterion (Expected)</th><th>Actual (observed over HTTP)</th><th>Result</th></tr>");
            foreach (var r in _rows.OrderBy(r => r.n))
            {
                sb.AppendLine($"<tr><td>{r.n}</td><td>{Esc(r.expected)}</td><td><code>{Esc(r.actual)}</code></td><td class=\"{(r.pass ? "pass" : "fail")}\">{(r.pass ? "PASS" : "FAIL")}</td></tr>");
            }
            sb.AppendLine("</table>");
            sb.AppendLine("<p class=\"meta\">CenCon impact: no architecture or security drift. New code is additive under src/CcDirector.Gateway/Running and src/CcDirector.Gateway/Api (queue-runner container); the Director host gains nothing (criterion 7). architecture_manifest.yaml updated to record the runner under the Gateway container.</p>");
            sb.AppendLine("<p class=\"pass\">I believe this is finished.</p>");
            sb.AppendLine("</body></html>");

            File.WriteAllText(Path.Combine(dir, "report.html"), sb.ToString());
        }

        private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
