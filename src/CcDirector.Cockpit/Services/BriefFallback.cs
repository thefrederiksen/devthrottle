namespace CcDirector.Cockpit.Services;

/// <summary>
/// Client-side mirror of BriefBuilder.FallbackNeedsYou (Core) for the degrade path where an
/// OLD Director serves only /summary: the reply's final non-empty paragraph is shown as the
/// needs-you block - verbatim by construction. The Cockpit stays Contracts-only, so the few
/// lines are duplicated rather than referencing Core.
/// </summary>
public static class BriefFallback
{
    public static string? FinalParagraph(string? reply, int maxChars = 600)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var paragraphs = reply.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        for (var i = paragraphs.Length - 1; i >= 0; i--)
        {
            var p = paragraphs[i].Trim();
            if (p.Length == 0) continue;
            return p.Length <= maxChars ? p : p[^maxChars..];
        }
        return null;
    }
}
