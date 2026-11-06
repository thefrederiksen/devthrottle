using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Security;
using CcDirector.Core.Tenancy;
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
/// AN UNDEFINED FACTORY'S SECRETARY STARTS NOTHING EITHER (Factory Inbox mission, phase 2; the Tech Lead's ruling of 5
/// November 2026). It carries no seat, so the start-door refusal knows it only by the wake's own record. Proved end to end:
/// the REAL wake starts the Secretary and then the Boss of an undefined factory over one throwaway database, each is given a
/// session key in the REAL session key registry, and each key is sent through the REAL authentication middleware - wired
/// to the same wake store the wake wrote, as the Gateway host wires it - at the generic spawn and the factory seat start.
/// The Secretary is refused at both; the Boss, and a session in the same factory the wake never recorded, reach both.
/// No real clock, port, process or database server.
/// </summary>
public sealed class FactoryInboxUndefinedSecretaryStartsNothingTests : IAsyncLifetime
{
    private const string Ledger = "ledger";
    private const string Machine = "LEDGER_PC";
    private const string GenericSpawn = "/machines/LEDGER_PC/sessions";
    private const string SeatStart = "/gateway/factory/seats/writer/start";
    private static readonly TenantId A = new("acct-a");
    private static readonly DateTime T0 = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Secretary = Guid.Parse("b1111111-1111-4111-8111-111111111111");
    private static readonly Guid Boss = Guid.Parse("b2222222-2222-4222-8222-222222222222");
    private static readonly Guid Bystander = Guid.Parse("b3333333-3333-4333-8333-333333333333");

    private static readonly RegisteredFactoryDto Registered = new()
    {
        Factory = Ledger,
        Title = "Ledger",
        Folder = "D:/factories/ledger",
        BossSeat = "boss",
        Seats = { new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Role = "Boss", Computer = Machine, BriefFile = "agents/boss.yaml" } },
    };

    private readonly GatewayDbTestHarness _h = new();
    private readonly Environment _env = new();
    private readonly Dictionary<string, string> _keys = new(StringComparer.Ordinal);
    private FactoryInboxStore _store = null!;
    private FactoryInboxWakeStore _wakes = null!;
    private SessionKeyRegistry _sessions = null!;
    private WebApplication? _app;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var db = _h.Open();
        _store = new FactoryInboxStore(db, _ => DictationDictionary.Empty);
        _wakes = new FactoryInboxWakeStore(db);
        _sessions = new SessionKeyRegistry(db, isHosted: false, clock: () => T0);

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        var config = new AuthMiddleware.RequireToken
        {
            Hosted = false, Token = "shared-machine-token", Devices = null, Sessions = _sessions,
            // Every session here is in the undefined factory and carries no seat: its history row says only that.
            SessionSeatOf = identity =>
            {
                Assert.Equal(A, identity.Tenant);
                return SessionFactoryLookup.In(Ledger);
            },
            // As the Gateway host wires it: the wake's own record, in the key's account.
            IsTheWakesSecretary = (identity, factory) => _wakes.RecordsAsSecretary(identity.Tenant, factory, identity.SessionId.ToString("D")),
        };
        app.Use(async (ctx, next) => await AuthMiddleware.Run(ctx, config, next));
        app.MapPost(GenericSpawn, () => Results.Ok(new { reached = true }));
        app.MapPost(SeatStart, () => Results.Ok(new { reached = true }));
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        _h.Dispose();
    }

    private void GiveAKey(string who, Guid session)
    {
        var key = GatewaySessionKey.Mint();
        Assert.True(_sessions.Register(A, "d-ledger", session.ToString("D"), GatewaySessionKey.Hash(key), T0.AddHours(1)));
        _keys[who] = key;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Call(string path, string who)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _keys[who]);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task TheWakesSecretaryOfAnUndefinedFactory_IsRefusedAtTheSpawnAndTheSeatStart_ItsBossIsNot()
    {
        var wake = new FactoryInboxWake(_wakes, _env, CancellationToken.None);

        // The owner leaves a message; the window closes and the wake starts ONE Secretary, with no seat.
        await _store.SendAsync(A, new FactoryInboxSendInput(Ledger, "Reconcile October.", FactoryInboxWords.Notes.None,
            FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Phone, "owner-phone", T0));
        _env.Now = T0.AddSeconds(60);
        _env.NextSession.Enqueue(Secretary.ToString("D"));
        Assert.Equal(FactoryInboxWakeAct.Started, (await wake.LookAsync(A, Ledger, CancellationToken.None)).Secretary);
        Assert.Null(Assert.Single(_env.Starts).FactorySeat);
        GiveAKey("secretary", Secretary);
        GiveAKey("bystander", Bystander);

        // The Secretary hands its bundle; no Boss is alive, so the wake starts ONE, also with no seat.
        _store.Receive(A, Ledger, Secretary.ToString("D"), null, [], [], [], T0.AddSeconds(70));
        _store.HandBundle(A, Ledger, Secretary.ToString("D"), null, T0.AddSeconds(71));
        _env.Now = T0.AddSeconds(72);
        _env.NextSession.Enqueue(Boss.ToString("D"));
        Assert.Equal(FactoryInboxWakeAct.Started, (await wake.LookAsync(A, Ledger, CancellationToken.None)).Boss);
        Assert.Null(_env.Starts[1].FactorySeat);
        GiveAKey("boss", Boss);

        foreach (var door in new[] { GenericSpawn, SeatStart })
        {
            // The Secretary the wake recorded starts nothing, and is told why in a sentence.
            var (refused, body) = await Call(door, "secretary");
            Assert.Equal(HttpStatusCode.Forbidden, refused);
            Assert.Equal("secretary_starts_nothing", body.GetProperty("code").GetString());
            Assert.Equal(SecretaryStartLimit.Refusal, body.GetProperty("error").GetString());

            // Its Boss decides who does the work, and starts it; so may a session the wake never recorded.
            foreach (var who in new[] { "boss", "bystander" })
            {
                var (status, reached) = await Call(door, who);
                Assert.True(status == HttpStatusCode.OK, $"{who} at {door} answered {(int)status}: {reached}");
                Assert.True(reached.GetProperty("reached").GetBoolean());
            }
        }
    }

    /// <summary>The Gateway around the wake: one computer with one Director, no definition, nothing on the roster.</summary>
    private sealed class Environment : IFactoryInboxWakeEnvironment
    {
        public DateTime Now;
        public readonly Queue<string> NextSession = new();
        public readonly List<NewSessionRequest> Starts = new();

        public RegisteredFactoryDto? Factory(TenantId tenant, string factory) => factory == Ledger ? Registered : null;
        public PublishedFactoryDefinition? Published(TenantId tenant, string factory) => null;
        public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant) => [];
        public (string DirectorId, SessionDto Session)? LastKnown(TenantId tenant, string sessionId) => null;
        public bool HasEnded(TenantId tenant, string sessionId) => false;
        public FactoryGivenBrief? PublishedBrief(TenantId tenant, string factory, string seat) => null;
        public string? DirectorOn(TenantId tenant, string machine, bool buildsKit) => machine == Machine ? "d-ledger" : null;

        public Task<TriggerStartAttempt> StartAsync(TenantId tenant, string machine, NewSessionRequest request, CancellationToken ct)
        {
            Starts.Add(request);
            return Task.FromResult(TriggerStartAttempt.Started(NextSession.Dequeue()));
        }

        public Task<FleetManagerPromptSend> RingAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
            => Task.FromResult(FleetManagerPromptSend.Accepted);

        public bool FactoryAgentsOn(TenantId tenant) => true;
        public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
        public DateTime NowUtc() => Now;
    }
}
