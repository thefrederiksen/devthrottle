using CcDirector.Core.Storage;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// Issue #1879: <c>CC_DIRECTOR_ROOT</c> redirects where a Director WRITES but not where this migration
/// READS. It copies from fixed legacy paths, so every explicitly pinned root filled on first boot with
/// the machine owner's real accounts, repository list and session history - and that history carries
/// first-prompt snippets and turn summaries, i.e. real prompt text.
///
/// The part that made it more than untidy: <see cref="CcStorageMigration"/> copies whenever the
/// destination file is MISSING, and the migration runs on every process start. So deleting the copied
/// data did not stop it - the next start put it straight back. A root cleaned today refilled tomorrow,
/// silently.
///
/// These tests pin the guard. The predicate test states the rule; the behavioural test is the one that
/// matters, because it asserts on what a real migration run actually does to a pinned root rather than
/// on what the flag says it should do.
/// </summary>
[Collection("ProcessWideState")]
public sealed class CcStorageMigrationPinnedRootTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DefaultRoot_StillMigrates(string? pinned) =>
        Assert.True(CcStorageMigration.ShouldMigrate(pinned));

    [Theory]
    [InlineData(@"D:\some\test\root")]
    [InlineData(@"C:\Users\someone\AppData\Local\cc-director")]
    public void PinnedRoot_DoesNotMigrate(string pinned) =>
        Assert.False(CcStorageMigration.ShouldMigrate(pinned));

    /// <summary>
    /// The behavioural proof: point CC_DIRECTOR_ROOT at an empty throwaway directory, run the real
    /// migration, and assert it stayed empty. Without the guard the legacy sources on the developer's own
    /// machine are copied in, which is the defect. Asserting "still empty" rather than "no specific file"
    /// keeps the test honest about sources it does not know the names of.
    /// </summary>
    [Fact]
    public void RunningTheMigrationWithAPinnedRoot_LeavesItCompletelyEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), "ccdir-migration-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previous = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", root);

            CcStorageMigration.EnsureMigrated();

            var copied = Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories);
            Assert.Empty(copied);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", previous);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
