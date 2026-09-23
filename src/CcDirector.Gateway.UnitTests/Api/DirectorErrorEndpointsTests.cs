using System.Text.Json;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// Issue #3311: Director and launcher errors on the Gateway. The store must survive a restart (it is files
/// on the durable root, not memory), an account must only ever read its own errors, and every bound on the
/// report route must hold whatever the client sent.
/// </summary>
public sealed class DirectorErrorEndpointsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId TenantA = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId TenantB = new("22222222-2222-2222-2222-222222222222");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "error-reports-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private ErrorReportStore NewStore(DateTime? now = null) => new(_root, () => now ?? Now);

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static string UniqueDevice() => "dev-" + Guid.NewGuid().ToString("N")[..10];

    private static ErrorReportItem Item(string message = "Save FAILED: disk full", string component = "director",
        string? machineId = null, string version = "2.9.0", string? stack = null) => new(
        Component: component, Source: "SessionManager", Kind: "logged", Message: message,
        ExceptionType: "System.IO.IOException", Stack: stack, RepeatCount: 3,
        FirstSeenUtc: Now.AddMinutes(-5), LastSeenUtc: Now.AddMinutes(-1), ProductVersion: version,
        Os: "macos", OsVersion: "Darwin 24.6.0", Arch: "arm64", MachineId: machineId ?? ErrorReportMachineId.Of("devthrottle-mac-mini"));

    private static ErrorReportBatch Batch(params ErrorReportItem[] items) => new(items);

    private static ErrorReportQuery Everything(string? account = null) => new(Now.AddDays(-1), Now.AddMinutes(1), Account: account);

    [Fact]
    public void HandlePost_AValidBatch_IsStoredUnderTheCallersAccount()
    {
        var store = NewStore();

        var result = DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item()), Now);

        Assert.Equal(StatusCodes.Status202Accepted, Status(result));
        var record = Assert.Single(store.Query(Everything()).Records);
        Assert.Equal(TenantA.Value, record.Account);
        Assert.Equal("director", record.Component);
        Assert.Equal(3, record.RepeatCount);
        Assert.Equal("System.IO.IOException", record.ExceptionType);
    }

    [Fact]
    public void Store_SurvivesARestart_BecauseItIsFilesNotMemory()
    {
        DirectorErrorEndpoints.HandlePost(NewStore(), TenantA, UniqueDevice(), Batch(Item()), Now);

        // A new store over the same root is what a redeployed Gateway is.
        var afterDeploy = NewStore();

        Assert.Single(afterDeploy.Query(Everything()).Records);
    }

    [Fact]
    public void HandlePost_ScrubsAgainWhateverTheClientSent()
    {
        var store = NewStore();

        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(),
            Batch(Item(message: "open /Users/robert/secret.txt FAILED token=abc123def", stack: @"at C:\Users\robert\x.cs")), Now);

        var record = Assert.Single(store.Query(Everything()).Records);
        Assert.DoesNotContain("robert", record.Message);
        Assert.DoesNotContain("abc123def", record.Message);
        Assert.DoesNotContain("robert", record.Stack);
    }

    [Fact]
    public void Query_AnAccountFilter_NeverReturnsAnotherAccountsErrors()
    {
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item("A FAILED")), Now);
        DirectorErrorEndpoints.HandlePost(store, TenantB, UniqueDevice(), Batch(Item("B FAILED")), Now);

        var page = store.Query(Everything(TenantA.Value));

        var record = Assert.Single(page.Records);
        Assert.Equal("A FAILED", record.Message);
        Assert.Equal(2, store.Query(Everything()).TotalMatched);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("gateway")]
    [InlineData("")]
    public void HandlePost_AComponentADeviceMayNotReportAs_IsRefused(string component)
    {
        var store = NewStore();

        Assert.Equal(StatusCodes.Status400BadRequest,
            Status(DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item(component: component)), Now)));
        Assert.Empty(store.Query(Everything()).Records);
    }

    [Fact]
    public void HandlePost_AMachineNameInsteadOfItsHash_IsRefused()
    {
        var store = NewStore();

        var result = DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item(machineId: "SOREN_NORTH")), Now);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
    }

    [Fact]
    public void HandlePost_TooManyReportsInOneBatch_IsRefused()
    {
        var items = Enumerable.Range(0, ErrorReportLimits.MaxReportsPerBatch + 1).Select(i => Item($"E{i} FAILED")).ToArray();

        Assert.Equal(StatusCodes.Status400BadRequest,
            Status(DirectorErrorEndpoints.HandlePost(NewStore(), TenantA, UniqueDevice(), Batch(items), Now)));
    }

    [Fact]
    public void HandlePost_OneDeviceOverItsHourlyLimit_IsRateLimited()
    {
        var store = NewStore();
        var device = UniqueDevice();
        var batch = Batch(Enumerable.Range(0, ErrorReportLimits.MaxReportsPerBatch).Select(i => Item($"E{i} FAILED")).ToArray());
        var accepted = 0;
        IResult last = Results.Ok();
        for (var i = 0; i < 10; i++)
        {
            last = DirectorErrorEndpoints.HandlePost(store, TenantA, device, batch, Now);
            if (Status(last) == StatusCodes.Status202Accepted) accepted += ErrorReportLimits.MaxReportsPerBatch;
        }

        Assert.True(accepted <= DirectorErrorEndpoints.MaxReportsPerDevicePerHour);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Status(last));
        // Another device is unaffected.
        Assert.Equal(StatusCodes.Status202Accepted, Status(DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item()), Now)));
    }

    [Fact]
    public void Query_FiltersByMachineVersionAndComponent()
    {
        var store = NewStore();
        var mac = ErrorReportMachineId.Of("devthrottle-mac-mini");
        var linux = ErrorReportMachineId.Of("devlinux");
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(
            Item("one FAILED", machineId: mac, version: "2.9.0"),
            Item("two FAILED", machineId: linux, version: "2.8.1"),
            Item("three FAILED", component: "launcher", machineId: mac, version: "2.9.1")), Now);

        Assert.Equal(2, store.Query(Everything() with { MachineId = mac }).TotalMatched);
        Assert.Equal(2, store.Query(Everything() with { ProductVersion = "2.9" }).TotalMatched);
        var launcher = store.Query(Everything() with { Component = "launcher" });
        Assert.Equal("three FAILED", Assert.Single(launcher.Records).Message);
        Assert.Equal(2, store.Query(Everything()).ByComponent["director"]);
    }

    [Fact]
    public void Query_ATimeRange_ReadsOnlyThoseDays()
    {
        var yesterday = NewStore(Now.AddDays(-1));
        DirectorErrorEndpoints.HandlePost(yesterday, TenantA, UniqueDevice(), Batch(Item("old FAILED")), Now.AddDays(-1));
        DirectorErrorEndpoints.HandlePost(NewStore(), TenantA, UniqueDevice(), Batch(Item("new FAILED")), Now);

        var recent = NewStore().Query(new ErrorReportQuery(Now.AddHours(-1), Now.AddMinutes(1)));

        Assert.Equal("new FAILED", Assert.Single(recent.Records).Message);
    }

    [Fact]
    public void Query_ALineCutShort_IsSkippedNotFatal()
    {
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item()), Now);
        var file = Directory.GetFiles(Path.Combine(_root, "2026-09-22"), "*.jsonl").Single();
        File.AppendAllText(file, "{\"received_utc\":\"2026-09-22T12:00:00Z\",\"comp");

        Assert.Single(store.Query(Everything()).Records);
    }

    [Fact]
    public void Append_AfterALineCutShort_StartsOnAFreshLine_SoTheRetryIsKept()
    {
        // A write cut off part way leaves a line with no ending; the retry that follows must not be glued onto it.
        var store = NewStore();
        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item()), Now);
        var file = Directory.GetFiles(Path.Combine(_root, "2026-09-22"), "*.jsonl").Single();
        File.AppendAllText(file, "{\"received_utc\":\"2026-09-22T12:00:00Z\",\"comp");

        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item("Retry FAILED: after the cut")), Now);

        var records = store.Query(Everything()).Records;
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Message == "Retry FAILED: after the cut");
    }

    [Fact]
    public void Append_TwoGatewayProcesses_WriteSeparateFiles()
    {
        // During a deploy two containers share the storage mount; one file each is what stops them
        // clobbering each other's records.
        DirectorErrorEndpoints.HandlePost(NewStore(), TenantA, UniqueDevice(), Batch(Item("one FAILED")), Now);
        DirectorErrorEndpoints.HandlePost(NewStore(), TenantA, UniqueDevice(), Batch(Item("two FAILED")), Now);

        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "2026-09-22"), "*.jsonl").Length);
        Assert.Equal(2, NewStore().Query(Everything()).TotalMatched);
    }

    [Fact]
    public void Prune_DeletesOnlyDateFoldersOlderThanRetention()
    {
        Directory.CreateDirectory(Path.Combine(_root, "2026-07-01"));
        Directory.CreateDirectory(Path.Combine(_root, "2026-09-20"));
        Directory.CreateDirectory(Path.Combine(_root, "not-a-date"));

        var deleted = NewStore().Prune(Now);

        Assert.Equal(1, deleted);
        Assert.False(Directory.Exists(Path.Combine(_root, "2026-07-01")));
        Assert.True(Directory.Exists(Path.Combine(_root, "2026-09-20")));
        Assert.True(Directory.Exists(Path.Combine(_root, "not-a-date")));
    }

    [Theory]
    [InlineData("6h", 6)]
    [InlineData("30m", 0.5)]
    [InlineData("2d", 48)]
    public void TryParseMoment_AnAge_CountsBackFromNow(string text, double hours)
    {
        Assert.True(DirectorErrorEndpoints.TryParseMoment(text, Now, out var utc));
        Assert.Equal(Now.AddHours(-hours), utc);
    }

    [Fact]
    public void TryBuildQuery_AMachineName_IsHashedToMatchTheStore()
    {
        var q = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["machine"] = "devthrottle-mac-mini" });

        Assert.True(DirectorErrorEndpoints.TryBuildQuery(q, Now, out var query, out _));
        Assert.Equal(ErrorReportMachineId.Of("devthrottle-mac-mini"), query.MachineId);
        Assert.Equal(Now.AddHours(-24), query.SinceUtc);
    }

    [Theory]
    [InlineData("since", "yesterday-ish")]
    [InlineData("limit", "0")]
    [InlineData("limit", "many")]
    [InlineData("limit", "1000")]
    [InlineData("component", "gateway")]
    public void TryBuildQuery_AMalformedFilter_IsA400NotIgnored(string key, string value)
    {
        var q = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { [key] = value });

        Assert.False(DirectorErrorEndpoints.TryBuildQuery(q, Now, out _, out var bad));
        Assert.Equal(StatusCodes.Status400BadRequest, Status(bad));
    }

    [Fact]
    public void SessionKey_MayReadItsAccountsErrors_ButNeverReportThem()
    {
        Assert.True(SessionKeyGuard.Check("GET", DirectorErrorEndpoints.Path).Allowed);
        Assert.False(SessionKeyGuard.Check("POST", DirectorErrorEndpoints.Path).Allowed);
        Assert.False(SessionKeyGuard.Check("GET", DirectorErrorEndpoints.AdminPath).Allowed);
    }

    [Fact]
    public void AuthMiddleware_ExemptsOnlyTheAdminRead_WhichCarriesItsOwnServiceTokenGate()
    {
        var field = typeof(AuthMiddleware).GetField("PublicPaths",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var publicPaths = ((IEnumerable<string>)field.GetValue(null)!).ToList();

        Assert.Contains(DirectorErrorEndpoints.AdminPath, publicPaths);
        Assert.DoesNotContain(DirectorErrorEndpoints.Path, publicPaths);
    }

    [Fact]
    public void Store_WritesNoNullFieldsForADirectorRecord()
    {
        // Read the line the STORE wrote, so this watches the store's own serializer settings.
        DirectorErrorEndpoints.HandlePost(NewStore(), TenantA, UniqueDevice(), Batch(Item()), Now);

        var line = File.ReadAllLines(Directory.GetFiles(Path.Combine(_root, "2026-09-22"), "*.jsonl").Single()).Single();

        Assert.DoesNotContain("\"diagnostics\"", line);
        Assert.DoesNotContain("\"step\"", line);
        Assert.Contains("\"component\":\"director\"", line);
    }

    [Fact]
    public void HandlePost_AStalledStore_AnswersBusyQuickly_AndRefundsTheAllowance()
    {
        // A write stuck on a stalled share holds the lock. Every other report must be refused at once rather
        // than park a Gateway thread behind it - and must not use up the device's hourly allowance.
        var store = NewStore();
        var device = UniqueDevice();
        var full = Batch(Enumerable.Range(0, ErrorReportLimits.MaxReportsPerBatch).Select(i => Item($"E{i} FAILED")).ToArray());
        IResult result = Results.Ok();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // The lock is thread-affine, so it is taken and released on this thread and the write runs on another.
        using (store.HoldWriteLockForTests())
        {
            var writer = new Thread(() => result = DirectorErrorEndpoints.HandlePost(store, TenantA, device, full, Now));
            writer.Start();
            writer.Join();
        }
        clock.Stop();

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, Status(result));
        Assert.True(clock.Elapsed < ErrorReportStore.WriteLockTimeout + TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
        Assert.Empty(store.Query(Everything()).Records);
        // The refused batch was refunded: the device still has its whole allowance.
        var accepted = 0;
        for (var i = 0; i < DirectorErrorEndpoints.MaxReportsPerDevicePerHour / ErrorReportLimits.MaxReportsPerBatch; i++)
            if (Status(DirectorErrorEndpoints.HandlePost(store, TenantA, device, full, Now)) == StatusCodes.Status202Accepted) accepted++;
        Assert.Equal(DirectorErrorEndpoints.MaxReportsPerDevicePerHour / ErrorReportLimits.MaxReportsPerBatch, accepted);
    }

    [Fact]
    public void HandlePost_AnInvalidBatch_IsStillCharged()
    {
        // An invalid batch was scrubbed before it was refused, so its sender has spent that cost. If it were
        // refunded, a device could repeat it without limit and the per-device limit would never engage.
        var store = NewStore();
        var device = UniqueDevice();
        var bad = Batch(Enumerable.Range(0, ErrorReportLimits.MaxReportsPerBatch).Select(_ => Item(machineId: "NOT-A-HASH")).ToArray());
        var fits = DirectorErrorEndpoints.MaxReportsPerDevicePerHour / ErrorReportLimits.MaxReportsPerBatch;
        for (var i = 0; i < fits; i++)
            Assert.Equal(StatusCodes.Status400BadRequest, Status(DirectorErrorEndpoints.HandlePost(store, TenantA, device, bad, Now)));

        Assert.Equal(StatusCodes.Status429TooManyRequests, Status(DirectorErrorEndpoints.HandlePost(store, TenantA, device, bad, Now)));
    }

    [Fact]
    public void Append_JustBeforeMidnight_IsFiledUnderTheDayItWasReceived()
    {
        var lastSecond = new DateTime(2026, 9, 22, 23, 59, 59, DateTimeKind.Utc);
        // The store's clock has already ticked into the next day; the record's own stamp has not.
        var store = new ErrorReportStore(_root, () => lastSecond.AddSeconds(2));

        DirectorErrorEndpoints.HandlePost(store, TenantA, UniqueDevice(), Batch(Item()), lastSecond);

        Assert.True(Directory.Exists(Path.Combine(_root, "2026-09-22")));
        Assert.False(Directory.Exists(Path.Combine(_root, "2026-09-23")));
    }

    [Theory]
    [InlineData("until", "0001-01-01T00:00:00Z")]
    [InlineData("since", "99999999d")]
    [InlineData("since", "2019-01-01T00:00:00Z")]
    [InlineData("until", "2099-01-01T00:00:00Z")]
    public void TryBuildQuery_AMomentOutOfRange_IsA400NotA503(string key, string value)
    {
        var q = new QueryCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { [key] = value });

        Assert.False(DirectorErrorEndpoints.TryBuildQuery(q, Now, out _, out var bad));
        Assert.Equal(StatusCodes.Status400BadRequest, Status(bad));
    }
}
