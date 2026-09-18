using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Git;

/// <summary>
/// Raised when this machine's cc-worktrees records exist but cannot be read, so which directories
/// belong to a pool is UNKNOWN.
///
/// It is thrown rather than swallowed because every caller of <see cref="CcWorktreesPoolSlots"/> is
/// about to decide whether a directory may be removed or handed to somebody else, and "I could not
/// tell" must never arrive there looking like "none of them". That is the same fail-closed rule
/// <see cref="WorktreeReservationStore"/> already follows for reservations, and for the same reason.
/// </summary>
public sealed class CcWorktreesStateUnreadableException : Exception
{
    public CcWorktreesStateUnreadableException(string message, Exception? inner = null) : base(message, inner)
    {
    }
}

/// <summary>
/// Answers one question for the Director's own worktree services: does this directory belong to a
/// cc-worktrees pool?
///
/// A pool slot is NOT the Director's to reap, reserve or hand out. cc-worktrees owns it, holds the
/// lease on it, and is the only thing that knows whether the work in it has landed. Two owners for
/// one directory is how a slot gets deleted out from under the session working in it.
///
/// THE MARKER IS WHAT THE POOL ITSELF WRITES - there is no second registry here, and nothing asks the
/// Director to remember which directories it handed out. Two independent signals, and either one is
/// enough:
///
/// 1. THE RECORDS. <c>registry.json</c> lists every repository with a pool on this machine, and each
///    pool's state file under <c>pools/</c> names its slots and their paths. This is the tool's own
///    account of itself.
/// 2. THE LAYOUT. A pool slot is created at <c>&lt;repo-parent&gt;/&lt;repo-name&gt;.worktrees/wtNN</c>
///    and nothing else in this product creates that shape. It needs no state file at all, so a slot is
///    still recognised on a machine whose records were lost - which is exactly the moment the records
///    would otherwise say "not a pool slot" about a directory full of somebody's work.
/// </summary>
public sealed class CcWorktreesPoolSlots
{
    /// <summary>The variable cc-worktrees itself reads to put its state somewhere other than the default.</summary>
    public const string HomeEnvVar = "CC_WORKTREES_HOME";

    /// <summary>A slot directory's name, as the tool names them: wt01, wt02, ... (it allows more than two digits).</summary>
    private static readonly Regex SlotName = new(@"^wt[0-9]{2,}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The suffix the tool gives the directory it keeps a repository's slots in.</summary>
    private const string SlotsDirectorySuffix = ".worktrees";

    private readonly Func<string?> _stateHome;

    public CcWorktreesPoolSlots() : this(null)
    {
    }

    /// <param name="stateHome">
    /// Where cc-worktrees keeps its state, for a test that must not read or touch the real one. Null
    /// resolves it the way the tool does.
    /// </param>
    public CcWorktreesPoolSlots(string? stateHome)
    {
        _stateHome = stateHome is null ? DefaultStateHome : () => stateHome;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a cc-worktrees pool slot, or is inside one.
    ///
    /// Throws <see cref="CcWorktreesStateUnreadableException"/> when the records exist and cannot be
    /// read. The caller must treat that as "do not act", never as "no".
    /// </summary>
    public bool Owns(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        return HasSlotLayout(path) || IsInside(path, RecordedSlotDirectories());
    }

    /// <summary>
    /// True when <paramref name="path"/> is one of <paramref name="slotDirectories"/> or sits inside
    /// one. The list comes from <see cref="RecordedSlotDirectories"/>; a caller with many paths to
    /// judge reads it once and passes it here rather than re-reading the records for each one.
    ///
    /// This does NOT check the layout signal - a caller that wants both asks
    /// <see cref="HasSlotLayout"/> too, or uses <see cref="Owns"/>.
    /// </summary>
    public static bool IsInside(string? path, IReadOnlyList<string> slotDirectories)
    {
        if (string.IsNullOrWhiteSpace(path) || slotDirectories is null || slotDirectories.Count == 0)
            return false;

        var normalized = WorktreeReaperService.NormalizePath(path);
        foreach (var slot in slotDirectories)
        {
            if (normalized.Equals(slot, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(slot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="path"/> has the shape the pool creates:
    /// <c>&lt;anything&gt;/&lt;name&gt;.worktrees/wtNN</c>. Pure, and needs nothing on disk - this is
    /// the signal that still works when the tool's records are gone.
    /// </summary>
    public static bool HasSlotLayout(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var full = WorktreeReaperService.NormalizePath(path);
            var name = Path.GetFileName(full);
            if (!SlotName.IsMatch(name))
                return false;

            var parent = Path.GetFileName(Path.GetDirectoryName(full) ?? "");
            return parent.Length > SlotsDirectorySuffix.Length
                && parent.EndsWith(SlotsDirectorySuffix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            // A path this malformed is not one of ours, and the caller's own path handling will fail on
            // it in a place that can say more about it than this can.
            FileLog.Write($"[CcWorktreesPoolSlots] HasSlotLayout: could not read {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Every slot directory the tool's own records name, normalized. Empty when this machine has no
    /// cc-worktrees state at all, which is a positive answer: no pool was ever made here.
    /// </summary>
    /// <exception cref="CcWorktreesStateUnreadableException">The records exist and cannot be read.</exception>
    public IReadOnlyList<string> RecordedSlotDirectories()
    {
        var home = _stateHome();
        if (string.IsNullOrWhiteSpace(home) || !Directory.Exists(home))
            return Array.Empty<string>();

        var found = new List<string>();

        // Every repository with a pool, from the registry. Each one's slots live in one directory, so
        // the directory alone covers slots the state file has not caught up with (one is created
        // before the state is saved) as well as those it names.
        foreach (var repo in RegisteredRepositories(home))
        {
            try
            {
                var full = WorktreeReaperService.NormalizePath(repo);
                var parent = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(parent))
                    continue;
                found.Add(WorktreeReaperService.NormalizePath(
                    Path.Combine(parent, Path.GetFileName(full) + SlotsDirectorySuffix)));
            }
            catch (Exception ex)
            {
                throw new CcWorktreesStateUnreadableException(
                    $"the cc-worktrees registry names a repository this machine cannot resolve ({repo}): {ex.Message}", ex);
            }
        }

        // And the slot paths each pool state file records, which is where a pool whose repository moved
        // is still described truthfully.
        foreach (var path in StateFileSlotPaths(home))
            found.Add(path);

        return found;
    }

    private static IEnumerable<string> RegisteredRepositories(string home)
    {
        var registry = Path.Combine(home, "registry.json");
        if (!File.Exists(registry))
            return Array.Empty<string>();

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(registry));
            if (!document.RootElement.TryGetProperty("repos", out var repos) || repos.ValueKind != JsonValueKind.Array)
                throw new CcWorktreesStateUnreadableException(
                    $"the cc-worktrees registry {registry} has no list of repositories in it");
            return repos.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.String)
                .Select(r => r.GetString()!)
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .ToList();
        }
        catch (CcWorktreesStateUnreadableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CcWorktreesStateUnreadableException(
                $"the cc-worktrees registry {registry} could not be read, so which directories belong to a pool is unknown: {ex.Message}", ex);
        }
    }

    private static IEnumerable<string> StateFileSlotPaths(string home)
    {
        var pools = Path.Combine(home, "pools");
        if (!Directory.Exists(pools))
            return Array.Empty<string>();

        string[] files;
        try
        {
            files = Directory.GetFiles(pools, "*.json");
        }
        catch (Exception ex)
        {
            throw new CcWorktreesStateUnreadableException(
                $"the cc-worktrees pool state directory {pools} could not be listed, so which directories belong to a pool is unknown: {ex.Message}", ex);
        }

        var paths = new List<string>();
        foreach (var file in files)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                if (!document.RootElement.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var slot in slots.EnumerateObject())
                {
                    if (slot.Value.ValueKind == JsonValueKind.Object
                        && slot.Value.TryGetProperty("path", out var path)
                        && path.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(path.GetString()))
                        paths.Add(WorktreeReaperService.NormalizePath(path.GetString()!));
                }
            }
            catch (Exception ex)
            {
                throw new CcWorktreesStateUnreadableException(
                    $"the cc-worktrees pool state {file} could not be read, so which directories belong to a pool is unknown: {ex.Message}", ex);
            }
        }
        return paths;
    }

    /// <summary>
    /// Where cc-worktrees keeps its state, resolved exactly as the tool resolves it. A Director that
    /// looked somewhere else would answer "not a pool slot" about every slot on the machine.
    /// </summary>
    public static string? DefaultStateHome()
    {
        var overridden = Environment.GetEnvironmentVariable(HomeEnvVar);
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden;

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            return string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "cc-worktrees");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "cc-worktrees");

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(home, ".local", "share", "cc-worktrees")
            : Path.Combine(xdg, "cc-worktrees");
    }
}
