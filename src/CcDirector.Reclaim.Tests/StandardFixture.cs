using CcDirector.Reclaim.Scanning;

namespace CcDirector.Reclaim.Tests;

/// <summary>
/// The tree the scanner is proved against, and the exact numbers it must report for it.
///
/// The numbers are written here once, as constants, and every test asserts against them. They are
/// exact: not a range, not "more than none". A scan that returned one byte more or one file fewer
/// than a tree this test built with its own hands would be wrong, and the suite says so.
///
/// The tree holds each of the three things a disk scan has to get right and is easiest to get wrong:
/// a junction it must count and must not walk through, a folder that refuses to be listed which it
/// must count and name, and a file whose bytes are in a cloud store which it must count apart from
/// the bytes on the disk. It also holds enough real bytes for the unseen-gap line to have two sides.
/// </summary>
public static class StandardFixture
{
    /// <summary>Bytes in alpha/a1.txt.</summary>
    public const int AlphaFirstFileBytes = 100;

    /// <summary>Bytes in alpha/a2.txt.</summary>
    public const int AlphaSecondFileBytes = 200;

    /// <summary>Bytes in alpha/nested/n1.txt.</summary>
    public const int NestedFileBytes = 50;

    /// <summary>Bytes in beta/b1.txt.</summary>
    public const int BetaFileBytes = 1000;

    /// <summary>Bytes in top.txt, at the root of the tree.</summary>
    public const int TopFileBytes = 10;

    /// <summary>Bytes in refused/r1.txt, which the scan must never see.</summary>
    public const int HiddenFileBytes = 4242;

    /// <summary>Bytes the cloud placeholder claims, none of which are on this disk.</summary>
    public const int PlaceholderBytes = 4096;

    /// <summary>Every byte the scan must see: the five ordinary files and nothing else.</summary>
    public const long ExpectedBytesSeen =
        AlphaFirstFileBytes + AlphaSecondFileBytes + NestedFileBytes + BetaFileBytes + TopFileBytes;

    /// <summary>Every ordinary file the scan must see. The placeholder is not one of them.</summary>
    public const long ExpectedFilesSeen = 5;

    /// <summary>alpha, alpha/nested, beta and refused. The link is not a folder and the root is not counted.</summary>
    public const long ExpectedFoldersSeen = 4;

    /// <summary>Everything under alpha, including nested.</summary>
    public const long ExpectedAlphaBytes = AlphaFirstFileBytes + AlphaSecondFileBytes + NestedFileBytes;

    /// <summary>The ordinary files under alpha, including the one in nested.</summary>
    public const long ExpectedAlphaFiles = 3;

    /// <summary>Everything under alpha/nested.</summary>
    public const long ExpectedNestedBytes = NestedFileBytes;

    /// <summary>Everything under beta.</summary>
    public const long ExpectedBetaBytes = BetaFileBytes;

    /// <summary>What the placeholder claims, none of it on this disk.</summary>
    public const long ExpectedPlaceholderBytesInCloud = PlaceholderBytes;

    /// <summary>
    /// Build the tree. The caller owns it and disposes it.
    /// </summary>
    /// <param name="name">A short name for the test, used in the folder name.</param>
    public static FixtureTree Build(string name)
    {
        var tree = new FixtureTree(name);

        tree.File(Path.Combine("alpha", "a1.txt"), AlphaFirstFileBytes);
        tree.File(Path.Combine("alpha", "a2.txt"), AlphaSecondFileBytes);
        tree.File(Path.Combine("alpha", "nested", "n1.txt"), NestedFileBytes);
        tree.File(Path.Combine("beta", "b1.txt"), BetaFileBytes);
        tree.File("top.txt", TopFileBytes);

        // Written before the folder is closed, so the test proves the scan cannot see behind a
        // refusal rather than proving an empty folder is empty.
        tree.File(Path.Combine("refused", "r1.txt"), HiddenFileBytes);
        tree.DenyListing("refused");

        tree.CloudPlaceholder("cloud.bin", PlaceholderBytes);
        tree.DirectoryLink("link-to-alpha", "alpha");

        return tree;
    }

    /// <summary>Scan the tree with the ordinary options.</summary>
    /// <param name="tree">The tree to scan.</param>
    /// <param name="maxFolderDepth">How deep to record folder totals.</param>
    public static ScanResult Scan(FixtureTree tree, int maxFolderDepth = ScanOptions.DefaultFolderDepth)
    {
        ArgumentNullException.ThrowIfNull(tree);
        return new DirectoryScanner().Scan(new ScanOptions
        {
            RootPath = tree.Root,
            MaxFolderDepth = maxFolderDepth
        });
    }
}
