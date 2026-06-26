using CcDirector.Core.Utilities;

namespace CcDirector.Cockpit.Services;

/// <summary>One actionable link detected in a History bubble.</summary>
public sealed record HistoryLink(string Text, bool IsUrl);

/// <summary>
/// Extracts the actionable file paths and URLs from a History bubble body, reusing the product's
/// one shared recognizer (<see cref="LinkDetector"/>) rather than an ad-hoc parser (#740). The
/// Cockpit runs in a (possibly remote) browser, so it cannot stat the Director host's filesystem:
/// detection is run with no repo root and no existence check, which yields URLs and ABSOLUTE paths
/// only - relative paths are deliberately not guessed (no existence check to validate them), so
/// there are no false positives. URLs open in a new tab plus Copy URL; paths get Copy Path only.
/// </summary>
public static class HistoryLinks
{
    /// <summary>Distinct links in the body, in first-seen order.</summary>
    public static List<HistoryLink> Extract(string? body)
    {
        var result = new List<HistoryLink>();
        if (string.IsNullOrWhiteSpace(body))
            return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        // LinkDetector scans a single line at a time; a bubble body is multi-line.
        foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
        {
            // repoPath = null, pathExistsCheck = null: URLs + absolute paths, no relative guessing.
            foreach (var match in LinkDetector.FindAllLinkMatches(line, repoPath: null, pathExistsCheck: null))
            {
                var isUrl = match.Type == LinkDetector.LinkType.Url;
                var key = (isUrl ? "u:" : "p:") + match.Text;
                if (seen.Add(key))
                    result.Add(new HistoryLink(match.Text, isUrl));
            }
        }
        return result;
    }
}
