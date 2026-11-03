using System.Diagnostics;

namespace CcDirector.Core.Git;

/// <summary>
/// Turns off git's OPTIONAL locks for a git process the Director starts (issue #2872).
///
/// A plain <c>git status</c> refreshes the index and writes it back as a side effect, holding
/// <c>index.lock</c> while it does. The Director reads status in the background on every session
/// worktree, and a superseded read is cancelled by killing the git process tree. A killed git cannot
/// remove its lock, so a 0-byte <c>index.lock</c> stayed behind for good and every later commit, pull,
/// checkout, rebase or worktree removal in that checkout failed with "File exists" - while status
/// itself kept working, so nothing in the Director showed it.
///
/// <c>GIT_OPTIONAL_LOCKS=0</c> tells git to skip every sub-operation that needs a lock it does not
/// strictly require, which is exactly that refresh-and-write. It changes nothing for a command that
/// is MEANT to write: commit, checkout, branch and the rest still take the locks they need. That is
/// why it is safe to set in a shared runner that also carries the odd write.
/// </summary>
public static class GitOptionalLocks
{
    /// <summary>The environment variable git reads.</summary>
    public const string VariableName = "GIT_OPTIONAL_LOCKS";

    /// <summary>The child-environment addition for <see cref="Utilities.ProcessRunner"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string> Off =
        new Dictionary<string, string> { [VariableName] = "0" };

    /// <summary>Sets optional locks off in <paramref name="startInfo"/>'s child environment.</summary>
    public static void ApplyTo(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.Environment[VariableName] = "0";
    }
}
