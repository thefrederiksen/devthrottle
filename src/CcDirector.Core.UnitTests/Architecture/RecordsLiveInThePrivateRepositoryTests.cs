using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Architecture;

/// <summary>
/// The product's internal records moved to the private repository, thefrederiksen/devthrottle_internal, under
/// product/ (the owner's decision of 9 November 2026, the first stage of moving the product there). This guard
/// keeps the public repository from filling up with them again: a new mission note, proof folder, quality
/// assurance run or review written here fails the gate, and the message says where it goes instead.
///
/// Three folders are still allowed here until the product code itself moves, because live tests read them.
/// When the code moves, they move with it and this guard goes too.
/// </summary>
public sealed class RecordsLiveInThePrivateRepositoryTests
{
    private static readonly string[] MovedFolders =
    {
        "docs/missions/",
        "docs/qa/",
        "docs/proof/",
        "docs/reviews/",
        "docs/cencon/",
        "missions/",
        "research/",
    };

    private static readonly string[] StillHereUntilTheCodeMoves =
    {
        "missions/license-switching/",
        "missions/stop-a-session/",
        "docs/qa/mail-desk/phase-0/c-model/prompts/",
    };

    [Fact]
    public void RepositoryFiles_InTheMovedRecordFolders_AreOnlyTheThreeKeptFolders()
    {
        // Arrange
        var files = RepositorySourceIndex.Files
            .Select(f => RepositorySourceIndex.Relative(f).Replace('\\', '/'))
            .ToList();

        // Act
        var offenders = files
            .Where(f => MovedFolders.Any(m => f.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !StillHereUntilTheCodeMoves.Any(k => f.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // Assert
        Assert.True(offenders.Count == 0,
            "Mission records, proof, quality assurance runs and reviews now live in the private repository "
            + "thefrederiksen/devthrottle_internal, under product/ at the same path (for example docs/proof/x "
            + "goes to product/docs/proof/x). Move these files there:" + Environment.NewLine
            + string.Join(Environment.NewLine, offenders.Take(50).Select(f => "  " + f))
            + (offenders.Count > 50 ? $"{Environment.NewLine}  ... and {offenders.Count - 50} more" : ""));
    }

    [Fact]
    public void RepositoryFiles_TheKeptFoldersTheTestsRead_AreStillHere()
    {
        // Arrange
        var files = RepositorySourceIndex.Files
            .Select(f => RepositorySourceIndex.Relative(f).Replace('\\', '/'))
            .ToList();

        // Act
        var missing = StillHereUntilTheCodeMoves
            .Where(k => !files.Any(f => f.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Assert - an empty list from the first test must mean "nothing is in the wrong place", never
        // "the index saw nothing at all", so this proves the index does see the folders that are allowed.
        Assert.True(missing.Count == 0,
            "These folders are read by live tests and must stay until the product code moves: "
            + string.Join(", ", missing));
    }
}
