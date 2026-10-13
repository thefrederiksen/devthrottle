using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE LICENSES ROUTES (License Switching), each handler run over the real store on a throwaway SQLite file and its
/// answer read back as the JSON the Cockpit receives. What these hold down: the exact shape of each answer (the
/// Licenses page is built from these names, so a rename fails here); one account never sees another's accounts,
/// machines, forecast or log; an account that never reported gets an empty answer with a sentence, not an error; the
/// log pages without overlap or loss; and a log row of a kind this build does not know does not break the log.
/// </summary>
public sealed class LicenseEndpointsTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly LicenseUsageHistory _history = new();
    private DateTime _now = T0;

    private static readonly TenantId TenantOne = new("acct-one");
    private static readonly TenantId TenantTwo = new("acct-two");

    public LicenseEndpointsTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _snapshots = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
    }

    public void Dispose() => _harness.Dispose();

    // ---- plumbing ----

    private static DefaultHttpContext Context(string query = "")
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Request.QueryString = new QueryString(query);
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static (int Status, string Text, JsonElement Json) Read(DefaultHttpContext ctx, IResult result)
    {
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return (ctx.Response.StatusCode, text, JsonDocument.Parse(text).RootElement.Clone());
    }

    private (int Status, string Text, JsonElement Json) Overview(TenantId? tenant)
    {
        var ctx = Context();
        return Read(ctx, LicenseEndpoints.Overview(ctx, _ => tenant, _snapshots, () => _now));
    }

    private (int Status, string Text, JsonElement Json) Forecast(TenantId? tenant)
    {
        var ctx = Context();
        return Read(ctx, LicenseEndpoints.Forecast(ctx, _ => tenant, _snapshots, _history, () => _now));
    }

    private (int Status, string Text, JsonElement Json) Log(TenantId? tenant, string query = "")
    {
        var ctx = Context(query);
        return Read(ctx, LicenseEndpoints.Log(ctx, _ => tenant, _store, () => _now));
    }

    private Guid Apply(TenantId tenant, LicenseUsageReport report, long seq = 1, DateTime? at = null)
        => _store.ApplyReport(tenant, "dir-1", seq, report, at ?? T0, TimeSpan.Zero).MachineId;

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    private void SeedTwoAccounts()
    {
        var one = Apply(TenantOne, Report("machine-of-one", Holding(A, true, 96, 61), Holding(B, false, 4, 22)));
        _store.CreatePendingSwitch(TenantOne, one, A, B, LicenseSwitchReasons.Manual, T0);
        _history.Record(TenantOne, Report("machine-of-one", Holding(A, true, 86, readAt: T0.AddMinutes(-20))), TimeSpan.Zero, T0);
        _history.Record(TenantOne, Report("machine-of-one", Holding(A, true, 96)), TimeSpan.Zero, T0);

        var two = Apply(TenantTwo, Report("machine-of-two", Holding(C, true, 30, 40), Holding("account-d@example.com", false, 5, 6)));
        _store.CreatePendingSwitch(TenantTwo, two, C, "account-d@example.com", LicenseSwitchReasons.Threshold, T0);
    }

    // ---- the shape of each answer ----

    [Fact]
    public void Overview_AnswersTheExactShapeTheLicensesPageIsBuiltFrom()
    {
        var expires = Holding(B, false, 4, 22);
        expires.SignInExpiresAtUtc = T0.AddDays(9);
        expires.SignedInAtUtc = T0.AddDays(-21);
        Apply(TenantOne, Report("the-windows-machine", Holding(A, true, 96, 61), expires));

        var (status, _, json) = Overview(TenantOne);

        Assert.Equal(200, status);
        Assert.Equal(new[] { "asOfUtc", "isEmpty", "emptyText", "accounts", "machines" }, Names(json));
        Assert.False(json.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("emptyText").ValueKind);
        Assert.Equal("2026-10-11T03:12:00Z", json.GetProperty("asOfUtc").GetString());

        var account = json.GetProperty("accounts")[0];
        Assert.Equal(new[]
        {
            "accountEmail", "plan", "planText", "rateLimitTier", "status", "statusText", "statusTone", "statusDetail", "isInUse",
            "fiveHour", "sevenDay", "isStale", "readAtUtc", "readAgeText", "readError", "readErrorText", "extraUsage",
            "renewsOn", "renewsText", "signInExpiry", "activeOnMachines", "spareOnMachines", "canSwitchNow", "switchNowText", "switchNowTargetAccountEmail", "switchTargets",
        }, Names(account));
        Assert.Equal(new[] { "expiresAtUtc", "machine", "verdict", "text", "tone" }, Names(account.GetProperty("signInExpiry")));
        // No renewal date was set for this account, and the answer says so in words - never a guessed date.
        Assert.Equal((JsonValueKind.Null, "Renewal date not set"), (account.GetProperty("renewsOn").ValueKind, account.GetProperty("renewsText").GetString()));
        Assert.Equal(new[] { "machineId", "machineName", "osUser", "label" }, Names(account.GetProperty("activeOnMachines")[0]));
        var spareAccount = json.GetProperty("accounts")[1];
        Assert.Equal(new[] { "machineId", "machineName", "osUser", "label" }, Names(spareAccount.GetProperty("spareOnMachines")[0]));
        Assert.Equal(new[] { "machineId", "machineName", "osUser", "label" }, Names(spareAccount.GetProperty("signInExpiry").GetProperty("machine")));
        Assert.Equal(("2026-10-20T03:12:00Z", "fine", "Sign-in on the-windows-machine expires in 9 days", "good"),
            (spareAccount.GetProperty("signInExpiry").GetProperty("expiresAtUtc").GetString(), spareAccount.GetProperty("signInExpiry").GetProperty("verdict").GetString(),
             spareAccount.GetProperty("signInExpiry").GetProperty("text").GetString(), spareAccount.GetProperty("signInExpiry").GetProperty("tone").GetString()));
        Assert.Equal(json.GetProperty("machines")[0].GetProperty("machineId").GetString(),
            account.GetProperty("activeOnMachines")[0].GetProperty("machineId").GetString());
        Assert.Equal((A, "in-use", "In use", "good"),
            (account.GetProperty("accountEmail").GetString(), account.GetProperty("status").GetString(), account.GetProperty("statusText").GetString(), account.GetProperty("statusTone").GetString()));
        Assert.Equal(new[] { "utilization", "utilizationText", "level", "resetsAtUtc", "resetsText" }, Names(account.GetProperty("fiveHour")));
        Assert.Equal((96d, "96%", "normal", "2026-10-11T05:12:00Z"),
            (account.GetProperty("fiveHour").GetProperty("utilization").GetDouble(), account.GetProperty("fiveHour").GetProperty("utilizationText").GetString(),
             account.GetProperty("fiveHour").GetProperty("level").GetString(), account.GetProperty("fiveHour").GetProperty("resetsAtUtc").GetString()));
        Assert.Equal(new[] { "state", "text", "utilization" }, Names(account.GetProperty("extraUsage")));
        Assert.True(account.GetProperty("canSwitchNow").GetBoolean());
        Assert.Equal(B, account.GetProperty("switchNowTargetAccountEmail").GetString());
        Assert.Equal(new[] { "accountEmail", "canChoose", "unavailableText", "isAutomaticChoice" }, Names(account.GetProperty("switchTargets")[0]));

        var machine = json.GetProperty("machines")[0];
        Assert.Equal(new[]
        {
            "machineId", "machineName", "osUser", "label", "activeAccountEmail", "status", "statusText", "statusTone", "isStale",
            "lastReportAtUtc", "lastReportText", "active", "spares", "switchInProgress",
        }, Names(machine));
        Assert.Equal(("the-windows-machine", "owner", A), (machine.GetProperty("machineName").GetString(), machine.GetProperty("osUser").GetString(), machine.GetProperty("activeAccountEmail").GetString()));
        var spare = machine.GetProperty("spares")[0];
        Assert.Equal(new[]
        {
            "accountEmail", "isActive", "signedInAtUtc", "signedInText", "signInExpiresAtUtc", "expiryVerdict", "expiryText", "expiryTone",
            "renewalFailedAtUtc", "renewalFailedText", "renewalFailedTone", "isReadingStale", "readError", "readErrorText",
        }, Names(spare));
        Assert.Equal((B, "fine", "Sign-in expires in 9 days", "2026-10-20T03:12:00Z", "Signed in 21 days ago"),
            (spare.GetProperty("accountEmail").GetString(), spare.GetProperty("expiryVerdict").GetString(), spare.GetProperty("expiryText").GetString(),
             spare.GetProperty("signInExpiresAtUtc").GetString(), spare.GetProperty("signedInText").GetString()));
        Assert.Equal(JsonValueKind.Null, machine.GetProperty("switchInProgress").ValueKind);
        // Null when the last renewal worked or is not known.
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null, JsonValueKind.Null),
            (spare.GetProperty("renewalFailedAtUtc").ValueKind, spare.GetProperty("renewalFailedText").ValueKind, spare.GetProperty("renewalFailedTone").ValueKind));
    }

    [Fact]
    public void Overview_AMachineMidSwitch_CarriesTheSwitchInProgress()
    {
        var id = Apply(TenantOne, Report("m1", Holding(A, true), Holding(B, false)));
        var row = _store.CreatePendingSwitch(TenantOne, id, A, B, LicenseSwitchReasons.Manual, T0)!;

        var inProgress = Overview(TenantOne).Json.GetProperty("machines")[0].GetProperty("switchInProgress");

        Assert.Equal(new[] { "switchId", "fromAccountEmail", "toAccountEmail", "text" }, Names(inProgress));
        Assert.Equal((row.SwitchId, A, B), (inProgress.GetProperty("switchId").GetString(), inProgress.GetProperty("fromAccountEmail").GetString(), inProgress.GetProperty("toAccountEmail").GetString()));
    }

    [Fact]
    public void Forecast_AnswersTheExactShapeTheLicensesPageIsBuiltFrom()
    {
        Apply(TenantOne, Report("machine-of-one", Holding(A, true, 96, 61), Holding(B, false, 4, 22)));
        _history.Record(TenantOne, Report("machine-of-one", Holding(A, true, 86, readAt: T0.AddMinutes(-20))), TimeSpan.Zero, T0);
        _history.Record(TenantOne, Report("machine-of-one", Holding(A, true, 96)), TimeSpan.Zero, T0);

        var (status, _, json) = Forecast(TenantOne);

        Assert.Equal(200, status);
        Assert.Equal(new[] { "asOfUtc", "isEmpty", "emptyText", "nextSwitch", "nextSwitchText", "accounts", "machines" }, Names(json));
        var next = json.GetProperty("nextSwitch");
        Assert.Equal(new[] { "fromAccountEmail", "expectedAtUtc", "window", "windowText", "targetAccountEmail", "machines" }, Names(next));
        Assert.Equal((A, "five-hour", "5-hour window", B),
            (next.GetProperty("fromAccountEmail").GetString(), next.GetProperty("window").GetString(), next.GetProperty("windowText").GetString(), next.GetProperty("targetAccountEmail").GetString()));
        Assert.Equal(new[] { "machineId", "machineName", "osUser", "label" }, Names(next.GetProperty("machines")[0]));
        Assert.EndsWith("Z", next.GetProperty("expectedAtUtc").GetString());
        Assert.Contains("is expected to reach 97% of its 5-hour window in about", json.GetProperty("nextSwitchText").GetString());

        var account = json.GetProperty("accounts")[0];
        Assert.Equal(new[]
        {
            "accountEmail", "isInUse", "isStale", "fiveHour", "sevenDay", "expectedAtLimitUtc", "expectedText",
            "nextSwitchTargetAccountEmail", "nextSwitchText",
        }, Names(account));
        Assert.Equal(new[] { "utilization", "utilizationText", "level", "resetsAtUtc", "resetsText", "percentPerHour", "rateText", "expectedAtLimitUtc", "expectedText" },
            Names(account.GetProperty("fiveHour")));
        Assert.Equal(("96%", "normal", "Resets in 2 hours"),
            (account.GetProperty("fiveHour").GetProperty("utilizationText").GetString(), account.GetProperty("fiveHour").GetProperty("level").GetString(),
             account.GetProperty("fiveHour").GetProperty("resetsText").GetString()));
        Assert.Equal("30% an hour", account.GetProperty("fiveHour").GetProperty("rateText").GetString());

        var machine = json.GetProperty("machines")[0];
        Assert.Equal(new[] { "machineId", "machineName", "osUser", "label", "activeAccountEmail", "nextAccountEmail", "expectedSwitchAtUtc", "text" }, Names(machine));
        Assert.Equal(32, machine.GetProperty("machineId").GetString()!.Length);
        Assert.Equal(("machine-of-one", A, B), (machine.GetProperty("machineName").GetString(), machine.GetProperty("activeAccountEmail").GetString(), machine.GetProperty("nextAccountEmail").GetString()));
        Assert.StartsWith($"Next: {B}, in about ", machine.GetProperty("text").GetString());
    }

    // ---- PUT /gateway/licenses/accounts/renewal ----

    private (int Status, string Text, JsonElement Json) PutRenewal(TenantId? tenant, LicenseRenewalDateRequest? body)
    {
        var ctx = Context();
        return Read(ctx, LicenseEndpoints.SetRenewalDate(ctx, _ => tenant, _store, () => _now, body));
    }

    [Fact]
    public void RenewalDate_Set_AnswersWhatWasStored_AndTheOverviewThenSaysIt()
    {
        Apply(TenantOne, Report("m1", Holding(A, true), Holding(B, false)));

        var (status, _, json) = PutRenewal(TenantOne, new LicenseRenewalDateRequest { AccountEmail = "Account-A@example.com", RenewsOn = "2026-10-17" });

        Assert.Equal(200, status);
        Assert.Equal(new[] { "accountEmail", "renewsOn", "renewsText" }, Names(json));
        Assert.Equal((A, "2026-10-17", "Renews 17 Oct"),
            (json.GetProperty("accountEmail").GetString(), json.GetProperty("renewsOn").GetString(), json.GetProperty("renewsText").GetString()));
        var accounts = Overview(TenantOne).Json.GetProperty("accounts");
        Assert.Equal(("2026-10-17", "Renews 17 Oct"), (accounts[0].GetProperty("renewsOn").GetString(), accounts[0].GetProperty("renewsText").GetString()));
        Assert.Equal((JsonValueKind.Null, "Renewal date not set"), (accounts[1].GetProperty("renewsOn").ValueKind, accounts[1].GetProperty("renewsText").GetString()));
    }

    [Fact]
    public void RenewalDate_ClearedWithNull_IsNotSetAgain()
    {
        Apply(TenantOne, Report("m1", Holding(A, true)));
        PutRenewal(TenantOne, new LicenseRenewalDateRequest { AccountEmail = A, RenewsOn = "2026-10-17" });

        var (status, _, json) = PutRenewal(TenantOne, new LicenseRenewalDateRequest { AccountEmail = A, RenewsOn = null });

        Assert.Equal(200, status);
        Assert.Equal((JsonValueKind.Null, "Renewal date not set"), (json.GetProperty("renewsOn").ValueKind, json.GetProperty("renewsText").GetString()));
        Assert.Equal(JsonValueKind.Null, Overview(TenantOne).Json.GetProperty("accounts")[0].GetProperty("renewsOn").ValueKind);
    }

    [Theory]
    [InlineData("17 Oct 2026")]
    [InlineData("2026-10-17T00:00:00Z")]    // a date only: no time, no zone
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("10/17/2026")]
    [InlineData("")]
    public void RenewalDate_ADateNotWrittenYearMonthDay_IsRefused_NotGuessed_AndNothingIsStored(string renewsOn)
    {
        Apply(TenantOne, Report("m1", Holding(A, true)));

        var (status, _, json) = PutRenewal(TenantOne, new LicenseRenewalDateRequest { AccountEmail = A, RenewsOn = renewsOn });

        Assert.Equal((400, "invalid_date"), (status, json.GetProperty("code").GetString()));
        Assert.Equal(JsonValueKind.Null, Overview(TenantOne).Json.GetProperty("accounts")[0].GetProperty("renewsOn").ValueKind);
    }

    [Fact]
    public void RenewalDate_WithNoBody_NoAccount_OrNoCredentialAccount_IsRefused()
    {
        Apply(TenantOne, Report("m1", Holding(A, true)));

        var noBody = PutRenewal(TenantOne, null);
        var noEmail = PutRenewal(TenantOne, new LicenseRenewalDateRequest { AccountEmail = " ", RenewsOn = "2026-10-17" });
        var noTenant = PutRenewal(null, new LicenseRenewalDateRequest { AccountEmail = A, RenewsOn = "2026-10-17" });

        Assert.Equal((400, "invalid_body"), (noBody.Status, noBody.Json.GetProperty("code").GetString()));
        Assert.Equal((400, "account_required"), (noEmail.Status, noEmail.Json.GetProperty("code").GetString()));
        Assert.Equal((403, "no_account"), (noTenant.Status, noTenant.Json.GetProperty("code").GetString()));
        Assert.Equal(JsonValueKind.Null, Overview(TenantOne).Json.GetProperty("accounts")[0].GetProperty("renewsOn").ValueKind);
    }

    [Fact]
    public void RenewalDate_OneDevThrottleAccount_CanNeitherSetNorLearnOfAnothersClaudeAccount()
    {
        Apply(TenantOne, Report("machine-of-one", Holding(A, true)));
        Apply(TenantTwo, Report("machine-of-two", Holding(C, true)));

        var others = PutRenewal(TenantOne, new LicenseRenewalDateRequest { AccountEmail = C, RenewsOn = "2026-10-17" });
        var nobodys = PutRenewal(TenantOne, new LicenseRenewalDateRequest { AccountEmail = "nobody@example.com", RenewsOn = "2026-10-17" });

        // The same answer for an account that exists in another DevThrottle account and one that exists nowhere.
        Assert.Equal((404, "unknown_account"), (others.Status, others.Json.GetProperty("code").GetString()));
        Assert.Equal(nobodys.Text, others.Text);
        Assert.Equal(JsonValueKind.Null, Overview(TenantTwo).Json.GetProperty("accounts")[0].GetProperty("renewsOn").ValueKind);
    }

    [Fact]
    public void Overview_AHoldingWhoseRenewalFailed_CarriesItsThreeFields()
    {
        var spare = Holding(B, false);
        spare.RenewalFailedAtUtc = T0.AddHours(-3);
        spare.RenewalError = LicenseRenewalErrors.NoApprover;
        spare.SignInExpiresAtUtc = T0.AddDays(1);
        Apply(TenantOne, Report("m1", Holding(A, true), spare));

        var holding = Overview(TenantOne).Json.GetProperty("machines")[0].GetProperty("spares")[0];

        Assert.Equal(("2026-10-11T00:12:00Z", "Renewing this sign-in failed 3 hours ago: no browser is set to approve this account.", "bad"),
            (holding.GetProperty("renewalFailedAtUtc").GetString(), holding.GetProperty("renewalFailedText").GetString(), holding.GetProperty("renewalFailedTone").GetString()));
    }

    // ---- the reviewer's three reports: refused at the door, and the forecast still answers ----

    [Theory]
    [InlineData("infinite-rate")]      // -1e308 and then 96: the rise over the time overflows to an infinite rate
    [InlineData("year-9999")]          // a read time in the last minutes of the year 9999, with use rising
    [InlineData("edge-of-calendar")]   // the same read time from a clock corrected by years: the sum passes the end
    public void Forecast_AfterAReportNoRealDirectorSends_StillAnswers_BecauseTheReportWasRefusedAtTheIngest(string which)
    {
        var ingest = new LicenseUsageIngest(_store, _history);
        ingest.RegisterConnection(TenantOne, "dir-1", "conn-1");
        var edge = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc).AddMinutes(-5);
        var later = T0.AddMinutes(2);
        var (first, second) = which switch
        {
            "infinite-rate" => (Report("m1", Holding(A, true, -1e308), Holding(B, false)), Report("m1", later, Holding(A, true, 96, readAt: later), Holding(B, false, readAt: later))),
            "year-9999" => (Report("m1", Holding(A, true, 50, readAt: edge), Holding(B, false)), Report("m1", later, Holding(A, true, 60, readAt: edge.AddMinutes(2)), Holding(B, false, readAt: later))),
            _ => (Report("m1", T0.AddYears(-3), Holding(A, true, 50, readAt: edge), Holding(B, false, readAt: T0.AddYears(-3))),
                  Report("m1", T0.AddYears(-3).AddMinutes(2), Holding(A, true, 60, readAt: edge.AddMinutes(2)), Holding(B, false, readAt: T0.AddYears(-3)))),
        };

        var outcomes = new[] { ingest.Ingest(TenantOne, "dir-1", "conn-1", 1, first, T0), ingest.Ingest(TenantOne, "dir-1", "conn-1", 2, second, later) };
        _now = later;
        var (forecastStatus, forecastText, _) = Forecast(TenantOne);
        var (overviewStatus, overviewText, _) = Overview(TenantOne);

        Assert.Contains(LicenseIngestOutcome.Malformed, outcomes);
        Assert.Equal((200, 200), (forecastStatus, overviewStatus));
        Assert.DoesNotContain("Infinity", forecastText + overviewText);
        Assert.DoesNotContain("9999", forecastText + overviewText);
    }

    [Fact]
    public void Log_NamesTwoUsersOfOneMachineApart_WithTheIdentifierOfEachRowInTheOverview()
    {
        var anna = Report("m1", Holding(A, true), Holding(B, false));
        anna.OsUser = "anna";
        var ben = Report("M1", Holding(A, true), Holding(B, false));
        ben.OsUser = "ben";
        var annaId = Apply(TenantOne, anna);
        var benId = Apply(TenantOne, ben);
        var otherId = Apply(TenantOne, Report("m2", Holding(A, true), Holding(B, false)));
        _store.CreatePendingSwitch(TenantOne, annaId, A, B, LicenseSwitchReasons.Manual, T0);
        _store.CreatePendingSwitch(TenantOne, benId, A, B, LicenseSwitchReasons.Manual, T0);
        _store.CreatePendingSwitch(TenantOne, otherId, A, B, LicenseSwitchReasons.Manual, T0);

        var entries = Log(TenantOne).Json.GetProperty("entries").EnumerateArray()
            .ToDictionary(e => e.GetProperty("machineId").GetString()!, e => (Label: e.GetProperty("label").GetString(), Summary: e.GetProperty("summary").GetString()));
        var overview = Overview(TenantOne).Json.GetProperty("machines").EnumerateArray()
            .ToDictionary(m => m.GetProperty("machineId").GetString()!, m => m.GetProperty("label").GetString());

        Assert.Equal(("m1 (anna)", $"m1 (anna) is switching from {A} to {B}."), entries[annaId.ToString("N")]);
        Assert.Equal(("M1 (ben)", $"M1 (ben) is switching from {A} to {B}."), entries[benId.ToString("N")]);
        Assert.Equal(("m2", $"m2 is switching from {A} to {B}."), entries[otherId.ToString("N")]);
        // The same identifiers and the same labels as the overview's machine rows: the page joins on machineId.
        Assert.Equal(overview.OrderBy(p => p.Key).Select(p => (p.Key, p.Value)), entries.OrderBy(p => p.Key).Select(p => (p.Key, p.Value.Label)));
    }

    [Fact]
    public void Log_AnswersTheExactShapeTheLicensesPageIsBuiltFrom()
    {
        SeedTwoAccounts();

        var (status, _, json) = Log(TenantOne);

        Assert.Equal(200, status);
        Assert.Equal(new[] { "asOfUtc", "isEmpty", "emptyText", "entries", "hasMore", "nextCursor" }, Names(json));
        var entry = Assert.Single(json.GetProperty("entries").EnumerateArray().ToList());
        Assert.Equal(new[]
        {
            "entryId", "kind", "kindText", "atUtc", "machineId", "machineName", "osUser", "label", "fromAccountEmail", "toAccountEmail", "why", "whyText",
            "state", "stateText", "stateTone", "isOpen", "reasonCode", "reasonText", "message", "summary", "sendCount", "lastSentAtUtc",
            "startedOnMachineAtUtc", "finishedOnMachineAtUtc", "exchangeMilliseconds", "finishedAtUtc", "timingText", "verifiedByUsageRead",
            "verifiedText",
        }, Names(entry));
        Assert.Equal(("switch", "Switch", "machine-of-one", A, B, "manual", "By hand", "pending", "Waiting to be sent", true, "2026-10-11T03:12:00Z"),
            (entry.GetProperty("kind").GetString(), entry.GetProperty("kindText").GetString(), entry.GetProperty("machineName").GetString(),
             entry.GetProperty("fromAccountEmail").GetString(), entry.GetProperty("toAccountEmail").GetString(), entry.GetProperty("why").GetString(),
             entry.GetProperty("whyText").GetString(), entry.GetProperty("state").GetString(), entry.GetProperty("stateText").GetString(),
             entry.GetProperty("isOpen").GetBoolean(), entry.GetProperty("atUtc").GetString()));
        Assert.False(json.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("nextCursor").ValueKind);
    }

    // ---- one account never sees another's ----

    [Fact]
    public void EveryRoute_AnswersOnlyTheCallersOwnAccount()
    {
        SeedTwoAccounts();
        var ofOne = new[] { A, B, "machine-of-one" };
        var ofTwo = new[] { C, "account-d@example.com", "machine-of-two" };

        foreach (var read in new Func<TenantId?, (int Status, string Text, JsonElement Json)>[] { Overview, Forecast, t => Log(t) })
        {
            var one = read(TenantOne);
            var two = read(TenantTwo);

            Assert.Equal((200, 200), (one.Status, two.Status));
            Assert.All(ofOne, word => Assert.Contains(word, one.Text));
            Assert.All(ofTwo, word => Assert.DoesNotContain(word, one.Text));
            Assert.All(ofTwo, word => Assert.Contains(word, two.Text));
            Assert.All(ofOne, word => Assert.DoesNotContain(word, two.Text));
        }
    }

    [Fact]
    public void Forecast_TheRateOfOneAccount_IsNeverUsedForAnother()
    {
        // The same account email in two DevThrottle accounts is two separate accounts (contract section 6 rule 4).
        Apply(TenantOne, Report("m1", Holding(A, true, 60), Holding(B, false)));
        Apply(TenantTwo, Report("m9", Holding(A, true, 60), Holding(B, false)));
        _history.Record(TenantOne, Report("m1", Holding(A, true, 50, readAt: T0.AddMinutes(-20))), TimeSpan.Zero, T0);
        _history.Record(TenantOne, Report("m1", Holding(A, true, 60)), TimeSpan.Zero, T0);

        var one = Forecast(TenantOne).Json;
        var two = Forecast(TenantTwo).Json;

        Assert.Equal(JsonValueKind.Object, one.GetProperty("nextSwitch").ValueKind);
        Assert.Equal(JsonValueKind.Null, two.GetProperty("nextSwitch").ValueKind);
        Assert.Equal("No rate yet", two.GetProperty("accounts")[0].GetProperty("fiveHour").GetProperty("rateText").GetString());
    }

    [Fact]
    public void EveryRoute_WithNoAccountBoundToTheRequest_IsRefused()
    {
        SeedTwoAccounts();

        foreach (var read in new Func<TenantId?, (int Status, string Text, JsonElement Json)>[] { Overview, Forecast, t => Log(t) })
        {
            var (status, text, json) = read(null);

            Assert.Equal(403, status);
            Assert.Equal("no_account", json.GetProperty("code").GetString());
            Assert.DoesNotContain("example.com", text);
        }
    }

    // ---- an account that never reported ----

    [Fact]
    public void AnAccountNoDirectorHasReportedFor_GetsEmptyAnswersWithASentence_NotAnError()
    {
        Apply(TenantTwo, Report("machine-of-two", Holding(C, true)));

        var overview = Overview(TenantOne);
        var forecast = Forecast(TenantOne);
        var log = Log(TenantOne);

        Assert.Equal((200, 200, 200), (overview.Status, forecast.Status, log.Status));
        Assert.True(overview.Json.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(LicenseDisplayFold.NothingReportedText, overview.Json.GetProperty("emptyText").GetString());
        Assert.Equal((0, 0), (overview.Json.GetProperty("accounts").GetArrayLength(), overview.Json.GetProperty("machines").GetArrayLength()));
        Assert.True(forecast.Json.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(LicenseDisplayFold.NothingReportedText, forecast.Json.GetProperty("emptyText").GetString());
        Assert.Equal(LicenseDisplayFold.NoSwitchExpectedText, forecast.Json.GetProperty("nextSwitchText").GetString());
        Assert.True(log.Json.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(LicenseDisplayFold.EmptyLogText, log.Json.GetProperty("emptyText").GetString());
        Assert.Equal(0, log.Json.GetProperty("entries").GetArrayLength());
    }

    // ---- the log: newest first, paged ----

    private List<string> SeedLog(int switches)
    {
        var machine = Apply(TenantOne, Report("m1", Holding(A, true), Holding(B, false)));
        var newestFirst = new List<string>();
        for (var i = 0; i < switches; i++)
        {
            var switchId = _store.CreatePendingSwitch(TenantOne, machine, A, B, LicenseSwitchReasons.Manual, T0.AddMinutes(i))!.SwitchId!;
            newestFirst.Insert(0, switchId);
            // A machine never has two open switches (contract section 5.4), so each one ends before the next is written.
            Assert.True(_store.TryMark(TenantOne, switchId, LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorError,
                "The Director refused.", T0.AddMinutes(i).AddSeconds(5)));
        }
        _now = T0.AddHours(1);
        return newestFirst;
    }

    private string RowIdOf(string switchId) => _store.FindSwitch(TenantOne, switchId)!.Id.ToString("N");

    private static List<string> EntryIds(JsonElement json) => json.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("entryId").GetString()!).ToList();

    [Fact]
    public void Log_IsNewestFirst_AndPagesWithoutOverlapOrLoss_EvenWhileRowsAreWritten()
    {
        var seeded = SeedLog(5);
        var expected = seeded.Select(RowIdOf).ToList();

        var first = Log(TenantOne, "?limit=2").Json;
        // A switch written while the page is being walked: it belongs to a later walk, never to this one.
        var machine = _store.FindSwitch(TenantOne, seeded[0])!.MachineId;
        _store.CreatePendingSwitch(TenantOne, machine, A, B, LicenseSwitchReasons.Manual, _now.AddMinutes(1));
        _now = _now.AddMinutes(2);
        var second = Log(TenantOne, "?limit=2&cursor=" + first.GetProperty("nextCursor").GetString()).Json;
        var third = Log(TenantOne, "?limit=2&cursor=" + second.GetProperty("nextCursor").GetString()).Json;

        Assert.Equal(expected.Take(2), EntryIds(first));
        Assert.Equal(expected.Skip(2).Take(2), EntryIds(second));
        Assert.Equal(expected.Skip(4), EntryIds(third));
        Assert.Equal((true, true, false), (first.GetProperty("hasMore").GetBoolean(), second.GetProperty("hasMore").GetBoolean(), third.GetProperty("hasMore").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, third.GetProperty("nextCursor").ValueKind);
        Assert.False(second.GetProperty("isEmpty").GetBoolean());
        // A fresh walk sees the new row first.
        Assert.Equal(6, EntryIds(Log(TenantOne).Json).Count);
        Assert.DoesNotContain(EntryIds(Log(TenantOne).Json)[0], expected);
    }

    [Fact]
    public void Log_RowsWrittenAtTheSameMoment_AreNeverSplitWrongAcrossPages()
    {
        // One switch of an account moves several machines: their rows carry the same time.
        var ids = new List<string>();
        foreach (var name in new[] { "m1", "m2", "m3" })
        {
            var machine = Apply(TenantOne, Report(name, Holding(A, true), Holding(B, false)));
            ids.Add(RowIdOf(_store.CreatePendingSwitch(TenantOne, machine, A, B, LicenseSwitchReasons.Threshold, T0)!.SwitchId!));
        }

        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 3; page++)
        {
            var json = Log(TenantOne, "?limit=1" + (cursor is null ? "" : "&cursor=" + cursor)).Json;
            seen.AddRange(EntryIds(json));
            cursor = json.GetProperty("nextCursor").GetString();
        }

        Assert.Null(cursor);
        Assert.Equal(ids.OrderBy(i => i), seen.OrderBy(i => i));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(200, 5)]
    public void Log_TakesAPageSizeFromOneToTwoHundred(int limit, int entries)
    {
        SeedLog(5);

        var (status, _, json) = Log(TenantOne, "?limit=" + limit);

        Assert.Equal(200, status);
        Assert.Equal(entries, json.GetProperty("entries").GetArrayLength());
    }

    [Theory]
    [InlineData("?limit=0", "invalid_limit")]
    [InlineData("?limit=201", "invalid_limit")]
    [InlineData("?limit=-1", "invalid_limit")]
    [InlineData("?limit=ten", "invalid_limit")]
    [InlineData("?cursor=nonsense", "invalid_cursor")]
    [InlineData("?cursor=12.-3", "invalid_cursor")]
    [InlineData("?cursor=12", "invalid_cursor")]
    [InlineData("?cursor=1.2.3", "invalid_cursor")]
    public void Log_APageSizeOrCursorItCannotRead_IsRefused_NotGuessed(string query, string code)
    {
        SeedLog(2);

        var (status, text, json) = Log(TenantOne, query);

        Assert.Equal(400, status);
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.DoesNotContain("entries", text);
    }

    [Fact]
    public void Log_ListsSpareRowsAndARowOfAKindThisBuildDoesNotKnow_AndNeverFailsOnIt()
    {
        Apply(TenantOne, Report("m1", Holding(A, true), Holding(B, false)));
        Apply(TenantOne, Report("m1", T0.AddMinutes(2), Holding(A, true), Holding(C, false)), seq: 2, at: T0.AddMinutes(2));
        using (var ctx = _db.CreateContext(TenantOne))
        {
            ctx.LicenseSwitchLog.Add(new LicenseSwitchLogEntity
            {
                Kind = LicenseDisplayFold.NoRoomKind, FromAccountEmail = A, State = "open",
                Message = "No account had room for the machines on account-a@example.com. You were emailed.",
                CreatedAtUtc = T0.AddMinutes(4), TenantId = ctx.ActiveTenant!,
            });
            ctx.LicenseSwitchLog.Add(new LicenseSwitchLogEntity
            {
                Kind = "a-kind-from-a-later-build", State = "a-later-state", Message = "Something a later build wrote.",
                CreatedAtUtc = T0.AddMinutes(6), TenantId = ctx.ActiveTenant!,
            });
            ctx.SaveChanges();
        }
        _now = T0.AddMinutes(10);

        var (status, _, json) = Log(TenantOne);
        var entries = json.GetProperty("entries").EnumerateArray().ToList();

        Assert.Equal(200, status);
        Assert.Equal(4, entries.Count);
        Assert.Equal(("a-kind-from-a-later-build", "Note", "Something a later build wrote."),
            (entries[0].GetProperty("kind").GetString(), entries[0].GetProperty("kindText").GetString(), entries[0].GetProperty("summary").GetString()));
        Assert.Equal(("no-room", "No account had room", true),
            (entries[1].GetProperty("kind").GetString(), entries[1].GetProperty("kindText").GetString(), entries[1].GetProperty("isOpen").GetBoolean()));
        Assert.Equal(new[] { "Spare added", "Spare removed" }, entries.Skip(2).Select(e => e.GetProperty("kindText").GetString()).OrderBy(t => t));
        Assert.All(entries.Skip(2), e => Assert.Equal("Recorded", e.GetProperty("stateText").GetString()));
    }

    [Fact]
    public void LogCursor_IsReadBackExactlyAsItWasWritten()
    {
        var cursor = new LicenseEndpoints.LogCursor(T0.AddTicks(7), 150);

        Assert.True(LicenseEndpoints.LogCursor.TryParse(cursor.ToString(), out var parsed));
        Assert.Equal(cursor, parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.UntilUtc.Kind);
    }
}
