using System.Net;
using System.Text.Json;
using CcDirector.Core.ErrorReports;
using Xunit;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3311, B1: errors a launcher or Director logs before the machine has signed in. They used to be
/// dropped. These pin the promises that replace that: kept on disk before anything is sent, sent to the public
/// install-report route within its per-machine limit, removed only when the Gateway accepts them, and never
/// lost to a failure, a refusal or a count that grew while a report was in flight.
/// </summary>
public sealed class PreSignInOutboxTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-outbox-test-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string FilePath => Path.Combine(_dir, "director-before-sign-in.json");

    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<(string Url, string? Auth, string Body)> Requests = new();
        public Func<HttpStatusCode> Status = () => HttpStatusCode.Accepted;
        public Exception? Throw;
        public Action? DuringSend;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            DuringSend?.Invoke();
            if (Throw is not null) throw Throw;
            return new HttpResponseMessage(Status());
        }
    }

    private (PreSignInOutbox Outbox, StubHandler Handler) NewOutbox()
    {
        var handler = new StubHandler();
        var outbox = new PreSignInOutbox(FilePath, ErrorReportLimits.Director,
            () => "3f2a9c1e-0000-4000-8000-000000000001", () => "https://gateway.example/", new HttpClient(handler), () => _now);
        return (outbox, handler);
    }

    private ErrorReportItem Item(string message, int count = 1, string source = "SessionManager", string stack = "")
        => new(ErrorReportLimits.Director, source, "logged", message, "System.IO.IOException", stack, count,
            _now, _now, "2.9.0+abc", "macos", "Darwin 25.0.0", "arm64", "0123456789abcdef");

    private static InstallReportPayload Payload(StubHandler handler, int request = 0)
        => JsonSerializer.Deserialize<InstallReportPayload>(handler.Requests[request].Body)!;

    [Fact]
    public void Keep_TheSameErrorTwice_IsOneEntryWithTheCountsAdded_AndSurvivesANewProcess()
    {
        var (outbox, _) = NewOutbox();

        outbox.Keep(new[] { Item("Save FAILED: attempt 3", 2) });
        outbox.Keep(new[] { Item("Save FAILED: attempt 4", 5) });

        var (reopened, _) = NewOutbox();
        Assert.Equal(1, reopened.Count);
        Assert.Contains("\"repeat_count\":7", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Keep_MoreDistinctErrorsThanItHolds_CountsTheOnesThatDidNotFit()
    {
        var (outbox, _) = NewOutbox();

        outbox.Keep(Enumerable.Range(0, PreSignInOutbox.MaxKept + 3).Select(i => Item($"Save FAILED: {i}", source: $"C{i}")).ToList());

        Assert.Equal(PreSignInOutbox.MaxKept, outbox.Count);
        Assert.Contains("\"not_kept\":3", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task SendBeforeSignIn_PostsOneInstallReportWithTheMachinesId_AndEmptiesTheFileWhenAccepted()
    {
        var (outbox, handler) = NewOutbox();
        outbox.Keep(new[] { Item("Save FAILED: disk full"), Item("Load FAILED: gone", source: "Store") });

        var sent = await outbox.SendBeforeSignInAsync(final: false, CancellationToken.None);

        Assert.Equal(2, sent);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://gateway.example/install-reports", request.Url);
        Assert.Null(request.Auth);
        var payload = Payload(handler);
        Assert.Equal("3f2a9c1e-0000-4000-8000-000000000001", payload.InstallId);
        Assert.Equal("director", payload.Installer);
        Assert.Equal("director", payload.Component);
        Assert.Equal(PreSignInOutbox.Step, payload.Step);
        Assert.Equal("macos", payload.Os);
        Assert.Equal("2.9.0+abc", payload.ProductVersion);
        Assert.Contains("Save FAILED: disk full", payload.Diagnostics);
        Assert.Contains("Load FAILED: gone", payload.Diagnostics);
        Assert.Equal(0, outbox.Count);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task SendBeforeSignIn_Refused429_KeepsEverything_AndWaitsBeforeTryingAgain()
    {
        var (outbox, handler) = NewOutbox();
        handler.Status = () => HttpStatusCode.TooManyRequests;
        outbox.Keep(new[] { Item("Save FAILED: x") });

        Assert.Equal(0, await outbox.SendBeforeSignInAsync(false, CancellationToken.None));
        Assert.Equal(1, outbox.Count);

        handler.Status = () => HttpStatusCode.Accepted;
        Assert.Equal(0, await outbox.SendBeforeSignInAsync(false, CancellationToken.None));
        Assert.Single(handler.Requests);

        _now += PreSignInOutbox.PauseAfterRateLimit + TimeSpan.FromSeconds(1);
        Assert.Equal(1, await outbox.SendBeforeSignInAsync(false, CancellationToken.None));
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public async Task SendBeforeSignIn_NoAnswer_KeepsEverything()
    {
        var (outbox, handler) = NewOutbox();
        handler.Throw = new HttpRequestException("no route to host");
        outbox.Keep(new[] { Item("Save FAILED: x") });

        Assert.Equal(0, await outbox.SendBeforeSignInAsync(false, CancellationToken.None));

        Assert.Equal(1, outbox.Count);
    }

    [Fact]
    public async Task SendBeforeSignIn_StaysInsideItsHourlyShare_ButTheLastSendBeforeExitGoesAnyway()
    {
        var (outbox, handler) = NewOutbox();
        handler.Status = () => HttpStatusCode.ServiceUnavailable;
        outbox.Keep(new[] { Item("Save FAILED: x") });

        for (var i = 0; i < PreSignInOutbox.MaxReportsPerHour + 2; i++)
            await outbox.SendBeforeSignInAsync(false, CancellationToken.None);
        Assert.Equal(PreSignInOutbox.MaxReportsPerHour, handler.Requests.Count);

        handler.Status = () => HttpStatusCode.Accepted;
        Assert.Equal(1, await outbox.SendBeforeSignInAsync(final: true, CancellationToken.None));
        Assert.Equal(PreSignInOutbox.MaxReportsPerHour + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task SendBeforeSignIn_TheSameErrorAgainWhileTheReportIsInFlight_KeepsTheOccurrencesNotYetSent()
    {
        var (outbox, handler) = NewOutbox();
        outbox.Keep(new[] { Item("Save FAILED: x", 2) });
        handler.DuringSend = () => outbox.Keep(new[] { Item("Save FAILED: x", 3) });

        Assert.Equal(1, await outbox.SendBeforeSignInAsync(false, CancellationToken.None));

        Assert.Equal(1, outbox.Count);
        Assert.Contains("\"repeat_count\":3", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Compose_MoreThanOneReportHolds_TakesWholeErrorsFromTheFront_AndSaysTheRestAreWaiting()
    {
        var big = new string('s', PreSignInOutbox.MaxStackInReport);
        var items = Enumerable.Range(0, 40).Select(i => Item($"Save FAILED: {i}", source: $"C{i}", stack: big)).ToList();

        var (payload, used) = PreSignInOutbox.Compose("launcher", "id-12345678", items, notKept: 4);

        Assert.InRange(used, 1, items.Count - 1);
        Assert.True(payload.Diagnostics.Length <= PreSignInOutbox.DiagnosticsBudget);
        Assert.Contains($"{items.Count - used} more are waiting", payload.Message);
        Assert.Contains("4 further distinct error(s) were not kept", payload.Message);
        Assert.Contains("[C0] Save FAILED: 0", payload.Message);
    }

    [Fact]
    public void Compose_OneErrorLargerThanAReport_IsStillSent_CutToFit()
    {
        var huge = Item("Save FAILED: x", stack: new string('s', 100_000)) with { Message = new string('m', 30_000) };

        var (payload, used) = PreSignInOutbox.Compose("director", "id-12345678", new[] { huge }, 0);

        Assert.Equal(1, used);
        Assert.Equal(PreSignInOutbox.DiagnosticsBudget, payload.Diagnostics.Length);
        Assert.True(payload.Message.Length <= InstallReportLimits.MaxMessage);
    }

    [Fact]
    public async Task SendSignedIn_RemovesOnlyWhatTheDeviceRouteAccepted()
    {
        var (outbox, _) = NewOutbox();
        outbox.Keep(new[] { Item("Save FAILED: x"), Item("Load FAILED: y", source: "Store") });

        Assert.Equal(0, await outbox.SendSignedInAsync(60, _ => Task.FromResult(false)));
        Assert.Equal(2, outbox.Count);

        IReadOnlyList<ErrorReportItem>? handed = null;
        Assert.Equal(2, await outbox.SendSignedInAsync(60, items => { handed = items; return Task.FromResult(true); }));
        Assert.Equal(2, handed!.Count);
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public void AnUnreadableFile_IsMovedAsideNotDeleted()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var (outbox, _) = NewOutbox();

        Assert.Equal(0, outbox.Count);

        var aside = Assert.Single(Directory.GetFiles(_dir, "*.unreadable-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
    }

    [Fact]
    public void InstallId_IsCreatedOnce_AndAGarbledFileIsReplaced()
    {
        var first = InstallId.ReadOrCreate(_dir);
        Assert.True(Guid.TryParse(first, out _));
        Assert.Equal(first, InstallId.ReadOrCreate(_dir));

        File.WriteAllText(Path.Combine(_dir, InstallId.FileName), "garbage");
        var replaced = InstallId.ReadOrCreate(_dir);
        Assert.True(Guid.TryParse(replaced, out _));
        Assert.NotEqual(first, replaced);
    }
}
