using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// THE NAME A PERSON CAN TELL THE ROWS APART BY (the one-repository-list mission, "the catalogue
/// forgets", gap 2).
///
/// A used row's stored name is whatever the session carried - the repository's GitHub slug, or nothing at
/// all - and a Director's push can never correct it, because a used row is untouchable from
/// <c>ObserveDiscovered</c>. Measured against the live Gateway on 20 September 2026: one machine's 90
/// rows carried THREE distinct names between them - 66 said <c>thefrederiksen/devthrottle</c>, 6 said
/// <c>thefrederiksen/devthrottle_internal</c>, and 18 said nothing at all - across ninety different
/// folders. A list where the Name column says the same thing sixty-six times is a list nobody can use.
///
/// The ruling: the Gateway serves the FOLDER NAME from the path when the stored name is blank, or when it
/// is shared with another row and therefore tells them apart from nothing. It is done in the one fold and
/// never in a client (Critical Rule 7), and the order follows it, because all three screens should sort
/// by the name a person actually sees.
/// </summary>
public sealed class TheNameARepositoryIsServedUnderTests : IDisposable
{
    private const string Machine = "SOREN_NORTH";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly DateTime _now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _harness.Dispose();

    private KnownRepositoryEntity Row(string path, string name, DateTime? lastUsed) =>
        new()
        {
            TenantId = TenantId.Local.Value,
            MachineKey = KnownRepositoryStore.NormalizeMachineKey(Machine),
            PathKey = KnownRepositoryStore.NormalizePathKey(path),
            MachineName = Machine,
            Path = path,
            Name = name,
            LastUsedUtc = lastUsed,
        };

    private static string[] Names(IEnumerable<KnownRepositoryEntity> rows)
        => KnownRepositoryStore.OrderOneList(rows.ToList(), KnownRepositoryStore.NormalizeMachineKey(Machine))
            .Select(row => row.Name).ToArray();

    // ---------- THE FLOW ----------

    /// <summary>
    /// THE FLOW, and the measurement that produced it: one slug across many folders becomes each folder's
    /// own name, and a blank name becomes one too. Nothing here is a guess - these are the two shapes the
    /// live catalogue actually held.
    /// </summary>
    [Fact]
    public void OneSlugAcrossManyFolders_AndABlankName_AreBothServedAsTheFolderName()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/roots/work/devthrottle", "thefrederiksen/devthrottle", _now.AddMinutes(-1)),
            Row("/roots/work/devthrottle-repo-list", "thefrederiksen/devthrottle", _now.AddMinutes(-2)),
            Row("/roots/work/devthrottle-ci-p1", "thefrederiksen/devthrottle", _now.AddMinutes(-3)),
            Row("/roots/work/devthrottle-fast-ci", "", _now.AddMinutes(-4)),
        };

        Assert.Equal(
            new[] { "devthrottle", "devthrottle-repo-list", "devthrottle-ci-p1", "devthrottle-fast-ci" },
            Names(rows));
    }

    /// <summary>
    /// A name that tells its row apart from every other keeps its stored spelling, which is the ruling as
    /// given: a slug that distinguishes the row is a good name for it. This is the contrast that stops the
    /// test above from passing under a rule that simply replaced every name.
    /// </summary>
    [Fact]
    public void ANameNoOtherRowShares_IsServedExactlyAsItIsStored()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/roots/work/devthrottle_internal", "thefrederiksen/devthrottle_internal", _now.AddMinutes(-1)),
            Row("/roots/work/other", "something-else", _now.AddMinutes(-2)),
        };

        Assert.Equal(new[] { "thefrederiksen/devthrottle_internal", "something-else" }, Names(rows));
    }

    /// <summary>
    /// THE ORDER CHANGES WITH THE NAME, and that is accepted rather than incidental: all three screens
    /// sort by the name a person sees. These four never-opened rows all tie on time, so the name is the
    /// whole order - stored, they would come back in path order because their stored names are identical;
    /// served, they come back in folder-name order.
    /// </summary>
    [Fact]
    public void TheOrderFollowsTheNameThatIsServed_NotTheOneThatIsStored()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/roots/work/alpha", "thefrederiksen/devthrottle", null),
            Row("/roots/work/zulu", "thefrederiksen/devthrottle", null),
            Row("/roots/work/mike", "thefrederiksen/devthrottle", null),
        };

        var served = KnownRepositoryStore.OrderOneList(rows, KnownRepositoryStore.NormalizeMachineKey(Machine));

        Assert.Equal(new[] { "alpha", "mike", "zulu" }, served.Select(row => row.Name).ToArray());
        Assert.Equal(
            new[] { "/roots/work/alpha", "/roots/work/mike", "/roots/work/zulu" },
            served.Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// And the order change is visible where it counts: a row whose stored slug sorts one way and whose
    /// folder name sorts the other comes back in the FOLDER name's order. Without the fold, "aardvark"
    /// would be first; with it, "zzz-folder" carries the aardvark slug and goes last.
    /// </summary>
    [Fact]
    public void ARowWhoseSlugAndFolderNameDisagree_IsPlacedByTheFolderName()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/roots/work/zzz-folder", "aardvark/repository", null),
            Row("/roots/work/aaa-folder", "aardvark/repository", null),
        };

        Assert.Equal(new[] { "aaa-folder", "zzz-folder" }, Names(rows));
    }

    // ---------- THE FAILURE CASES ----------

    /// <summary>
    /// PATH COMPARISON, THIS MISSION'S RECURRING DEFECT. The folder name is read from the PATH'S OWN
    /// SHAPE. The Gateway is a Linux container holding paths pushed up by Windows and macOS machines, and
    /// <c>Path.GetFileName</c> handed a Windows path on Linux finds no separator and returns the whole
    /// path - which is exactly the fifth instance of this defect, fixed in pull request 3195. This test
    /// runs a Windows path on whatever machine the suite runs on.
    /// </summary>
    [Fact]
    public void AWindowsPath_FindsItsFolderNameOnAnyMachine()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row(@"D:\ReposFred\devthrottle", "thefrederiksen/devthrottle", null),
            Row(@"D:\ReposFred\devthrottle-mac", "thefrederiksen/devthrottle", null),
        };

        Assert.Equal(new[] { "devthrottle", "devthrottle-mac" }, Names(rows));
    }

    /// <summary>
    /// FAILURE CASE. A path with no folder name in it at all keeps whatever name it had: an empty name is
    /// not an improvement on a poor one.
    /// </summary>
    [Fact]
    public void APathWithNoFolderNameInIt_KeepsTheNameItHad()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/", "the-root", null),
        };

        Assert.Equal(new[] { "the-root" }, Names(rows));
    }

    /// <summary>
    /// FAILURE CASE. A blank name over a path with no folder name in it is served blank rather than
    /// invented. The Gateway does not make a name up.
    /// </summary>
    [Fact]
    public void ABlankNameOverAPathWithNoFolderName_IsServedBlank()
    {
        Assert.Equal(new[] { "" }, Names(new[] { Row("/", "", null) }));
    }

    /// <summary>
    /// FAILURE CASE. Two rows whose names differ only in case do not tell each other apart either, so
    /// both take their folder name. The sharing is judged the same way the tiebreak sorts.
    /// </summary>
    [Fact]
    public void NamesThatDifferOnlyInCase_AreBothReplaced()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/roots/work/alpha", "TheFrederiksen/DevThrottle", null),
            Row("/roots/work/beta", "thefrederiksen/devthrottle", null),
        };

        Assert.Equal(new[] { "alpha", "beta" }, Names(rows));
    }

    /// <summary>
    /// FAILURE CASE. Two rows whose FOLDER names are also identical stay identical, and the list is still
    /// a total order because the path breaks the tie. A screen does not reshuffle between reads.
    /// </summary>
    [Fact]
    public void TwoRowsWhoseFolderNamesAreAlsoTheSame_AreOrderedByPath()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/roots/beta/devthrottle", "thefrederiksen/devthrottle", null),
            Row("/roots/alpha/devthrottle", "thefrederiksen/devthrottle", null),
        };

        var served = KnownRepositoryStore.OrderOneList(rows, KnownRepositoryStore.NormalizeMachineKey(Machine));

        Assert.Equal(new[] { "devthrottle", "devthrottle" }, served.Select(row => row.Name).ToArray());
        Assert.Equal(
            new[] { "/roots/alpha/devthrottle", "/roots/beta/devthrottle" },
            served.Select(row => row.Path).ToArray());
    }

    /// <summary>
    /// The sharing is counted over the list AS SERVED, after de-duplication and after another machine's
    /// rows have been dropped: a slug that is repeated on a DIFFERENT machine's rows still distinguishes
    /// this machine's one row, so it is kept.
    /// </summary>
    [Fact]
    public void ANameSharedOnlyWithAnotherMachinesRow_IsStillKept()
    {
        var rows = new List<KnownRepositoryEntity>
        {
            Row("/roots/work/devthrottle", "thefrederiksen/devthrottle", null),
            new()
            {
                TenantId = TenantId.Local.Value,
                MachineKey = KnownRepositoryStore.NormalizeMachineKey("SOREN_SOUTH"),
                PathKey = KnownRepositoryStore.NormalizePathKey("/elsewhere/devthrottle"),
                MachineName = "SOREN_SOUTH",
                Path = "/elsewhere/devthrottle",
                Name = "thefrederiksen/devthrottle",
            },
        };

        Assert.Equal(new[] { "thefrederiksen/devthrottle" }, Names(rows));
    }

    /// <summary>
    /// And the same rows read back through the real store, so this is the list a client is actually
    /// served and not only what the fold returns when a test hands it rows.
    /// </summary>
    [Fact]
    public void ReadForMachine_ServesTheFoldedNames()
    {
        var store = new KnownRepositoryStore(_harness.Open());
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle", "thefrederiksen/devthrottle", _now.AddMinutes(-1));
        store.Observe(TenantId.Local, Machine, "/roots/work/devthrottle-p6", "thefrederiksen/devthrottle", _now.AddMinutes(-2));
        store.Observe(TenantId.Local, Machine, "/roots/work/nameless", null, _now.AddMinutes(-3));

        Assert.Equal(
            new[] { "devthrottle", "devthrottle-p6", "nameless" },
            store.ReadForMachine(TenantId.Local, Machine).Select(row => row.Name).ToArray());
    }
}
