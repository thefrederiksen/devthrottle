using CcDirector.Core.Git;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// WHAT EXISTS UNDER THIS DIRECTOR'S REGISTERED ROOT FOLDERS, RIGHT NOW (the one-repository-list
/// mission, "the catalogue forgets"). It is the one statement that lets the Gateway catalogue forget a
/// repository, and it is deliberately a much simpler question than the one the root-folder scan answers.
///
/// <para><b>Why the scan cannot answer it.</b> The Gateway holds a durable, machine-keyed catalogue of
/// every repository a session has ever run in. Rows that have been USED are never removed by anything -
/// <c>KnownRepositoryStore.ObserveDiscovered</c> reconciles never-opened rows only - so a folder that is
/// created, worked in, and deleted stays in the list for ever. Measured against the live Gateway on 20
/// September 2026: one machine's catalogue held 90 repositories of which 76 no longer existed on disk.
/// The obvious fix - forget a row the Director no longer reports - is WRONG, because the scan does not
/// report everything that exists. <c>RemoteRepoProvider.ScanLocalRepos</c> accepts a direct child only
/// when its <c>.git</c> is a DIRECTORY, so a git WORKTREE, whose <c>.git</c> is a file, has never been in
/// a push at all. On that same machine ELEVEN of the fourteen surviving repositories were worktrees. A
/// rule built on "absent from the push" would have deleted all eleven live folders.</para>
///
/// <para><b>So this answers the question the scan does not:</b> for each registered root folder, the
/// direct child folders that exist - every one of them, git or not. It asks nothing about what is inside
/// them, which is what makes it both cheap and true.</para>
///
/// <para><b>THE PROPERTY THAT MAKES THIS SAFE: a root the Director CANNOT list is omitted entirely, so
/// nothing beneath it is ever forgotten.</b> That is the Delivery Lead's ruling in his own words, and it
/// is the direction a destructive operation must fail in - it acts only on what it can positively prove
/// is disposable, and enumerates what to DELETE rather than what to skip. An entry present with no
/// children authorises the Gateway to forget every repository it holds under that root; an unmounted
/// disk, an unreadable folder and a root that has stopped being watched all produce exactly that shape
/// from a naive listing, and each of them means <b>"I know nothing here"</b> and never <b>"nothing is
/// here"</b>. The lister therefore answers null for a root it could not read, and a null root is
/// dropped. It is the case the first version of this rule got wrong, and
/// <c>DirectorRootFoldersTests.Build_ARootThatCouldNotBeListed_IsNotReportedAtAll</c> and
/// <c>TheCatalogueForgetsTunnelProofTests.ARootFolderTheDirectorCannotRead_ForgetsNothingUnderIt</c> are
/// what hold it shut.</para>
///
/// <para>It is a pure function over its arguments, with the directory listing injected, for the same
/// reason <see cref="DirectorRepositorySnapshot"/> is: the rules have to be testable without a machine
/// whose disk happens to be laid out the right way, and the Windows spellings this product carries have
/// to be testable on macOS.</para>
/// </summary>
public static class DirectorRootFolders
{
    /// <summary>
    /// Build the listing for one push.
    /// </summary>
    /// <param name="roots">The registered root folders, as <c>RootDirectoryStore.Roots</c> holds them.
    /// Blank entries are dropped, and a root registered twice is listed once.</param>
    /// <param name="listChildFolders">Lists the direct child folders of one root as full paths, and
    /// answers NULL when it could not read that folder at all. Null is not the same as an empty list and
    /// the difference decides whether anything is forgotten - see the class remarks.</param>
    public static List<RootFolderListingDto> Build(
        IReadOnlyList<string>? roots,
        Func<string, IReadOnlyList<string>?> listChildFolders)
    {
        if (listChildFolders is null)
            throw new ArgumentNullException(nameof(listChildFolders));

        var listings = new List<RootFolderListingDto>();
        if (roots is null || roots.Count == 0)
            return listings;

        // One entry per root even when the user registered the same folder twice, compared on the
        // Director because the Director is the machine that owns these paths. The Gateway compares the
        // same paths its own way, through KnownRepositoryStore.NormalizePathKey, because it is a Linux
        // container holding paths from Windows and macOS machines and is never the machine a path
        // describes. Two questions, two comparisons, and neither one is a copy of the other's rule.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var trimmed = root.Trim();
            if (!taken.Add(WorktreeReaperService.NormalizePath(trimmed)))
                continue;

            var children = listChildFolders(trimmed);
            if (children is null)
            {
                // Could not read it. Reporting it with no children would tell the Gateway that every
                // repository it holds under this root has gone away.
                FileLog.Write($"[DirectorRootFolders] root could not be listed and is NOT reported: {trimmed}");
                continue;
            }

            listings.Add(new RootFolderListingDto
            {
                Path = trimmed,
                ChildPaths = children.Where(child => !string.IsNullOrWhiteSpace(child))
                    .Select(child => child.Trim())
                    .ToList(),
            });
        }

        FileLog.Write($"[DirectorRootFolders] Build: roots={roots.Count} reported={listings.Count} "
                      + $"children={listings.Sum(listing => listing.ChildPaths.Count)}");
        return listings;
    }

    /// <summary>
    /// The real listing: every direct child FOLDER of <paramref name="root"/>, as a full path. Returns
    /// null when the folder does not exist or cannot be read, which is what keeps an unmounted drive
    /// from reading as an empty one.
    /// </summary>
    public static IReadOnlyList<string>? ListChildFolders(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return null;
            return Directory.GetDirectories(root);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorRootFolders] ListChildFolders FAILED for {root}: {ex.Message}");
            return null;
        }
    }
}
