using CcDirector.Core.ErrorReports;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The flood record (the Error Logging mission, the owner's ruling of 9 October): every hour in which an error-intake
/// route dropped reports because a limit was hit is ONE row in the error store, with the route, the count and the hour -
/// never dropped by the limit it reports, and never recursing.
/// </summary>
public sealed class ErrorIntakeFloodTests : IDisposable
{
    private static readonly DateTime TenPast = new(2026, 10, 9, 10, 10, 0, DateTimeKind.Utc);
    private static readonly GatewayErrorSink.ProcessFacts Facts = new("2.18.0-test", "linux", "Linux 6", "x64", ErrorReportMachineId.Of("gw"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "error-floods-" + Guid.NewGuid().ToString("N"));

    // The routes' rate windows are process-wide and shared with their own test classes, which run beside this one, so
    // nothing here resets them: every test uses an install id or a device of its own.
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            else if (File.Exists(_root)) File.Delete(_root);
        }
        catch (IOException) { }
    }

    private static InstallReportEndpoints.InstallReportPost Install(string installId) => new(
        InstallId: installId, Component: "launcher", Step: "start", Message: "Start FAILED: no launchd",
        Diagnostics: null, Os: "macos", OsVersion: "15", Arch: "arm64", ProductVersion: "2.18.0", Installer: "setup");

    /// <summary>Every flood record in the store, read from its files - a query pages at 500, and these tests write more.</summary>
    private static IReadOnlyList<ErrorReportRecord> Floods(ErrorReportStore store) => !Directory.Exists(store.Root)
        ? []
        : Directory.EnumerateFiles(store.Root, "*.jsonl", SearchOption.AllDirectories)
            .SelectMany(File.ReadAllLines)
            .Where(l => l.Length > 0)
            .Select(l => System.Text.Json.JsonSerializer.Deserialize<ErrorReportRecord>(l)!)
            .Where(r => r.Kind == ErrorIntakeFloods.Kind)
            .ToList();

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    [Fact]
    public void A_burst_past_the_install_limit_is_one_record_for_the_hour_with_the_count_dropped()
    {
        var clock = TenPast;
        var store = new ErrorReportStore(_root, () => clock);
        var floods = new ErrorIntakeFloods();
        var sink = new GatewayErrorSink(store, floods, () => clock, Facts);
        var installId = "install-" + Guid.NewGuid().ToString("N")[..12];

        var refused = 0;
        for (var i = 0; i < InstallReportEndpoints.MaxReportsPerInstallPerHour + 7; i++)
            if (Status(InstallReportEndpoints.Handle(Install(installId), clock.AddSeconds(i), store, floods)) == StatusCodes.Status429TooManyRequests)
                refused++;
        Assert.Equal(7, refused);

        // While the hour is running nothing is written: the hour's record is written once, when the hour has closed.
        Assert.Equal(0, sink.Flush());
        Assert.Empty(Floods(store));

        clock = new DateTime(2026, 10, 9, 11, 0, 5, DateTimeKind.Utc);
        Assert.Equal(1, sink.Flush());
        Assert.Equal(0, sink.Flush());

        var record = Assert.Single(Floods(store));
        Assert.Equal(ErrorReportLimits.Gateway, record.Component);
        Assert.Equal("POST /install-reports", record.Surface);
        Assert.Equal("POST /install-reports dropped 7 report(s) over its limit in the hour from 2026-10-09T10:00Z", record.Message);
        Assert.Equal(7, record.RepeatCount);
        Assert.Equal(StatusCodes.Status429TooManyRequests, record.HttpStatus);
        Assert.Equal("rate_limited", record.ErrorCode);
        Assert.Equal("", record.Account);
    }

    [Fact]
    public void Two_routes_and_two_hours_are_one_record_each()
    {
        var clock = TenPast;
        var store = new ErrorReportStore(_root, () => clock);
        var floods = new ErrorIntakeFloods();
        var sink = new GatewayErrorSink(store, floods, () => clock, Facts);

        floods.Dropped(ErrorIntakeFloods.InstallReports, 3, TenPast);
        floods.Dropped(ErrorIntakeFloods.InstallReports, 2, TenPast.AddMinutes(30));
        floods.Dropped(ErrorIntakeFloods.DirectorErrors, 25, TenPast.AddMinutes(5));
        floods.Dropped(ErrorIntakeFloods.InstallReports, 4, TenPast.AddHours(1));

        clock = TenPast.AddHours(2);
        Assert.Equal(3, sink.Flush());

        var records = Floods(store);
        Assert.Equal(3, records.Count);
        Assert.Contains(records, r => r.Message == "POST /install-reports dropped 5 report(s) over its limit in the hour from 2026-10-09T10:00Z");
        Assert.Contains(records, r => r.Message == "POST /install-reports dropped 4 report(s) over its limit in the hour from 2026-10-09T11:00Z");
        Assert.Contains(records, r => r.Message == "POST /gateway/director-errors dropped 25 report(s) over its limit in the hour from 2026-10-09T10:00Z");
        // Every flood of one route is one problem: the count and the hour fold away in the fingerprint.
        Assert.Equal(2, records.Select(r => r.Fingerprint).Distinct().Count());
    }

    [Fact]
    public void No_burst_no_record()
    {
        var clock = TenPast;
        var store = new ErrorReportStore(_root, () => clock);
        var floods = new ErrorIntakeFloods();
        var sink = new GatewayErrorSink(store, floods, () => clock, Facts);
        var installId = "install-" + Guid.NewGuid().ToString("N")[..12];

        for (var i = 0; i < InstallReportEndpoints.MaxReportsPerInstallPerHour; i++)
            Assert.Equal(StatusCodes.Status202Accepted, Status(InstallReportEndpoints.Handle(Install(installId), clock, store, floods)));

        clock = clock.AddHours(2);
        sink.Flush();
        sink.Dispose();

        Assert.Empty(Floods(store));
        Assert.Equal(0, floods.Pending);
    }

    [Fact]
    public void A_device_over_its_limit_on_the_director_route_is_counted_by_reports()
    {
        var store = new ErrorReportStore(_root, () => TenPast);
        var floods = new ErrorIntakeFloods();
        var device = "dev-" + Guid.NewGuid().ToString("N")[..10];
        var batch = new ErrorReportBatch(Enumerable.Range(0, ErrorReportLimits.MaxReportsPerBatch)
            .Select(i => new ErrorReportItem("director", "S", "logged", $"E{i} FAILED", null, null, 1, TenPast, TenPast,
                "2.18.0", "windows", "10", "x64", ErrorReportMachineId.Of("pc")))
            .ToList());

        var refusedReports = 0;
        for (var i = 0; i < 8; i++)
            if (Status(DirectorErrorEndpoints.HandlePost(store, new TenantId("tenant-a"), device, batch, TenPast, floods)) == StatusCodes.Status429TooManyRequests)
                refusedReports += batch.Reports!.Count;

        Assert.True(refusedReports > 0);
        var flood = Assert.Single(floods.TakeAll());
        Assert.Equal(ErrorIntakeFloods.DirectorErrors, flood.Intake);
        Assert.Equal(refusedReports, flood.Dropped);
    }

    [Fact]
    public void The_record_is_written_even_when_the_sinks_own_budget_is_spent()
    {
        // The record must never be dropped by a limit: fill this hour's budget for the Gateway's own errors, then close
        // a flood hour - its record is still written.
        var clock = TenPast;
        var store = new ErrorReportStore(_root, () => clock);
        var floods = new ErrorIntakeFloods();
        var sink = new GatewayErrorSink(store, floods, () => clock, Facts);
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < GatewayErrorSink.MaxPending; i++)
                sink.Report(new GatewayError($"S{round}-{i}", "logged", "Save FAILED"));
            sink.Flush();
        }
        sink.Report(new GatewayError("Waiting", "logged", "Save FAILED"));

        floods.Dropped(ErrorIntakeFloods.InstallReports, 9, TenPast.AddHours(-1));
        Assert.Equal(1, sink.Flush());

        Assert.Single(Floods(store));
        Assert.Equal(1, sink.PendingCount);
    }

    [Fact]
    public void A_record_the_store_cannot_take_is_kept_and_written_later_never_lost()
    {
        File.WriteAllText(_root, "not a folder");
        var floods = new ErrorIntakeFloods();
        var sink = new GatewayErrorSink(new ErrorReportStore(_root, () => TenPast), floods, () => TenPast, Facts);
        floods.Dropped(ErrorIntakeFloods.ClientErrors, 4, TenPast.AddHours(-1));

        for (var i = 0; i < 5; i++) Assert.Equal(0, sink.Flush());
        Assert.Equal(4, floods.Pending);

        File.Delete(_root);
        var store = new ErrorReportStore(_root, () => TenPast);
        Assert.Equal(1, new GatewayErrorSink(store, floods, () => TenPast, Facts).Flush());
        Assert.Equal("POST /client-errors dropped 4 report(s) over its limit in the hour from 2026-10-09T09:00Z", Assert.Single(Floods(store)).Message);
    }

    [Fact]
    public void A_stopping_Gateway_writes_its_running_hour()
    {
        var store = new ErrorReportStore(_root, () => TenPast);
        var floods = new ErrorIntakeFloods();
        var sink = new GatewayErrorSink(store, floods, () => TenPast, Facts);
        floods.Dropped(ErrorIntakeFloods.InstallReports, 2, TenPast);

        sink.Dispose();

        Assert.Single(Floods(store));
        Assert.Equal(0, floods.Pending);
    }
}
