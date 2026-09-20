using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Core.Storage;
using CcDirector.Gateway.Contracts;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE ONE LIST, ONE ORDER, ONE ROUTE, PROVED END TO END (the one-repository-list mission, phase 3).
///
/// A real Director tunnel to a started <see cref="GatewayHost"/> pushes both halves of the catalog the way
/// a real Director does - session snapshots for the repositories that have been used, and the root-folder
/// scan for the ones that have only been found - and the one route the Cockpit, the phone and the
/// Director's own dialog read serves them as ONE list in ONE order, over real HTTP.
///
/// The case that matters is the MIXED machine: some repositories used, some never opened. The never-opened
/// ones must be at the BOTTOM. That is goal 2 of the mission, and it is the assertion a client would
/// otherwise have to make for itself - which is exactly what Critical Rule 7 forbids.
///
/// WHY THERE IS NO ASSERTION HERE THAT ONE PUSH IS NEWER THAN ANOTHER. The Gateway stamps the last-used
/// time itself, from its own clock, and <c>DateTime.UtcNow</c> is coarse on Windows - two pushes a few
/// milliseconds apart can carry the same instant. A test that asserted their relative order would be
/// measuring how busy the machine is rather than the product. What is asserted instead is the property
/// that holds whatever the clock does: the served list never goes UP in last-used time, and every
/// never-opened repository is beneath every used one. Recency ordering over controlled times is proved
/// where the times can be controlled, in <c>OneRepositoryListOrderTests</c>.
/// </summary>
[Collection("DirectorRoot")]
public sealed class OneRepositoryListTunnelProofTests : IAsyncLifetime
{
    private const string Token = "one-repository-list-proof-token";
    private const string DirectorId = "one-repository-list-director";
    private const string Machine = "SOREN_NORTH";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "cc-one-repository-list-" + Guid.NewGuid().ToString("N"));
    private string? _previousRoot;
    // Assigned by xUnit's asynchronous lifecycle before any test runs.
    private GatewayHost _gateway = null!;
    private HttpClient _http = null!;

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
    private static RepoStatusDto Found(string path, string name) => new()
    {
        DirectorId = DirectorId,
        MachineName = Machine,
        Path = path,
        Name = name,
        Branch = "main",
        IsClean = true,
    };

    /// <summary>One session the Director reports, which is what makes a repository a USED one.</summary>
    private static SessionDto Session(string sessionId, string path, string name) => new()
    {
        SessionId = sessionId,
        Name = sessionId,
        RepoName = name,
        RepoPath = path,
        Agent = "RawCli",
        CurrentModel = "configured-model",
        CreatedAt = DateTime.UtcNow.AddMinutes(-1),
        LastActivityAt = DateTime.UtcNow,
        ActivityState = "Working",
        Status = "Running",
    };

    /// <summary>The one route, read over real HTTP exactly as a client reads it.</summary>
    private async Task<List<KnownRepositoryDto>> ServedAsync()
    {
        using var response = await _http.GetAsync("directors/" + DirectorId + "/known-repositories");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<KnownRepositoryDto>>();
        Assert.NotNull(rows);
        return rows;
    }

    /// <summary>
    /// THE FLOW. A machine whose repositories are a mix of used and never-opened is served as one list,
    /// with every never-opened repository beneath every used one, and with the used half in recency order.
    /// </summary>
    [Fact]
    public async Task MixedMachine_IsServedAsOneList_WithTheNeverOpenedRepositoriesAtTheBottom()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        // Two repositories that have been worked in. The push is the ordinary session snapshot, so this is
        // every surface that can start a session, not the desktop dialog's own button.
        await director.PushSnapshotAsync(Session("session-1", @"D:\Repos\alpha", "alpha"));
        await director.PushSnapshotAsync(
            Session("session-1", @"D:\Repos\alpha", "alpha"),
            Session("session-2", @"D:\Repos\bravo", "bravo"));

        // And the root-folder scan, which finds those two AND two nobody has ever opened.
        await director.PushRepoSnapshotAsync(
            Found(@"D:\Repos\alpha", "alpha"),
            Found(@"D:\Repos\bravo", "bravo"),
            Found(@"D:\Repos\zulu", "zulu"),
            Found(@"D:\Repos\kilo", "kilo"));

        var served = await ServedAsync();

        Assert.Equal(4, served.Count);
        // GOAL 2, on the wire: the two that have been used are the top of the list, and the two that have
        // only been found are beneath them - in that order, by name, so the list is a total order.
        Assert.Equal(new[] { @"D:\Repos\kilo", @"D:\Repos\zulu" },
            served.Skip(2).Select(row => row.Path).ToArray());
        Assert.Equal(new[] { @"D:\Repos\alpha", @"D:\Repos\bravo" },
            served.Take(2).Select(row => row.Path).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { false, false, true, true }, served.Select(row => row.NeverOpened).ToArray());

        // The used half is in recency order: the served list never goes UP in last-used time. See the class
        // remarks for why this is asserted as a property rather than between two named pushes.
        Assert.True(served[0].LastUsed >= served[1].LastUsed,
            "the list was served with an older repository above a newer one");
        Assert.All(served.Take(2), row => Assert.NotNull(row.LastUsed));
        Assert.All(served.Skip(2), row => Assert.Null(row.LastUsed));
    }

    /// <summary>
    /// THE FAILURE CASE THE WHOLE CATALOG EXISTS FOR: the Director is gone. A pull would return nothing
    /// exactly when the Cockpit and the phone still need the list; this route answers from durable
    /// storage, in the same one order, with both halves intact.
    /// </summary>
    [Fact]
    public async Task TheDirectorGoesAway_TheOneListIsStillServedInTheSameOrder()
    {
        List<KnownRepositoryDto> whileConnected;
        await using (var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine))
        {
            await director.PushSnapshotAsync(Session("session-1", @"D:\Repos\alpha", "alpha"));
            await director.PushRepoSnapshotAsync(
                Found(@"D:\Repos\alpha", "alpha"),
                Found(@"D:\Repos\zulu", "zulu"));
            whileConnected = await ServedAsync();
        }

        // The tunnel is closed and its endpoint was never reachable, so nothing can be pulled from this
        // Director at all.
        var afterItWent = await ServedAsync();

        Assert.Equal(new[] { @"D:\Repos\alpha", @"D:\Repos\zulu" },
            afterItWent.Select(row => row.Path).ToArray());
        Assert.Equal(new[] { false, true }, afterItWent.Select(row => row.NeverOpened).ToArray());
        Assert.Equal(whileConnected.Select(row => row.Path).ToArray(),
            afterItWent.Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// FAILURE CASE - a repository that has only ever been FOUND is opened. It does not become a second
    /// entry and it does not stay at the bottom: it is the same row, it gains the time, and it rises into
    /// the used half. This is the moment the two halves meet, and it is the one a two-store design would
    /// have got wrong.
    /// </summary>
    [Fact]
    public async Task ANeverOpenedRepositoryIsOpened_RisesOutOfTheBottomHalfAsTheSameEntry()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);
        await director.PushRepoSnapshotAsync(
            Found(@"D:\Repos\alpha", "alpha"),
            Found(@"D:\Repos\zulu", "zulu"));

        var before = await ServedAsync();
        Assert.Equal(new[] { @"D:\Repos\alpha", @"D:\Repos\zulu" }, before.Select(row => row.Path).ToArray());
        Assert.All(before, row => Assert.True(row.NeverOpened));

        // A session starts in zulu - from any surface; this is the snapshot every one of them produces.
        await director.PushSnapshotAsync(Session("session-1", @"D:\Repos\zulu", "zulu"));

        var after = await ServedAsync();
        Assert.Equal(2, after.Count);
        Assert.Equal(@"D:\Repos\zulu", after[0].Path);
        Assert.False(after[0].NeverOpened);
        Assert.NotNull(after[0].LastUsed);
        Assert.Equal(@"D:\Repos\alpha", after[1].Path);
        Assert.True(after[1].NeverOpened);
    }

    /// <summary>
    /// FAILURE CASE - a machine that has never been scanned and never run a session. An empty list, with
    /// an OK status: a new machine is not an error, and a client must be able to tell "nothing here" from
    /// "this failed".
    /// </summary>
    [Fact]
    public async Task AMachineNothingIsKnownAbout_IsServedAnEmptyListRatherThanAnError()
    {
        await using var director = await FakeTunnelDirector.StartAsync(_gateway, Token, DirectorId, Machine);

        Assert.Empty(await ServedAsync());
    }
}
