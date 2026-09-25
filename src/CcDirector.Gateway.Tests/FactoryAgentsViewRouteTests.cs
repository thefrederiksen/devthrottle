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
/// The owner's Factory Agents pages on a REAL host (Website Business Factory, product track, Screens 1-4), wired to
/// the real activity record: rows a session writes through the record's own route are what the owner's pages
/// show, the switch route answers either way, the pages 404 while the switch is off, a session key is refused
/// the owner's pages, and "I have handled it" clears an escalation by writing a correcting row. The fold's own
/// claims are proved without a server in <c>FactoryAgentsFoldTests</c>.
///
/// Each test uses its own factory id, so it reads only its own rows whatever else the host's database holds.
/// </summary>
public sealed class FactoryAgentsViewRouteTests
{
    private const string Token = "factory-agents-view-route-token";
    private const string DirectorId = "director-factory-agents-view-route";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Host : IAsyncDisposable
    {
        public GatewayHost Gateway = null!;
        public HttpClient Owner = null!;
        public HttpClient Session = null!;
        public Guid SessionId = Guid.NewGuid();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-factory-views-" + Guid.NewGuid().ToString("N"));

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

            // A session key registered exactly as a Director's Hello registers one.
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

    private static async Task<FactoryActivityDto> RecordAsync(HttpClient session, string factory, string outcome, string what)
    {
        var resp = await session.PostAsJsonAsync("gateway/factory/activity", new
        {
            factory,
            factoryAgent = "front-desk",
            outcome,
            what,
            subject = "Pine Valley Plumbing",
        });
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Created, $"expected 201, got {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<FactoryActivityDto>(text, Web)!;
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        var resp = await client.GetAsync(path);
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"GET {path}: expected 200, got {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, Web)!;
    }

    [Fact]
    public async Task Switch_on_the_owners_pages_show_the_rows_the_record_holds()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N");
        await RecordAsync(h.Session, factory, FactoryActivityOutcome.Done, "Added an address to the remove-me list.");
        await RecordAsync(h.Session, factory, FactoryActivityOutcome.Blocked, "Refused to send a price nobody approved.");

        Assert.True((await GetAsync<FactoryAgentsSwitchDto>(h.Owner, "gateway/factory-agents/switch")).Enabled);

        var factories = await GetAsync<FactoriesViewDto>(h.Owner, "gateway/factory-agents/factories");
        Assert.Contains(factories.Factories, c => c.Id == factory);

        var activity = await GetAsync<FactoryActivityViewDto>(h.Owner, $"gateway/factory-agents/activity?factory={factory}");
        Assert.Contains(activity.Rows, r => r.What == "Added an address to the remove-me list.");
        Assert.Contains(activity.Rows, r => r.What == "Refused to send a price nobody approved.");
    }

    [Fact]
    public async Task Switch_on_I_have_handled_it_writes_a_correcting_row_and_the_escalation_leaves_the_waiting_list()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N");
        var escalation = await RecordAsync(h.Session, factory, FactoryActivityOutcome.Escalated, "A customer asked for a refund.");

        var before = await GetAsync<FactoryWaitingViewDto>(h.Owner, $"gateway/factory-agents/waiting?factory={factory}");
        Assert.Contains(before.Items, i => i.Id == escalation.Id);

        var handled = await h.Owner.PostAsync($"gateway/factory-agents/waiting/{escalation.Id}/handled", null);
        var text = await handled.Content.ReadAsStringAsync();
        Assert.True(handled.StatusCode == HttpStatusCode.Created, $"expected 201, got {(int)handled.StatusCode}: {text}");
        var correction = JsonSerializer.Deserialize<FactoryActivityDto>(text, Web)!;
        Assert.Equal(escalation.Id, correction.CorrectsId);
        Assert.StartsWith("owner", correction.Actor);

        var after = await GetAsync<FactoryWaitingViewDto>(h.Owner, $"gateway/factory-agents/waiting?factory={factory}");
        Assert.DoesNotContain(after.Items, i => i.Id == escalation.Id);
    }

    [Fact]
    public async Task Switch_on_a_real_trigger_shows_on_its_factory_and_Pause_and_Resume_reach_it()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N");
        var created = await h.Owner.PostAsJsonAsync("triggers", new TriggerDefinitionRequest
        {
            Name = "new-mail-" + factory,
            Factory = factory,
            FactoryAgent = "front-desk",
            Machine = Environment.MachineName,
            RepoPath = Path.GetTempPath(),
            CheckCommand = "cc-website-factory mail-waiting --json",
            IntervalSeconds = 300,
            Prompt = "New mail: {count} threads.",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var trigger = (await created.Content.ReadFromJsonAsync<TriggerDto>(Web))!;

        var card = Assert.Single((await GetAsync<FactoriesViewDto>(h.Owner, "gateway/factory-agents/factories")).Factories,
            c => c.Id == factory);
        Assert.Equal("RUNNING", card.StatusWord);
        Assert.Equal("1 factory agent, 1 trigger", card.Subtitle);
        Assert.NotNull(card.Pause);

        Assert.Equal(HttpStatusCode.OK, (await h.Owner.PostAsync($"gateway/factory-agents/factories/{factory}/pause", null)).StatusCode);
        Assert.True((await GetAsync<TriggerDto>(h.Owner, $"triggers/{trigger.Id}")).Paused);
        var paused = Assert.Single((await GetAsync<FactoriesViewDto>(h.Owner, "gateway/factory-agents/factories")).Factories,
            c => c.Id == factory);
        Assert.Equal("PAUSED", paused.StatusWord);

        Assert.Equal(HttpStatusCode.OK,
            (await h.Owner.PostAsync($"gateway/factory-agents/factories/{factory}/agents/front-desk/resume", null)).StatusCode);
        Assert.False((await GetAsync<TriggerDto>(h.Owner, $"triggers/{trigger.Id}")).Paused);
    }

    [Fact]
    public async Task Switch_on_a_session_publishes_its_factorys_map_and_the_owners_Map_tab_draws_it_with_live_status()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N");
        await RecordAsync(h.Session, factory, FactoryActivityOutcome.Done, "Drafted a reply.");

        // Before any map: the factory is known from its record row, and the tab says how a map arrives.
        var before = await GetAsync<FactoryMapViewDto>(h.Owner, $"gateway/factory-agents/factories/{factory}/map");
        Assert.Contains("has not published a map yet", before.EmptyText);
        Assert.Contains(before.Agents, a => a.AgentId == "front-desk");

        var map = new PublishFactoryMapRequest
        {
            Factory = factory, Title = "Test Factory", Source = "factory.yaml in a test", Width = 400, Height = 200,
            Nodes = new()
            {
                new() { Id = "front-desk", Title = "Front Desk", X = 100, Y = 100, Width = 90, Height = 40,
                    Spec = new() { new() { Label = "Inputs", Text = "mail-threads" } } },
                new() { Id = "owner", Kind = FactoryMapNodeKind.Owner, Title = "You", X = 300, Y = 100, Width = 90, Height = 40 },
            },
            Edges = new()
            {
                new() { From = "front-desk", To = "owner", Kind = FactoryMapEdgeKind.Escalate, Label = "escalates",
                    Points = new() { new[] { 145.0, 100 }, new[] { 180.0, 100 }, new[] { 220.0, 100 }, new[] { 245.0, 100 } },
                    Tip = new[] { 255.0, 100 } },
            },
        };
        var put = await h.Session.PutAsJsonAsync("gateway/factory/map", map);
        var putText = await put.Content.ReadAsStringAsync();
        Assert.True(put.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)put.StatusCode}: {putText}");

        var view = await GetAsync<FactoryMapViewDto>(h.Owner, $"gateway/factory-agents/factories/{factory}/map");
        Assert.Null(view.EmptyText);
        Assert.Equal("Test Factory", view.Title);
        Assert.Contains($"by session {h.SessionId}", view.SourceText);
        var desk = view.Nodes.Single(n => n.Id == "front-desk");
        Assert.Equal("IDLE", desk.StatusWord);
        Assert.Equal($"/factory-agents/{factory}/front-desk", desk.Href);
        Assert.Contains(desk.Spec, r => r.Label == "Inputs" && r.Text == "mail-threads");
        Assert.Equal("dashed", Assert.Single(view.Edges).Line);

        var factories = await GetAsync<FactoriesViewDto>(h.Owner, "gateway/factory-agents/factories");
        Assert.Equal($"/factory-agents/{factory}", factories.Factories.Single(c => c.Id == factory).MapHref);

        // A broken map is refused whole with the reason, and the last good one stays.
        map.Edges[0].To = "nobody";
        var bad = await h.Session.PutAsJsonAsync("gateway/factory/map", map);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("not on the map", await bad.Content.ReadAsStringAsync());
        Assert.Single((await GetAsync<FactoryMapViewDto>(h.Owner, $"gateway/factory-agents/factories/{factory}/map")).Edges);

        // The Map tab is the owner's page: a session key is refused it, and an unknown factory is a 404.
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Session.GetAsync($"gateway/factory-agents/factories/{factory}/map")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.GetAsync("gateway/factory-agents/factories/no-such-factory/map")).StatusCode);
        // Ids are exact everywhere: another spelling of this factory is not this factory, so the map is never found
        // without its card (review of #3390, finding 3).
        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.GetAsync($"gateway/factory-agents/factories/{factory.ToUpperInvariant()}/map")).StatusCode);
    }

    [Fact]
    public async Task Switch_on_a_session_key_is_refused_the_owners_pages()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);

        var resp = await h.Session.GetAsync("gateway/factory-agents/factories");

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Switch_off_the_switch_route_says_off_and_the_pages_are_404()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: false);

        Assert.False((await GetAsync<FactoryAgentsSwitchDto>(h.Owner, "gateway/factory-agents/switch")).Enabled);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.GetAsync("gateway/factory-agents/factories")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.GetAsync("gateway/factory-agents/activity")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.GetAsync("gateway/factory-agents/waiting")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await h.Owner.PutAsJsonAsync("gateway/factory/map", new { factory = "x" })).StatusCode);
    }
}
