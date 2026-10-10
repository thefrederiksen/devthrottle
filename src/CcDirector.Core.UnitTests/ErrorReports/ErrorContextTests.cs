using System.Net;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using Xunit;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3675: <see cref="ErrorContext"/>, the one way a Director error report gets its optional fields
/// (user_visible, surface, action, correlation_id, http_status, error_code, session_id). Every test sends through a
/// fake Gateway and reads the batch that would have left the machine.
/// </summary>
public sealed class ErrorContextTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<(string Url, string Body)> Requests = new();
        public Exception? Throw;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), body));
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    private readonly DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private (ErrorReporter Reporter, StubHandler Handler) NewReporter()
    {
        var handler = new StubHandler();
        var cfg = new GatewayConfig { Url = "https://gateway.example", Token = "device-key" };
        var reporter = new ErrorReporter(ErrorReportLimits.Director, () => cfg, new HttpClient(handler),
            () => _now, machineName: "TEST-MACHINE", productVersion: "2.9.0+abc");
        return (reporter, handler);
    }

    private static List<ErrorReportItem> Sent(StubHandler handler)
        => handler.Requests.SelectMany(r => JsonSerializer.Deserialize<ErrorReportBatch>(r.Body)!.Reports!).ToList();

    [Fact]
    public async Task OnLogLine_InsideAContext_EveryFieldReachesTheSentBatch()
    {
        var (reporter, handler) = NewReporter();

        using (ErrorContext.Begin(correlationId: "cmd-1", sessionId: "3f2a9c1e-0000-4000-8000-000000000001",
                   surface: "session terminal", action: "send a prompt to the session", userVisible: true,
                   httpStatus: 409, errorCode: "session_busy"))
        {
            reporter.OnLogLine("[SessionCommandExecutor] Command FAILED: refused");
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.Equal("cmd-1", item.CorrelationId);
        Assert.Equal("3f2a9c1e-0000-4000-8000-000000000001", item.SessionId);
        Assert.Equal("session terminal", item.Surface);
        Assert.Equal("send a prompt to the session", item.Action);
        Assert.True(item.UserVisible);
        Assert.Equal(409, item.HttpStatus);
        Assert.Equal("session_busy", item.ErrorCode);
    }

    [Fact]
    public async Task OnLogLine_OutsideAnyContext_SendsNoneOfTheOptionalFields()
    {
        var (reporter, handler) = NewReporter();

        reporter.OnLogLine("[X] Save FAILED: nope");
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.Null(item.CorrelationId);
        Assert.Null(item.SessionId);
        Assert.Null(item.Surface);
        Assert.Null(item.Action);
        Assert.Null(item.UserVisible);
        Assert.Null(item.HttpStatus);
        Assert.Null(item.ErrorCode);
    }

    [Fact]
    public async Task OnLogLine_SurfaceAndAction_AreScrubbedAndCapped()
    {
        var (reporter, handler) = NewReporter();

        using (ErrorContext.Begin(surface: "C:\\Users\\robert\\" + new string('s', 300),
                   action: "token=abc123secret " + new string('a', 400)))
        {
            reporter.OnLogLine("[X] Save FAILED: nope");
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.DoesNotContain("robert", item.Surface);
        Assert.True(item.Surface!.Length <= ErrorReportLimits.MaxShortField);
        Assert.DoesNotContain("abc123secret", item.Action);
        Assert.Contains(ErrorTextScrubber.Redacted, item.Action);
        Assert.True(item.Action!.Length <= ErrorReportLimits.MaxAction);
    }

    [Fact]
    public async Task OnLogLine_ContextFields_HaveThisMachinesNameTakenOut()
    {
        var (reporter, handler) = NewReporter();
        var machine = Environment.MachineName;

        using (ErrorContext.Begin(surface: "terminal on " + machine, action: "send a prompt from " + machine))
        {
            reporter.OnLogLine("[X] Save FAILED: nope");
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.Equal("terminal on " + ErrorTextScrubber.MachinePlaceholder, item.Surface);
        Assert.Equal("send a prompt from " + ErrorTextScrubber.MachinePlaceholder, item.Action);
    }

    [Fact]
    public void Begin_Nested_InnerWinsPerFieldAndOuterFieldsAreKept()
    {
        using var outer = ErrorContext.Begin(correlationId: "outer-id", sessionId: "session-a", surface: "phone chat");
        using var inner = ErrorContext.Begin(correlationId: "inner-id", userVisible: true);

        var current = ErrorContext.Current!;
        Assert.Equal("inner-id", current.CorrelationId);
        Assert.Equal("session-a", current.SessionId);
        Assert.Equal("phone chat", current.Surface);
        Assert.True(current.UserVisible);
    }

    [Fact]
    public async Task Dispose_PutsTheOuterContextBack_AndTheLastDisposeLeavesNone()
    {
        var (reporter, handler) = NewReporter();

        using (ErrorContext.Begin(correlationId: "outer-id"))
        {
            using (ErrorContext.Begin(correlationId: "inner-id", userVisible: true))
            {
                reporter.OnLogLine("[A] Inner FAILED: x");
            }
            Assert.Equal("outer-id", ErrorContext.Current!.CorrelationId);
            Assert.Null(ErrorContext.Current.UserVisible);
            reporter.OnLogLine("[A] Outer FAILED: x");
        }
        Assert.Null(ErrorContext.Current);
        reporter.OnLogLine("[A] After FAILED: x");
        await reporter.SendPendingAsync(CancellationToken.None);

        var sent = Sent(handler).ToDictionary(i => i.Message!);
        Assert.Equal("inner-id", sent["Inner FAILED: x"].CorrelationId);
        Assert.True(sent["Inner FAILED: x"].UserVisible);
        Assert.Equal("outer-id", sent["Outer FAILED: x"].CorrelationId);
        Assert.Null(sent["Outer FAILED: x"].UserVisible);
        Assert.Null(sent["After FAILED: x"].CorrelationId);
    }

    [Fact]
    public async Task OnLogLine_InATaskStartedInsideTheContext_CarriesTheFields()
    {
        var (reporter, handler) = NewReporter();

        using (ErrorContext.Begin(correlationId: "cmd-7", sessionId: "session-b"))
        {
            await Task.Run(() => reporter.OnLogLine("[Worker] Deliver FAILED: timed out"));
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.Equal("cmd-7", item.CorrelationId);
        Assert.Equal("session-b", item.SessionId);
    }

    [Fact]
    public async Task OnLogLine_InATaskThatOutlivesItsContext_CarriesNoneOfTheFields()
    {
        var (reporter, handler) = NewReporter();
        var release = new TaskCompletionSource();
        Task late;

        using (ErrorContext.Begin(correlationId: "cmd-8", sessionId: "session-c", userVisible: true))
        {
            late = Task.Run(async () =>
            {
                await release.Task;
                reporter.OnLogLine("[Worker] Late FAILED: the answer came after the scope closed");
            });
        }
        release.SetResult();
        await late;
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.Null(item.CorrelationId);
        Assert.Null(item.SessionId);
        Assert.Null(item.UserVisible);
    }

    [Fact]
    public async Task OnLogLine_TheSameTextUnderTwoIds_StaysTwoRows_AndTheSameIdTwiceIsOneCountedRow()
    {
        var (reporter, handler) = NewReporter();

        for (var i = 0; i < 6; i++)
        {
            using var _ = ErrorContext.Begin(correlationId: $"cmd-{Guid.NewGuid():N}");
            reporter.OnLogLine("[SessionCommandExecutor] Command FAILED: session busy");
        }
        for (var i = 0; i < 2; i++)
        {
            using var _ = ErrorContext.Begin(correlationId: "cmd-same");
            reporter.OnLogLine("[SessionCommandExecutor] Command FAILED: session busy");
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        var sent = Sent(handler);
        Assert.Equal(7, sent.Count);
        Assert.Equal(7, sent.Select(i => i.CorrelationId).Distinct().Count());
        Assert.Equal(2, sent.Single(i => i.CorrelationId == "cmd-same").RepeatCount);
        Assert.All(sent.Where(i => i.CorrelationId != "cmd-same"), i => Assert.Equal(1, i.RepeatCount));
    }

    [Fact]
    public async Task ReportOutcome_InsideAContext_IsStampedToo()
    {
        var (reporter, handler) = NewReporter();

        using (ErrorContext.Begin(correlationId: "repair-1"))
        {
            reporter.ReportOutcome("LauncherRepair", "launcher-repair", "rebuilt", "");
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        Assert.Equal("repair-1", Assert.Single(Sent(handler)).CorrelationId);
    }

    // ---- Withheld text: a report never carries what a screen showed or what a person typed ----

    private static string Unique(string label) => $"{label}-{Guid.NewGuid():N}";

    [Fact]
    public async Task Withhold_InsideAContext_TheStringIsReplacedInMessageAndStack()
    {
        var (reporter, handler) = NewReporter();
        var typed = Unique("the words a person typed");

        using (ErrorContext.Begin(correlationId: "cmd-w1"))
        {
            ErrorContext.Withhold($"'{typed}'");
            reporter.OnLogLine($"[TerminalSubmit] Resolve FAILED: what it read: text='{typed}', cursor=1,2\nrow='{typed}' again");
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.DoesNotContain(typed, item.Message + item.Stack);
        Assert.Contains($"text=<withheld: {typed.Length + 2} characters>, cursor=1,2", item.Message);
        Assert.Contains($"row=<withheld: {typed.Length + 2} characters> again", item.Stack);
    }

    [Fact]
    public async Task Withhold_InAnInnerScope_StillCoversALineTheOuterScopeLogsAfterIt()
    {
        var (reporter, handler) = NewReporter();
        var typed = Unique("inner");

        using (ErrorContext.Begin(correlationId: "cmd-w2"))
        {
            using (ErrorContext.Begin(sessionId: "session-w2"))
                ErrorContext.Withhold(typed);
            reporter.OnLogLine($"[GatewayStreamClient] Command FAILED: verb=prompt, error=the composer held {typed}");
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        Assert.DoesNotContain(typed, Assert.Single(Sent(handler)).Message);
    }

    [Fact]
    public async Task Withhold_ALineLoggedOutsideAnyContext_IsStillCovered_FromEightCharacters()
    {
        var (reporter, handler) = NewReporter();
        var typed = Unique("relay");
        const string shortText = "q7z";

        using (ErrorContext.Begin(correlationId: "cmd-w3"))
        {
            ErrorContext.Withhold(typed);
            ErrorContext.Withhold(shortText);
        }
        reporter.OnLogLine($"[FleetRelay] Deliver FAILED: {typed} and {shortText}");
        await reporter.SendPendingAsync(CancellationToken.None);

        var message = Assert.Single(Sent(handler)).Message!;
        Assert.DoesNotContain(typed, message);
        // A string shorter than the floor is withheld inside its own context only.
        Assert.Contains(shortText, message);
    }

    [Fact]
    public async Task Withhold_PastTheCapForOneChain_KeepsOnlyTheHeadOfTheLine()
    {
        var (reporter, handler) = NewReporter();

        using (ErrorContext.Begin(correlationId: "cmd-w4"))
        {
            for (var i = 0; i <= ErrorContext.MaxWithheld; i++) ErrorContext.Withhold($"s{i}");
            reporter.OnLogLine("[TerminalSubmit] Resolve FAILED: the composer held something never recorded");
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.DoesNotContain("never recorded", item.Message);
        Assert.StartsWith("Resolve FAILED: <withheld:", item.Message);
    }

    [Fact]
    public void WithholdPrompt_BelowFourCharacters_IsNotWithheld()
    {
        using var _ = ErrorContext.Begin(correlationId: "cmd-w5");
        ErrorContext.WithholdPrompt("go");

        Assert.Equal("[X] A FAILED: go on", ErrorContext.ApplyWithheld("[X] A FAILED: go on", ErrorContext.Current));
    }

    [Fact]
    public async Task ReportOutcome_InsideAContext_WithholdsFromMessageAndDetail()
    {
        var (reporter, handler) = NewReporter();
        var typed = Unique("outcome");

        using (ErrorContext.Begin(correlationId: "cmd-w6"))
        {
            ErrorContext.Withhold(typed);
            reporter.ReportOutcome("LauncherRepair", "launcher-repair", $"rebuilt {typed}", $"detail {typed}");
        }
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(Sent(handler));
        Assert.DoesNotContain(typed, item.Message + item.Stack);
    }

    // ---- Before sign-in: the outbox carries the fields ----

    [Fact]
    public async Task Outbox_BeforeSignIn_TheFieldsTravelInTheInstallReport_AndAsFieldsOnceSignedIn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-context-test-" + Guid.NewGuid().ToString("N"));
        var handler = new StubHandler();
        var config = new GatewayConfig { Url = "", Token = "" };
        var http = new HttpClient(handler);
        var reporter = new ErrorReporter(ErrorReportLimits.Director, () => config, http, () => _now,
            machineName: "TEST-MACHINE", productVersion: "2.9.0+abc",
            preSignIn: _ => new PreSignInOutbox(Path.Combine(dir, "director-before-sign-in.json"), ErrorReportLimits.Director,
                () => "3f2a9c1e-0000-4000-8000-000000000002", () => "https://hosted.example", http, () => _now));
        try
        {
            using (ErrorContext.Begin(correlationId: "cmd-9", sessionId: "session-c", surface: "session terminal",
                       action: "send a prompt to the session", userVisible: true))
            {
                reporter.OnLogLine("[SessionCommandExecutor] Command FAILED: refused");
            }
            using (ErrorContext.Begin(correlationId: "cmd-10"))
            {
                reporter.OnLogLine("[SessionCommandExecutor] Command FAILED: refused");
            }

            // No credential, and the public route does not answer: both are kept on disk, as two entries.
            handler.Throw = new HttpRequestException("offline");
            Assert.Equal(0, await reporter.SendPendingAsync(CancellationToken.None));
            Assert.Equal(2, reporter.Outbox!.Count);
            var installReport = JsonSerializer.Deserialize<InstallReportPayload>(handler.Requests[0].Body)!;
            Assert.Contains("correlation_id=cmd-9, session_id=session-c, user_visible=true, surface=session terminal, action=send a prompt to the session", installReport.Diagnostics);
            Assert.Contains("correlation_id=cmd-10", installReport.Diagnostics);

            // Signed in: the kept errors reach the device route with the fields as fields.
            handler.Throw = null;
            config = new GatewayConfig { Url = "https://gateway.example", Token = "device-key" };
            Assert.Equal(2, await reporter.SendPendingAsync(CancellationToken.None));

            var last = JsonSerializer.Deserialize<ErrorReportBatch>(handler.Requests[^1].Body)!.Reports!;
            var first = last.Single(i => i.CorrelationId == "cmd-9");
            Assert.Equal("session-c", first.SessionId);
            Assert.Equal("session terminal", first.Surface);
            Assert.Equal("send a prompt to the session", first.Action);
            Assert.True(first.UserVisible);
            Assert.Contains(last, i => i.CorrelationId == "cmd-10");
        }
        finally
        {
            reporter.Dispose();
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
