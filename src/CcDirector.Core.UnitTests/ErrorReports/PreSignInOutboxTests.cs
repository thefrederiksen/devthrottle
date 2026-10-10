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
    public void Keep_Full_ANewDistinctErrorTakesThePlaceOfTheMostRepeated_AndItsSourceIsCounted()
    {
        // Issue #3647: past the limit a new distinct error used to be only a number. Now the entry seen most often
        // gives way (the oldest of those), and the occurrences it carried are counted under its source.
        var (outbox, _) = NewOutbox();
        var noisy = Item("Inventory FAILED: scan", count: 40, source: "WorktreeInventoryService");
        outbox.Keep(new[] { noisy }.Concat(Enumerable.Range(1, PreSignInOutbox.MaxKept - 1)
            .Select(i => Item($"Save FAILED: {i}", source: $"C{i}"))).ToList());

        outbox.Keep(new[] { Item("Enroll FAILED: refused", source: "Enrollment") });

        Assert.Equal(PreSignInOutbox.MaxKept, outbox.Count);
        var text = File.ReadAllText(FilePath);
        Assert.Contains("Enroll FAILED: refused", text);
        Assert.DoesNotContain("Inventory FAILED: scan", text);
        Assert.Contains("\"not_kept\":1", text);
        Assert.Contains("\"not_kept_by_source\":{\"WorktreeInventoryService\":40}", text);
    }

    [Fact]
    public void Keep_FullOfOneOffs_TheNewcomerIsTheOneCounted_UnderItsSource()
    {
        var (outbox, _) = NewOutbox();
        outbox.Keep(Enumerable.Range(0, PreSignInOutbox.MaxKept).Select(i => Item($"Save FAILED: {i}", source: $"C{i}")).ToList());

        outbox.Keep(new[] { Item("Load FAILED: late", source: "Store") });

        Assert.DoesNotContain("Load FAILED: late", File.ReadAllText(FilePath));
        Assert.Contains("\"not_kept_by_source\":{\"Store\":1}", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Tally_PastTheLimitOfNamedSources_AddsTheRestUpTogether()
    {
        var bySource = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < PreSignInOutbox.MaxDroppedSources + 5; i++) PreSignInOutbox.Tally(bySource, $"S{i}", 2);

        Assert.Equal(PreSignInOutbox.MaxDroppedSources + 1, bySource.Count);
        Assert.Equal(10, bySource[PreSignInOutbox.OtherSources]);
    }

    [Fact]
    public async Task SendBeforeSignIn_SaysWhichSourcesLostErrors_AndClearsTheCountOnceAccepted()
    {
        var (outbox, handler) = NewOutbox();
        outbox.Keep(Enumerable.Range(0, PreSignInOutbox.MaxKept).Select(i => Item($"Save FAILED: {i}", source: $"C{i}")).ToList());
        outbox.Keep(new[] { Item("Load FAILED: a", source: "Store"), Item("Load FAILED: b", source: "Store") });

        await outbox.SendBeforeSignInAsync(final: true, CancellationToken.None);

        Assert.Contains("2 further distinct errors were not kept because the queue on disk was full; the occurrences given up, by source: Store 2 occurrences.",
            Payload(handler).Message);
        Assert.DoesNotContain("not_kept_by_source", File.ReadAllText(FilePath));
        Assert.Contains("\"not_kept\":0", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task SendSignedIn_TheDroppedCountTravelsAsAReportOfItsOwn_AndIsClearedWhenAccepted()
    {
        // Issue #3647: a signed-in machine never sends on the install route again, so the count must go to the
        // account on the device route, or it would stay in the file for ever.
        var (outbox, _) = NewOutbox();
        outbox.Keep(Enumerable.Range(0, PreSignInOutbox.MaxKept).Select(i => Item($"Save FAILED: {i}", source: $"C{i}")).ToList());
        outbox.Keep(new[] { Item("Load FAILED: late", source: "Store", count: 3) });

        var handed = new List<ErrorReportItem>();
        for (var tick = 0; tick < 100 && File.Exists(FilePath); tick++)
            await outbox.SendSignedInAsync(ErrorReportLimits.MaxReportsPerBatch, items => { handed.AddRange(items); return Task.FromResult(true); });

        Assert.Equal(PreSignInOutbox.MaxKept + 1, handed.Count);
        var dropped = Assert.Single(handed, i => i.Kind == PreSignInOutbox.DroppedKind);
        Assert.Contains("1 distinct error logged by the director before this machine signed in was not kept", dropped.Message);
        Assert.Contains("the occurrences given up, by source: Store 3 occurrences.", dropped.Message);
        Assert.False(File.Exists(FilePath));
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
        Assert.Contains("4 further distinct errors were not kept", payload.Message);
        Assert.Contains("[C0] Save FAILED: 0", payload.Message);
    }

    [Fact]
    public void Compose_AnEntryKeptBeforeItsSenderScrubbed_LeavesWithoutCredentialIdNamesOrThisMachinesNames()
    {
        // Issue #3644: an entry written to the file by an older version, before the sender scrubbed with this
        // machine's names, is scrubbed again on its way out.
        var key = "Kq3vZ8wYp2LmN5tR7xB1cD4fG6hJ9kQ0sT2uV5wX8yZ";
        var item = Item($"Enroll FAILED on {Environment.MachineName} for {Environment.UserName}: token={key}",
            stack: "uid=501(robertziegler) gid=20(staff)\n   at C:/Users/robertziegler/x.cs");

        var (payload, _) = PreSignInOutbox.Compose("launcher", "id-12345678", new[] { item }, 0);
        var leaves = payload.Message + "\n" + payload.Diagnostics;

        Assert.DoesNotContain(key, leaves);
        Assert.DoesNotContain("robertziegler", leaves);
        Assert.DoesNotContain("(staff)", leaves);
        if (Environment.MachineName.Length >= 3)
            Assert.DoesNotContain(Environment.MachineName, leaves, StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName.Length >= 3)
            Assert.DoesNotContain(Environment.UserName, leaves, StringComparison.OrdinalIgnoreCase);
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
    public async Task SendSignedIn_AFileThatCannotBeRead_CostsTheTick_NeverThrows()
    {
        // Review of #3425: this read ran unguarded on every tick of a signed-in machine, and an exception
        // from it ended the reporter's send loop for the life of the process.
        var (outbox, _) = NewOutbox();
        outbox.Keep(new[] { Item("Save FAILED: locked") });

        int delivered;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            delivered = await outbox.SendSignedInAsync(10, _ => Task.FromResult(true));

        Assert.Equal(0, delivered);
        Assert.Equal(1, outbox.Count);
    }

    [Fact]
    public async Task SendBeforeSignIn_AcceptedButTheFileIsLockedAfterwards_NeverThrows_AndKeepsTheErrorsToSendAgain()
    {
        var (outbox, handler) = NewOutbox();
        outbox.Keep(new[] { Item("Save FAILED: locked after send") });
        FileStream? held = null;
        handler.DuringSend = () => held = new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        int sent;
        try
        {
            sent = await outbox.SendBeforeSignInAsync(final: true, CancellationToken.None);
        }
        finally
        {
            held?.Dispose();
        }

        Assert.Equal(1, sent);
        // Could not be taken out, so it goes again: a duplicate, never a loss.
        Assert.Equal(1, outbox.Count);
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
