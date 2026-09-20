using System.Net;
using System.Text.Json;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The recipient list for the daily report.
///
/// This endpoint returns EVERY account's email address, which makes it more sensitive than any single
/// report, so the tests that matter are the refusals. It must be impossible to reach it more easily
/// than the report itself: unconfigured token is 503 and never an open door, wrong token is 401, and
/// neither leaks a single address on the way out.
/// </summary>
[Collection("ReportRecipients")] // serial: mutates the REPORT_SERVICE_TOKEN process env var
public sealed class ReportRecipientsEndpointTests : IDisposable
{
    private const string Token = "test-report-token";
    private readonly string? _previousToken;
    private readonly GatewayDbTestHarness _h = new();

    public ReportRecipientsEndpointTests()
    {
        _previousToken = Environment.GetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar);
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, Token);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, _previousToken);
        _h.Dispose();
    }

    private TenantRegistry Registry() => new(_h.Open());

    /// <summary>The per-tenant settings over the SAME database as the registry, so a cadence written for a
    /// minted tenant is the one the endpoint reads back (issue #1000).</summary>
    private TenantSettingsResolver Settings() => new(new TenantSettingsStore(_h.Open()));

    /// <summary>An IResult needs a service provider to execute, exactly as the report endpoint's own
    /// tests build one.</summary>
    private static readonly IServiceProvider Services =
        new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();

    private static HttpContext Ctx(string? bearer = Token)
    {
        var ctx = new DefaultHttpContext { RequestServices = Services, Response = { Body = new MemoryStream() } };
        if (bearer is not null) ctx.Request.Headers.Authorization = $"Bearer {bearer}";
        return ctx;
    }

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result, HttpContext ctx)
    {
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task WithNoTokenConfigured_ItRefuses_RatherThanServingUnguarded()
    {
        Environment.SetEnvironmentVariable(MorningReportEndpoint.ServiceTokenEnvVar, null);

        var ctx = Ctx();
        var (status, _) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, Registry(), Settings()), ctx);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
    }

    [Fact]
    public async Task WithTheWrongToken_ItRefuses()
    {
        var ctx = Ctx("not-the-token");
        var (status, _) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, Registry(), Settings()), ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task WithNoAuthorizationHeaderAtAll_ItRefuses()
    {
        var ctx = Ctx(bearer: null);
        var (status, _) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, Registry(), Settings()), ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task ARefusalLeaksNoAddresses()
    {
        // The failure path must not be a quieter way to read the list.
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-1", "leaked@example.com");

        var ctx = Ctx("wrong");
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, Settings()), ctx);

        Assert.DoesNotContain("leaked@example.com", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ItReturnsEveryAccountThatHasAnEmail()
    {
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-1", "alice@example.com");
        registry.MintOrLookupBySubject("subject-2", "bob@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, Settings()), ctx);
        var emails = body.GetProperty("recipients").EnumerateArray()
            .Select(r => r.GetProperty("email").GetString()).ToList();

        Assert.Equal(new[] { "alice@example.com", "bob@example.com" }, emails);
    }

    [Fact]
    public async Task AnAccountWithNoEmailIsOmitted_NotReturnedBlank()
    {
        // A recipient with nothing to send to is not a recipient. Returning it blank would invite the
        // sender to try anyway and fail once per morning, forever.
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-no-email", null);
        registry.MintOrLookupBySubject("subject-with", "real@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, Settings()), ctx);
        var recipients = body.GetProperty("recipients").EnumerateArray().ToList();

        Assert.Equal("real@example.com", Assert.Single(recipients).GetProperty("email").GetString());
    }

    [Fact]
    public async Task TheSameAddressOnTwoAccountsIsSentToOnce()
    {
        // Two tenants can legitimately carry one address. Mailing it twice each morning would read as
        // a bug to the person receiving it, and would be one.
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-1", "same@example.com");
        registry.MintOrLookupBySubject("subject-2", "SAME@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, Settings()), ctx);

        Assert.Single(body.GetProperty("recipients").EnumerateArray());
    }

    [Fact]
    public async Task WithNoAccountsAtAll_ItReturnsAnEmptyListRatherThanFailing()
    {
        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, Registry(), Settings()), ctx);

        Assert.Empty(body.GetProperty("recipients").EnumerateArray());
    }

    // ---- the account's own answer to "do you want this mail" (issue #1000) ---------------------------

    [Fact]
    public async Task AnAccountThatTurnedTheReportOff_IsNotMailed()
    {
        var registry = Registry();
        var settings = Settings();
        var quiet = registry.MintOrLookupBySubject("subject-quiet", "quiet@example.com");
        registry.MintOrLookupBySubject("subject-wants", "wants@example.com");
        settings.SetDailyReportCadence(quiet, ReportCadence.Off, DateTime.UtcNow);

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, settings), ctx);
        var emails = body.GetProperty("recipients").EnumerateArray()
            .Select(r => r.GetProperty("email").GetString()).ToList();

        Assert.Equal(new[] { "wants@example.com" }, emails);
    }

    [Fact]
    public async Task AnAccountThatNeverChose_IsStillMailed()
    {
        // The default is daily, so switching this setting on cannot silently stop anybody's report. This is
        // the arm that would catch a filter written the wrong way round - one that mailed only accounts
        // holding an explicit "daily", which is nobody on the day it ships.
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-untouched", "untouched@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, Settings()), ctx);
        var emails = body.GetProperty("recipients").EnumerateArray()
            .Select(r => r.GetProperty("email").GetString()).ToList();

        Assert.Equal(new[] { "untouched@example.com" }, emails);
    }

    [Fact]
    public async Task AnAccountThatTurnedTheReportBackOn_IsMailedAgain()
    {
        var registry = Registry();
        var settings = Settings();
        var tenant = registry.MintOrLookupBySubject("subject-returning", "returning@example.com");
        settings.SetDailyReportCadence(tenant, ReportCadence.Off, DateTime.UtcNow);
        settings.SetDailyReportCadence(tenant, ReportCadence.Daily, DateTime.UtcNow);

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, settings), ctx);

        Assert.Equal("returning@example.com",
            Assert.Single(body.GetProperty("recipients").EnumerateArray()).GetProperty("email").GetString());
    }

    [Fact]
    public async Task OneAddressOnTwoAccounts_OneOfWhichWantsIt_IsStillMailedOnce()
    {
        // Silence here would be an opt-out the second account never asked for; two copies would be the
        // duplicate the endpoint already refuses. One copy is the only answer that serves both.
        var registry = Registry();
        var settings = Settings();
        var off = registry.MintOrLookupBySubject("subject-1", "shared@example.com");
        registry.MintOrLookupBySubject("subject-2", "shared@example.com");
        settings.SetDailyReportCadence(off, ReportCadence.Off, DateTime.UtcNow);

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, settings), ctx);

        Assert.Equal("shared@example.com",
            Assert.Single(body.GetProperty("recipients").EnumerateArray()).GetProperty("email").GetString());
    }

    // ---- whose seven o'clock (#3124) ----

    [Fact]
    public async Task AnAccountThatChoseATimeZone_IsHandedOverWithIt()
    {
        var registry = Registry();
        var settings = Settings();
        var tokyo = registry.MintOrLookupBySubject("subject-tokyo", "tokyo@example.com");
        settings.SetTimeZone(tokyo, "Asia/Tokyo", DateTime.UtcNow);

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, settings), ctx);
        var row = body.GetProperty("recipients").EnumerateArray().Single();

        Assert.Equal("Asia/Tokyo", row.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task AnAccountThatNeverChoseATimeZone_CarriesNoTimeZoneKeyAtAll()
    {
        // Not null, not the operator default: ABSENT. The sender keeps an account with no key on the send
        // time everybody had before, and a default filled in here would silently move them off it.
        var registry = Registry();
        registry.MintOrLookupBySubject("subject-plain", "plain@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, Settings()), ctx);
        var row = body.GetProperty("recipients").EnumerateArray().Single();

        Assert.False(row.TryGetProperty("timeZone", out _));
        Assert.Equal("plain@example.com", row.GetProperty("email").GetString());
        Assert.Equal("plain@example.com", row.GetProperty("account").GetString());
    }

    [Fact]
    public async Task OneAddressOnTwoAccounts_TheChosenTimeZoneWins_WhicheverAccountCameFirst()
    {
        var registry = Registry();
        var settings = Settings();
        registry.MintOrLookupBySubject("subject-first-silent", "shared@example.com");
        var chose = registry.MintOrLookupBySubject("subject-second-chose", "shared@example.com");
        settings.SetTimeZone(chose, "Europe/London", DateTime.UtcNow);
        var choseFirst = registry.MintOrLookupBySubject("subject-third-chose", "other@example.com");
        settings.SetTimeZone(choseFirst, "Asia/Tokyo", DateTime.UtcNow);
        registry.MintOrLookupBySubject("subject-fourth-silent", "other@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, settings), ctx);
        var zones = body.GetProperty("recipients").EnumerateArray()
            .ToDictionary(r => r.GetProperty("email").GetString()!, r => r.GetProperty("timeZone").GetString());

        Assert.Equal(2, zones.Count);
        Assert.Equal("Europe/London", zones["shared@example.com"]);
        Assert.Equal("Asia/Tokyo", zones["other@example.com"]);
    }

    [Fact]
    public void AStoredZoneThisHostCannotRead_IsNoChoice_NotACrashAndNotTheDefault()
    {
        var h = _h.Open();
        var store = new TenantSettingsStore(h);
        var tenant = Registry().MintOrLookupBySubject("subject-odd", "odd@example.com");
        store.Set(tenant, TenantSettingKeys.TimeZone, "Not/AZone", DateTime.UtcNow);

        Assert.Null(new TenantSettingsResolver(store).ChosenTimeZoneIana(tenant));
    }

    [Fact]
    public async Task EveryAccountTurningItOff_LeavesAnEmptyList_NotEveryAccount()
    {
        var registry = Registry();
        var settings = Settings();
        foreach (var (subject, email) in new[] { ("s1", "a@example.com"), ("s2", "b@example.com") })
            settings.SetDailyReportCadence(registry.MintOrLookupBySubject(subject, email), ReportCadence.Off, DateTime.UtcNow);

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, settings), ctx);

        Assert.Empty(body.GetProperty("recipients").EnumerateArray());
    }

    [Fact]
    public async Task TheOrderIsStable_SoASendCanBeComparedBetweenMornings()
    {
        var registry = Registry();
        registry.MintOrLookupBySubject("s3", "carol@example.com");
        registry.MintOrLookupBySubject("s1", "alice@example.com");
        registry.MintOrLookupBySubject("s2", "bob@example.com");

        var ctx = Ctx();
        var (_, body) = await ExecuteAsync(ReportRecipientsEndpoint.Handle(ctx, registry, Settings()), ctx);
        var emails = body.GetProperty("recipients").EnumerateArray()
            .Select(r => r.GetProperty("email").GetString()).ToList();

        Assert.Equal(new[] { "alice@example.com", "bob@example.com", "carol@example.com" }, emails);
    }
}
