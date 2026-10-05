using CcDirector.Gateway.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE REPOSITORY HISTORY IS SAVED BY THE HOST, PROVED END TO END (Money Saver, 4 October 2026).
///
/// An observation no longer writes the history file; the host writes it on a five-minute clock and once at the end
/// of a graceful stop. Every store-level test calls <c>SaveIfChanged</c> by hand, so they would all stay green if
/// the host's wiring were deleted and the hosted Gateway never wrote the file again. This one goes through a real
/// Director tunnel and a real <see cref="GatewayHost"/>, and reads the file only after <c>StopAsync</c>.
/// </summary>
[Collection("DirectorRoot")]
public sealed class RepoHistoryIsSavedAtShutdownTunnelProofTests : IAsyncLifetime
{
    private const string Token = "repo-history-shutdown-proof-token";
    private const string DirectorId = "repo-history-shutdown-director";
    private const string Machine = "SOREN_NORTH";
    private const string RepoName = "history-shutdown-proof-repo";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-repo-history-shutdown-" + Guid.NewGuid().ToString("N"));
    private string? _previousRoot;
    private GatewayHost? _gateway;

    public async Task InitializeAsync()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(_root);
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: Token,
            authEnabled: true,
            instancesDirectory: Path.Combine(_root, "instances"),
            workListsPath: Path.Combine(_root, "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_gateway is not null) await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of a throwaway test root.
        }
    }

    [Fact]
    public async Task PushedRepositoryHistory_IsWrittenToTheFile_WhenTheGatewayStops()
    {
        var historyPath = Path.Combine(_root, "repo-history.jsonl");
        await using (var director = await FakeTunnelDirector.StartAsync(_gateway!, Token, DirectorId, Machine))
        {
            await director.PushRepoSnapshotAsync(new RepoStatusDto
            {
                DirectorId = DirectorId,
                MachineName = Machine,
                Path = "D:/Repos/" + RepoName,
                Name = RepoName,
                Branch = "main",
                IsClean = true,
                WorktreeCount = 3,
            });
        }

        // The push was observed, but nothing has written it: the clock's first tick is five minutes away.
        Assert.False(File.Exists(historyPath) && File.ReadAllText(historyPath).Contains(RepoName),
            "an observation must not write the file; the host's clock and shutdown do");

        await _gateway!.StopAsync();
        _gateway = null;

        Assert.True(File.Exists(historyPath), "a graceful stop must write the unsaved repository history");
        Assert.Contains(RepoName, File.ReadAllText(historyPath));
    }
}
