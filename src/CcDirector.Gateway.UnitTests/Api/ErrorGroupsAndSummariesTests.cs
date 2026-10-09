using System.Text.Json;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// Issue #3675, phase 1 of the Error Logging mission: the new optional fields on the report, the fingerprint
/// stamped once by the store, the grouped read, and the retention the owner set - "Full reports for 90 days, plus a
/// small per-problem summary kept for good (count, first and last seen, linked issue - no message text)."
/// </summary>
public sealed class ErrorGroupsAndSummariesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "error-groups-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ErrorReportStore NewStore(DateTime? now = null) => new(_root, () => now ?? Now);

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string UniqueDevice() => "dev-" + Guid.NewGuid().ToString("N")[..10];

    private static ErrorReportItem Item(string message = "Prompt delivery FAILED for session 2c3c4215: no answer",
        string component = "director", bool? userVisible = null, string? action = null, string? surface = null,
        string? correlationId = null, int? httpStatus = null, string? errorCode = null, string? sessionId = null,
        DateTime? firstSeen = null, DateTime? lastSeen = null, int repeat = 1) => new(
        Component: component, Source: "Session", Kind: "logged", Message: message,
        ExceptionType: null, Stack: null, RepeatCount: repeat,
        FirstSeenUtc: firstSeen ?? Now.AddMinutes(-5), LastSeenUtc: lastSeen ?? Now.AddMinutes(-1), ProductVersion: "2.18.0",
        Os: "windows", OsVersion: "10.0.26200", Arch: "x64", MachineId: ErrorReportMachineId.Of("devthrottle-pc"),
        UserVisible: userVisible, Surface: surface, Action: action, CorrelationId: correlationId,
        HttpStatus: httpStatus, ErrorCode: errorCode, SessionId: sessionId);

    private static ErrorReportBatch Batch(params ErrorReportItem[] items) => new(items);

    private static ErrorReportQuery Everything(string? account = null) => new(Now.AddDays(-89), Now.AddMinutes(1), Account: account);

    private static JsonElement Json(string text) => JsonSerializer.Deserialize<JsonElement>(text);

    // ---- The contract ------------------------------------------------------------------------------------------

    [Fact]
    public void Components_IsTheOneListOfEverySurface()
    {
        Assert.Equal(
            ["director", "launcher", "install", "tool", "cockpit", "mobile", "gateway", "gateway-app", "website"],
            ErrorReportLimits.Components);
    }

    [Fact]
    public void ReportedComponents_LeavesOutTheInstallerAndTheGatewayItself()
    {
        Assert.DoesNotContain(ErrorReportLimits.Install, ErrorReportLimits.ReportedComponents);
        Assert.DoesNotContain(ErrorReportLimits.Gateway, ErrorReportLimits.ReportedComponents);
        Assert.Equal(ErrorReportLimits.Components.Count - 2, ErrorReportLimits.ReportedComponents.Count);
        Assert.All(ErrorReportLimits.DeviceComponents, c => Assert.Contains(c, ErrorReportLimits.ReportedComponents));
    }

    [Theory]
    [InlineData("cockpit")]
    [InlineData("mobile")]
    [InlineData("tool")]
    [InlineData("gateway-app")]
    [InlineData("website")]
    public void HandlePost_ANewSurfaceComponent_IsAccepted(string component)
    {
        var store = NewStore();

        Assert.Equal(StatusCodes.Status202Accepted,
            Status(DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item(component: component)), Now)));
        Assert.Equal(component, Assert.Single(store.Query(Everything()).Records).Component);
    }

    [Fact]
    public void HandlePost_ABatchFromAnOlderDirectorWithoutTheNewFields_IsStoredUnchanged()
    {
        // The exact wire shape a Director built before #3675 sends: none of the new fields at all.
        const string oldWire = """
            {"reports":[{"component":"director","source":"SessionManager","kind":"logged","message":"Save FAILED: disk full",
            "exception_type":null,"stack":null,"repeat_count":1,"first_seen_utc":"2026-10-08T11:00:00Z",
            "last_seen_utc":"2026-10-08T11:01:00Z","product_version":"2.17.0","os":"windows","os_version":"10",
            "arch":"x64","machine_id":"0123456789abcdef"}]}
            """;
        var batch = JsonSerializer.Deserialize<ErrorReportBatch>(oldWire);
        var store = NewStore();

        Assert.Equal(StatusCodes.Status202Accepted, Status(DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), batch, Now)));

        var record = Assert.Single(store.Query(Everything()).Records);
        Assert.Null(record.UserVisible);
        Assert.Null(record.Action);
        Assert.Null(record.CorrelationId);
        Assert.Null(record.HttpStatus);
        Assert.True(ErrorFingerprint.IsFingerprint(record.Fingerprint!));
    }

    [Fact]
    public void HandlePost_TheNewFields_AreStored()
    {
        var store = NewStore();

        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item(
            userVisible: true, surface: "phone chat", action: "send a prompt to the session",
            correlationId: "cmd-a1b2c3", httpStatus: 504, errorCode: "director_timeout",
            sessionId: "2c3c4215-3a02-43f0-9cf6-f8ca4d872347")), Now);

        var record = Assert.Single(store.Query(Everything()).Records);
        Assert.True(record.UserVisible);
        Assert.Equal("phone chat", record.Surface);
        Assert.Equal("send a prompt to the session", record.Action);
        Assert.Equal("cmd-a1b2c3", record.CorrelationId);
        Assert.Equal(504, record.HttpStatus);
        Assert.Equal("director_timeout", record.ErrorCode);
        Assert.Equal("2c3c4215-3a02-43f0-9cf6-f8ca4d872347", record.SessionId);
    }

    [Fact]
    public void HandlePost_TheNewFreeTextFields_AreScrubbedWhateverTheClientSent()
    {
        var store = NewStore();

        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item(
            action: @"open C:\Users\robert\notes.txt token=abc123def",
            surface: "/Users/robert/screen",
            errorCode: "password=hunter2secret",
            correlationId: "api_key=zzz999yyy",
            sessionId: "/home/robert/x")), Now);

        var record = Assert.Single(store.Query(Everything()).Records);
        var all = string.Join("|", record.Action, record.Surface, record.ErrorCode, record.CorrelationId, record.SessionId);
        Assert.DoesNotContain("robert", all);
        Assert.DoesNotContain("abc123def", all);
        Assert.DoesNotContain("hunter2secret", all);
        Assert.DoesNotContain("zzz999yyy", all);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    public void HandlePost_AnHttpStatusOutsideTheRange_IsRefused(int status)
    {
        var store = NewStore();

        Assert.Equal(StatusCodes.Status400BadRequest,
            Status(DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item(httpStatus: status)), Now)));
        Assert.Empty(store.Query(Everything()).Records);
    }

    [Fact]
    public void TryBuildQuery_EveryComponentInTheContract_IsAValidFilter()
    {
        foreach (var component in ErrorReportLimits.Components)
        {
            var q = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["component"] = component });
            Assert.True(DirectorErrorEndpoints.TryBuildQuery(q, Now, out var query, out _), component);
            Assert.Equal(component, query.Component);
        }
    }

    // ---- The fingerprint, stamped by the store -----------------------------------------------------------------

    [Fact]
    public void Append_StampsTheFingerprint_ReplacingWhateverTheWriterSet()
    {
        var store = NewStore();
        var record = new ErrorReportRecord
        {
            ReceivedUtc = Now, Component = "director", Source = "Session", Message = "Save FAILED", Fingerprint = "ffffffffffffffff",
        };

        store.Append([record]);

        var stored = Assert.Single(store.Query(Everything()).Records);
        Assert.Equal(ErrorFingerprint.Of("director", "Session", null, "Save FAILED"), stored.Fingerprint);
        Assert.Equal(ErrorFingerprint.RulesVersion, stored.FingerprintRules);
    }

    [Fact]
    public void Query_ARecordStoredBeforeFingerprints_IsGivenOneAsItIsRead()
    {
        var day = Path.Combine(_root, "2026-10-08");
        Directory.CreateDirectory(day);
        File.WriteAllText(Path.Combine(day, "old.jsonl"),
            """{"received_utc":"2026-10-08T11:00:00Z","component":"director","source":"Session","message":"Save FAILED"}""" + "\n");

        var stored = Assert.Single(NewStore().Query(Everything()).Records);

        Assert.Equal(ErrorFingerprint.Of("director", "Session", null, "Save FAILED"), stored.Fingerprint);
        Assert.Equal(ErrorFingerprint.RulesVersion, stored.FingerprintRules);
    }

    [Fact]
    public void Append_TheHttpStatusAndErrorCode_AreInTheFingerprint()
    {
        // The same words from two different Gateway answers are two problems (review of pull request 3724).
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(
            Item("Send FAILED", httpStatus: 403, errorCode: "forbidden"),
            Item("Send FAILED", httpStatus: 503, errorCode: "forbidden"),
            Item("Send FAILED", httpStatus: 503, errorCode: "director_timeout"),
            Item("Send FAILED", httpStatus: 503, errorCode: "director_timeout")), Now);

        var page = store.Group(Everything());

        Assert.Equal(3, page.TotalGroups);
        Assert.Equal(2, page.Groups[0].Count);
    }

    // ---- The grouped read --------------------------------------------------------------------------------------

    [Fact]
    public void Group_TheSameFailureOnTwoSessions_IsOneProblem()
    {
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(
            Item("Prompt delivery FAILED for session 2c3c4215: no answer", userVisible: true,
                firstSeen: Now.AddHours(-3), lastSeen: Now.AddHours(-2), repeat: 2),
            Item("Prompt delivery FAILED for session 9f8eab7d: no answer", userVisible: false,
                firstSeen: Now.AddHours(-1), lastSeen: Now.AddMinutes(-10)),
            Item("Save FAILED: disk full")), Now);

        var page = store.Group(Everything());

        Assert.Equal(2, page.TotalGroups);
        Assert.Equal(3, page.TotalReports);
        var delivery = page.Groups[0];
        Assert.Equal(2, delivery.Count);
        Assert.Equal(3, delivery.Occurrences);
        Assert.Equal(1, delivery.UserVisible);
        Assert.Equal(Now.AddHours(-3), delivery.FirstSeenUtc);
        Assert.Equal(Now.AddMinutes(-10), delivery.LastSeenUtc);
        Assert.Equal("director", delivery.Component);
        Assert.Equal("Session", delivery.Source);
        Assert.Contains("Prompt delivery FAILED", delivery.SampleMessage);
        Assert.Equal(1, page.Groups[1].Count);
    }

    [Fact]
    public void Group_OneProblemOnTwoMachinesAndTwoVersions_SaysHowManyMachinesAndWhichVersions()
    {
        // Issue #3646: the grouped read says how many machines and which versions a problem reached.
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(
            Item() with { MachineId = ErrorReportMachineId.Of("pc-one"), ProductVersion = "2.18.0" },
            Item() with { MachineId = ErrorReportMachineId.Of("pc-two"), ProductVersion = "2.17.1" },
            Item() with { MachineId = ErrorReportMachineId.Of("pc-two"), ProductVersion = "2.18.0" }), Now);

        var group = Assert.Single(store.Group(Everything()).Groups);

        Assert.Equal(2, group.Machines);
        Assert.Equal(["2.17.1", "2.18.0"], group.Versions);
    }

    [Fact]
    public void Group_AnAccountFilter_NeverCountsAnotherAccountsReports()
    {
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item()), Now);
        DirectorErrorEndpoints.HandlePost(store, TenantB, UniqueDevice(), Batch(Item(), Item()), Now);

        var page = store.Group(Everything(TenantA.Value));

        Assert.Equal(1, Assert.Single(page.Groups).Count);
    }

    [Fact]
    public void Group_TheLimitCountsProblems_NotReports()
    {
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item("Save FAILED"), Item("Load FAILED"), Item("Sync FAILED")), Now);

        var page = store.Group(Everything() with { Limit = 2 });

        Assert.Equal(2, page.Groups.Count);
        Assert.Equal(3, page.TotalGroups);
    }

    [Fact]
    public void SessionKey_MayReadItsAccountsGroups_ButNoAdministratorRoute()
    {
        Assert.True(SessionKeyGuard.Check("GET", DirectorErrorEndpoints.GroupsPath).Allowed);
        Assert.False(SessionKeyGuard.Check("GET", DirectorErrorEndpoints.AdminGroupsPath).Allowed);
        Assert.False(SessionKeyGuard.Check("GET", DirectorErrorEndpoints.AdminSummariesPath).Allowed);
        Assert.False(SessionKeyGuard.Check("PUT", DirectorErrorEndpoints.AdminLinkedIssuePath).Allowed);
    }

    [Fact]
    public void AuthMiddleware_ExemptsTheAdministratorRoutes_ButNotTheAccountGroupedRead()
    {
        var field = typeof(AuthMiddleware).GetField("PublicPaths",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var publicPaths = ((IEnumerable<string>)field.GetValue(null)!).ToList();

        Assert.Contains(DirectorErrorEndpoints.AdminGroupsPath, publicPaths);
        Assert.Contains(DirectorErrorEndpoints.AdminSummariesPath, publicPaths);
        Assert.Contains(DirectorErrorEndpoints.AdminLinkedIssuePath, publicPaths);
        Assert.DoesNotContain(DirectorErrorEndpoints.GroupsPath, publicPaths);
    }

    // ---- Retention and the summaries kept for good -------------------------------------------------------------

    [Fact]
    public void RetentionDays_IsNinety()
    {
        Assert.Equal(90, ErrorReportStore.RetentionDays);
    }

    [Fact]
    public void Prune_AFolderEightyNineDaysOld_IsKept_AndOneNinetyOneDaysOld_IsDeleted()
    {
        Directory.CreateDirectory(Path.Combine(_root, "2026-07-11"));
        Directory.CreateDirectory(Path.Combine(_root, "2026-07-09"));

        NewStore().Prune(Now);

        Assert.True(Directory.Exists(Path.Combine(_root, "2026-07-11")));
        Assert.False(Directory.Exists(Path.Combine(_root, "2026-07-09")));
    }

    [Fact]
    public void Summary_SurvivesThePruneOfEveryReportItCounted()
    {
        var old = Now.AddDays(-120);
        DirectorErrorEndpoints.HandlePost(NewStore(old), TenantA, UniqueDevice(),
            Batch(Item(firstSeen: old.AddMinutes(-5), lastSeen: old.AddMinutes(-1), repeat: 4)), old);
        var fingerprint = Assert.Single(NewStore().Summaries()).Fingerprint;

        Assert.Equal(1, NewStore().Prune(Now));

        Assert.Empty(NewStore().Query(new ErrorReportQuery(Now.AddYears(-1), Now)).Records);
        var summary = Assert.Single(NewStore().Summaries());
        Assert.Equal(fingerprint, summary.Fingerprint);
        Assert.Equal(1, summary.Count);
        Assert.Equal(4, summary.Occurrences);
        Assert.Equal(old.AddMinutes(-5), summary.FirstSeenUtc);
        Assert.Equal(old.AddMinutes(-1), summary.LastSeenUtc);
    }

    [Fact]
    public void Summary_CountsAcrossBatches_AndWidensFirstAndLastSeen()
    {
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(),
            Batch(Item("Prompt delivery FAILED for session 2c3c4215: no answer", firstSeen: Now.AddHours(-5), lastSeen: Now.AddHours(-4))), Now);
        DirectorErrorEndpoints.HandlePost(store, TenantB, UniqueDevice(),
            Batch(Item("Prompt delivery FAILED for session 9f8eab7d: no answer", firstSeen: Now.AddHours(-1), lastSeen: Now.AddMinutes(-2))), Now);

        var summary = Assert.Single(store.Summaries());

        Assert.Equal(2, summary.Count);
        Assert.Equal(Now.AddHours(-5), summary.FirstSeenUtc);
        Assert.Equal(Now.AddMinutes(-2), summary.LastSeenUtc);
    }

    [Fact]
    public void Summary_OnDisk_HoldsNoMessageTextNoAccountAndNoMachine()
    {
        DirectorErrorEndpoints.HandlePost(NewStore(), TenantA, UniqueDevice(), Batch(Item("Prompt delivery FAILED: distinctive-words-here")), Now);

        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ErrorReportStore.SummaryFolder)));
        var text = File.ReadAllText(file);
        var keys = Json(text).EnumerateObject().Select(p => p.Name).ToList();

        Assert.DoesNotContain("distinctive", text);
        Assert.DoesNotContain(TenantA.Value, text);
        Assert.DoesNotContain(ErrorReportMachineId.Of("devthrottle-pc"), text);
        Assert.Equal(["fingerprint", "fingerprint_rules", "component", "source", "count", "occurrences", "first_seen_utc", "last_seen_utc"], keys);
    }

    [Fact]
    public void Summary_AFileThatDoesNotParse_IsAFailureNotAFreshStart()
    {
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item()), Now);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ErrorReportStore.SummaryFolder)));
        File.WriteAllText(file, "{ not json");

        Assert.ThrowsAny<JsonException>(() => store.Summaries());
    }

    [Fact]
    public void Summary_AFileThatDoesNotParse_FailsNamingTheFile()
    {
        // One unreadable summary stops every grouped read until it is repaired by hand on the share, so the failure
        // must say WHICH file - the review of pull request 3724 found the parser's own message did not.
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item()), Now);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ErrorReportStore.SummaryFolder)));
        File.WriteAllText(file, "{ not json");

        var ex = Assert.ThrowsAny<JsonException>(() => store.Group(Everything()));

        Assert.Contains(file, ex.Message);
    }

    [Fact]
    public void Summary_CarriesTheFingerprintRulesVersion()
    {
        DirectorErrorEndpoints.HandlePost(NewStore(), TenantA, UniqueDevice(), Batch(Item()), Now);

        var summary = Assert.Single(NewStore().Summaries());

        Assert.Equal(ErrorFingerprint.RulesVersion, summary.FingerprintRules);
    }

    // ---- Linking a problem to its work item ------------------------------------------------------------------

    private string StoreOneProblem(ErrorReportStore store)
    {
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item()), Now);
        return Assert.Single(store.Summaries()).Fingerprint;
    }

    [Theory]
    [InlineData("#3675")]
    [InlineData("thefrederiksen/devthrottle#3675")]
    [InlineData("https://github.com/thefrederiksen/devthrottle/issues/3675")]
    public void HandleSetLinkedIssue_AWorkItem_IsKeptOnTheSummary_AndShownOnTheGroup(string issue)
    {
        var store = NewStore();
        var fingerprint = StoreOneProblem(store);

        var result = DirectorErrorEndpoints.HandleSetLinkedIssue(store,
            Json(JsonSerializer.Serialize(new { fingerprint, linked_issue = issue })));

        Assert.Equal(200, Status(result));
        Assert.Equal(issue, Assert.Single(store.Summaries()).LinkedIssue);
        Assert.Equal(issue, Assert.Single(store.Group(Everything()).Groups).LinkedIssue);
    }

    [Fact]
    public void HandleSetLinkedIssue_Null_ClearsTheLink()
    {
        var store = NewStore();
        var fingerprint = StoreOneProblem(store);
        store.SetLinkedIssue(fingerprint, "#1");

        DirectorErrorEndpoints.HandleSetLinkedIssue(store, Json($$"""{"fingerprint":"{{fingerprint}}","linked_issue":null}"""));

        Assert.Null(Assert.Single(store.Summaries()).LinkedIssue);
    }

    [Fact]
    public void HandleSetLinkedIssue_AFingerprintNeverStored_IsA404()
    {
        var store = NewStore();
        StoreOneProblem(store);

        var result = DirectorErrorEndpoints.HandleSetLinkedIssue(store, Json("""{"fingerprint":"0123456789abcdef","linked_issue":"#1"}"""));

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        Assert.Single(store.Summaries());
    }

    [Theory]
    [InlineData("""{"fingerprint":"../../etc/passwd","linked_issue":"#1"}""")]
    [InlineData("""{"fingerprint":"0123456789abcdef","linked_issue":"see the chat"}""")]
    [InlineData("""{"fingerprint":"0123456789abcdef","linked_issue":3675}""")]
    [InlineData("""{"fingerprint":"0123456789abcdef"}""")]
    [InlineData("""{"linked_issue":"#1"}""")]
    [InlineData("""[]""")]
    public void HandleSetLinkedIssue_AMalformedBody_IsA400(string body)
    {
        var store = NewStore();
        StoreOneProblem(store);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(DirectorErrorEndpoints.HandleSetLinkedIssue(store, Json(body))));
        Assert.Null(Assert.Single(store.Summaries()).LinkedIssue);
    }

    [Fact]
    public void SetLinkedIssue_AStalledStore_AnswersBusy()
    {
        var store = NewStore();
        var fingerprint = StoreOneProblem(store);

        var holder = new Thread(() =>
        {
            using (store.HoldWriteLockForTests()) Thread.Sleep(ErrorReportStore.WriteLockTimeout + TimeSpan.FromSeconds(1));
        });
        holder.Start();
        Thread.Sleep(200);

        var result = DirectorErrorEndpoints.HandleSetLinkedIssue(store, Json($$"""{"fingerprint":"{{fingerprint}}","linked_issue":"#1"}"""));
        holder.Join();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Status(result));
    }
}
