using System.Net;
using System.Text.Json;
using CcDirector.Core.ErrorReports;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3311, B5 ("Send logs to DevThrottle"), B2 (a startup notice that shows off Windows) and B4 (the
/// launcher's own log file name).
/// </summary>
public sealed class LogSenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-logsender-test-" + Guid.NewGuid().ToString("N"));

    public LogSenderTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Url;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri!.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private string WriteLog(params string[] lines)
    {
        var path = Path.Combine(_dir, "launcher-2026-09-26-4242.log");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ReadTail_KeepsTheNewestWholeLinesThatFit_AndScrubsHomeFoldersAndKeys()
    {
        var path = WriteLog(Enumerable.Range(0, 2000).Select(i => $"line {i} at /Users/robert/Library/x token=abc{i}").ToArray());

        var tail = LogSender.ReadTail(path, 4000);

        Assert.True(tail.Length <= 4000 + 100);
        Assert.Contains("line 1999 at ~/Library/x token=<redacted>", tail);
        Assert.DoesNotContain("line 0 ", tail);
        Assert.DoesNotContain("robert", tail);
    }

    [Fact]
    public void ReadTail_AFileThisProcessStillHoldsOpenForWriting_IsRead()
    {
        var path = Path.Combine(_dir, "held.log");
        using var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        writer.WriteLine("still being written");

        Assert.Contains("still being written", LogSender.ReadTail(path, 4000));
    }

    [Fact]
    public async Task Send_PostsTheTailToThePublicRoute_WithTheInstallId_AndGivesAReference()
    {
        var path = WriteLog("[LauncherCore] Register FAILED: refused");
        var handler = new StubHandler(HttpStatusCode.Accepted);

        var result = await LogSender.SendAsync("launcher", "tray menu", path,
            () => "3f2a9c1e-0000-4000-8000-000000000003", () => "https://hosted.example", new HttpClient(handler), "2.9.0", CancellationToken.None);

        Assert.True(result.Sent);
        Assert.Equal("3f2a9c1e", result.Reference);
        Assert.Contains("3f2a9c1e", result.Detail);
        Assert.Equal("https://hosted.example/install-reports", handler.Url);
        var payload = JsonSerializer.Deserialize<InstallReportPayload>(handler.Body!)!;
        Assert.Equal(LogSender.Step, payload.Step);
        Assert.Equal("launcher", payload.Component);
        Assert.Equal("3f2a9c1e-0000-4000-8000-000000000003", payload.InstallId);
        Assert.Contains("Register FAILED: refused", payload.Diagnostics);
        Assert.Contains("tray menu", payload.Message);
    }

    [Fact]
    public async Task Send_RateLimited_SaysToTryLater_AndIsNotCalledSent()
    {
        var path = WriteLog("x");

        var result = await LogSender.SendAsync("director", "Help menu", path,
            () => "3f2a9c1e-0000-4000-8000-000000000003", () => "https://hosted.example",
            new HttpClient(new StubHandler(HttpStatusCode.TooManyRequests)), "2.9.0", CancellationToken.None);

        Assert.False(result.Sent);
        Assert.Contains("try again later", result.Detail);
    }

    [Fact]
    public void MacNotice_PassesTheTextAsArguments_NeverInsideTheScript()
    {
        const string hostile = "boom\" & do shell script \"rm -rf ~\" & \"";

        var args = NativeNotice.MacArguments(hostile, "Director - Startup error", NativeNotice.Kind.Error);

        var script = string.Join('\n', args.Where((_, i) => i > 0 && args[i - 1] == "-e"));
        Assert.DoesNotContain("rm -rf", script);
        Assert.Contains("as critical", script);
        Assert.Equal(hostile, args[^1]);
        Assert.Equal("Director - Startup error", args[^2]);
    }

    [Fact]
    public void LinuxNotice_TurnsMarkupOff_SoTheTextIsShownAsWritten()
    {
        var args = NativeNotice.LinuxArguments("<b>x</b>", "t", NativeNotice.Kind.Warning);

        Assert.Contains("--no-markup", args);
        Assert.Contains("--warning", args);
        Assert.Equal("<b>x</b>", args[^1]);
    }

    [Fact]
    public void LogFile_AProcessThatNamesItselfLauncher_WritesLauncherFiles()
    {
        var writer = new FileLogWriter(_dir, "4242", () => new DateTime(2026, 9, 26), "launcher");

        Assert.Equal(Path.Combine(_dir, "launcher-2026-09-26-4242.log"), writer.ComputeLogPath(new DateTime(2026, 9, 26)));
    }

    [Fact]
    public void LogFile_ByDefault_IsStillADirectorFile()
    {
        var writer = new FileLogWriter(_dir, "4242", () => new DateTime(2026, 9, 26));

        Assert.Equal(Path.Combine(_dir, "director-2026-09-26-4242.log"), writer.ComputeLogPath(new DateTime(2026, 9, 26)));
    }
}
