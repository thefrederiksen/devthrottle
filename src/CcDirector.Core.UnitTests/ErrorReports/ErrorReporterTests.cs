using System.Net;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using Xunit;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3311: the Director and launcher error reporter. What matters most is what it must never do -
/// flood the Gateway, grow without bound, retry for ever, or report itself - so most of these pin a bound.
/// Every test builds its own reporter; none touches the process-wide <c>ErrorReporter.Current</c>.
/// </summary>
public sealed class ErrorReporterTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<(string Url, string? Auth, string Body)> Requests = new();
        public Func<HttpStatusCode> Status = () => HttpStatusCode.Accepted;
        public Exception? Throw;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(Status());
        }
    }

    private DateTime _now = new(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc);

    private (ErrorReporter Reporter, StubHandler Handler) NewReporter(GatewayConfig? config = null)
    {
        var handler = new StubHandler();
        var cfg = config ?? new GatewayConfig { Url = "https://gateway.example", Token = "device-key" };
        var reporter = new ErrorReporter(ErrorReportLimits.Director, () => cfg, new HttpClient(handler),
            () => _now, machineName: "TEST-MACHINE", productVersion: "2.9.0+abc");
        return (reporter, handler);
    }

    private static IReadOnlyList<ErrorReportItem> Sent(StubHandler handler, int request = 0)
        => JsonSerializer.Deserialize<ErrorReportBatch>(handler.Requests[request].Body)!.Reports!;

    [Fact]
    public async Task SendPending_AnErrorLine_IsPostedWithTheDeviceCredentialAndIdentity()
    {
        var (reporter, handler) = NewReporter();
        reporter.OnLogLine("[SessionManager] CreateSession FAILED: System.IO.IOException: disk full at /Users/robert/x");

        var sent = await reporter.SendPendingAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://gateway.example/gateway/director-errors", request.Url);
        Assert.Equal("Bearer device-key", request.Auth);
        var item = Assert.Single(Sent(handler));
        Assert.Equal("director", item.Component);
        Assert.Equal("SessionManager", item.Source);
        Assert.Equal("logged", item.Kind);
        Assert.Equal("System.IO.IOException", item.ExceptionType);
        Assert.Contains("~/x", item.Message);
        Assert.DoesNotContain("robert", item.Message);
        Assert.Equal("2.9.0+abc", item.ProductVersion);
        Assert.Equal(ErrorReportMachineId.Of("TEST-MACHINE"), item.MachineId);
        Assert.DoesNotContain("TEST-MACHINE", handler.Requests[0].Body);
    }

    [Fact]
    public void OnLogLine_AnOrdinaryLine_IsNotQueued()
    {
        var (reporter, _) = NewReporter();

        reporter.OnLogLine("[GatewayClient] heartbeat ok");
        reporter.OnLogLine("[ErrorReporter] 2 report(s) not delivered FAILED");

        Assert.Equal(0, reporter.PendingCount);
    }

    [Fact]
    public async Task OnLogLine_AnErrorLoop_CollapsesToOneReportWithACount()
    {
        var (reporter, handler) = NewReporter();
        for (var i = 0; i < 1000; i++)
            reporter.OnLogLine($"[Tunnel] Reconnect FAILED: attempt {i} refused");

        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.Equal(1000, item.RepeatCount);
    }

    [Fact]
    public void OnLogLine_BeyondTheTable_DropsAndCounts()
    {
        var (reporter, _) = NewReporter();
        for (var i = 0; i < ErrorReporter.MaxPending + 50; i++)
            reporter.OnLogLine($"[C{i}] Distinct FAILED: error kind {(char)('a' + i % 26)}{i}x");

        Assert.Equal(ErrorReporter.MaxPending, reporter.PendingCount);
        Assert.Equal(50, reporter.Dropped);
    }

    [Fact]
    public async Task SendPending_SendsAtMostOneBatchAndNoMoreThanTheHourlyBudget()
    {
        var (reporter, handler) = NewReporter();
        for (var i = 0; i < 150; i++)
            reporter.OnLogLine($"[C{i}] Distinct FAILED");

        var total = 0;
        for (var round = 0; round < 10; round++)
            total += await reporter.SendPendingAsync(CancellationToken.None);

        Assert.Equal(ErrorReporter.MaxSentPerHour, total);
        Assert.All(handler.Requests, r => Assert.True(JsonSerializer.Deserialize<ErrorReportBatch>(r.Body)!.Reports!.Count <= ErrorReportLimits.MaxReportsPerBatch));

        // An hour later the budget is back.
        _now = _now.AddHours(1).AddSeconds(1);
        Assert.Equal(ErrorReportLimits.MaxReportsPerBatch, await reporter.SendPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SendPending_UnreachableGateway_RetriesBoundedThenDrops()
    {
        var (reporter, handler) = NewReporter();
        handler.Throw = new HttpRequestException("connection refused");
        reporter.OnLogLine("[X] Save FAILED: nope");

        for (var i = 0; i < ErrorReporter.MaxAttempts + 2; i++)
            Assert.Equal(0, await reporter.SendPendingAsync(CancellationToken.None));

        Assert.Equal(ErrorReporter.MaxAttempts, handler.Requests.Count);
        Assert.Equal(0, reporter.PendingCount);
        Assert.Equal(1, reporter.Dropped);
    }

    [Fact]
    public async Task SendPending_GatewayWithoutTheRoute_PausesInsteadOfRetrying()
    {
        var (reporter, handler) = NewReporter();
        handler.Status = () => HttpStatusCode.NotFound;
        reporter.OnLogLine("[X] Save FAILED: one");
        await reporter.SendPendingAsync(CancellationToken.None);

        reporter.OnLogLine("[X] Load FAILED: two");
        await reporter.SendPendingAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(1, reporter.PendingCount);
    }

    [Fact]
    public async Task SendPending_RateLimited_DropsAndBacksOff()
    {
        var (reporter, handler) = NewReporter();
        handler.Status = () => HttpStatusCode.TooManyRequests;
        reporter.OnLogLine("[X] Save FAILED: one");

        await reporter.SendPendingAsync(CancellationToken.None);
        reporter.OnLogLine("[X] Load FAILED: two");
        await reporter.SendPendingAsync(CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(1, reporter.Dropped);

        _now = _now + ErrorReporter.PauseAfterRefusal + TimeSpan.FromSeconds(1);
        handler.Status = () => HttpStatusCode.Accepted;
        Assert.Equal(1, await reporter.SendPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SendPending_NoGatewayConfigured_SendsNothing()
    {
        var (reporter, handler) = NewReporter(new GatewayConfig { Url = "", Token = "" });
        reporter.OnLogLine("[X] Save FAILED: nope");

        Assert.Equal(0, await reporter.SendPendingAsync(CancellationToken.None));

        Assert.Empty(handler.Requests);
        Assert.Equal(1, reporter.Dropped);
    }

    [Fact]
    public async Task SendPending_ServerError_KeepsTheReportForTheNextTry()
    {
        var (reporter, handler) = NewReporter();
        handler.Status = () => HttpStatusCode.ServiceUnavailable;
        reporter.OnLogLine("[X] Save FAILED: nope");

        Assert.Equal(0, await reporter.SendPendingAsync(CancellationToken.None));
        Assert.Equal(1, reporter.PendingCount);

        handler.Status = () => HttpStatusCode.Accepted;
        Assert.Equal(1, await reporter.SendPendingAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task SendPending_TheSameErrorDuringAFailedSend_IsFoldedIntoOneReport()
    {
        // While the batch is in flight the same error happens twice more, and then the send fails. The retry
        // must carry ONE report counting all three, not two reports or a lost count.
        var handler = new RepeatWhileSendingHandler();
        var reporter = new ErrorReporter(ErrorReportLimits.Director,
            () => new GatewayConfig { Url = "https://gateway.example", Token = "k" }, new HttpClient(handler),
            () => _now, machineName: "M", productVersion: "v");
        handler.Target = reporter;
        reporter.OnLogLine("[X] Save FAILED: attempt 1");

        await reporter.SendPendingAsync(CancellationToken.None);
        Assert.Equal(1, reporter.PendingCount);

        handler.Fail = false;
        Assert.Equal(1, await reporter.SendPendingAsync(CancellationToken.None));
        var item = Assert.Single(JsonSerializer.Deserialize<ErrorReportBatch>(handler.LastBody!)!.Reports!);
        Assert.Equal(3, item.RepeatCount);
    }

    private sealed class RepeatWhileSendingHandler : HttpMessageHandler
    {
        public ErrorReporter? Target;
        public bool Fail = true;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            if (!Fail) return new HttpResponseMessage(HttpStatusCode.Accepted);
            Target!.OnLogLine("[X] Save FAILED: attempt 2");
            Target!.OnLogLine("[X] Save FAILED: attempt 3");
            throw new HttpRequestException("reset");
        }
    }

    [Fact]
    public async Task OnLogLine_AHugeExceptionDump_BecomesOneBoundedReport()
    {
        var (reporter, handler) = NewReporter();

        reporter.OnLogLine("[App] UNHANDLED UI-THREAD EXCEPTION: boom\n" + string.Join("\n", Enumerable.Repeat("   at A.B()", 20000)));
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.Equal("ui-thread", item.Kind);
        Assert.True(item.Stack!.Length <= ErrorReportLimits.MaxStack);
    }

    [Fact]
    public async Task Flush_AfterTheHourlyBudgetIsSpent_StillSendsTheCrash()
    {
        var (reporter, handler) = NewReporter();
        for (var i = 0; i < ErrorReporter.MaxSentPerHour; i++)
            reporter.OnLogLine($"[C{i}] Noise FAILED");
        while (await reporter.SendPendingAsync(CancellationToken.None) > 0) { }
        reporter.OnLogLine("[Program] UNHANDLED (terminating): System.InvalidOperationException: boom");

        Assert.Equal(0, await reporter.SendPendingAsync(CancellationToken.None));
        Assert.Equal(1, await reporter.FlushAsync(CancellationToken.None));

        var last = JsonSerializer.Deserialize<ErrorReportBatch>(handler.Requests[^1].Body)!.Reports!;
        Assert.Equal("unhandled", Assert.Single(last).Kind);
    }

    [Fact]
    public async Task Flush_WaitsForASendAlreadyInFlight()
    {
        var gate = new TaskCompletionSource();
        var handler = new SlowHandler(gate.Task);
        var reporter = new ErrorReporter(ErrorReportLimits.Director,
            () => new GatewayConfig { Url = "https://gateway.example", Token = "k" }, new HttpClient(handler),
            () => _now, machineName: "M", productVersion: "v");
        reporter.OnLogLine("[X] Save FAILED: one");
        var periodic = reporter.SendPendingAsync(CancellationToken.None);
        reporter.OnLogLine("[Program] UNHANDLED (terminating): boom");

        var flush = reporter.FlushAsync(CancellationToken.None);
        gate.SetResult();

        Assert.Equal(1, await periodic);
        Assert.Equal(1, await flush);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class SlowHandler(Task release) : HttpMessageHandler
    {
        public int Calls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            await release;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    [Fact]
    public void Constructor_AComponentADeviceMayNotReportAs_Throws()
        => Assert.Throws<ArgumentException>(() => new ErrorReporter(ErrorReportLimits.Install));

    // ---- Before sign-in (issue #3311, B1) ----

    private sealed class SignInState
    {
        public GatewayConfig Config = new() { Url = "", Token = "" };
    }

    private (ErrorReporter Reporter, StubHandler Handler, SignInState State, string Dir) NewReporterWithOutbox()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-reporter-test-" + Guid.NewGuid().ToString("N"));
        var handler = new StubHandler();
        var state = new SignInState();
        var http = new HttpClient(handler);
        var reporter = new ErrorReporter(ErrorReportLimits.Launcher, () => state.Config, http,
            () => _now, machineName: "TEST-MACHINE", productVersion: "2.9.0+abc",
            preSignIn: r => new PreSignInOutbox(Path.Combine(dir, "launcher-before-sign-in.json"), ErrorReportLimits.Launcher,
                () => "3f2a9c1e-0000-4000-8000-000000000002", () => "https://hosted.example", http, () => _now));
        return (reporter, handler, state, dir);
    }

    [Fact]
    public async Task SendPending_NoCredential_SendsToThePublicInstallReportRoute_AndDropsNothing()
    {
        var (reporter, handler, _, dir) = NewReporterWithOutbox();
        try
        {
            reporter.OnLogLine("[LauncherCore] Register FAILED: System.Net.Http.HttpRequestException: refused");

            Assert.Equal(1, await reporter.SendPendingAsync(CancellationToken.None));

            var request = Assert.Single(handler.Requests);
            Assert.Equal("https://hosted.example/install-reports", request.Url);
            Assert.Null(request.Auth);
            var payload = JsonSerializer.Deserialize<InstallReportPayload>(request.Body)!;
            Assert.Equal("launcher", payload.Component);
            Assert.Equal(PreSignInOutbox.Step, payload.Step);
            Assert.Contains("Register FAILED", payload.Diagnostics);
            Assert.Equal(0, reporter.Dropped);
            Assert.Equal(0, reporter.Outbox!.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPending_NoCredentialAndNoAnswer_KeepsTheErrorOnDisk_ThenSendsItWithTheCredentialOnceSignedIn()
    {
        var (reporter, handler, state, dir) = NewReporterWithOutbox();
        try
        {
            handler.Throw = new HttpRequestException("offline");
            reporter.OnLogLine("[LauncherCore] Register FAILED: refused");

            Assert.Equal(0, await reporter.SendPendingAsync(CancellationToken.None));
            Assert.Equal(0, reporter.PendingCount);
            Assert.Equal(1, reporter.Outbox!.Count);
            Assert.Equal(0, reporter.Dropped);

            handler.Throw = null;
            state.Config = new GatewayConfig { Url = "https://gateway.example", Token = "device-key" };
            Assert.Equal(1, await reporter.SendPendingAsync(CancellationToken.None));

            var last = handler.Requests[^1];
            Assert.Equal("https://gateway.example/gateway/director-errors", last.Url);
            Assert.Equal("Bearer device-key", last.Auth);
            Assert.Equal("Register FAILED: refused", Assert.Single(Sent(handler, handler.Requests.Count - 1)).Message);
            Assert.Equal(0, reporter.Outbox.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SendPending_SignedIn_AKeptErrorTheGatewayRefuses_StaysOnDisk()
    {
        var (reporter, handler, state, dir) = NewReporterWithOutbox();
        try
        {
            handler.Throw = new HttpRequestException("offline");
            reporter.OnLogLine("[LauncherCore] Register FAILED: refused");
            await reporter.SendPendingAsync(CancellationToken.None);

            handler.Throw = null;
            handler.Status = () => HttpStatusCode.Forbidden;
            state.Config = new GatewayConfig { Url = "https://gateway.example", Token = "device-key" };
            Assert.Equal(0, await reporter.SendPendingAsync(CancellationToken.None));

            Assert.Equal(1, reporter.Outbox!.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Flush_BeforeSignIn_KeepsTheCrashOnDiskEvenWhenItCannotBeSent()
    {
        var (reporter, handler, _, dir) = NewReporterWithOutbox();
        try
        {
            handler.Throw = new HttpRequestException("offline");
            reporter.OnLogLine("[Program] UNHANDLED (terminating): System.InvalidOperationException: boom");

            Assert.Equal(0, await reporter.FlushAsync(CancellationToken.None));

            Assert.Equal(1, reporter.Outbox!.Count);
            Assert.Contains("boom", File.ReadAllText(Path.Combine(dir, "launcher-before-sign-in.json")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
