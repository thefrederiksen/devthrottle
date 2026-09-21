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
    }
}
