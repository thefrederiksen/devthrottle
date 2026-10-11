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

        // The scan read the real tree: the file that used to start the poll, and the Gateway client
        // that makes a real outbound call, are both in it. (The account store that used to be the second
        // witness was retired by the License Switching mission.)
        Assert.Contains(files, f => f.EndsWith(Path.Combine("CcDirector.Avalonia", "App.axaml.cs"), StringComparison.Ordinal));
        Assert.Contains(files, f => f.EndsWith(Path.Combine("CcDirector.ControlApi", "GatewayClient.cs"), StringComparison.Ordinal));
        Assert.True(files.Count > 500, $"expected the Director's sources, found {files.Count} files under {src}");

        var offenders = files.Where(f => File.ReadAllText(f).Contains(RetiredEndpoint, StringComparison.Ordinal)).ToList();
        Assert.True(offenders.Count == 0,
            "the Director must not poll the provider's usage endpoint; found it in:\n" + string.Join("\n", offenders));
    }
}
