using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// Resolves bare @file prompts into the referenced file's CONTENT for the wingman's view
/// (issue #208, review rounds 3-5): dictation submits "@.temp/input_*.txt" and seeds
/// submit "@.temp/seed-*.md", so the package's YOU ASKED anchor renders as an opaque
/// path - the cold reader cannot see what the user actually said. Substituting the file
/// content fixes the brain's prompt, the saved package, and the review corpus in one
/// place.
///
/// Boundary, not fallback: substitution requires the session's repo path and a readable
/// file on THIS machine. A remote Director's repo is not readable here - the original
/// @reference is kept verbatim and the skip is logged. Nothing is invented.
/// </summary>
public static class DictatedPromptResolver
{
    /// <summary>Substitution cap; mirrors the user-prompt truncation budget in
    /// <see cref="TurnBriefContract"/>'s prompt assembly.</summary>
    public const int MaxChars = 4_000;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    // The ENTIRE prompt is one @<relative path> token - no spaces, no surrounding text.
    // Mixed prompts ("look at @.temp/x.txt and tell me...") keep the user's words and
    // are never touched.
    private static readonly Regex BareAtReference = new(
        @"^@([\w.\-]+(?:[/\\][\w.\-]+)+)$", RegexOptions.Compiled, RegexTimeout);

    /// <summary>Cheap pre-check so callers only fetch the repo path when it matters.</summary>
    public static bool NeedsResolution(TurnPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return IsBareAtReference(package.FirstUserPrompt, out _)
               || IsBareAtReference(package.LastUserPrompt, out _);
    }

    /// <summary>True when the whole prompt is a single @relative-path token (and the
    /// path does not try to escape upward).</summary>
    public static bool IsBareAtReference(string? prompt, out string relativePath)
    {
        relativePath = "";
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        var m = BareAtReference.Match(prompt.Trim());
        if (!m.Success) return false;
        relativePath = m.Groups[1].Value;
        return !relativePath.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>Returns the package with bare @file prompts replaced by the files'
    /// content, or the SAME package when nothing resolves.</summary>
    public static TurnPackage Resolve(TurnPackage package, string? repoPath)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(repoPath)) return package;

        var first = ResolveOne(package.FirstUserPrompt, repoPath, package.SessionId, "firstUserPrompt");
        var last = ResolveOne(package.LastUserPrompt, repoPath, package.SessionId, "lastUserPrompt");
        if (first is null && last is null) return package;

        return package with
        {
            FirstUserPrompt = first ?? package.FirstUserPrompt,
            LastUserPrompt = last ?? package.LastUserPrompt,
        };
    }

    private static string? ResolveOne(string? prompt, string repoPath, Guid sessionId, string field)
    {
        if (!IsBareAtReference(prompt, out var relative)) return null;

        var root = Path.GetFullPath(repoPath);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[DictatedPromptResolver] sid={sessionId} {field}: '{relative}' escapes the repo root; kept verbatim");
            return null;
        }
        if (!File.Exists(full))
        {
            // Remote Director repos are not readable from this machine - the documented
            // boundary. The raw @reference stays, exactly as the pane shows today.
            FileLog.Write($"[DictatedPromptResolver] sid={sessionId} {field}: '{relative}' not found under '{root}' (remote Director or deleted); kept verbatim");
            return null;
        }

        var content = File.ReadAllText(full).Trim();
        if (content.Length == 0)
        {
            FileLog.Write($"[DictatedPromptResolver] sid={sessionId} {field}: '{relative}' is empty; kept verbatim");
            return null;
        }
        if (content.Length > MaxChars) content = content[..MaxChars] + "...";
        FileLog.Write($"[DictatedPromptResolver] sid={sessionId} {field}: substituted {content.Length} chars from '{relative}'");
        return content;
    }
}
