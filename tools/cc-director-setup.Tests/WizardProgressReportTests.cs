using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Issue #3640: the Windows setup wizard users download reports a failed step and an error it shows, through the
/// same before-sign-in route as every other installer, instead of leaving it in the setup log on the machine.
/// </summary>
[Collection("WizardProgressReport")]
public sealed class WizardProgressReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wpr-{Guid.NewGuid():N}");
    private readonly CapturingHandler _handler = new();

    public WizardProgressReportTests()
    {
        WizardProgressReport.UseReporterForTests(new InstallFailureReporter(new InstallLayout(_root),
            WizardProgressReport.Installer, new HttpClient(_handler), () => "https://gateway.example.test/"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(Uri Url, string Body)> Posts = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Posts.Enqueue((request.RequestUri!, await request.Content!.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    [Fact]
    public async Task ComponentsFailed_AFailedComponent_SendsOneReportNamingIt_AndNoneForTheOthers()
    {
        var results = new[]
        {
            new ApplyResult("director", ApplyStatus.Failed, "2.16.0", "2.17.0", "SHA-256 mismatch for cc-director-win-x64.zip", null),
            new ApplyResult("launcher", ApplyStatus.Installed, "2.16.0", "2.17.0", null, null),
            new ApplyResult("gateway", ApplyStatus.Skipped, null, null, "Not in release", null),
        };

        var sends = WizardProgressReport.ComponentsFailed(results);
        var accepted = await Task.WhenAll(sends);

        Assert.Equal(new[] { true }, accepted);
        var (url, body) = Assert.Single(_handler.Posts);
        Assert.Equal("https://gateway.example.test/install-reports", url.ToString());
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        Assert.Equal(WizardProgressReport.Installer, r.GetProperty("installer").GetString());
        Assert.Equal("director", r.GetProperty("component").GetString());
        Assert.Equal("place", r.GetProperty("step").GetString());
        Assert.Contains("SHA-256 mismatch", r.GetProperty("message").GetString());
        Assert.Contains("to version: 2.17.0", r.GetProperty("diagnostics").GetString());
        Assert.Contains("setup log (last", r.GetProperty("diagnostics").GetString());
    }

    [Fact]
    public async Task Error_AnErrorTheWizardShows_IsReportedWithItsException()
    {
        var ex = new InvalidOperationException("the browser could not be started");

        var accepted = await WizardProgressReport.Error("report-problem", "could not open the browser", ex);

        Assert.True(accepted);
        var (_, body) = Assert.Single(_handler.Posts);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("report-problem", doc.RootElement.GetProperty("step").GetString());
        Assert.Contains("InvalidOperationException", doc.RootElement.GetProperty("diagnostics").GetString());
    }

    [Fact]
    public void CrashAndWait_SendsTheCrashBeforeReturning()
    {
        WizardProgressReport.CrashAndWait("crash", new NullReferenceException("boom"));

        var (_, body) = Assert.Single(_handler.Posts);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("crash", doc.RootElement.GetProperty("step").GetString());
        Assert.Contains("NullReferenceException", doc.RootElement.GetProperty("message").GetString());
    }
}
