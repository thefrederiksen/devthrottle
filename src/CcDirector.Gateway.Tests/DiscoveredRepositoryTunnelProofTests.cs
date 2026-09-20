using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The one-repository-list mission, phase 2, proved END TO END: a repository found under a registered
/// root folder travels the tunnel a Director already pushes on, is held durably as found-but-never-opened,
/// and survives that Director going away.
///
/// What the one route then SERVES, and in what order, is phase 3 and is proved in
/// <see cref="OneRepositoryListTunnelProofTests"/>. These tests are about what is STORED, so they read the
/// rows out of the Gateway's own database file.
///
/// It writes through the HUB and reads through the ENDPOINT'S OWN PATH deliberately. The endpoint looks
/// rows up by the machine name on the Director REGISTRATION, so a writer that used any other spelling
/// would leave rows that exist while no screen could ever show them - and a store-level test that wrote
/// and read with the same string would pass while proving nothing about that.
/// </summary>
[Collection("DirectorRoot")]
public sealed class DiscoveredRepositoryTunnelProofTests : IAsyncLifetime
{
    private const string Token = "discovered-repository-proof-token";
    private const string DirectorId = "discovered-repository-director";
    private const string Machine = "SOREN_NORTH";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-discovered-repository-" + Guid.NewGuid().ToString("N"));
    private string? _previousRoot;
    // Assigned by xUnit's asynchronous lifecycle before any test runs.
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);
        Directory.CreateDirectory(_root);
        // A one-second staleness window, written before the Gateway starts and read by it exactly as a real
        // install's would be. The pushed repository snapshot is time-gated, and the contrast this proof
        // rests on - the in-memory copy going away while the catalog stays - needs that window to be short
        // enough to wait for rather than simulated.
        var configPath = CcStorage.ConfigJson();
        Directory.CreateDirectory(Path.GetDirectoryName(configPath) ?? _root);
        await File.WriteAllTextAsync(configPath, "{\"gateway\":{\"staleAfterSeconds\":1}}");
        _gateway = new GatewayHost(
            port: GatewayHost.OperatingSystemAssignedPort,
            token: Token,
            authEnabled: true,
            instancesDirectory: Path.Combine(_root, "instances"),
            workListsPath: Path.Combine(_root, "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + _gateway.Port + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _gateway.StopAsync();
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

    /// <summary>What a Director's root-folder scan pushes for one repository it found.</summary>
    private static RepoStatusDto Found(string path, string name, bool provisional = false) => new()
    {
        DirectorId = DirectorId,
        MachineName = Machine,
        Path = path,
        Name = name,
        Branch = "main",
        IsClean = true,
        Provisional = provisional,
    };

    /// <summary>
    /// The catalog rows the real Gateway really wrote, read straight out of its own database file - the
    /// discovered facts on the row itself (which Director reported it, and that it has no last-used time),
    /// which no read projects.
    /// </summary>
    private static List<(string Path, string Name, string MachineName, string? LastUsedUtc, string? DiscoveredBy)> CatalogRows()
    {
        var rows = new List<(string, string, string, string?, string?)>();
        using var connection = new SqliteConnection("Data Source=" + CcStorage.GatewayDb() + ";Mode=ReadOnly;Cache=Private");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Path, Name, MachineName, LastUsedUtc, DiscoveredByDirectorId FROM known_repositories ORDER BY Path";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        return rows;
    }

    private async Task<List<KnownRepositoryDto>> ServedAsync()
    {
        using var response = await _http.GetAsync("directors/" + DirectorId + "/known-repositories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<KnownRepositoryDto>>();
        Assert.NotNull(rows);
        return rows;
    }

    /// <summary>The machine name the READ side resolves this Director by - the registration's, never a payload's.</summary>
    private async Task<string> RegisteredMachineNameAsync()
    {
        using var response = await _http.GetAsync("directors");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var directors = await response.Content.ReadFromJsonAsync<List<DirectorDto>>();
        Assert.NotNull(directors);
        return directors.Single(d => d.DirectorId == DirectorId).MachineName;
    }

    [Fact]
    public async Task RootFolderScan_PushedUpTheTunnel_IsHeldAsFoundButNeverOpened_UnderTheMachineTheEndpointReads()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        await director.PushRepoSnapshotAsync(
            Found(@"D:\Repos\alpha", "alpha"),
            Found(@"D:\Repos\beta", "beta"));

        var rows = CatalogRows();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Null(row.LastUsedUtc));
        Assert.All(rows, row => Assert.Equal(DirectorId, row.DiscoveredBy));
        // The name the Director computed on the machine that owns the path, carried rather than re-derived:
        // the Gateway is a Linux container and this is a Windows path.
        Assert.Equal(new[] { "alpha", "beta" }, rows.Select(row => row.Name).ToArray());

        // THE TWO ENDS AGREE. The rows are written under exactly the machine name the read side resolves
        // this Director by, taken from the registration through the API rather than restated as a literal.
        var registered = await RegisteredMachineNameAsync();
        Assert.All(rows, row => Assert.Equal(registered, row.MachineName));

        // And the one route serves them (phase 3), in the Gateway's order: nothing here has ever been
        // opened, so the whole list is the never-opened half, by name.
        var served = await ServedAsync();
        Assert.Equal(new[] { @"D:\Repos\alpha", @"D:\Repos\beta" }, served.Select(row => row.Path).ToArray());
        Assert.All(served, row => Assert.True(row.NeverOpened));
        Assert.All(served, row => Assert.Null(row.LastUsed));
    }

    [Fact]
    public async Task DiscoveredRepositories_OutliveTheDirector_WhileThePushedSnapshotGoesStale()
    {
        await using (var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine))
        {
            await director.PushRepoSnapshotAsync(Found(@"D:\Repos\alpha", "alpha"));
            // While the tunnel is up the in-memory copy answers too.
            Assert.NotEmpty(await PushedRepositoriesAsync());
        }

        // The tunnel is gone and its endpoint was never reachable, so nothing can be pulled from this
        // Director. The in-memory copy ages out; this is the whole reason the catalog is a push and not a
        // pull - the other screens need the list exactly when the Director is unreachable.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await PushedRepositoriesAsync()).Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline,
                "the pushed repository snapshot never went stale, so this proof never reached the state it is about");
            await Task.Delay(100);
        }

        var row = Assert.Single(CatalogRows());
        Assert.Equal(@"D:\Repos\alpha", row.Path);
        Assert.Null(row.LastUsedUtc);
    }

    [Fact]
    public async Task RepositoryThatIsUsed_KeepsItsLastUsedTime_WhenTheRootFolderScanRunsOverIt()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);
        await director.PushSnapshotAsync(new SessionDto
        {
            SessionId = "session-1",
            Name = "Session 1",
            RepoName = "alpha",
            RepoPath = @"D:\Repos\alpha",
            Agent = "RawCli",
            CurrentModel = "configured-model",
            CreatedAt = DateTime.UtcNow.AddMinutes(-1),
            LastActivityAt = DateTime.UtcNow,
            ActivityState = "Working",
            Status = "Running",
        });
        var before = Assert.Single(await ServedAsync());

        await director.PushRepoSnapshotAsync(
            Found(@"D:\Repos\alpha", "alpha"),
            Found(@"D:\Repos\beta", "beta"));

        // One row for alpha, not two, and the time it was used is exactly where it was. The used half is
        // untouchable from the discovered side.
        var rows = CatalogRows();
        Assert.Equal(2, rows.Count);
        var alpha = rows.Single(row => row.Path == @"D:\Repos\alpha");
        Assert.NotNull(alpha.LastUsedUtc);
        Assert.Null(alpha.DiscoveredBy);
        // Served as one list: alpha keeps the time it was used and stays at the top, and beta - found by
        // the same scan and never opened - sits beneath it.
        var served = await ServedAsync();
        Assert.Equal(new[] { @"D:\Repos\alpha", @"D:\Repos\beta" }, served.Select(row => row.Path).ToArray());
        Assert.Equal(before.LastUsed, served[0].LastUsed);
        Assert.False(served[0].NeverOpened);
        Assert.True(served[1].NeverOpened);
    }

    [Fact]
    public async Task RootFolderRemoved_RemovesTheNeverOpenedRows_AndAnEmptyPushRemovesNothing()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);
        await director.PushRepoSnapshotAsync(
            Found(@"D:\Repos\alpha", "alpha"),
            Found(@"D:\Repos\beta", "beta"));
        Assert.Equal(2, CatalogRows().Count);

        // The beta root folder is unregistered, so the next complete scan no longer reports it.
        await director.PushRepoSnapshotAsync(Found(@"D:\Repos\alpha", "alpha"));
        Assert.Equal(@"D:\Repos\alpha", Assert.Single(CatalogRows()).Path);

        // An empty push and an all-provisional push both LOOK like "every repository was removed" - a cold
        // start before the first live scan, and a warm-cache push - and neither is.
        await director.PushRepoSnapshotAsync();
        Assert.Equal(@"D:\Repos\alpha", Assert.Single(CatalogRows()).Path);
        await director.PushRepoSnapshotAsync(Found(@"D:\Repos\gamma", "gamma", provisional: true));
        Assert.Equal(@"D:\Repos\alpha", Assert.Single(CatalogRows()).Path);
    }

    private async Task<List<RepoStatusDto>> PushedRepositoriesAsync()
    {
        using var response = await _http.GetAsync("repositories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<RepoStatusDto>>();
        Assert.NotNull(rows);
        return rows;
    }
}
