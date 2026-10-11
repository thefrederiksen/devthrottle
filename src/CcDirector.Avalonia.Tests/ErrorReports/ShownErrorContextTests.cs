using System.Net;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Avalonia.Tests.ErrorReports;

/// <summary>
/// Issue #3675, step 4a with <see cref="ErrorContext"/>: an error shown through the helper reaches the Gateway with
/// user_visible, surface and action as FIELDS of the report, not only as words in its message. Driven end to end on
/// the real route - <see cref="ShownError.Report"/>, the real <see cref="FileLog"/>, its error observer, a real
/// <see cref="ErrorReporter"/> - as far as the wire. It lives in this assembly because it starts the process-wide log,
/// and this assembly runs its tests one at a time.
/// </summary>
public sealed class ShownErrorContextTests
{
    [Fact]
    public async Task Report_TheSentReportCarriesUserVisibleSurfaceAndActionAsFields_AndOnlyThatRowDoes()
    {
        // Arrange
        var handler = new StubHandler();
        var config = new GatewayConfig { Url = "https://gateway.example", Token = "device-key" };
        using var reporter = new ErrorReporter(ErrorReportLimits.Director, () => config, new HttpClient(handler),
            () => new DateTime(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc), machineName: "TEST-MACHINE", productVersion: "2.15.0");
        using var log = FileLog.RedirectForTests();
        FileLog.ErrorObserver += reporter.OnLogLine;
        try
        {
            // Act: a path scope with the ids, the shown error inside it, then an ordinary error on the same path.
            using (ErrorContext.Begin(correlationId: "cmd-41", sessionId: "3f2a9c1e-0000-4000-8000-000000000041"))
            {
                ShownError.Report("worktrees", "read the worktrees", "Could not read worktrees: access denied");
                Assert.Null(ErrorContext.Current!.UserVisible); // the shown row's scope closed behind it
                FileLog.Write("[WorktreesView] Refresh FAILED: the background scan stopped");
            }
            await reporter.SendPendingAsync(CancellationToken.None);
        }
        finally
        {
            FileLog.ErrorObserver -= reporter.OnLogLine;
        }

        // Assert: on the wire, as named fields.
        var body = Assert.Single(handler.Requests);
        using var json = JsonDocument.Parse(body);
        var rows = json.RootElement.GetProperty("reports").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        var shown = Assert.Single(rows, r => r.GetProperty("message").GetString()!.Contains("could not read the worktrees"));
        Assert.True(shown.GetProperty("user_visible").GetBoolean());
        Assert.Equal("worktrees", shown.GetProperty("surface").GetString());
        Assert.Equal("read the worktrees", shown.GetProperty("action").GetString());
        Assert.Equal("cmd-41", shown.GetProperty("correlation_id").GetString()); // the caller's ids still apply
        Assert.Equal("3f2a9c1e-0000-4000-8000-000000000041", shown.GetProperty("session_id").GetString());

        // The row logged beside it on the same path shares the ids and is NOT marked as seen by the user.
        var other = Assert.Single(rows, r => r.GetProperty("message").GetString()!.Contains("background scan stopped"));
        Assert.Equal("cmd-41", other.GetProperty("correlation_id").GetString());
        Assert.False(other.TryGetProperty("user_visible", out var uv) && uv.ValueKind == JsonValueKind.True);
        Assert.False(other.TryGetProperty("surface", out var sf) && sf.ValueKind == JsonValueKind.String);
        Assert.False(other.TryGetProperty("action", out var ac) && ac.ValueKind == JsonValueKind.String);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<string> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }
}
