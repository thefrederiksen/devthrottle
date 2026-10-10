using CcDirector.Core.ErrorReports;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// The Error Logging mission, step 2 (issue #3675): the Gateway's own failure lines reach the error store, through the
/// same recogniser the Director uses, and an unwritable store fails loudly without the sink ever reporting its own
/// failure into itself.
/// </summary>
[Collection(FileLogCaptureCollection.Name)]
public sealed class GatewayErrorSinkTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 9, 10, 15, 0, DateTimeKind.Utc);
    private static readonly GatewayErrorSink.ProcessFacts Facts = new("2.18.0-test", "linux", "Linux 6", "x64", ErrorReportMachineId.Of("gw"));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gateway-errors-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            else if (File.Exists(_root)) File.Delete(_root);
        }
        catch (IOException) { }
    }

    private ErrorReportStore Store() => new(_root, () => Now);

    private static IReadOnlyList<ErrorReportRecord> All(ErrorReportStore store)
        => store.Query(new ErrorReportQuery(Now.AddDays(-1), Now.AddDays(1), Limit: 500)).Records;

    [Fact]
    public void A_logged_FAILED_line_becomes_one_gateway_row()
    {
        var store = Store();
        var sink = new GatewayErrorSink(store, new ErrorIntakeFloods(), () => Now, Facts);

        sink.OnLogLine("[DirectorErrorEndpoints] POST /gateway/director-errors FAILED (IOException): the share went away");
        Assert.Equal(1, sink.Flush());

        var row = Assert.Single(All(store));
        Assert.Equal(ErrorReportLimits.Gateway, row.Component);
        Assert.Equal("DirectorErrorEndpoints", row.Source);
        Assert.Equal("POST /gateway/director-errors FAILED (IOException): the share went away", row.Message);
        Assert.Equal("logged", row.Kind);
        Assert.Equal("", row.Account);
        Assert.Equal("2.18.0-test", row.ProductVersion);
        Assert.Equal(16, row.Fingerprint!.Length);
    }

    [Fact]
    public void An_ordinary_line_is_not_a_row()
    {
        var store = Store();
        var sink = new GatewayErrorSink(store, new ErrorIntakeFloods(), () => Now, Facts);

        sink.OnLogLine("[GatewayHost] GET /sessions -> 200 (4ms)");
        sink.OnLogLine($"{ErrorLine.ReporterTag} gateway: 3 error(s) not stored (IOException): FAILED: the disk");

        Assert.Equal(0, sink.Flush());
        Assert.Empty(All(store));
    }

    [Fact]
    public void The_same_failure_repeated_is_one_row_with_a_count()
    {
        var store = Store();
        var sink = new GatewayErrorSink(store, new ErrorIntakeFloods(), () => Now, Facts);

        for (var i = 0; i < 5; i++)
            sink.OnLogLine($"[CronJobStore] Save FAILED: attempt {i} timed out");
        sink.Flush();

        var row = Assert.Single(All(store));
        Assert.Equal(5, row.RepeatCount);
    }

    [Fact]
    public void A_line_logged_inside_a_request_carries_its_correlation_id_and_route()
    {
        var store = Store();
        var sink = new GatewayErrorSink(store, new ErrorIntakeFloods(), () => Now, Facts);

        var id = RunInRequest(sink, "GET /gateway/skills",
            () => sink.OnLogLine("[SkillEndpoints] GET /gateway/skills FAILED: the store is locked"));
        sink.Flush();

        var row = Assert.Single(All(store));
        Assert.Equal(32, id.Length);
        Assert.Equal(id, row.CorrelationId);
        Assert.Equal("GET /gateway/skills", row.Surface);
    }

    [Fact]
    public void Every_free_text_field_is_scrubbed_before_it_is_kept()
    {
        var store = Store();
        var sink = new GatewayErrorSink(store, new ErrorIntakeFloods(), () => Now, Facts);

        sink.Report(new GatewayError("Probe", "refused", "POST /x answered 503 for /home/robert/secret",
            Stack: "at C:\\Users\\robert\\repo\\File.cs", Action: "open /Users/robert/notes", SessionId: "s1"));
        sink.Flush();

        var line = File.ReadAllText(Directory.EnumerateFiles(_root, "*.jsonl", SearchOption.AllDirectories).Single());
        Assert.DoesNotContain("robert", line);
    }

    [Fact]
    public void An_unwritable_store_is_tried_then_dropped_and_counted_never_thrown()
    {
        // The store's root is a FILE, so every write throws an IOException.
        File.WriteAllText(_root, "not a folder");
        var floods = new ErrorIntakeFloods();
        var sink = new GatewayErrorSink(new ErrorReportStore(_root, () => Now), floods, () => Now, Facts);

        sink.OnLogLine("[TurnLogStore] Append FAILED: the disk is full");
        for (var i = 0; i < GatewayErrorSink.MaxAttempts; i++)
        {
            Assert.Equal(1, sink.PendingCount);
            Assert.Equal(0, sink.Flush());
        }

        Assert.Equal(0, sink.PendingCount);
        Assert.Equal(1, sink.Dropped);
        // The loss is counted for the hour's flood record, which is kept until the store takes it.
        Assert.Equal(1, floods.Pending);
    }

    [Fact]
    public void An_unwritable_store_does_not_feed_itself_through_the_log()
    {
        // The loop this guards against: the sink's write fails, the failure is logged, the log line is observed, the
        // sink writes it, that write fails, and so on for ever. With the sink attached to the real process log, flush
        // an unwritable store several times and count what comes back.
        File.WriteAllText(_root, "not a folder");
        var sink = new GatewayErrorSink(new ErrorReportStore(_root, () => Now), new ErrorIntakeFloods(), () => Now, Facts);
        var observed = 0;
        Action<string> observer = line => { observed++; sink.OnLogLine(line); };

        using var log = FileLog.RedirectForTests();
        FileLog.ErrorObserver += observer;
        try
        {
            sink.OnLogLine("[TurnLogStore] Append FAILED: the disk is full");
            for (var i = 0; i < 10; i++) sink.Flush();
        }
        finally
        {
            FileLog.ErrorObserver -= observer;
        }

        var lines = log.DrainAndReadLines();
        Assert.Contains(lines, l => l.Contains($"{ErrorLine.ReporterTag} gateway:") && l.Contains("not stored"));
        Assert.Equal(0, observed);
        Assert.Equal(0, sink.PendingCount);
    }

    [Fact]
    public void A_failure_the_store_logs_while_the_sink_writes_is_not_queued_again()
    {
        // The store CAN take the reports but not the summary: its summaries folder is a file. It stores the row and
        // logs "UpdateSummaries FAILED" - on the sink's own thread, during the sink's own write. Were that line queued,
        // every flush would store one more row and log one more failure, for ever.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ErrorReportStore.SummaryFolder), "not a folder");
        var store = Store();
        var sink = new GatewayErrorSink(store, new ErrorIntakeFloods(), () => Now, Facts);

        using var log = FileLog.RedirectForTests();
        FileLog.ErrorObserver += sink.OnLogLine;
        try
        {
            sink.OnLogLine("[TurnLogStore] Append FAILED: the disk is full");
            Assert.Equal(1, sink.Flush());
            Assert.Equal(0, sink.PendingCount);
            Assert.Equal(0, sink.Flush());
        }
        finally
        {
            FileLog.ErrorObserver -= sink.OnLogLine;
        }

        Assert.Contains(log.DrainAndReadLines(), l => l.Contains("UpdateSummaries FAILED"));
        Assert.Single(All(store));
    }

    [Fact]
    public void A_failure_the_sink_does_not_expect_still_loses_nothing()
    {
        // Step 2 review, observation 8. A root holding a NUL character makes the store throw an ArgumentException - none
        // of the three failures the write expects. The exception goes on to the caller (the loop logs it), and both the
        // waiting error and the closed flood hour are put back first.
        var floods = new ErrorIntakeFloods();
        var sink = new GatewayErrorSink(new ErrorReportStore(_root + "\0bad"), floods, () => Now, Facts);
        sink.OnLogLine("[TurnLogStore] Append FAILED: the disk is full");
        floods.Dropped(ErrorIntakeFloods.InstallReports, 3, Now.AddHours(-1));

        Assert.ThrowsAny<ArgumentException>(() => sink.Flush());
        Assert.Equal(3, floods.Pending);

        floods.TakeAll();
        Assert.ThrowsAny<ArgumentException>(() => sink.Flush());
        Assert.Equal(1, sink.PendingCount);
        Assert.Equal(0, sink.Dropped);
    }

    [Fact]
    public void A_full_table_drops_new_errors_and_counts_them_for_the_flood_record()
    {
        var floods = new ErrorIntakeFloods();
        var sink = new GatewayErrorSink(Store(), floods, () => Now, Facts);

        for (var i = 0; i < GatewayErrorSink.MaxPending + 3; i++)
            sink.Report(new GatewayError($"Source{i}", "logged", "Save FAILED"));

        Assert.Equal(GatewayErrorSink.MaxPending, sink.PendingCount);
        Assert.Equal(3, sink.Dropped);
        Assert.Equal(3, floods.Pending);
    }

    [Fact]
    public void Within_an_hour_no_more_than_the_budget_is_written_and_the_rest_waits()
    {
        var clock = Now;
        var store = new ErrorReportStore(_root, () => clock);
        var sink = new GatewayErrorSink(store, new ErrorIntakeFloods(), () => clock, Facts);

        var written = 0;
        for (var round = 0; round < 4; round++)
        {
            for (var i = 0; i < GatewayErrorSink.MaxPending; i++)
                sink.Report(new GatewayError($"Source{round}-{i}", "logged", "Save FAILED"));
            written += sink.Flush();
        }

        Assert.Equal(GatewayErrorSink.MaxStoredPerHour, written);
        Assert.Equal(GatewayErrorSink.MaxPending, sink.PendingCount);

        clock = clock.AddHours(1).AddMinutes(1);
        Assert.Equal(GatewayErrorSink.MaxPending, sink.Flush());
    }

    /// <summary>Run <paramref name="action"/> inside the request scope the middleware opens, and return its id.</summary>
    private static string RunInRequest(GatewayErrorSink sink, string route, Action action)
    {
        var id = "";
        GatewayRequestErrors.RunAsync(new Microsoft.AspNetCore.Http.DefaultHttpContext(), () =>
        {
            var scope = GatewayRequestErrors.Current!;
            scope.Route = route;
            id = scope.CorrelationId;
            action();
            return Task.CompletedTask;
        }, sink).GetAwaiter().GetResult();
        return id;
    }
}
