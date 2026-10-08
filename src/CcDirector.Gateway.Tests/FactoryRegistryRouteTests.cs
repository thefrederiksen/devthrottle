using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The factory registry and goal number routes on a REAL host with authentication enforced (Factories screen
/// mission, phase A). The point of this file is the session key: <c>cc-devthrottle factory register</c> and
/// <c>factory goal-number</c> run inside sessions, and a route SessionKeyGuard does not name answers 403 to every
/// one of them while every store test stays green. So each route is called here with a session's own key, minted
/// and registered exactly as a Director's Hello registers one.
///
/// Each test uses its own factory id, so it reads only its own rows whatever else the host's database holds.
/// </summary>
public sealed class FactoryRegistryRouteTests
{
    private const string Token = "factory-registry-route-token";
    private const string DirectorId = "director-factory-registry-route";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Host : IAsyncDisposable
    {
        public GatewayHost Gateway = null!;
        public HttpClient Owner = null!;
        public HttpClient Session = null!;
        public Guid SessionId = Guid.NewGuid();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-factory-registry-" + Guid.NewGuid().ToString("N"));

        public static async Task<Host> StartAsync(bool factoryAgentsEnabled)
        {
            var h = new Host();
            h.Gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
                instancesDirectory: h._dir,
                workListsPath: Path.Combine(h._dir, "worklists", "worklists.json"),
                factoryAgentsEnabled: factoryAgentsEnabled);
            await h.Gateway.StartAsync();

            var baseAddress = new Uri($"http://127.0.0.1:{h.Gateway.Port}/");
            h.Owner = new HttpClient { BaseAddress = baseAddress };
            h.Owner.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

            var key = GatewaySessionKey.Mint();
            Assert.True(h.Gateway.SessionKeys.Register(TenantId.Local, DirectorId, h.SessionId.ToString(),
                GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
            h.Session = new HttpClient { BaseAddress = baseAddress };
            h.Session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return h;
        }

        public async ValueTask DisposeAsync()
        {
            Owner.Dispose();
            Session.Dispose();
            await Gateway.StopAsync();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }
    }

    private static object Manifest(string factory, string ceoName = "Nora Hale") => new
    {
        factory,
        title = "WarmForward",
        folder = @"D:\ReposFred\cc-consult\ideas\warmforward-factory",
        computer = "SOREN_NORTH",
        ceoSeat = "nora-hale",
        goalText = "A cash engine that runs without your time.",
        goalFile = "GOAL.md",
        goalApprovedOn = "2026-10-04",
        seats = new object[]
        {
            new { id = "nora-hale", name = ceoName, role = "CEO", briefFile = "agents/ceo.yaml", schedules = Array.Empty<string>() },
            new { id = "savings-engineer", name = "Savings Engineer", role = "Savings Engineer", briefFile = "agents/savings.yaml", schedules = Array.Empty<string>() },
        },
    };

    private static object Number(string factory, string? by = null) => new
    {
        factory,
        value = "not yet proven",
        unit = "propane saved this season",
        asOf = "2026-10-06",
        link = "https://github.com/thefrederiksen/websites/issues/276",
        postedBy = by,
    };

    private static async Task<(HttpStatusCode Status, string Body)> Send(Task<HttpResponseMessage> call)
    {
        using var resp = await call;
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_session_key_registers_lists_posts_and_reads_a_goal_number()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N")[..12];

        var put = await Send(h.Session.PutAsJsonAsync("gateway/factory/registry", Manifest(factory)));
        Assert.True(put.Status == HttpStatusCode.OK, $"PUT registry with a session key: {(int)put.Status} {put.Body}");
        var registered = JsonSerializer.Deserialize<RegisteredFactoryDto>(put.Body, Web)!;
        Assert.Equal(factory, registered.Factory);
        Assert.Equal("session " + h.SessionId, registered.RegisteredBy);

        var list = await Send(h.Session.GetAsync("gateway/factory/registry"));
        Assert.True(list.Status == HttpStatusCode.OK, $"GET registry with a session key: {(int)list.Status} {list.Body}");
        Assert.Contains(JsonSerializer.Deserialize<FactoryRegistryListDto>(list.Body, Web)!.Factories, f => f.Factory == factory);

        var post = await Send(h.Session.PostAsJsonAsync("gateway/factory/goal-numbers", Number(factory, by: "nora-hale")));
        Assert.True(post.Status == HttpStatusCode.Created, $"POST goal-numbers with a session key: {(int)post.Status} {post.Body}");
        var posted = JsonSerializer.Deserialize<GoalNumberDto>(post.Body, Web)!;
        Assert.Equal("nora-hale", posted.PostedBy);
        Assert.Equal(h.SessionId.ToString(), posted.PostedBySession);

        var show = await Send(h.Session.GetAsync($"gateway/factory/goal-numbers?factory={factory}"));
        Assert.True(show.Status == HttpStatusCode.OK, $"GET goal-numbers with a session key: {(int)show.Status} {show.Body}");
        var read = JsonSerializer.Deserialize<GoalNumbersDto>(show.Body, Web)!;
        Assert.Equal(1, read.Count);
        Assert.Equal("not yet proven", read.Latest!.Value);
    }

    [Fact]
    public async Task A_factory_agents_own_session_posts_as_that_agent_and_may_not_name_another_seat_or_factory()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N")[..12];
        Assert.Equal(HttpStatusCode.OK, (await h.Owner.PutAsJsonAsync("gateway/factory/registry", Manifest(factory))).StatusCode);

        // The activity record says nora-hale of this factory started this session.
        var started = await Send(h.Owner.PostAsJsonAsync("gateway/factory/activity", new
        {
            factory, factoryAgent = "nora-hale", outcome = FactoryActivityOutcome.Started,
            what = "Morning run started.", sessionId = h.SessionId.ToString(), actor = "trigger:test",
        }));
        Assert.True(started.Status == HttpStatusCode.Created, started.Body);

        var asItself = await Send(h.Session.PostAsJsonAsync("gateway/factory/goal-numbers", Number(factory)));
        Assert.True(asItself.Status == HttpStatusCode.Created, asItself.Body);
        Assert.Equal("nora-hale", JsonSerializer.Deserialize<GoalNumberDto>(asItself.Body, Web)!.PostedBy);

        var asAnother = await Send(h.Session.PostAsJsonAsync("gateway/factory/goal-numbers", Number(factory, by: "savings-engineer")));
        Assert.Equal(HttpStatusCode.BadRequest, asAnother.Status);
        Assert.Contains("Leave --by out", asAnother.Body);

        var otherFactory = await Send(h.Session.PostAsJsonAsync("gateway/factory/goal-numbers", Number("some-other-factory", by: "nora-hale")));
        Assert.Equal(HttpStatusCode.Forbidden, otherFactory.Status);
    }

    [Fact]
    public async Task A_goal_number_for_an_unregistered_factory_is_409_with_the_reason_not_a_bare_404()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var post = await Send(h.Session.PostAsJsonAsync("gateway/factory/goal-numbers", Number("never-registered-" + Guid.NewGuid().ToString("N")[..8], by: "nora-hale")));
        Assert.Equal(HttpStatusCode.Conflict, post.Status);
        Assert.Contains("not registered", post.Body);
    }

    [Fact]
    public async Task A_refused_manifest_is_400_with_the_reason()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var put = await Send(h.Session.PutAsJsonAsync("gateway/factory/registry", new { factory = "x", title = "X", folder = "relative/path", computer = "N", seats = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, put.Status);
        Assert.Contains("absolute", put.Body);
    }

    [Fact]
    public async Task The_owner_reads_the_Factories_screen_and_a_session_key_is_refused_it()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N")[..12];
        // A CEO name no other test registers: two registered CEOs with one name read "Talk to the CEO".
        var ceo = "Nora " + factory;
        Assert.Equal(HttpStatusCode.OK, (await h.Owner.PutAsJsonAsync("gateway/factory/registry", Manifest(factory, ceo))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await h.Owner.PostAsJsonAsync("gateway/factory/goal-numbers", Number(factory, by: "nora-hale"))).StatusCode);

        var list = await Send(h.Owner.GetAsync("gateway/factories"));
        Assert.True(list.Status == HttpStatusCode.OK, $"GET factories: {(int)list.Status} {list.Body}");
        var row = Assert.Single(JsonSerializer.Deserialize<FactoriesListViewDto>(list.Body, Web)!.Rows, r => r.Id == factory);
        Assert.Equal("PAUSED", row.StatusWord); // its one schedule id names no schedule, so nothing runs its seats
        Assert.Equal("Talk to " + ceo, row.Talk!.Label);

        var page = await Send(h.Owner.GetAsync($"gateway/factories/{factory}"));
        Assert.True(page.Status == HttpStatusCode.OK, $"GET page: {(int)page.Status} {page.Body}");
        var dto = JsonSerializer.Deserialize<FactoryPageViewDto>(page.Body, Web)!;
        Assert.Equal("A cash engine that runs without your time.", dto.Goal.Text);
        Assert.Equal("Propane saved this season: not yet proven", dto.GoalNumber.ValueText);
        Assert.Equal("None yet.", dto.LastTalk.Text);

        var seats = await Send(h.Owner.GetAsync($"gateway/factories/{factory}/seats"));
        Assert.True(seats.Status == HttpStatusCode.OK, $"GET seats: {(int)seats.Status} {seats.Body}");
        Assert.Equal(new[] { "nora-hale", "savings-engineer" },
            JsonSerializer.Deserialize<FactorySeatsViewDto>(seats.Body, Web)!.Rows.Select(r => r.SeatId));

        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.GetAsync("gateway/factories/never-registered")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Session.GetAsync("gateway/factories")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Session.GetAsync($"gateway/factories/{factory}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Session.GetAsync($"gateway/factories/{factory}/seats")).StatusCode);
    }

    [Fact]
    public async Task The_owner_marks_a_failure_handled_with_a_new_row_and_it_stops_counting_and_a_session_key_may_not()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N")[..12];
        Assert.Equal(HttpStatusCode.OK, (await h.Owner.PutAsJsonAsync("gateway/factory/registry", Manifest(factory, "Nora " + factory))).StatusCode);
        var wrote = await Send(h.Owner.PostAsJsonAsync("gateway/factory/activity", new
        {
            factory, factoryAgent = "nora-hale", outcome = FactoryActivityOutcome.Failed, subject = "Zone 8",
            what = "Meter read failed.", actor = "test",
        }));
        Assert.True(wrote.Status == HttpStatusCode.Created, $"POST activity: {(int)wrote.Status} {wrote.Body}");
        var failed = JsonSerializer.Deserialize<FactoryActivityDto>(wrote.Body, Web)!;

        var before = JsonSerializer.Deserialize<FactoryPageViewDto>((await Send(h.Owner.GetAsync($"gateway/factories/{factory}"))).Body, Web)!;
        Assert.Equal("FAILING", before.StatusWord);
        Assert.Equal($"/factories/{factory}#failing", before.StatusHref);
        Assert.Equal(failed.Id, Assert.Single(before.Failures!.Items).Id);

        var url = $"gateway/factories/{factory}/failures/{failed.Id}/handled";
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Session.PostAsync(url, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.PostAsync($"gateway/factories/{factory}/failures/{Guid.NewGuid()}/handled", null)).StatusCode);

        var handled = await Send(h.Owner.PostAsync(url, null));
        Assert.True(handled.Status == HttpStatusCode.Created, $"POST handled: {(int)handled.Status} {handled.Body}");
        var row = JsonSerializer.Deserialize<FactoryActivityDto>(handled.Body, Web)!;
        Assert.Equal(failed.Id, row.CorrectsId);
        Assert.NotEqual(failed.Id, row.Id);

        var after = JsonSerializer.Deserialize<FactoryPageViewDto>((await Send(h.Owner.GetAsync($"gateway/factories/{factory}"))).Body, Web)!;
        Assert.NotEqual("FAILING", after.StatusWord);
        Assert.Null(after.Failures);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.Owner.PostAsync(url, null)).StatusCode);

        // The failed row is still in the record, unchanged.
        var kept = await Send(h.Owner.GetAsync($"gateway/factory/activity?factory={factory}&outcome=failed"));
        Assert.Contains(failed.Id.ToString(), kept.Body);
    }

    /// <summary>
    /// Issue #3650, round-2 review finding 2: the firing guard as the REAL host wires it. A schedule whose seat was
    /// dropped while it was off is run on demand, and the run is refused by the guard before any machine is asked -
    /// read from the starter's own log line, because a machine that does not exist would also leave "not-started".
    /// Replace the host's guard with one that never refuses and this test goes red.
    /// </summary>
    [Fact]
    public async Task A_schedule_whose_seat_was_dropped_while_off_is_refused_by_the_hosts_firing_guard()
    {
        using var log = CcDirector.Core.Utilities.FileLog.RedirectForTests();
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N")[..12];
        Assert.Equal(HttpStatusCode.OK, (await h.Owner.PutAsJsonAsync("gateway/factory/registry", Manifest(factory))).StatusCode);

        var job = new CronJobDto
        {
            Name = "Savings Engineer - nightly", ScheduleKind = "recurring", CronExpression = "0 3 * * *", TimeZoneId = "UTC",
            Factory = factory, Seat = "savings-engineer",
            Target = new CronJobTarget { Machine = "NO-SUCH-MACHINE" },
            Action = new CronJobAction { RepoPath = @"D:\factory", Seed = "/savings" },
        };
        var created = await Send(h.Owner.PostAsJsonAsync("cron/jobs", job));
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var id = JsonSerializer.Deserialize<CronJobDto>(created.Body, Web)!.Id;
        job.Enabled = false;
        Assert.Equal(HttpStatusCode.OK, (await h.Owner.PutAsJsonAsync($"cron/jobs/{id}", job)).StatusCode);

        // The seat goes while its schedule is off - allowed - and the schedule is then run by hand.
        var withoutSeat = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(Manifest(factory), Web), Web)!;
        withoutSeat["seats"] = new object[] { new { id = "nora-hale", name = "Nora Hale", role = "CEO", briefFile = "agents/ceo.yaml" } };
        var reregistered = await Send(h.Owner.PutAsJsonAsync("gateway/factory/registry", withoutSeat));
        Assert.True(reregistered.Status == HttpStatusCode.OK, reregistered.Body);
        var run = await Send(h.Owner.PostAsync($"cron/jobs/{id}/run", null));

        Assert.True(run.Status == HttpStatusCode.OK, run.Body);
        Assert.Contains("not-started", run.Body);
        var lines = log.DrainAndReadLines();
        Assert.Contains(lines, l => l.Contains($"[DirectorCronSessionStarter] start REFUSED: job={id}") && l.Contains("'savings-engineer' is not a seat"));
        Assert.DoesNotContain(lines, l => l.Contains($"[DirectorCronSessionStarter] start: job={id}"));
    }

    /// <summary>Round-3 review finding 1: run-now on a schedule of an ARCHIVED factory is refused by the host's guard.</summary>
    [Fact]
    public async Task Running_a_schedule_of_an_archived_factory_now_is_refused_by_the_hosts_firing_guard()
    {
        using var log = CcDirector.Core.Utilities.FileLog.RedirectForTests();
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N")[..12];
        Assert.Equal(HttpStatusCode.OK, (await h.Owner.PutAsJsonAsync("gateway/factory/registry", Manifest(factory))).StatusCode);
        var created = await Send(h.Owner.PostAsJsonAsync("cron/jobs", new CronJobDto
        {
            Name = "CEO - morning", ScheduleKind = "recurring", CronExpression = "0 7 * * *", TimeZoneId = "UTC",
            Factory = factory, Seat = "nora-hale",
            Target = new CronJobTarget { Machine = "NO-SUCH-MACHINE" },
            Action = new CronJobAction { RepoPath = @"D:\factory", Seed = "/ceo" },
        }));
        Assert.True(created.Status == HttpStatusCode.Created, created.Body);
        var id = JsonSerializer.Deserialize<CronJobDto>(created.Body, Web)!.Id;
        h.Gateway.FactoryRegistry.Archive(TenantId.Local, factory, "owner (test)", new[] { id }, DateTime.UtcNow);

        var run = await Send(h.Owner.PostAsync($"cron/jobs/{id}/run", null));

        Assert.True(run.Status == HttpStatusCode.OK, run.Body);
        var lines = log.DrainAndReadLines();
        Assert.Contains(lines, l => l.Contains($"[DirectorCronSessionStarter] start REFUSED: job={id}") && l.Contains("is archived"));
        Assert.DoesNotContain(lines, l => l.Contains($"[DirectorCronSessionStarter] start: job={id}"));
    }

    [Fact]
    public async Task Switch_off_every_registry_route_answers_404()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: false);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Session.PutAsJsonAsync("gateway/factory/registry", Manifest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Session.GetAsync("gateway/factory/registry")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Session.PostAsJsonAsync("gateway/factory/goal-numbers", Number("x", "nora-hale"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.GetAsync("gateway/factory/goal-numbers?factory=x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.GetAsync("gateway/factories")).StatusCode);
    }
}
