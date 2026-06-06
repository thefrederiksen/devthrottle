using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Cockpit "Interrupted sessions" aggregator (issue #212 W3): GET /interrupted fans out
/// to every Director for the crash journals left on its machine, flattens them to one row per
/// recoverable session, dedupes journals reported by sibling Directors that share a machine
/// dir, and routes a dismiss to the reporting Director.
/// </summary>
public sealed class GatewayInterruptedTests : IAsyncLifetime
{
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;
    private readonly List<FakeDirector> _fakes = new();
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-instances-" + Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: FreePort(), token: "test-token", authEnabled: true,
            instancesDirectory: _instancesDir);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        foreach (var f in _fakes) await f.DisposeAsync();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { }
    }

    [Fact]
    public async Task Aggregates_journals_into_one_row_per_session()
    {
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A", User = "alice",
            LastUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            Sessions =
            {
                new CrashJournalSessionDto { SessionId = "s1", Name = "alpha", RepoPath = "/repo/a" },
                new CrashJournalSessionDto { SessionId = "s2", Name = "beta", RepoPath = "/repo/b" },
            },
        };
        await Register(await StartFake("MACHINE_A", new[] { journal }));

        var rows = await GetInterrupted();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.SessionId == "s1" && r.Name == "alpha" && r.DeadDirectorId == "dead-1");
        Assert.Contains(rows, r => r.SessionId == "s2" && r.MachineName == "MACHINE_A");
        Assert.All(rows, r => Assert.Equal(9001, r.DeadPid));
    }

    [Fact]
    public async Task Dedupes_a_journal_reported_by_two_sibling_directors()
    {
        // Two live Directors on the same machine share the crash-journal dir, so both report
        // the same dead journal. The aggregator must list its sessions once.
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A",
            Sessions = { new CrashJournalSessionDto { SessionId = "s1", Name = "alpha", RepoPath = "/r" } },
        };
        await Register(await StartFake("MACHINE_A", new[] { journal }));
        await Register(await StartFake("MACHINE_A", new[] { journal }));

        var rows = await GetInterrupted();

        Assert.Single(rows);
    }

    [Fact]
    public async Task Empty_when_no_director_has_a_dirty_journal()
    {
        await Register(await StartFake("MACHINE_A", Array.Empty<CrashJournalDto>()));
        Assert.Empty(await GetInterrupted());
    }

    [Fact]
    public async Task Dismiss_routes_to_the_reporting_director()
    {
        var journal = new CrashJournalDto
        {
            DirectorId = "dead-1", Pid = 9001, MachineName = "MACHINE_A",
            Sessions = { new CrashJournalSessionDto { SessionId = "s1", Name = "alpha", RepoPath = "/r" } },
        };
        var fake = await StartFake("MACHINE_A", new[] { journal });
        await Register(fake);
        var rows = await GetInterrupted();
        var reportedBy = rows[0].ReportedByDirectorId;

        var resp = await _http.DeleteAsync($"interrupted/dead-1/9001?via={reportedBy}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(("dead-1", 9001), fake.LastDismiss);
    }

    [Fact]
    public async Task Dismiss_without_via_is_rejected()
    {
        await Register(await StartFake("MACHINE_A", Array.Empty<CrashJournalDto>()));
        var resp = await _http.DeleteAsync("interrupted/dead-1/9001");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ---- helpers ----

    private async Task<List<InterruptedSessionDto>> GetInterrupted()
    {
        var list = await _http.GetFromJsonAsync<List<InterruptedSessionDto>>("interrupted");
        // Restrict to journals our fakes serve (a real Director on the dev box can't leak here:
        // fakes use random director ids, but dead-journal ids are fixed - filter by those).
        return (list ?? new()).Where(r => r.DeadDirectorId.StartsWith("dead-")).ToList();
    }

    private async Task Register(FakeDirector fake)
    {
        var req = new DirectorRegistrationRequest
        {
            DirectorId = fake.DirectorId, TailnetEndpoint = fake.BaseUrl, Pid = 1234,
            MachineName = fake.MachineName, User = "tester", Version = "test", StartedAt = DateTime.UtcNow,
        };
        var resp = await _http.PostAsJsonAsync("directors/register", req);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private async Task<FakeDirector> StartFake(string machine, CrashJournalDto[] journals)
    {
        var fake = new FakeDirector(machine, journals);
        await fake.StartAsync();
        _fakes.Add(fake);
        return fake;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    private sealed class FakeDirector : IAsyncDisposable
    {
        public string DirectorId { get; } = Guid.NewGuid().ToString();
        public string MachineName { get; }
        public string BaseUrl { get; private set; } = "";
        public (string, int)? LastDismiss { get; private set; }

        private readonly CrashJournalDto[] _journals;
        private WebApplication? _app;

        public FakeDirector(string machine, CrashJournalDto[] journals)
        {
            MachineName = machine;
            _journals = journals;
        }

        public async Task StartAsync()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ApplicationName = "FakeDirector" });
            builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
            builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port));
            builder.Logging.ClearProviders();
            builder.Services.AddRoutingCore();

            _app = builder.Build();
            _app.UseRouting();
            _app.MapGet("/interrupted", () => Results.Json(_journals));
            _app.MapDelete("/interrupted/{deadDirectorId}/{deadPid:int}", (string deadDirectorId, int deadPid) =>
            {
                LastDismiss = (deadDirectorId, deadPid);
                return Results.Json(new { dismissed = true });
            });
            await _app.StartAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null)
            {
                try { await _app.StopAsync(TimeSpan.FromSeconds(2)); } catch { }
                await _app.DisposeAsync();
                _app = null;
            }
        }
    }
}
