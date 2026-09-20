using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE DIRECTOR'S HALF of "the registry reaches the Gateway" (the one-repository-list mission).
///
/// A Director knows about a repository in two ways - the root-folder scan measured it, or a person put
/// it in the machine's registered list by hand - and until this change only the first ever left the
/// machine. These are the rules of the union that closes that gap:
/// <see cref="DirectorRepositorySnapshot.Union"/>, the one function that decides what a Director pushes.
///
/// It is tested here as a pure function, with no monitor, no registry file and no machine to scan,
/// because that is what the signature was chosen for. The same rules are proved again through a real
/// tunnel and a real endpoint in <c>CcDirector.Gateway.Tests.RegistryReachesTheGatewayTunnelProofTests</c>;
/// what those tests cannot do is drive every failure case.
/// </summary>
public sealed class DirectorRepositorySnapshotTests
{
    private const string DirectorId = "director-under-test";
    private const string Machine = "SOREN_NORTH";

    /// <summary>A repository the root-folder scan found and measured.</summary>
    private static RepositoryStatus Scanned(string path, string name, string branch = "main",
        int uncommitted = 0, bool provisional = false) => new()
        {
            Path = path,
            Name = name,
            Branch = branch,
            IsClean = uncommitted == 0,
            UncommittedCount = uncommitted,
            Provider = RepoProvider.GitHub,
            Provisional = provisional,
        };

    /// <summary>An entry in the machine's hand-built registered repository list.</summary>
    private static RepositoryConfig Registered(string path, string name = "", DateTime? lastUsed = null) => new()
    {
        Path = path,
        Name = name,
        LastUsed = lastUsed,
    };

    private static List<RepoStatusDto> Union(
        IReadOnlyList<RepositoryStatus>? scanned,
        IReadOnlyList<RepositoryConfig>? registered,
        bool scanHasCompleted = true)
        => DirectorRepositorySnapshot.Union(scanned, registered, scanHasCompleted, DirectorId, Machine);

    // ---------- THE FLOW: a hand-added repository no watched folder covers leaves the machine ----------

    [Fact]
    public void Union_ARegisteredRepositoryNoWatchedFolderCovers_IsPushed_MarkedAsCarryingNoStatus()
    {
        var pushed = Union(
            new[] { Scanned("/roots/work/alpha", "alpha") },
            new[] { Registered("/elsewhere/entirely/bravo", "bravo") });

        Assert.Equal(2, pushed.Count);
        var handAdded = Assert.Single(pushed, row => row.Path == "/elsewhere/entirely/bravo");
        Assert.True(handAdded.StatusNotComputed);
        Assert.Equal("bravo", handAdded.Name);
        Assert.Equal(Machine, handAdded.MachineName);
        Assert.Equal(DirectorId, handAdded.DirectorId);
    }

    [Fact]
    public void Union_AnIdentityOnlyRow_InventsNoStatusAtAll()
    {
        // The row a repository nobody has measured produces. Every one of these defaults is the
        // ABSENCE of a measurement, and the flag is what says so out loud - a reader must never take a
        // blank branch or a zero count off this row as a finding. It is also why the registry's own
        // last-used stamp is not here: there is no field for it and there will not be one, because the
        // last-used time is the Gateway's, observed from session starts on every surface.
        var row = Assert.Single(Union(
            scanned: Array.Empty<RepositoryStatus>(),
            registered: new[] { Registered("/elsewhere/bravo", "bravo", lastUsed: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)) }));

        Assert.True(row.StatusNotComputed);
        Assert.False(row.Provisional);      // it is verified identity, not unverified status
        Assert.Equal("", row.Branch);
        Assert.False(row.IsClean);
        Assert.Equal(0, row.UncommittedCount);
        Assert.Null(row.DirtySinceUtc);
        Assert.Equal(0, row.AheadCount);
        Assert.Equal(0, row.BehindCount);
        Assert.Equal(0, row.BehindMainCount);
        Assert.Equal(0, row.WorktreeCount);
        Assert.Equal(0, row.WorktreesSafeToReap);
        Assert.Equal(0L, row.WorktreeBytes);
        Assert.Empty(row.Worktrees);
        Assert.Null(row.RemoteUrl);
        Assert.Equal("None", row.Provider);
    }

    [Fact]
    public void Union_AMachineWithNoWatchedFoldersAtAll_StillPushesItsRegisteredList()
    {
        // The scan ran and found nothing, which is a real answer and not an absent one. Without this,
        // a user who has never added a watched folder and works entirely from a hand-built list would
        // be exactly the user whose repositories never reach the Gateway.
        var pushed = Union(Array.Empty<RepositoryStatus>(), new[] { Registered("/only/here/charlie", "charlie") });

        var row = Assert.Single(pushed);
        Assert.Equal("/only/here/charlie", row.Path);
        Assert.True(row.StatusNotComputed);
    }

    [Fact]
    public void Union_TheScannedRows_AreExactlyWhatTheyWereBeforeTheRegistryJoinedThem()
    {
        var scanned = Scanned("/roots/work/alpha", "alpha", branch: "feature/x", uncommitted: 3);

        var pushed = Union(new[] { scanned }, new[] { Registered("/elsewhere/bravo", "bravo") });

        var row = Assert.Single(pushed, r => r.Path == "/roots/work/alpha");
        Assert.False(row.StatusNotComputed);
        Assert.Equal("feature/x", row.Branch);
        Assert.Equal(3, row.UncommittedCount);
        Assert.False(row.IsClean);
        Assert.Equal("GitHub", row.Provider);
    }

    // ---------- THE TWO SOURCES MEETING ON ONE REPOSITORY ----------

    [Fact]
    public void Union_ARepositoryThatIsBothRegisteredAndUnderAWatchedFolder_IsPushedOnce_WithItsStatus()
    {
        // The normal case on a developer's machine, not the exception: the repository was added by hand
        // AND its parent folder is watched. Two rows for one repository is the double-count the whole
        // mission exists to end, and the scanned row is the one to keep because it carries a status.
        var pushed = Union(
            new[] { Scanned("/roots/work/alpha", "alpha", branch: "main", uncommitted: 2) },
            new[] { Registered("/roots/work/alpha", "alpha renamed by hand") });

        var row = Assert.Single(pushed);
        Assert.False(row.StatusNotComputed);
        Assert.Equal("alpha", row.Name);
        Assert.Equal(2, row.UncommittedCount);
    }

    [Fact]
    public void Union_TheSameFolderRegisteredTwice_IsPushedOnce()
    {
        // One machine's registered list can hold the same folder written two ways - a trailing
        // separator, a different case on a case-insensitive disk - because nothing has ever compared
        // the entries to each other. The comparison is the monitor's own, so the answer agrees with
        // what "already in the scan" means.
        var pushed = Union(Array.Empty<RepositoryStatus>(), new[]
        {
            Registered("/elsewhere/bravo", "bravo"),
            Registered("/elsewhere/bravo/", "bravo again"),
        });

        var row = Assert.Single(pushed);
        Assert.Equal("bravo", row.Name);
    }

    // ---------- THE FAILURE CASES ----------

    [Fact]
    public void Union_BeforeTheFirstScanHasCompleted_PushesTheScanAloneAndSaysNothingAboutTheRegistry()
    {
        // THE ONE THAT WOULD HAVE DELETED THE OTHER HALF. The Gateway reads a push with no unverified
        // row in it as a COMPLETE statement of what this Director knows, and reconciles against it. A
        // Director that came up on a machine with no warm-start cache and pushed its registry before
        // the first scan had run would therefore have said "every repository under every watched folder
        // is gone", and the Gateway would have removed rows that had simply not been looked at yet.
        var pushed = Union(
            Array.Empty<RepositoryStatus>(),
            new[] { Registered("/elsewhere/bravo", "bravo") },
            scanHasCompleted: false);

        Assert.Empty(pushed);
    }

    [Fact]
    public void Union_BeforeTheFirstScanHasCompleted_TheWarmStartCacheIsStillPushedUntouched()
    {
        var pushed = Union(
            new[] { Scanned("/roots/work/alpha", "alpha", provisional: true) },
            new[] { Registered("/elsewhere/bravo", "bravo") },
            scanHasCompleted: false);

        var row = Assert.Single(pushed);
        Assert.Equal("/roots/work/alpha", row.Path);
        Assert.True(row.Provisional);
    }

    [Fact]
    public void Union_ARegisteredEntryWithNoPath_IsNotPushed()
    {
        // The path IS the identity; there is nothing to key a pathless entry by, so it is dropped
        // rather than pushed as a row the Gateway would have to decide what to do with.
        var pushed = Union(Array.Empty<RepositoryStatus>(), new[]
        {
            Registered("   ", "a name and nothing else"),
            Registered("/elsewhere/bravo", "bravo"),
        });

        var row = Assert.Single(pushed);
        Assert.Equal("/elsewhere/bravo", row.Path);
    }

    [Fact]
    public void Union_ARegisteredEntryWithNoName_TakesOneFromThePathsOwnShape()
    {
        // A Windows path read on this machine, whatever this machine is. Path.GetFileName would find no
        // separator at all on macOS or Linux and hand back the whole string, and the name computed here
        // is the one two clients render - nothing downstream recomputes it.
        var row = Assert.Single(Union(Array.Empty<RepositoryStatus>(), new[] { Registered(@"D:\ReposFred\devthrottle", "") }));

        Assert.Equal("devthrottle", row.Name);
    }

    [Fact]
    public void Union_NoRegisteredListAtAll_PushesExactlyWhatItAlwaysPushed()
    {
        var pushed = Union(new[] { Scanned("/roots/work/alpha", "alpha") }, registered: null);

        var row = Assert.Single(pushed);
        Assert.Equal("/roots/work/alpha", row.Path);
        Assert.False(row.StatusNotComputed);
    }

    [Fact]
    public void Union_NothingKnownEitherWay_IsAnEmptyPushRatherThanAFailure()
    {
        Assert.Empty(Union(null, null));
    }
}
