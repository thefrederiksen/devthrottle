using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Background;

/// <summary>
/// The License Switching mission retired the copying Claude account store and its Accounts dialog, on the condition
/// that nobody's existing accounts file is deleted: the Director stops reading it and leaves it where it is.
///
/// So the only Director code that may name the file is the storage migration, which COPIES it forward to the new
/// storage location and never deletes it. Presence, not absence (skill checks-that-fail-open): the scan proves it read
/// the Director's sources by finding that migration, so a scan that read nothing cannot pass.
/// </summary>
public sealed class RetiredAccountsFileIsLeftAloneTests
{
    private const string AccountsFile = "accounts.json";

    [Fact]
    public void OnlyTheStorageMigrationNamesTheAccountsFile_AndItOnlyCopies()
    {
        var src = Path.Combine(RepositorySourceIndex.Root, "src");
        var directorProjects = new[] { "CcDirector.Core", "CcDirector.Avalonia", "CcDirector.ControlApi", "CcDirector.Engine" };
        var files = directorProjects
            .Select(p => Path.Combine(src, p))
            .Where(Directory.Exists)
            .SelectMany(d => RepositorySourceIndex.Under(d, ".cs"))
            .ToList();
        Assert.True(files.Count > 500, $"expected the Director's sources, found {files.Count} files under {src}");

        var naming = files.Where(f => File.ReadAllText(f).Contains(AccountsFile, StringComparison.Ordinal)).ToList();

        var migration = Assert.Single(naming);
        Assert.EndsWith(Path.Combine("Storage", "CcStorageMigration.cs"), migration, StringComparison.Ordinal);
        var lines = File.ReadAllLines(migration).Where(l => l.Contains(AccountsFile, StringComparison.Ordinal)).ToList();
        Assert.Contains(lines, l => l.Contains("CopyFileIfNewer(", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Delete", StringComparison.Ordinal) || l.Contains("Move", StringComparison.Ordinal));
    }
}
