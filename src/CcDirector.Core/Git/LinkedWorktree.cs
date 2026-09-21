using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// A WORKTREE IS NOT A REPOSITORY. USING A WORKTREE OF <c>devthrottle</c> IS USING <c>devthrottle</c>
/// (the one-repository-list mission, "a worktree is not a repository").
///
/// This resolves a folder to the repository it is a linked worktree OF, and answers null for
/// everything else. It is the rule that stops an agent's throwaway worktree becoming a row in the one
/// repository list: measured against the live Gateway on 20 September 2026, one Windows machine's
/// catalogue served 559 repositories, of which 110 were live worktrees of just four repositories and
/// 378 were folders that no longer existed. The owner read the number off the Cockpit and said
/// "here you are showing the work trees. We should only be showing the repos."
///
/// <para><b>ONLY THE DIRECTOR CAN ANSWER THIS, which is why it lives here and not on the Gateway.</b>
/// The answer is written inside the folder, so it can only be read by the machine holding the disk. The
/// Gateway is a Linux container holding paths pushed up by Windows and macOS Directors and is never the
/// machine a path describes, so it must never infer this. Everything below reads the local filesystem
/// deliberately - <c>Path.GetDirectoryName</c>, <c>Directory.Exists</c>, <c>File.Exists</c> - and that
/// is correct HERE and only here, because here the host IS the machine that owns the path. Do not
/// "fix" these into the host-independent helpers in <see cref="RepositoryPaths"/>; those exist for
/// code that reads somebody else's path.</para>
///
/// <para><b>NO TEST ON THE PATH'S TEXT DECIDES ANYTHING.</b> A worktree cannot be recognised by what it
/// is called: on the machine measured above, <c>D:\ReposFred\devthrottle-p5-run-a</c> is a worktree and
/// carries no "worktrees" segment, while <c>D:\ReposMindzie\worktrees\idle-p5-a</c> is one and does. The
/// only thing consulted here is git's own record of itself - the <c>.git</c> file git writes into a
/// linked worktree, and the directory layout it documents.</para>
///
/// <para><b>Why it reads the file rather than asking git.</b> This runs on the session-creation path,
/// where a person is waiting: a file read is measured in microseconds and starting a process is not,
/// and CLAUDE.md's first rule is that every user action answers inside 100 milliseconds. It also works
/// on a machine with no git installed, which this product supports, and where asking git would answer
/// "I cannot tell" for every worktree on the disk and quietly put all of them back in the list.
/// <see cref="RepositoryMonitor"/> asks git the same question by a different route, because it is on a
/// background scan where a process costs nothing and it wants git's resolved spelling of the path; the
/// one step the two share - which repository a git directory belongs to - is
/// <see cref="PrimaryWorkingTreeOf"/>, and there is no second copy of it.</para>
///
/// <para><b>IT LEANS TO KEEP.</b> Anything it cannot positively prove is a linked worktree of a
/// repository that exists right now answers null, and a null means the caller records the folder it was
/// given, exactly as the product did before. A worktree whose repository has been deleted, a
/// <c>.git</c> file pointing at a path that is gone, a bare repository's worktree, a git SUBMODULE
/// (which IS a repository and must keep its own place in the list), and a folder that was never a
/// repository at all are all left alone rather than guessed at.</para>
/// </summary>
public static class LinkedWorktree
{
    /// <summary>The prefix git writes in the <c>.git</c> file of a linked worktree.</summary>
    private const string GitDirPrefix = "gitdir:";

    /// <summary>
    /// The folder name git gives the directory holding one entry per linked worktree, inside the
    /// repository's git directory (<c>$GIT_DIR/worktrees/&lt;id&gt;</c>, gitrepository-layout). It is
    /// git's own constant, not a convention anybody on this machine chose, and it is what tells a
    /// linked worktree apart from a SUBMODULE - whose <c>.git</c> file points into
    /// <c>$GIT_DIR/modules/&lt;name&gt;</c> instead, and which is a repository in its own right.
    /// </summary>
    private const string WorktreesSegment = "worktrees";

    private static readonly char[] EitherSeparator = { '/', '\\' };

    /// <summary>
    /// Is this folder a LINKED WORKTREE - that is, does it hold a <c>.git</c> FILE rather than a
    /// <c>.git</c> directory?
    ///
    /// <para><b>This guard is the whole reason the rule is safe, and it is not the same as "git can
    /// answer".</b> Asked from a plain sub-folder INSIDE a repository, git walks up the tree and
    /// cheerfully names the repository above it - so a session started in <c>repo/src</c> would be
    /// credited to <c>repo</c>, which is a repository the person did not pick. A <c>.git</c> file is
    /// present only where git itself put one, and it is present in exactly the folders this rule is
    /// allowed to move. <see cref="RepositoryMonitor"/> takes the same guard before it canonicalizes.
    /// </para>
    /// </summary>
    public static bool IsOne(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            return File.Exists(Path.Combine(path.Trim(), ".git"));
        }
        catch (Exception ex)
        {
            // An unreadable or malformed path is not a worktree as far as this rule is concerned; the
            // caller keeps the folder it was given.
            FileLog.Write($"[LinkedWorktree] IsOne FAILED for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The repository a folder is a linked worktree of, or null when it is not one or the repository
    /// cannot be positively proved to exist.
    ///
    /// The chain, each step of which can only narrow: the folder holds a <c>.git</c> FILE; that file
    /// names a git directory; that git directory is a worktree entry inside some repository's git
    /// directory; that git directory belongs to a working tree; and that working tree exists on this
    /// disk right now and is itself a repository proper. A break anywhere answers null.
    /// </summary>
    public static string? ParentRepositoryOf(string? path)
    {
        if (!IsOne(path))
            return null;

        var worktreePath = path!.Trim();
        string contents;
        try
        {
            contents = File.ReadAllText(Path.Combine(worktreePath, ".git"));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinkedWorktree] ParentRepositoryOf: could not read the .git file of {worktreePath}: {ex.Message}");
            return null;
        }

        var gitDirectory = GitDirectoryFrom(contents, worktreePath);
        if (gitDirectory is null)
        {
            FileLog.Write($"[LinkedWorktree] ParentRepositoryOf: {worktreePath} holds a .git file that names no git directory");
            return null;
        }

        var repositoryGitDirectory = RepositoryGitDirectoryOf(gitDirectory);
        if (repositoryGitDirectory is null)
        {
            // A submodule lands here, and so does anything else whose git directory is not a worktree
            // entry. Both are left alone.
            FileLog.Write($"[LinkedWorktree] ParentRepositoryOf: {worktreePath} points at {gitDirectory}, which is not a linked-worktree entry");
            return null;
        }

        var repository = PrimaryWorkingTreeOf(repositoryGitDirectory);
        if (repository is null)
        {
            FileLog.Write($"[LinkedWorktree] ParentRepositoryOf: {repositoryGitDirectory} has no working tree - a bare repository");
            return null;
        }

        // THE POSITIVE PROOF, and the reason there is one. Everything above is arithmetic on a string
        // that came out of a file; this is the only step that looks at the disk and says the answer is
        // real. A repository that has been deleted out from under its worktrees, and a .git file left
        // pointing at a path that no longer exists, both stop here and the folder is kept as it is.
        if (!IsRepositoryProper(repository))
        {
            FileLog.Write($"[LinkedWorktree] ParentRepositoryOf: {worktreePath} names {repository}, which is not a repository on this disk - keeping the folder as it is");
            return null;
        }

        FileLog.Write($"[LinkedWorktree] ParentRepositoryOf: {worktreePath} is a linked worktree of {repository}");
        return repository;
    }

    /// <summary>
    /// The git directory a linked worktree's <c>.git</c> file names, as an absolute path - or null when
    /// the contents are not a <c>gitdir:</c> line at all.
    ///
    /// A relative target is resolved against the worktree's own folder, because that is what it is
    /// relative to: git writes one when a worktree is created with relative paths, so that a repository
    /// and its worktrees can be moved together.
    /// </summary>
    internal static string? GitDirectoryFrom(string? gitFileContents, string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(gitFileContents))
            return null;

        // The file git writes holds one line. Reading the first line that carries the prefix, rather
        // than the whole file, keeps a trailing newline or a stray blank line from becoming part of a
        // path.
        foreach (var line in gitFileContents.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var target = trimmed[GitDirPrefix.Length..].Trim();
            if (target.Length == 0)
                return null;

            try
            {
                return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(worktreePath, target));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[LinkedWorktree] GitDirectoryFrom FAILED for {target}: {ex.Message}");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// The repository's own git directory, given the git directory of one of its linked worktrees -
    /// that is, <c>&lt;git directory&gt;</c> given
    /// <c>&lt;git directory&gt;/worktrees/&lt;id&gt;</c>. Null when the path is not a worktree entry.
    ///
    /// <para>The second-to-last segment must be git's <c>worktrees</c> directory. That is the ONLY text
    /// comparison in this class, it is against a constant of git's documented repository layout rather
    /// than against anything a person named, and it is what keeps a git SUBMODULE - whose path runs
    /// through <c>modules</c> instead - out of this rule entirely.</para>
    /// </summary>
    internal static string? RepositoryGitDirectoryOf(string? worktreeGitDirectory)
    {
        var trimmed = (worktreeGitDirectory ?? "").Trim().TrimEnd(EitherSeparator);
        if (trimmed.Length == 0)
            return null;

        var lastSeparator = trimmed.LastIndexOfAny(EitherSeparator);
        if (lastSeparator <= 0)
            return null; // no <id> segment to strip

        var withoutId = trimmed[..lastSeparator];
        if (!string.Equals(RepositoryPaths.FolderName(withoutId), WorktreesSegment, StringComparison.OrdinalIgnoreCase))
            return null;

        var separatorBeforeWorktrees = withoutId.LastIndexOfAny(EitherSeparator);
        return separatorBeforeWorktrees <= 0 ? null : withoutId[..separatorBeforeWorktrees];
    }

    /// <summary>
    /// The working tree a git directory belongs to: the folder holding it, when the git directory is
    /// called <c>.git</c>. Null for a BARE repository, whose git directory is the repository and which
    /// therefore has no working tree for anybody to have started a session in.
    ///
    /// <para>THE ONE RULE, SHARED. <see cref="RepositoryMonitor"/> reaches this same question from the
    /// other side - it asks git for <c>rev-parse --git-common-dir</c> and needs the working tree that
    /// owns it - and calls this. A second copy would eventually disagree with this one about a bare
    /// repository, and the two catalogues that read it would then disagree about what a repository
    /// is.</para>
    /// </summary>
    public static string? PrimaryWorkingTreeOf(string? gitDirectory)
    {
        var trimmed = (gitDirectory ?? "").Trim().TrimEnd(EitherSeparator);
        if (trimmed.Length == 0)
            return null;
        if (!string.Equals(RepositoryPaths.FolderName(trimmed), ".git", StringComparison.OrdinalIgnoreCase))
            return null;

        var lastSeparator = trimmed.LastIndexOfAny(EitherSeparator);
        return lastSeparator <= 0 ? null : trimmed[..lastSeparator];
    }

    /// <summary>A repository proper: a folder that exists and holds a <c>.git</c> DIRECTORY.</summary>
    private static bool IsRepositoryProper(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.Exists(Path.Combine(path, ".git"));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LinkedWorktree] IsRepositoryProper FAILED for {path}: {ex.Message}");
            return false;
        }
    }
}
