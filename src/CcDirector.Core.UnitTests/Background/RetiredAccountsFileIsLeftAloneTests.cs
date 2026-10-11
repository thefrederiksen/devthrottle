using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Background;

/// <summary>
/// The License Switching mission retired the copying Claude account store and its Accounts dialog, on the condition
/// that nobody's existing accounts file is deleted: the Director stops using it and leaves it where it is. The file
/// holds sign-in tokens, so it is not copied forward either - a copy would be a second holder of the same sign-in,
/// which is what the retirement removes (contract 1.1; review of pull request 3870).
///
/// So no code in the Director, and none in the storage migrations - the C# one that runs at start-up and the two Python
/// ones - may name the file at all; a comment saying it is left alone may. Presence, not absence (skill
/// checks-that-fail-open): the scan proves it read the Director's sources by their count, and each migration by the
/// file it still carries forward, so a scan that read nothing cannot pass.
/// </summary>
public sealed class RetiredAccountsFileIsLeftAloneTests
{
    private const string AccountsFile = "accounts.json";

    /// <summary>A file every migration still carries forward, found in each one to prove the scan read it.</summary>
    private const string StillMigrated = "root-directories.json";

    private static readonly string[] Migrations =
    {
        Path.Combine("src", "CcDirector.Core", "Storage", "CcStorageMigration.cs"),
        Path.Combine("scripts", "migrate-storage.py"),
        Path.Combine("tools", "cc_storage", "migrate.py"),
    };

    [Fact]
    public void NoDirectorCodeOrStorageMigrationNamesTheAccountsFile_SoNothingCopiesOrDeletesIt()
    {
        var root = RepositorySourceIndex.Root;
        var src = Path.Combine(root, "src");
        var directorProjects = new[] { "CcDirector.Core", "CcDirector.Avalonia", "CcDirector.ControlApi", "CcDirector.Engine" };
        var directorSources = directorProjects
            .Select(p => Path.Combine(src, p))
            .Where(Directory.Exists)
            .SelectMany(d => RepositorySourceIndex.Under(d, ".cs"))
            .ToList();
        Assert.True(directorSources.Count > 500, $"expected the Director's sources, found {directorSources.Count} files under {src}");

        var migrations = Migrations.Select(m => Path.Combine(root, m)).ToList();
        foreach (var migration in migrations)
        {
            Assert.True(File.Exists(migration), $"the storage migration {migration} is gone; update this test's list");
            Assert.Contains(StillMigrated, File.ReadAllText(migration), StringComparison.Ordinal);
        }

        var naming = directorSources.Concat(migrations)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(l => l.Text.Contains(AccountsFile, StringComparison.Ordinal) && !IsComment(l.Text))
            .Select(l => $"{Path.GetRelativePath(root, l.File)}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        Assert.True(naming.Count == 0, "code that names the retired accounts file:\n" + string.Join("\n", naming));
    }

    private static bool IsComment(string line)
    {
        var text = line.TrimStart();
        return text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith('#') || text.StartsWith('*');
    }
}
