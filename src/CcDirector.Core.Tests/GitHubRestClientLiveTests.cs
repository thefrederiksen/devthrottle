using CcDirector.Core.Backends;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Live tests that hit the REAL GitHub REST API using the token in credentials.env.
/// Gated behind GITHUB_LIVE_TESTS=1 so the normal suite stays offline/hermetic.
/// These prove GitHubRestClient authenticates and parses real GitHub JSON - the
/// thing the StubGitHubClient unit tests cannot cover.
/// </summary>
public sealed class GitHubRestClientLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("GITHUB_LIVE_TESTS") == "1";

    // Target repo as "owner/repo". Kept out of source so no account name is committed.
    private static string? RepoSlug => Environment.GetEnvironmentVariable("GITHUB_TEST_REPO");

    [Fact]
    public async Task ListRuns_AgainstRealRepo_AuthenticatesAndParses()
    {
        if (!Enabled) return; // gated: no-op pass unless GITHUB_LIVE_TESTS=1

        var slug = RepoSlug;
        if (string.IsNullOrWhiteSpace(slug) || !slug.Contains('/'))
            return; // needs GITHUB_TEST_REPO=owner/repo

        var parts = slug.Split('/', 2);
        var owner = parts[0];
        var repo = parts[1];

        var token = GitHubCredentials.ReadToken();
        using var client = new GitHubRestClient(token);

        var runs = await client.ListRunsAsync(owner, repo, eventName: "",
            DateTimeOffset.UtcNow.AddDays(-365), CancellationToken.None);

        Assert.NotNull(runs);
        // If there are runs, GetRun must round-trip the newest one.
        if (runs.Count > 0)
        {
            var one = await client.GetRunAsync(owner, repo, runs[0].Id, CancellationToken.None);
            Assert.Equal(runs[0].Id, one.Id);
            Assert.False(string.IsNullOrEmpty(one.Status));
        }
    }
}
