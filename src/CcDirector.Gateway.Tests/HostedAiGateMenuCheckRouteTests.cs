using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.AgentBrain;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Streaming;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE MENU CHECK'S MODEL CALL IS ON THE PLAN (Pricing Change mission, B1, review finding 1). Two request routes ask a
/// model whether a menu-shaped screen really is a menu before they type into a session: the prompt front door's menu
/// guard (<c>POST /sessions/{sid}/prompt</c> with <c>menuGuard</c>) and the verdict answer route
/// (<c>POST /sessions/{sid}/turn-verdict/answer</c>). Before the fix neither asked the plan first, and the check's
/// catch-all turned a plan refusal - and an unreadable plan - into the false claim "a menu owns the screen".
///
/// The routes are mapped on a HOSTED boundary, the caller is an authenticated device key bound to a real tenant, the
/// gate reads that tenant's plan from a real database through the real entitlement registry, and the Director is a
/// command sender that answers the screen read with a real menu-shaped grid and records every command it is sent.
///
/// HOW THESE FAIL ON PURPOSE: let either route call the menu check without asking the plan and its Free/Remote test
/// goes red twice over - the model is reached, and the answer is the menu claim instead of 402. Refuse before the
/// free classifier has answered and the composer test goes red. Make the gate refuse everyone and the Pro tests go red.
/// </summary>
public sealed class HostedAiGateMenuCheckRouteTests : IAsyncLifetime
{
    private const string TenantHeader = "X-Test-Tenant";
    private const string DirectorId = "dir-menu-check";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // A real permission menu as the picker draws it: the selection marker (U+276F) on the chosen row, the hardware
    // cursor hidden. The pure classifier reads this as a confident menu, which is what makes the routes ask the model.
    private static readonly string[] MenuRows = { "Do you want to proceed?", "\u276F 1. Yes", "  2. No" };

    private readonly GatewayDbTestHarness _harness = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hosted-ai-gate-menu-" + Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<DirectorCommand> _commands = new();
    private readonly KeepingRecords _records = new();
    private readonly PushedSessionStore _sessions = new(() => DateTime.UtcNow);
    private GatewayDatabase _db = null!;
    private DirectorRegistry _registry = null!;
    private Func<string, ScreenGridResponse> _screen = MenuGrid;
    private int _brainCalls;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private TenantId _free;
    private TenantId _remote;
    private TenantId _pro;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _db = _harness.Open();
        using (var ctx = _db.CreateUnscopedContext())
        {
            ctx.Database.ExecuteSqlRaw(
                "CREATE TABLE IF NOT EXISTS entitlements (" +
                "subject TEXT NOT NULL PRIMARY KEY, status TEXT NOT NULL, " +
                "current_period_end TEXT NULL, stripe_subscription_id TEXT NULL, updated_at TEXT NULL, " +
                "livemode INTEGER NULL, tier TEXT NULL)");
            foreach (var (subject, tier) in new[] { ("sub-remote", EntitlementRegistry.TierRemote), ("sub-pro", EntitlementRegistry.TierPro) })
                ctx.Database.ExecuteSqlRaw(
                    "INSERT INTO entitlements (subject, status, current_period_end, livemode, tier) VALUES ({0}, {1}, {2}, {3}, {4})",
                    subject, EntitlementRegistry.StatusActive, new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), true, tier);
        }
        var tenants = new TenantRegistry(_db);
        _free = tenants.MintOrLookupBySubject("sub-free", "free@example.com");
        _remote = tenants.MintOrLookupBySubject("sub-remote", "remote@example.com");
        _pro = tenants.MintOrLookupBySubject("sub-pro", "pro@example.com");
        var gate = new TenantAiGate(new EntitlementRegistry(_db, requireLivemode: true, trials: new TrialRegistry(_db)), tenants);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        // The caller is an authenticated device key bound to the tenant the test names - what the auth middleware
        // stamps for a real request.
        _app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthMiddleware.AuthenticatedDeviceItemKey] = new DeviceCredentialIdentity(
                "dev-" + ctx.Request.Headers[TenantHeader], ctx.Request.Headers[TenantHeader].ToString(),
                DeviceRegistry.DefaultDeviceType, DeviceRegistry.StatusActive);
            await next();
        });

        // The model behind the menu check. It answers nothing: reaching it is what is asserted, and a model that
        // cannot be reached while a menu-shaped screen is up is the check's fail-closed "a menu owns the screen".
        var translator = new WingmanTranslator((_, _, _, _) =>
        {
            Interlocked.Increment(ref _brainCalls);
            throw new InvalidOperationException("the test model answers nothing; reaching it is what is asserted");
        }, _ => SpokenLanguages.English, log: _ => { });

        _registry = new DirectorRegistry(Path.Combine(_root, "directors"));
        GatewayEndpoints.Map(
            _app, true, _registry, version: "test", token: "test-token",
            tenantBoundary: new HostedTenantBoundary(new AsyncLocalTenantContext(), new DeviceRegistry(), hosted: true),
            sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            aiGate: gate,
            pushedSessions: _sessions,
            streamStaleAfter: TimeSpan.FromSeconds(30),
            sendCommand: (_, command, _) =>
            {
                _commands.Enqueue(command);
                return Task.FromResult<DirectorCommandResult?>(command.Verb switch
                {
                    "screen-grid" => DirectorCommandResult.Success(JsonSerializer.Serialize(_screen(command.SessionId ?? ""), Web)),
                    "prompt" => DirectorCommandResult.Success(JsonSerializer.Serialize(
                        new PromptResponse { Accepted = true, SentAt = DateTime.UtcNow, ActivityState = "Working" }, Web)),
                    _ => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {command.Verb}"),
                });
            },
            tenantSettings: new TenantSettingsResolver(new TenantSettingsStore(_harness.Open())),
            wingmanTranslator: translator,
            turnVerdictAnswers: new TurnVerdictAnswerService(_records));

        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()), Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _registry.Dispose();
        _harness.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ================================================================= the harness

    private static ScreenGridResponse MenuGrid(string sid) => new()
    {
        SessionId = sid,
        Rows = MenuRows.ToList(),
        CursorRow = 0,
        CursorCol = -1,
        CursorVisible = false,
        IsAlternateScreen = true,
        HasGrid = true,
    };

    // A composer: a visible cursor in the empty input box, framed by box borders and a footer. Plainly not a menu.
    private static ScreenGridResponse ComposerGrid(string sid) => new()
    {
        SessionId = sid,
        Rows = new List<string>
        {
            "I finished the change. What next?",
            "\u256D" + new string('\u2500', 38) + "\u256E",
            "\u2502 >                                     \u2502",
            "\u2570" + new string('\u2500', 38) + "\u256F",
            "  ? for shortcuts",
        },
        CursorRow = 2,
        CursorCol = 4,
        CursorVisible = true,
        IsAlternateScreen = false,
        HasGrid = true,
    };

    /// <summary>A waiting session of this tenant, held by a connected Director of this tenant.</summary>
    private string SessionOf(TenantId tenant)
    {
        var sid = Guid.NewGuid().ToString();
        var director = DirectorId + "-" + tenant.Value;
        _registry.RegisterFromStream(director, "machine", "member", "1.0", 4242, DateTime.UtcNow, tenant);
        _sessions.RegisterConnection(tenant, director, "conn-1");
        Assert.True(_sessions.ApplySnapshot(tenant, director, "conn-1", 1, new[]
        {
            new SessionDto { SessionId = sid, Name = "a voice session", Status = "WaitingForInput", ActivityState = "WaitingForInput", RepoPath = @"D:\repo" },
        }));
        return sid;
    }

    /// <summary>A reply verdict formed on the menu-shaped screen, stored as the session's latest.</summary>
    private string ReplyVerdictFor(string sid)
    {
        var verdict = new TurnVerdictDto
        {
            VerdictId = "verdict-" + Guid.NewGuid().ToString("N"),
            TurnEndObservedAtUtc = DateTime.UtcNow.AddSeconds(-12),
            JudgedAtUtc = DateTime.UtcNow,
            ScreenHash = WingmanScreenVerdictCache.HashRows(MenuRows),
            AnswerVia = "reply",
            Options = new List<TurnVerdictOptionDto> { new() { Key = "Go ahead", Send = "yes go ahead" } },
        };
        _records.Verdicts[verdict.VerdictId] = (sid, verdict);
        return verdict.VerdictId;
    }

    private Task<HttpResponseMessage> PostAsync(TenantId tenant, string path, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, Web), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(TenantHeader, tenant.Value);
        return _http.SendAsync(req);
    }

    private Task<HttpResponseMessage> GuardedPromptAsync(TenantId tenant, string sid)
        => PostAsync(tenant, $"/sessions/{sid}/prompt", new { text = "yes go ahead", appendEnter = true, menuGuard = true });

    private Task<HttpResponseMessage> AnswerAsync(TenantId tenant, string sid, string verdictId)
        => PostAsync(tenant, $"/sessions/{sid}/turn-verdict/answer", new { verdictId, optionIndexes = new[] { 0 } });

    private int PromptsSent => _commands.Count(c => c.Verb == "prompt");

    private void MakeThePlanUnreadable()
    {
        using var ctx = _db.CreateUnscopedContext();
        ctx.Database.ExecuteSqlRaw("DROP TABLE entitlements");
    }

    private static async Task AssertSubscriptionRequiredAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.PaymentRequired, $"expected 402, got {(int)res.StatusCode}: {body}");
        Assert.Equal(HostedAiState.SubscriptionRequired, HostedAiErrorMapper.Map402(body));
        Assert.Contains("\"state\":\"SubscriptionRequired\"", body);
        Assert.Contains("\"code\":\"subscription_required\"", body);
        Assert.DoesNotContain("menu", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertPlanUnknownAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.ServiceUnavailable, $"expected 503, got {(int)res.StatusCode}: {body}");
        Assert.Equal("10", res.Headers.RetryAfter?.ToString());
        Assert.Contains("\"code\":\"entitlement_unknown\"", body);
        Assert.DoesNotContain("menu", body, StringComparison.OrdinalIgnoreCase);
    }

    // ================================================================= the prompt front door's menu guard

    [Fact]
    public async Task FreeAndRemote_PromptMenuGuard_MenuShapedScreen_Is402_AndTheModelIsNeverAsked()
    {
        foreach (var tenant in new[] { _free, _remote })
            await AssertSubscriptionRequiredAsync(await GuardedPromptAsync(tenant, SessionOf(tenant)));

        Assert.Equal(0, _brainCalls);
        Assert.Equal(0, PromptsSent);
    }

    [Fact]
    public async Task UnreadablePlan_PromptMenuGuard_MenuShapedScreen_Is503_AndNeverTheMenuClaim()
    {
        var sid = SessionOf(_pro);
        MakeThePlanUnreadable();

        await AssertPlanUnknownAsync(await GuardedPromptAsync(_pro, sid));

        Assert.Equal(0, _brainCalls);
        Assert.Equal(0, PromptsSent);
    }

    [Fact]
    public async Task Pro_PromptMenuGuard_MenuShapedScreen_ReachesTheModel()
    {
        var res = await GuardedPromptAsync(_pro, SessionOf(_pro));

        // The test model cannot be reached, so the guard fails closed on the menu - its own answer, not the plan's.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var node = await res.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["blockedByMenu"]?.GetValue<bool>());
        Assert.Equal(1, _brainCalls);
        Assert.Equal(0, PromptsSent);
    }

    [Fact]
    public async Task Free_PromptMenuGuard_ScreenThatIsPlainlyNotAMenu_SendsThePrompt_WithNoModelAndNoRefusal()
    {
        // The pure classifier is free and answers for every plan: a Free member may send a prompt, so a screen that
        // is plainly not a menu must not turn the guard into a refusal.
        _screen = ComposerGrid;

        var res = await GuardedPromptAsync(_free, SessionOf(_free));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var node = await res.Content.ReadFromJsonAsync<JsonNode>();
        Assert.True(node?["accepted"]?.GetValue<bool>());
        Assert.Equal(0, _brainCalls);
        Assert.Equal(1, PromptsSent);
    }

    // ================================================================= the verdict answer route's menu check

    [Fact]
    public async Task FreeAndRemote_VerdictAnswer_MenuShapedScreen_Is402_AndTheModelIsNeverAsked()
    {
        foreach (var tenant in new[] { _free, _remote })
        {
            var sid = SessionOf(tenant);
            await AssertSubscriptionRequiredAsync(await AnswerAsync(tenant, sid, ReplyVerdictFor(sid)));
        }

        Assert.Equal(0, _brainCalls);
        Assert.Equal(0, PromptsSent);
        // Every activation leaves exactly one ledger line, and a plan refusal is recorded as one, never as a menu.
        Assert.Equal(2, _records.Lines.Count);
        Assert.All(_records.Lines, line =>
        {
            Assert.Equal(ActivityEventTypes.TurnVerdictAnswerRefused, line.EventType);
            Assert.Equal(ActivityCauses.NotOnPlan, line.Cause);
        });
    }

    [Fact]
    public async Task UnreadablePlan_VerdictAnswer_MenuShapedScreen_Is503_AndNeverTheMenuClaim()
    {
        var sid = SessionOf(_pro);
        var verdictId = ReplyVerdictFor(sid);
        MakeThePlanUnreadable();

        await AssertPlanUnknownAsync(await AnswerAsync(_pro, sid, verdictId));

        Assert.Equal(0, _brainCalls);
        Assert.Equal(0, PromptsSent);
        var line = Assert.Single(_records.Lines);
        Assert.Equal(ActivityEventTypes.TurnVerdictAnswerRefused, line.EventType);
        Assert.Equal(ActivityCauses.PlanUnknown, line.Cause);
    }

    [Fact]
    public async Task Pro_VerdictAnswer_MenuShapedScreen_ReachesTheModel()
    {
        var sid = SessionOf(_pro);

        var res = await AnswerAsync(_pro, sid, ReplyVerdictFor(sid));

        // The test model cannot be reached, so the check fails closed on the menu - its own 409, not the plan's 402.
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var node = await res.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(ActivityCauses.MenuOwnsScreen, node?["code"]?.GetValue<string>());
        Assert.Equal(1, _brainCalls);
        Assert.Equal(0, PromptsSent);
    }

    /// <summary>The verdicts the answer route finds, and every ledger line it writes.</summary>
    private sealed class KeepingRecords : ITurnVerdictAnswerRecords
    {
        public readonly ConcurrentDictionary<string, (string SessionId, TurnVerdictDto Verdict)> Verdicts = new();
        public readonly ConcurrentQueue<TurnVerdictRecord> Lines = new();

        public TurnVerdictLocated? FindVerdict(TenantId tenant, string verdictId)
            => Verdicts.TryGetValue(verdictId, out var found) ? new TurnVerdictLocated(found.SessionId, found.Verdict) : null;

        public TurnVerdictDto? Latest(TenantId tenant, string sessionId)
            => Verdicts.Values.Where(v => v.SessionId == sessionId).Select(v => v.Verdict).LastOrDefault();

        public bool MarkAnswered(TenantId tenant, TurnVerdictStoredAnswer answer) => true;

        public void Record(TurnVerdictRecord record) => Lines.Enqueue(record);
    }
}
