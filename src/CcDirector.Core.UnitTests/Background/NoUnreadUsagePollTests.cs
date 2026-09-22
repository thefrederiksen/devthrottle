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
        var src = FindSourceRoot();
        var directorProjects = new[] { "CcDirector.Core", "CcDirector.Avalonia", "CcDirector.ControlApi", "CcDirector.Engine" };
        var files = directorProjects
            .Select(p => Path.Combine(src, p))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
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

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CcDirector.Core");
            if (Directory.Exists(candidate)) return Path.Combine(dir.FullName, "src");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find the repository's src folder above " + AppContext.BaseDirectory);
    }
}
