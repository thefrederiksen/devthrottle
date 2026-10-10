using System.Net;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using Xunit;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3675, step 4a: the one call that shows an error to the user and reports it. The line it writes must be an
/// error line the reporter picks up, name the code that showed it, say what the user was trying to do, carry the
/// exception's type and stack, and carry what the site chose to report rather than the shown text when they differ.
/// The last test runs the line through a real reporter end to end, as far as the wire.
/// </summary>
public sealed class ShownErrorTests
{
    private const string CallerFile = @"C:\src\CcDirector.Avalonia\TurnReviewDialog.axaml.cs";

    [Fact]
    public void Report_ReturnsTheShownTextUnchanged()
    {
        var shown = ShownError.Report("turn reviews", "load the turn reviews", "Failed to load reviews.\n\nTry again.");

        Assert.Equal("Failed to load reviews.\n\nTry again.", shown);
    }

    [Fact]
    public void LineFor_IsAnErrorLine_NamedForTheCallersClassAndMethod()
    {
        var line = ShownError.LineFor("turn reviews", "load the turn reviews", "Failed to load reviews.", null, null,
            fatal: false, CallerFile, "LoadAsync");

        Assert.True(ErrorLine.IsError(line));
        Assert.Equal("logged", ErrorLine.KindOf(line));
        var (source, message, _, stack) = ErrorLine.Parse(line);
        Assert.Equal("TurnReviewDialog", source);
        Assert.Equal("LoadAsync FAILED: could not load the turn reviews: Failed to load reviews. (surface: turn reviews)", message);
        Assert.Equal("", stack);
    }

    [Fact]
    public void LineFor_WithAnException_CarriesItsTypeAndStackAfterTheFirstLine()
    {
        Exception thrown;
        try { throw new IOException("disk full"); }
        catch (IOException ex) { thrown = ex; }

        var line = ShownError.LineFor("settings", "save the settings", "Save failed: disk full", thrown, null,
            fatal: false, CallerFile, "BtnSave_Click");

        var (_, message, exceptionType, stack) = ErrorLine.Parse(line);
        Assert.StartsWith("BtnSave_Click FAILED: could not save the settings: Save failed: disk full", message);
        Assert.Equal("System.IO.IOException", exceptionType);
        Assert.Contains("ShownErrorTests", stack);
    }

    [Fact]
    public void LineFor_ShownTextOverSeveralLines_StaysOnTheFirstLine()
    {
        var line = ShownError.LineFor("main window", "start the session", "Director could not start the session.\r\n\r\nSee the log.",
            null, null, fatal: false, CallerFile, "M");

        var (_, message, _, stack) = ErrorLine.Parse(line);
        Assert.Contains("Director could not start the session. See the log.", message);
        Assert.Equal("", stack);
    }

    [Fact]
    public void LineFor_WithReported_CarriesTheReportedTextAndNeverTheShownText()
    {
        var line = ShownError.LineFor("main window", "send the dictation", "Your words were: please delete the branch",
            null, reported: "the dictation was not sent: timed out", fatal: false, CallerFile, "M");

        Assert.Contains("the dictation was not sent: timed out", line);
        Assert.DoesNotContain("please delete the branch", line);
    }

    [Fact]
    public void LineFor_Fatal_IsAFatalLine()
    {
        var line = ShownError.LineFor("start-up", "start the Director", "Director failed to start", null, null,
            fatal: true, @"/home/build/src/CcDirector.Avalonia/App.axaml.cs", "HandleFatalStartupError");

        Assert.True(ErrorLine.IsError(line));
        Assert.Equal("fatal", ErrorLine.KindOf(line));
        Assert.Equal("App", ErrorLine.Parse(line).Source);
    }

    [Theory]
    [InlineData(@"C:\a\b\SettingsDialog.axaml.cs", "SettingsDialog")]
    [InlineData("/a/b/BusyAction.cs", "BusyAction")]
    [InlineData("", "ShownError")]
    public void ClassNameOf_IsTheFileNameUpToItsFirstDot(string file, string expected)
        => Assert.Equal(expected, ShownError.ClassNameOf(file));

    [Theory]
    [InlineData("", "load")]
    [InlineData("settings", " ")]
    public void Report_WithoutASurfaceOrAnAction_Throws(string surface, string action)
        => Assert.ThrowsAny<ArgumentException>(() => ShownError.Report(surface, action, "x"));

    [Fact]
    public async Task TheLine_ReachesTheGateway_AsOneScrubbedReportNamedForTheCaller()
    {
        var handler = new StubHandler();
        var cfg = new GatewayConfig { Url = "https://gateway.example", Token = "device-key" };
        using var reporter = new ErrorReporter(ErrorReportLimits.Director, () => cfg, new HttpClient(handler),
            () => new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc), machineName: "TEST-MACHINE", productVersion: "2.15.0");

        reporter.OnLogLine(ShownError.LineFor("worktrees", "read the worktrees",
            @"Could not read worktrees: access denied to C:\Users\robert\repo", null, null, fatal: false,
            @"C:\src\CcDirector.Avalonia\Controls\WorktreesView.axaml.cs", "Render"));
        await reporter.SendPendingAsync(CancellationToken.None);

        var item = Assert.Single(JsonSerializer.Deserialize<ErrorReportBatch>(Assert.Single(handler.Requests))!.Reports!);
        Assert.Equal("WorktreesView", item.Source);
        Assert.Equal("logged", item.Kind);
        Assert.Contains("could not read the worktrees", item.Message);
        Assert.DoesNotContain("robert", item.Message);
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
