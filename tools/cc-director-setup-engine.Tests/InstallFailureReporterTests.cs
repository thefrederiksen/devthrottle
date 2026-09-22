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
}
