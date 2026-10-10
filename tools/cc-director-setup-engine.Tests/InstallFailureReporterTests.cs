using System.Net;
using System.Text.Json;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The installer's failure report (issue #3311): what leaves the machine, where it goes, and that a
/// report that cannot be delivered never turns into an install failure of its own.
/// </summary>
public sealed class InstallFailureReporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ifr-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? Url;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Url = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("no route to host");
    }

    [Theory]
    [InlineData("/Users/robertziegler/Library/Application Support/cc-director/logs", "~/Library/Application Support/cc-director/logs")]
    [InlineData("/home/alice/.local/share/cc-director", "~/.local/share/cc-director")]
    [InlineData(@"C:\Users\Bob\AppData\Local\cc-director", @"~\AppData\Local\cc-director")]
    public void Scrub_ReducesTheHomeFolderToTilde(string input, string expected)
    {
        Assert.Equal(expected, InstallFailureReporter.Scrub(input));
    }

    [Fact]
    public async Task ReportAsync_PostsAScrubbedReportToTheGateway()
    {
        var handler = new CapturingHandler(HttpStatusCode.Accepted);
        var reporter = new InstallFailureReporter(new InstallLayout(_root), "setup-wizard",
            new HttpClient(handler), () => "https://gateway.example.test/");

        var ok = await reporter.ReportAsync("launcher", "start",
            "Check /Users/robertziegler/Library/Application Support/cc-director/logs",
            "path = /Users/robertziegler/Library/LaunchAgents/com.devthrottle.cc-launcher.plist");

        Assert.True(ok);
        Assert.Equal("https://gateway.example.test/install-reports", handler.Url!.ToString());
        Assert.DoesNotContain("robertziegler", handler.Body);

        using var doc = JsonDocument.Parse(handler.Body!);
        var r = doc.RootElement;
        Assert.Equal("setup-wizard", r.GetProperty("installer").GetString());
        Assert.Equal("launcher", r.GetProperty("component").GetString());
        Assert.Equal("start", r.GetProperty("step").GetString());
        Assert.Contains("~/Library/Application Support", r.GetProperty("message").GetString());
        Assert.True(Guid.TryParse(r.GetProperty("install_id").GetString(), out _));
    }

    [Fact]
    public async Task ReportAsync_ACredentialAnIdLineAndThisMachinesNames_NeverLeaveTheMachine()
    {
        // Issue #3644: the installer scrubs with the same rules as Director errors, before sending.
        var handler = new CapturingHandler(HttpStatusCode.Accepted);
        var reporter = new InstallFailureReporter(new InstallLayout(_root), "setup-wizard",
            new HttpClient(handler), () => "https://gateway.example.test/");
        var key = "Kq3vZ8wYp2LmN5tR7xB1cD4fG6hJ9kQ0sT2uV5wX8yZ";

        await reporter.ReportAsync("launcher", "start",
            $"enroll FAILED: Authorization: Bearer {key} on {Environment.MachineName} for {Environment.UserName}",
            "id: uid=501(robertziegler) gid=20(staff) groups=20(staff),80(admin)\n" +
            $"connect FAILED token={key}\nhome: C:/Users/robertziegler/AppData");

        Assert.NotNull(handler.Body);
        Assert.DoesNotContain(key, handler.Body);
        Assert.DoesNotContain("robertziegler", handler.Body);
        Assert.DoesNotContain("(staff)", handler.Body);
        Assert.Contains("uid=501 gid=20 groups=20,80", handler.Body);
        if (Environment.MachineName.Length >= 3)
            Assert.DoesNotContain(Environment.MachineName, handler.Body, StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName.Length >= 3)
            Assert.DoesNotContain(Environment.UserName, handler.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallMacScript_SendsNumericIdsOnly_AndScrubsThisMacsNames()
    {
        // The macOS one-line installer is a shell script, so its report cannot be run here. What it may put in a
        // report is read from the script itself: the bare "id" command names the person (issue #3644).
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "install-mac.sh"));

        Assert.DoesNotContain("$(id 2>", script);
        Assert.DoesNotContain("$(id)", script);
        Assert.Contains("id -u", script);
        Assert.Contains("<machine>", script);
        Assert.Contains("<user>", script);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "install-mac.sh"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root (with scripts/install-mac.sh) was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void InstallId_IsStableAcrossReportsOnOneMachine()
    {
        var reporter = new InstallFailureReporter(new InstallLayout(_root), "setup-wizard",
            new HttpClient(new CapturingHandler(HttpStatusCode.Accepted)), () => "https://gateway.example.test");

        Assert.Equal(reporter.InstallId(), reporter.InstallId());
    }

    [Fact]
    public async Task ReportAsync_UndeliverableReport_ReturnsFalseAndDoesNotThrow()
    {
        var reporter = new InstallFailureReporter(new InstallLayout(_root), "setup-wizard",
            new HttpClient(new ThrowingHandler()), () => "https://gateway.example.test");

        Assert.False(await reporter.ReportAsync("launcher", "start", "boom", null));
    }

    [Fact]
    public async Task ReportAsync_RefusedReport_ReturnsFalse()
    {
        var reporter = new InstallFailureReporter(new InstallLayout(_root), "setup-wizard",
            new HttpClient(new CapturingHandler(HttpStatusCode.TooManyRequests)), () => "https://gateway.example.test");

        Assert.False(await reporter.ReportAsync("launcher", "start", "boom", null));
    }

    [Fact]
    public void BuildPayload_CutsTheMessageAndTheDiagnosticsToTheirLimits_AfterEverythingWasAppended()
    {
        var reporter = new InstallFailureReporter(new InstallLayout(_root), "setup-wizard", gatewayUrl: () => "http://127.0.0.1:9");
        var longMessage = new string('m', CcDirector.Core.ErrorReports.InstallReportLimits.MaxMessage + 500);
        var longDiagnostics = string.Join('\n', Enumerable.Range(1, 2000).Select(i => $"line {i:D4} of a very long report"));

        var payload = reporter.BuildPayload("launcher", "start", longMessage, longDiagnostics);

        Assert.True(payload.Message.Length <= CcDirector.Core.ErrorReports.InstallReportLimits.MaxMessage, $"message {payload.Message.Length}");
        Assert.EndsWith("(cut to fit the report)", payload.Message);
        Assert.True(payload.Diagnostics.Length <= CcDirector.Core.ErrorReports.InstallReportLimits.MaxDiagnostics, $"diagnostics {payload.Diagnostics.Length}");
        Assert.EndsWith("(cut to fit the report)", payload.Diagnostics);
        Assert.StartsWith("line 0001", payload.Diagnostics);
    }

    [Fact]
    public void BuildPayload_ControlCharactersThatSwellWhenSerialized_StillFitTheBodyLimit()
    {
        // 16,000 control characters are 16,000 characters and 96,000 bytes of JSON: the field limit is
        // satisfied and the Gateway would still refuse the body. The body is what is measured.
        var reporter = new InstallFailureReporter(new InstallLayout(_root), "setup-wizard", gatewayUrl: () => "http://127.0.0.1:9");
        var control = new string('\u0001', CcDirector.Core.ErrorReports.InstallReportLimits.MaxDiagnostics - 100);

        var payload = reporter.BuildPayload("launcher", "start", "m", control);

        Assert.True(InstallFailureReporter.BodyBytes(payload) <= CcDirector.Core.ErrorReports.InstallReportLimits.MaxBodyBytes,
            $"body {InstallFailureReporter.BodyBytes(payload)} bytes");
        Assert.EndsWith("(cut to fit the report)", payload.Diagnostics);
    }

    [Fact]
    public void BuildPayload_OneGiantLineAndNonAsciiText_FitTheirLimits()
    {
        var reporter = new InstallFailureReporter(new InstallLayout(_root), "setup-wizard", gatewayUrl: () => "http://127.0.0.1:9");
        var giant = new string('\u00e9', 40000);

        var payload = reporter.BuildPayload("launcher", "start", giant, giant);

        Assert.True(payload.Message.Length <= CcDirector.Core.ErrorReports.InstallReportLimits.MaxMessage);
        Assert.True(payload.Diagnostics.Length <= CcDirector.Core.ErrorReports.InstallReportLimits.MaxDiagnostics);
        Assert.True(InstallFailureReporter.BodyBytes(payload) <= CcDirector.Core.ErrorReports.InstallReportLimits.MaxBodyBytes);
    }

    [Fact]
    public void BuildPayload_ShortTexts_AreLeftAlone()
    {
        var reporter = new InstallFailureReporter(new InstallLayout(_root), "setup-wizard", gatewayUrl: () => "http://127.0.0.1:9");

        var payload = reporter.BuildPayload("launcher", "start", "short", "also short");

        Assert.Equal("short", payload.Message);
        Assert.Equal("also short", payload.Diagnostics);
    }
}
