using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The morning-report endpoint's OWN gate (issue #2119). This route is exempt from the host-wide token
/// middleware because its caller is a server with no device key - so everything that keeps one account's
/// report out of another's inbox lives here, and every one of those rules is asserted below:
///
///   no token / wrong token / wrong scheme -> 401
///   the service token not configured      -> 503 (refuse to serve, never serve unguarded)
///   a valid token, an unknown account     -> 404
///   a valid token, an AMBIGUOUS account   -> 409 (never pick one)
///   a valid token, account A              -> A's rows only, proven against a second seeded tenant
///
/// The exemption itself is asserted too: if <c>AuthMiddleware.PublicPaths</c> stopped containing this route
/// the cron would receive a 401 from the host gate at 07:00 and no email would go out - a failure that would
/// otherwise only be discovered in production, once, silently.
/// </summary>
public sealed class MorningReportEndpointTests : IDisposable
{
    private const string Token = "test-service-token-4f2a";
    private const string OtherToken = "test-service-token-4f2b";

    private readonly GatewayDbTestHarness _h = new();
    private readonly string _devPath = Path.Combine(Path.GetTempPath(), $"mr-dev-{Guid.NewGuid():N}.json");
    private readonly string? _savedToken = Environment.GetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar);

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly TenantId Bob = new("tenant-bob");
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, _savedToken);
        _h.Dispose();
        if (File.Exists(_devPath)) File.Delete(_devPath);
    }

    // ---- harness ---------------------------------------------------------------------------------------

    private GatewayDatabase Db => _h.Open(new AsyncLocalTenantContext());

    private HostedTenantBoundary HostedBoundary() =>
        new(new AsyncLocalTenantContext(), new DeviceRegistry(_devPath));

    private HostedTenantBoundary SelfHostBoundary() =>
        new(new SingleTenantContext(), new DeviceRegistry(_devPath));

    /// <summary>The services a minimal-API <c>IResult</c> needs to write itself out (JSON options +
    /// logging). Built once: these tests execute the real result objects rather than inspecting them, so
    /// the JSON asserted below is byte-for-byte what the website cron receives.</summary>
    private static readonly IServiceProvider Services =
        new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();

    private static HttpContext Request(string? bearer)
    {
        var ctx = new DefaultHttpContext { RequestServices = Services };
        if (bearer is not null)
            ctx.Request.Headers.Authorization = bearer;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private IResult Call(HttpContext ctx, string? account, GatewayDatabase db, HostedTenantBoundary boundary,
        string date = "2026-07-23", string tz = "America/Toronto", Streaming.PushedSessionStore? live = null) =>
        MorningReportEndpoint.Handle(
            ctx, account, date, tz,
            new MorningReportBuilder(db, pushedSessions: live, streamStale: TimeSpan.FromMinutes(5), utcNow: () => Now),
            new TenantRegistry(db),
            boundary);

    /// <summary>A Director connected for this account, pushing exactly these sessions.
    ///
    /// Needed by any test that expects a waiting row on the wire. Since #3124 the report will not claim a
    /// session is waiting on somebody unless it can currently SEE that session - so with no roster there
    /// is nothing to assert a shape against, and a test that seeds none is asserting silence.</summary>
    private static Streaming.PushedSessionStore Live(TenantId tenant, params SessionDto[] sessions)
    {
        var store = new Streaming.PushedSessionStore(() => Now);
        store.RegisterConnection(tenant, "dir-1", "conn-1");
        Assert.True(store.ApplySnapshot(tenant, "dir-1", "conn-1", 0, new List<SessionDto>(sessions)));
        return store;
    }

    /// <summary>Execute the result and read back the status and the JSON body the cron would actually receive.</summary>
    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        var text = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(text);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }

    /// <summary>Register a tenant in the account census, exactly as hosted enrollment would.</summary>
    private static void SeedTenant(GatewayDatabase db, TenantId tenant, string subject, string? email)
    {
        using var ctx = db.CreateUnscopedContext();
        ctx.Tenants.Add(new TenantEntity
        {
            Id = tenant.Value,
            AccountSubject = subject,
            Email = email,
            CreatedAtUtc = Now,
        });
        ctx.SaveChanges();
    }

    private static void SeedSessionEvent(GatewayDatabase db, TenantId tenant, string sessionId, string state, DateTime occurredUtc)
    {
        using var ctx = db.CreateContext(tenant);
        ctx.GovernanceEvents.Add(new GovernanceEventEntity
        {
            TenantId = tenant.Value,
            SubjectKind = GovernanceEventSubject.Session,
            SessionId = sessionId,
            State = state,
            OccurredUtc = occurredUtc,
            RecordedUtc = occurredUtc,
        });
        ctx.SaveChanges();
    }

    // ---- the exemption is deliberate, and it must stay -------------------------------------------------

    [Fact]
    public async Task The_route_is_exempt_from_the_host_wide_token_gate()
    {
        // Not decoration: the website cron holds no device key and no shared machine token, so without this
        // exemption every 07:00 call is a 401 and the email silently stops. Asserted through the middleware
        // itself, not by peeking at a set - the claim is "a credential-less request reaches the endpoint".
        var devices = new DeviceRegistry(_devPath);
        var cfg = new AuthMiddleware.RequireToken { Token = "shared-machine-token", Devices = devices };

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = MorningReportEndpoint.Path;
        ctx.Response.Body = new MemoryStream();

        var reached = false;
        await AuthMiddleware.Run(ctx, cfg, () => { reached = true; return Task.CompletedTask; });

        Assert.True(reached);
        Assert.NotEqual(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task A_NEIGHBOURING_report_route_is_still_gated()
    {
        // The exemption is EXACT-MATCH, not a prefix. If it were a prefix, adding any future
        // /gateway/reports/* route would silently publish it - so prove a sibling path still 401s.
        var devices = new DeviceRegistry(_devPath);
        var cfg = new AuthMiddleware.RequireToken { Token = "shared-machine-token", Devices = devices };

        var ctx = new DefaultHttpContext();
        ctx.Request.Path = MorningReportEndpoint.Path + "/everything";
        ctx.Response.Body = new MemoryStream();

        var reached = false;
        await AuthMiddleware.Run(ctx, cfg, () => { reached = true; return Task.CompletedTask; });

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    // ---- authorization negatives -----------------------------------------------------------------------

    [Fact]
    public async Task No_token_is_401()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = Db;
        SeedTenant(db, Alice, "sub-alice", "alice@example.com");

        var ctx = Request(bearer: null);
        var (status, body) = await ExecuteAsync(Call(ctx, "alice@example.com", db, HostedBoundary()), ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.True(body.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task A_wrong_token_is_401()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = Db;
        SeedTenant(db, Alice, "sub-alice", "alice@example.com");

        var ctx = Request($"Bearer {OtherToken}");
        var (status, _) = await ExecuteAsync(Call(ctx, "alice@example.com", db, HostedBoundary()), ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("test-service-token-4f2a")]              // the right token, no scheme
    [InlineData("Basic test-service-token-4f2a")]        // the right token, wrong scheme
    [InlineData("Bearer test-service-token-4f2")]        // a PREFIX of the right token
    [InlineData("Bearer test-service-token-4f2aa")]      // the right token plus a byte
    public async Task A_malformed_or_near_miss_credential_is_401(string header)
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = Db;
        SeedTenant(db, Alice, "sub-alice", "alice@example.com");

        var ctx = Request(header);
        var (status, _) = await ExecuteAsync(Call(ctx, "alice@example.com", db, HostedBoundary()), ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_unconfigured_service_token_refuses_to_serve_it_does_not_open_the_door(string? configured)
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, configured);
        var db = Db;
        SeedTenant(db, Alice, "sub-alice", "alice@example.com");

        // Even a caller presenting SOMETHING gets nothing: an unset token is a deployment fault, and the
        // tempting failure mode - "no token configured, so let everyone in" - would publish every account's
        // report to the internet.
        var ctx = Request("Bearer anything-at-all");
        var (status, _) = await ExecuteAsync(Call(ctx, "alice@example.com", db, HostedBoundary()), ctx);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
    }

    // ---- account resolution ----------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_token_naming_an_unknown_account_is_404()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = Db;
        SeedTenant(db, Alice, "sub-alice", "alice@example.com");

        var ctx = Request($"Bearer {Token}");
        var (status, _) = await ExecuteAsync(Call(ctx, "nobody@example.com", db, HostedBoundary()), ctx);

        Assert.Equal(StatusCodes.Status404NotFound, status);
    }

    [Fact]
    public async Task An_AMBIGUOUS_account_is_refused_never_guessed()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = Db;
        // Two DIFFERENT accounts (different subjects - the real key) that happen to carry the same display
        // email. Picking either one would email one person the other person's day.
        SeedTenant(db, Alice, "sub-alice", "shared@example.com");
        SeedTenant(db, Bob, "sub-bob", "shared@example.com");

        var ctx = Request($"Bearer {Token}");
        var (status, _) = await ExecuteAsync(Call(ctx, "shared@example.com", db, HostedBoundary()), ctx);

        Assert.Equal(StatusCodes.Status409Conflict, status);
    }

    [Fact]
    public async Task An_account_resolves_by_email_case_insensitively_and_by_tenant_id()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = Db;
        SeedTenant(db, Alice, "sub-alice", "Alice@Example.com");

        foreach (var account in new[] { "alice@example.com", "ALICE@EXAMPLE.COM", Alice.Value })
        {
            var ctx = Request($"Bearer {Token}");
            var (status, body) = await ExecuteAsync(Call(ctx, account, db, HostedBoundary()), ctx);

            Assert.Equal(StatusCodes.Status200OK, status);
            Assert.Equal(account, body.GetProperty("account").GetString());
        }
    }

    [Theory]
    [InlineData(null, "2026-07-23", "America/Toronto")]   // no account
    [InlineData("alice@example.com", "23-07-2026", "America/Toronto")]
    [InlineData("alice@example.com", "2026-07-23", "Mars/Olympus_Mons")]
    public async Task A_request_missing_or_malforming_its_coordinates_is_400(string? account, string date, string tz)
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = Db;
        SeedTenant(db, Alice, "sub-alice", "alice@example.com");

        var ctx = Request($"Bearer {Token}");
        var (status, _) = await ExecuteAsync(Call(ctx, account, db, HostedBoundary(), date, tz), ctx);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
    }

    // ---- the isolation the whole gate exists for -------------------------------------------------------

    [Fact]
    public async Task A_second_tenants_data_never_appears_in_the_first_tenants_report()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = Db;
        SeedTenant(db, Alice, "sub-alice", "alice@example.com");
        SeedTenant(db, Bob, "sub-bob", "bob@example.com");

        SeedSessionEvent(db, Alice, "alice-1", GovernanceEventState.Active, Now.AddHours(-1));
        SeedSessionEvent(db, Bob, "bob-waiting", GovernanceEventState.WaitingOnHuman, Now.AddHours(-5));

        var aliceCtx = Request($"Bearer {Token}");
        var (aliceStatus, aliceBody) = await ExecuteAsync(Call(aliceCtx, "alice@example.com", db, HostedBoundary()), aliceCtx);

        var bobCtx = Request($"Bearer {Token}");
        // Bob's Director is connected and pushing that session, so Bob genuinely HAS a row to leak. With
        // no roster the report would name nobody and the isolation claim would pass on an empty payload.
        var bobLive = Live(Bob, new SessionDto { SessionId = "bob-waiting", Name = "bob-session" });
        var (bobStatus, bobBody) = await ExecuteAsync(
            Call(bobCtx, "bob@example.com", db, HostedBoundary(), live: bobLive), bobCtx);

        Assert.Equal(StatusCodes.Status200OK, aliceStatus);
        Assert.Equal(StatusCodes.Status200OK, bobStatus);

        // Bob waited 5 hours ago - after the reported day closed - so it is Bob's attention row, and
        // it appears NOWHERE in Alice's payload.
        Assert.Equal(0, aliceBody.GetProperty("attention").GetArrayLength());
        Assert.Equal(1, bobBody.GetProperty("attention").GetArrayLength());
        Assert.Equal("bob-session", bobBody.GetProperty("attention")[0].GetProperty("session").GetString());

        // The strongest form of the claim: Bob's session identifiers appear NOWHERE in Alice's payload.
        Assert.DoesNotContain("bob-", aliceBody.GetRawText(), StringComparison.Ordinal);
    }

    // ---- the wire shape the website sender is coded against ---------------------------------------------

    [Fact]
    public async Task The_JSON_matches_the_agreed_contract_and_omits_what_the_Gateway_does_not_know()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = Db;
        SeedTenant(db, Alice, "sub-alice", "alice@example.com");
        // One session has been waiting since after the reported day closed - the attention row, and with
        // the stats gone (owner ruling, 2026-09-20) the ONLY content this report carries.
        SeedSessionEvent(db, Alice, "s1", GovernanceEventState.WaitingOnHuman, Now.AddHours(-7));

        var ctx = Request($"Bearer {Token}");
        // A connected Director that can see s1 and knows its name. Since #3124 the report refuses to call
        // a session "waiting on you" unless it can currently see it, so without this there is no row here
        // and this test would be asserting the shape of nothing.
        var live = Live(Alice, new SessionDto { SessionId = "s1", Name = "s1" });
        var (status, body) = await ExecuteAsync(
            Call(ctx, "alice@example.com", db, HostedBoundary(), live: live), ctx);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.Equal("alice@example.com", body.GetProperty("account").GetString());

        var window = body.GetProperty("window");
        Assert.Equal("2026-07-23", window.GetProperty("date").GetString());
        Assert.Equal("America/Toronto", window.GetProperty("tz").GetString());
        Assert.True(window.TryGetProperty("startUtc", out _));
        Assert.True(window.TryGetProperty("endUtc", out _));

        // NO YESTERDAY-STATS ON THE DAILY REPORT (owner ruling, 2026-09-20, issue #3124): the report
        // carries no scoreboard at all, so the key is ABSENT from the wire - not zero, not empty.
        Assert.False(body.TryGetProperty("stats", out _));

        // The Gateway invents no prose.
        Assert.False(body.TryGetProperty("observation", out _));

        Assert.Equal(1, body.GetProperty("attention").GetArrayLength());
        var item = body.GetProperty("attention")[0];
        Assert.Equal("waiting-session", item.GetProperty("type").GetString());
        Assert.Equal("s1", item.GetProperty("session").GetString());
        Assert.Equal(7.0, item.GetProperty("ageHours").GetDouble());
        Assert.True(item.TryGetProperty("waitingSinceUtc", out _));
        // The id rides along so the email can link into the Cockpit (#3124) - a row names a session AND
        // gives a way in, because a name the reader cannot act on is only half the row.
        Assert.Equal("s1", item.GetProperty("sessionId").GetString());
        // The roster knows this session but not its repository, so the key is absent, not "".
        Assert.False(item.TryGetProperty("repo", out _));
        // Polymorphism must not smuggle a synthetic discriminator into the contract.
        Assert.False(item.TryGetProperty("$type", out _));
    }

    // ---- self-host -------------------------------------------------------------------------------------

    [Fact]
    public async Task On_self_host_the_single_local_tenant_answers_and_the_token_still_gates()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
        var db = _h.Open(); // SingleTenantContext -> everything is TenantId.Local

        // A self-host install has no account census, so the account names the one install - but the service
        // token is still required.
        var denied = Request("Bearer wrong");
        var (deniedStatus, _) = await ExecuteAsync(Call(denied, "whoever@example.com", db, SelfHostBoundary()), denied);
        Assert.Equal(StatusCodes.Status401Unauthorized, deniedStatus);

        var ctx = Request($"Bearer {Token}");
        var (status, body) = await ExecuteAsync(Call(ctx, "whoever@example.com", db, SelfHostBoundary()), ctx);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.False(body.TryGetProperty("stats", out _));
    }
}
