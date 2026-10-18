using CcDirector.Core.Utilities;

namespace CcDirector.Core.Codex;

/// <summary>
/// WHAT A CODEX LAUNCH LINE CARRIES SO CODEX DOES NOT STOP ON A QUESTION BEFORE ITS FIRST PROMPT (Command Line Agent
/// Handling mission, the start-up questions; issues #3404 and #2898).
///
/// Two questions stopped Codex sessions that nobody could answer - a session opened by another session cannot type into
/// the one it opened - and each swallowed the first prompt (delivery rig, scenario claude-trusted-folder: 0 of 2 on
/// main, 16 and 17 October 2026, Codex 0.163.0):
/// <list type="bullet">
/// <item><b>"Update available! ... 1. Update now 2. Skip".</b> Codex's own setting <c>check_for_update_on_startup</c> turns
/// the check off. Updating Codex is a separate, deliberate step, never a question in the middle of a start.</item>
/// <item><b>"Trust this folder?"</b> in a repository the owner works in every day, because Codex keeps its own record of
/// trusted folders apart from Claude Code's. When Claude Code's record already trusts the folder (<see
/// cref="Claude.ClaudeFolderTrust.Covers"/>), the owner has trusted these files, and the launch line says so for this
/// one run. A folder nobody trusted is never trusted here: the question stays.</item>
/// </list>
///
/// BOTH ARE <c>-c</c> OVERRIDES, SO NOTHING IS WRITTEN. Codex reads them for this run only; its <c>config.toml</c> is not
/// touched, so nothing has to be locked against a Codex process writing the same file. Proven on Codex 0.163.0: with the
/// override, a folder Codex never saw, a sub-folder of one and a git worktree each start without the question, and the
/// file is unchanged.
///
/// The folder is a TOML literal string (single quotes), so a Windows path's backslashes need no escaping and the value
/// holds no double quote to fight the command line's quoting. A path that itself holds a quote cannot be written that
/// way and gets no trust line - Codex asks, as it always did.
/// </summary>
public static class CodexStartupArguments
{
    /// <summary>The override that turns Codex's update check at start-up off.</summary>
    public const string UpdateCheckOff = "-c check_for_update_on_startup=false";

    /// <summary>
    /// The override that tells Codex <paramref name="folder"/> is trusted for this run, or null when the folder's path
    /// holds a quote and cannot be written as a TOML literal string.
    /// </summary>
    public static string? TrustFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var full = Path.GetFullPath(folder);
        if (full.Contains('\'') || full.Contains('"'))
        {
            FileLog.Write($"[CodexStartupArguments] TrustFolder: {full} holds a quote, so no trust line is written; Codex will ask");
            return null;
        }
        return $"-c \"projects={{'{full}'={{trust_level='trusted'}}}}\"";
    }

    /// <summary>
    /// <paramref name="args"/> with the update check turned off - unless the line already sets it - and, when
    /// <paramref name="trustedByOwner"/>, the folder marked trusted.
    /// </summary>
    public static string Append(string args, string workingDirectory, bool trustedByOwner)
    {
        var line = args ?? "";
        if (!line.Contains("check_for_update_on_startup", StringComparison.Ordinal))
            line = $"{line} {UpdateCheckOff}".Trim();
        if (trustedByOwner && TrustFolder(workingDirectory) is { } trust)
            line = $"{line} {trust}";
        FileLog.Write($"[CodexStartupArguments] Append: folder={workingDirectory}, trustedByOwner={trustedByOwner}");
        return line;
    }
}
