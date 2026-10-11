using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Background;

/// <summary>
/// THE DIRECTOR DOES NOT POLL THE MODEL PROVIDER'S USAGE ENDPOINT (Director Optimizer mission,
/// devthrottle_internal #2237, Stage 1 item 6).
///
/// Until 22 September 2026 a service started at application start fetched the provider's OAuth usage
/// endpoint once a minute for every stored account and raised an event that nothing in the product
/// subscribed to: 1,882 log lines a day, one web request a minute, for a number nobody displayed. A
/// call whose result nothing reads is deleted, not kept for later - code that stays gets called again.
///
/// Presence, not absence (skill checks-that-fail-open): the scan proves it read the Director's own
/// sources by finding the file that used to start the poll and one that still makes a real outbound
/// call, so a scan that found nothing cannot pass.
///
/// CHANGED ON PURPOSE BY THE LICENSE SWITCHING MISSION (contract section 1.1). Usage is read again, because a
/// reader now exists: the Gateway's switch decision and the Licenses page. The reading itself is done by the
/// Python license tool, so the Director's C# still names no usage endpoint and the absence check stays as it was.
/// What is ADDED is the presence the old rule was really about - "a call whose result nothing reads is deleted":
/// <see cref="LicenseUsageReader_ResultReachesPushLicenseUsage"/> follows the reader's result, link by link, to the
/// hub method that carries it to the Gateway, so an unread poll cannot come back under cover of this one. The
/// behaviour of each link is tested in LicenseUsageReporterTests; this test proves the links are connected in the
/// product, where a test of the parts cannot see.
/// </summary>
public sealed class NoUnreadUsagePollTests
{
    private const string RetiredEndpoint = "api/oauth/usage";

    [Fact]
    public void NoDirectorSource_NamesTheProvidersUsageEndpoint()
    {
        var src = Path.Combine(RepositorySourceIndex.Root, "src");
        var directorProjects = new[] { "CcDirector.Core", "CcDirector.Avalonia", "CcDirector.ControlApi", "CcDirector.Engine" };
        var files = directorProjects
            .Select(p => Path.Combine(src, p))
            .Where(Directory.Exists)
            .SelectMany(d => RepositorySourceIndex.Under(d, ".cs"))
            .ToList();

        // The scan read the real tree: the file that used to start the poll, and the account store
        // that still talks to the provider for a token refresh, are both in it.
        Assert.Contains(files, f => f.EndsWith(Path.Combine("CcDirector.Avalonia", "App.axaml.cs"), StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith(Path.Combine("Claude", "ClaudeAccountStore.cs"), StringComparison.Ordinal));
        Assert.True(files.Count > 500, $"expected the Director's sources, found {files.Count} files under {src}");

        var offenders = files.Where(f => File.ReadAllText(f).Contains(RetiredEndpoint, StringComparison.Ordinal)).ToList();
        Assert.True(offenders.Count == 0,
            "the Director must not poll the provider's usage endpoint; found it in:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void LicenseUsageReader_ResultReachesPushLicenseUsage()
    {
        var src = Path.Combine(RepositorySourceIndex.Root, "src");
        string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { src }.Concat(parts).ToArray()));

        // 1. The reporter reads usage through the license tool and hands the report it builds to its sink.
        var reporter = Read("CcDirector.Core", "Licenses", "LicenseUsageReporter.cs");
        Assert.Contains("_tool.ReadUsageAsync(", reporter, StringComparison.Ordinal);
        Assert.Contains("await sink.SendAsync(report, cancellationToken);", reporter, StringComparison.Ordinal);

        // 2. The sink in the product is the Gateway tunnel's PushLicenseUsage.
        var sink = Read("CcDirector.ControlApi", "GatewayLicenseReportSink.cs");
        Assert.Contains("_client.PushLicenseUsageAsync(report, cancellationToken)", sink, StringComparison.Ordinal);
        var stream = Read("CcDirector.ControlApi", "GatewayStreamClient.cs");
        Assert.Contains("conn.InvokeAsync(LicenseContract.PushUsageHubMethod, seq, report", stream, StringComparison.Ordinal);

        // 3. The host attaches that sink, and the Director application starts the reporter's loop.
        var host = Read("CcDirector.ControlApi", "ControlApiHost.cs");
        Assert.Contains("AttachSink(streamClient is null ? null : new GatewayLicenseReportSink(streamClient))", host, StringComparison.Ordinal);
        Assert.Contains("AttachLicenseReporting(_streamClient);", host, StringComparison.Ordinal);
        var app = Read("CcDirector.Avalonia", "App.axaml.cs");
        Assert.Contains("LicenseSwitching.Shared.Start();", app, StringComparison.Ordinal);
    }
}
