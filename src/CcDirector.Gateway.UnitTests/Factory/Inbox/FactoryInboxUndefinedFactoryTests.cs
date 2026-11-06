using System.Net;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// AN UNDEFINED FACTORY, END TO END (Factory Inbox mission, phase 2; the Tech Lead's ruling of 5 November 2026: "every
/// factory with a Boss seat gets an inbox, defined or not"). Its sessions carry no seat, so the wake's own row is what
/// records which session it started as the Secretary and which as the Boss, and the inbox routes accept a session by that
/// record. Driven through the real routes on an in-memory test server and the real wake over one throwaway database: the
/// owner's message, the Secretary started and recorded, receive, the bundle, the Boss started and recorded, the decision.
/// No real clock, port, process or database server.
/// </summary>
public sealed class FactoryInboxUndefinedFactoryTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Account";
    private const string CallerHeader = "X-Test-Caller";
    private const string Phone = "phone";
    private const string Ledger = "ledger";
    private const string Machine = "LEDGER_PC";
    private const string Secretary = "a1111111-1111-4111-8111-111111111111";
    private const string Boss = "a2222222-2222-4222-8222-222222222222";
    private const string Worker = "a3333333-3333-4333-8333-333333333333";
    private const string Bystander = "a4444444-4444-4444-8444-444444444444";
    private static readonly TenantId A = new("acct-a");
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly Environment _env = new();
    private FactoryInboxWake _wake = null!;
    private WebApplication? _app;
    private HttpClient _client = null!;

    // Every session here is in the factory and carries no seat: the factory is undefined.
    private static readonly Dictionary<string, string?> Parents = new(StringComparer.Ordinal)
    {
        [Secretary] = null,
        [Boss] = null,
        [Worker] = Boss,
        [Bystander] = null,
    };

    private static readonly RegisteredFactoryDto Registered = new()
    {
        Factory = Ledger,
        Title = "Ledger",
        Folder = "D:/factories/ledger",
        BossSeat = "boss",
        Seats = { new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss", Computer = Machine, BriefFile = "agents/boss.yaml" } },
    };

    public async Task InitializeAsync()
    {
        var db = _h.Open();
        var store = new FactoryInboxStore(db, _ => DictationDictionary.Empty);
        var wakes = new FactoryInboxWakeStore(db);
        _env.Now = T0;
        _wake = new FactoryInboxWake(wakes, _env, CancellationToken.None);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            var caller = ctx.Request.Headers[CallerHeader].ToString();
            if (caller == Phone)
            {
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = Phone;
                ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity("owner-phone-1", A.Value, Phone, "active");
            }
            else if (Guid.TryParse(caller, out var session))
            {
                ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] = new SessionCredentialIdentity(session, A, "dir-a");
            }
            await next();
        });
        FactoryInboxEndpoints.Map(app, new FactoryInboxEndpoints.Parts(
            store,
            ctx => ctx.Request.Headers.ContainsKey(TenantHeader) ? A : null,
            _ => [Registered],
            (_, sid) => Parents.ContainsKey(sid) ? SessionFactoryLookup.In(Ledger) : SessionFactoryLookup.NotKnown,
            (_, sid) => Parents.TryGetValue(sid, out var p) ? new FactoryInboxRecordedParent(p) : null,
            (_, _) => false,
            () => _env.Now,
            wakes,
            // The routes tell the wake; this test then looks itself, at the moment it chooses, as the scheduled look would.
            (_, _, _) => { },
            (_, _) => { }));
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string method, string path, string caller, object? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add(TenantHeader, A.Value);
        request.Headers.Add(CallerHeader, caller);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task AnUndefinedFactory_MessageToDecision_ThroughTheWakesRecordedSecretaryAndBoss()
    {
        // The owner leaves a message.
        var (sent, message) = await Call("POST", $"/gateway/factory/{Ledger}/inbox", Phone, new { text = "Reconcile October." });
        Assert.Equal(HttpStatusCode.Created, sent);
        var id = message.GetProperty("message").GetProperty("id").GetString()!;

        // Before the wake has started anyone, a session in the factory is not its Secretary.
        var (early, earlyBody) = await Call("GET", $"/sessions/{Secretary}/factory-inbox", Secretary);
        Assert.Equal(HttpStatusCode.Forbidden, early);
        Assert.Equal("not_the_secretary", earlyBody.GetProperty("code").GetString());

        // The window closes: ONE Secretary starts, in the factory's folder, and the wake records it.
        _env.Now = T0.AddSeconds(60);
        _env.NextSession.Enqueue(Secretary);
        Assert.Equal(FactoryInboxWakeAct.Started, (await _wake.LookAsync(A, Ledger, CancellationToken.None)).Secretary);
        var secretaryStart = Assert.Single(_env.Starts);
        Assert.Equal(Machine, secretaryStart.Machine);
        Assert.Equal("D:/factories/ledger", secretaryStart.Request.RepoPath);
        Assert.Null(secretaryStart.Request.FactorySeat);

        // The recorded Secretary files it; a bystander in the same factory still may not.
        Assert.Equal(HttpStatusCode.Forbidden, (await Call("GET", $"/sessions/{Bystander}/factory-inbox", Bystander)).Status);
        var (listed, list) = await Call("GET", $"/sessions/{Secretary}/factory-inbox", Secretary);
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.Equal(1, list.GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await Call("POST", $"/sessions/{Secretary}/factory-inbox/receive", Secretary, new { })).Status);
        _env.Now = T0.AddSeconds(90);
        Assert.Equal(HttpStatusCode.Created, (await Call("POST", $"/sessions/{Secretary}/factory-inbox/bundle", Secretary, new { })).Status);

        // The bundle: no Boss alive, so ONE starts in the folder, and the wake records it.
        _env.NextSession.Enqueue(Boss);
        Assert.Equal(FactoryInboxWakeAct.Started, (await _wake.LookAsync(A, Ledger, CancellationToken.None)).Boss);
        Assert.Equal(2, _env.Starts.Count);
        var bossStart = _env.Starts[1];
        Assert.Contains("Read your brief first: D:/factories/ledger/agents/boss.yaml", bossStart.Request.PrePrompt);
        Assert.Contains(FactorySeatOpening.BossNeverDoesTheWork, bossStart.Request.PrePrompt);

        // The recorded Boss reads the bundle and delegates to a session it started; the Secretary is not the Boss.
        Assert.Equal(HttpStatusCode.Forbidden, (await Call("GET", $"/sessions/{Secretary}/factory-inbox/bundle", Secretary)).Status);
        var (read, bundles) = await Call("GET", $"/sessions/{Boss}/factory-inbox/bundle", Boss);
        Assert.Equal(HttpStatusCode.OK, read);
        Assert.Equal(1, bundles.GetProperty("count").GetInt32());
        var (decided, decision) = await Call("POST", $"/sessions/{Boss}/factory-inbox/{id}/decide", Boss, new { kind = "delegated", session = Worker });
        Assert.Equal(HttpStatusCode.OK, decided);
        Assert.Equal("Delegated", decision.GetProperty("message").GetProperty("stateLabel").GetString());

        // The thread shows the whole trail, and nothing more is started.
        var (_, thread) = await Call("GET", $"/gateway/factory/{Ledger}/inbox", Phone);
        var steps = thread.GetProperty("messages")[0].GetProperty("trail").EnumerateArray().Select(s => s.GetProperty("step").GetString()).ToList();
        Assert.Equal(new[] { "sent", "received", "with-boss", "decided" }, steps);
        await _wake.LookAsync(A, Ledger, CancellationToken.None);
        Assert.Equal(2, _env.Starts.Count);
    }

    /// <summary>The Gateway around the wake: one computer with one Director, no definition, nothing on the roster.</summary>
    private sealed class Environment : IFactoryInboxWakeEnvironment
    {
        public DateTime Now;
        public readonly Queue<string> NextSession = new();
        public readonly List<(string Machine, NewSessionRequest Request)> Starts = new();

        public RegisteredFactoryDto? Factory(TenantId tenant, string factory) => factory == Ledger ? Registered : null;
        public PublishedFactoryDefinition? Published(TenantId tenant, string factory) => null;
        public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant) => [];
        public (string DirectorId, SessionDto Session)? LastKnown(TenantId tenant, string sessionId) => null;
        public bool HasEnded(TenantId tenant, string sessionId) => false;
        public bool StartedAsTheBoss(TenantId tenant, string sessionId, string factory, string seat) => false;
        public FactoryGivenBrief? PublishedBrief(TenantId tenant, string factory, string seat) => null;
        public string? DirectorOn(TenantId tenant, string machine, bool buildsKit) => machine == Machine ? "d-ledger" : null;

        public Task<TriggerStartAttempt> StartAsync(TenantId tenant, string machine, NewSessionRequest request, CancellationToken ct)
        {
            Starts.Add((machine, request));
            return Task.FromResult(TriggerStartAttempt.Started(NextSession.Dequeue()));
        }

        public Task<FleetManagerPromptSend> RingAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
            => Task.FromResult(FleetManagerPromptSend.Accepted);

        public bool FactoryAgentsOn(TenantId tenant) => true;
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
        public DateTime NowUtc() => Now;
    }
}
