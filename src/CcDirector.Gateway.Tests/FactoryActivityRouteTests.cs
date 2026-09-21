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
/// The factory activity routes on a REAL host (Website Business Factory, product track): the switch, the
/// session-key guard, the actor stamp and the 400 - the parts only a booted Gateway runs. The store's own
/// claims are proved without a server in <c>FactoryActivityRecordTests</c>.
///
/// Each test uses its own factory id, so it counts only its own rows whatever else the host's database holds.
/// </summary>
public sealed class FactoryActivityRouteTests
{
    private const string Token = "factory-activity-route-token";
    private const string DirectorId = "director-factory-activity-route";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class Host : IAsyncDisposable
    {
        public GatewayHost Gateway = null!;
        public HttpClient Session = null!;
        public Guid SessionId = Guid.NewGuid();
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-factory-activity-" + Guid.NewGuid().ToString("N"));

        public static async Task<Host> StartAsync(bool factoryAgentsEnabled)
        {
            var h = new Host();
            h.Gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: Token, authEnabled: true,
                instancesDirectory: h._dir,
                workListsPath: Path.Combine(h._dir, "worklists", "worklists.json"),
                factoryAgentsEnabled: factoryAgentsEnabled);
            await h.Gateway.StartAsync();

            // A session key registered exactly as a Director's Hello registers one.
            var key = GatewaySessionKey.Mint();
            Assert.True(h.Gateway.SessionKeys.Register(TenantId.Local, DirectorId, h.SessionId.ToString(),
                GatewaySessionKey.Hash(key), DateTime.UtcNow.AddHours(1)));
            h.Session = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{h.Gateway.Port}/") };
            h.Session.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return h;
        }

        public async ValueTask DisposeAsync()
        {
            Session.Dispose();
            await Gateway.StopAsync();
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }
    }

    private static object Row(string factory, string outcome = FactoryActivityOutcome.Done) => new
    {
        factory,
        factoryAgent = "front-desk",
        outcome,
        what = "Added an address to the remove-me list.",
        subject = "Pine Valley Plumbing",
    };

    private static async Task<FactoryActivityPage> ReadAsync(HttpClient client, string factory)
    {
        var resp = await client.GetAsync($"gateway/factory/activity?factory={factory}&limit=1000");
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.OK, $"expected 200, got {(int)resp.StatusCode}: {text}");
        return JsonSerializer.Deserialize<FactoryActivityPage>(text, Web)!;
    }

    [Fact]
    public async Task Switch_on_a_session_key_appends_a_row_stamped_with_its_session_and_reads_it_back()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N");

        var resp = await h.Session.PostAsJsonAsync("gateway/factory/activity", Row(factory));
        var text = await resp.Content.ReadAsStringAsync();
        Assert.True(resp.StatusCode == HttpStatusCode.Created, $"expected 201, got {(int)resp.StatusCode}: {text}");
        var written = JsonSerializer.Deserialize<FactoryActivityDto>(text, Web)!;
        Assert.Equal("session:" + h.SessionId, written.Actor);
        Assert.Equal(h.SessionId.ToString(), written.SessionId);

        var row = Assert.Single((await ReadAsync(h.Session, factory)).Rows);
        Assert.Equal(written.Id, row.Id);
        Assert.Equal("Added an address to the remove-me list.", row.What);
    }

    [Fact]
    public async Task Switch_on_a_refused_outcome_is_a_400_listing_the_allowed_words_and_writes_nothing()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: true);
        var factory = "f-" + Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.Created, (await h.Session.PostAsJsonAsync("gateway/factory/activity", Row(factory))).StatusCode);
        var before = (await ReadAsync(h.Session, factory)).Rows.Count;

        var resp = await h.Session.PostAsJsonAsync("gateway/factory/activity", Row(factory, outcome: "succeeded"));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        foreach (var word in FactoryActivityOutcome.All)
            Assert.Contains(word, text);
        Assert.Equal(before, (await ReadAsync(h.Session, factory)).Rows.Count);
    }

    [Fact]
    public async Task Switch_off_both_routes_are_404()
    {
        await using var h = await Host.StartAsync(factoryAgentsEnabled: false);
        Assert.False(h.Gateway.FactoryAgentsEnabled);

        var post = await h.Session.PostAsJsonAsync("gateway/factory/activity", Row("f-off"));
        var get = await h.Session.GetAsync("gateway/factory/activity");

        Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }
}
