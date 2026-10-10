using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Git;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Onboarding;

/// <summary>One suggested code base folder: where it is, how many repositories it verifiably holds.</summary>
public sealed record CodeFolderSuggestion(string Path, int RepoCount);

/// <summary>
/// Finds the folders where this machine's git repositories plausibly live, for the wizard's
/// "Where does your code live?" step - WITHOUT ever walking a whole drive. The scan is a fixed
/// shortlist of shallow probes:
///
///   1. Known developer spots under the user profile (source\repos, Projects, Code, repos, git,
///      dev, src; plus ~/Developer on macOS).
///   2. One top-level directory listing per fixed drive root (Windows only) keeping folders whose
///      NAME suggests code (Repos*, Projects, Code, dev, src, git, source) - this is how
///      D:\Repos<anything> is found by name, never by walking the drive.
///
/// Each candidate is then VERIFIED with the same one-level rule the product's repository monitor
/// uses (immediate children holding a .git directory, via RemoteRepoProvider.ScanLocalRepos), so
/// the count the wizard promises is exactly what the board and New Session will show. Only
/// candidates with at least one repository are suggested.
/// </summary>
public static class CodeFolderScout
{
    /// <summary>Folder names that suggest code when seen at a drive root. Pure - unit-tested.</summary>
    private static readonly Regex CodeNamePattern = new(
        "^(repos.*|projects?|code|dev|src|git|source)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Whether a folder NAME (not path) suggests it holds code. Pure - unit-tested.</summary>
    public static bool NameSuggestsCode(string folderName) => CodeNamePattern.IsMatch(folderName);

    /// <summary>
    /// The candidate folders to probe, best-first, existence-filtered and deduplicated. Cheap: a
    /// handful of existence checks plus one top-level listing per fixed drive (Windows).
    /// </summary>
    public static IReadOnlyList<string> CandidateRoots()
    {
        var candidates = new List<string>();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(home))
        {
            // Visual Studio's default first on Windows; the common conventions everywhere.
            if (OperatingSystem.IsWindows())
                candidates.Add(Path.Combine(home, "source", "repos"));
            if (OperatingSystem.IsMacOS())
                candidates.Add(Path.Combine(home, "Developer"));
            foreach (var name in new[] { "Projects", "Code", "repos", "git", "dev", "src" })
                candidates.Add(Path.Combine(home, name));
        }

        // Code kept OUTSIDE the home directory. This is not an edge case - it is where most of the
        // repositories on the author's own machine live (C:\Repos, C:\ReposBPMN, C:\ReposFred, D:\Dev),
        // and none of them would be found by the home-directory probes above.
        //
        // Both platforms get an arm. Windows sweeps the top level of every fixed drive; macOS has no
        // drive letters, so the equivalents are the filesystem root (where a /Projects or /Code is
        // occasionally kept) and every mounted volume under /Volumes, which is where an external or
        // second disk appears. Without the macOS arm this screen found far less on a Mac than on
        // Windows, for no reason the user could see.
        // One stalled container must not cost the user every other one. A mounted network volume under
        // /Volumes, or a disconnected drive on Windows, can block inside Directory.GetDirectories for
        // a long time - and that call takes no cancellation token, so the scan's overall time budget
        // cannot interrupt it once it is inside. What we CAN do is stop starting new ones: each
        // container is given its own short budget, and a container that overruns is abandoned and
        // logged rather than allowed to hold up the rest.
        foreach (var container in TopLevelContainers())
        {
            try
            {
                var listing = Task.Run(() => Directory.GetDirectories(container));
                if (!listing.Wait(PerContainerListTimeout))
                {
                    FileLog.Write($"[CodeFolderScout] {container} did not answer within {PerContainerListTimeout.TotalSeconds:0}s - skipped (a stalled or disconnected volume)");
                    continue;
                }

                foreach (var dir in listing.Result)
                    if (NameSuggestsCode(Path.GetFileName(dir)))
                        candidates.Add(dir);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[CodeFolderScout] cannot list {container} FAILED: {ex.GetBaseException().Message}");
            }
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var result = new List<string>();
        foreach (var candidate in candidates)
        {
            if (!Directory.Exists(candidate)) continue;
            var full = Path.GetFullPath(candidate);
            if (seen.Add(full))
                result.Add(full);
        }
        FileLog.Write($"[CodeFolderScout] CandidateRoots -> {result.Count} folder(s)");
        return result;
    }

    /// <summary>
    /// The places to sweep for code folders that live outside the home directory, one top-level
    /// listing each. Windows: every fixed drive root. macOS: the filesystem root and each mounted
    /// volume. Anywhere else: nothing, so the caller simply falls back to the home-directory probes.
    /// </summary>
    /// <summary>
    /// How long one top-level listing may take before it is abandoned. Deliberately much shorter than
    /// the caller's whole-scan budget, because a stalled volume cannot be cancelled once entered - the
    /// only defence is to stop waiting on it.
    /// </summary>
    private static readonly TimeSpan PerContainerListTimeout = TimeSpan.FromSeconds(3);

    private static IEnumerable<string> TopLevelContainers()
    {
        if (OperatingSystem.IsWindows())
            return SafeFixedDriveRoots();

        if (!OperatingSystem.IsMacOS())
            return Array.Empty<string>();

        var containers = new List<string> { "/" };
        try
        {
            if (Directory.Exists("/Volumes"))
                containers.AddRange(Directory.GetDirectories("/Volumes"));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodeFolderScout] cannot list /Volumes FAILED: {ex.Message}");
        }
        return containers;
    }

    /// <summary>
    /// Count the repositories a base folder holds, by the SAME rule the repository monitor uses to
    /// list them (immediate children with a .git directory). The wizard must promise exactly what
    /// the product will deliver - one rule for both sides of the number.
    /// </summary>
    public static int CountRepos(string root) => RemoteRepoProvider.ScanLocalRepos(root).Count;

    /// <summary>Whether the folder itself is a git checkout (a .git directory or worktree .git file).</summary>
    public static bool IsItselfARepository(string path)
        => Directory.Exists(path)
           && (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")));

    /// <summary>
    /// Probe every candidate and stream back the ones that verifiably hold repositories, best-first.
    /// Each probe is one directory listing plus an existence check per child - milliseconds - and the
    /// token bounds the whole sweep.
    /// </summary>
    public static Task ScanAsync(IProgress<CodeFolderSuggestion> progress, CancellationToken ct = default)
        => Task.Run(() =>
        {
            foreach (var candidate in CandidateRoots())
            {
                ct.ThrowIfCancellationRequested();
                var count = CountRepos(candidate);
                if (count > 0)
                {
                    FileLog.Write($"[CodeFolderScout] suggest {candidate}: {count} repo(s)");
                    progress.Report(new CodeFolderSuggestion(candidate, count));
                }
            }
        }, ct);

    /// <summary>
    /// Resolve what a user-browsed folder should register as: the folder itself normally, but when
    /// they picked a single repository (the folder IS a git checkout), its PARENT - the roots list
    /// holds base folders, and the monitor only lists a root's children.
    /// </summary>
    public static string ResolveBrowsedFolder(string picked)
    {
        if (IsItselfARepository(picked) && CountRepos(picked) == 0)
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(picked).TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(parent))
            {
                FileLog.Write($"[CodeFolderScout] browsed folder is itself a repository; using parent {parent}");
                return parent;
            }
        }
        return Path.GetFullPath(picked);
    }

    private static IEnumerable<string> SafeFixedDriveRoots()
    {
        List<string> roots = new();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType == DriveType.Fixed && drive.IsReady)
                        roots.Add(drive.RootDirectory.FullName);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[CodeFolderScout] drive probe FAILED: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CodeFolderScout] GetDrives FAILED: {ex.Message}");
        }
        return roots;
    }
}
