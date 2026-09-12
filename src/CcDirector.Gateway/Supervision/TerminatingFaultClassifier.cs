namespace CcDirector.Gateway.Supervision;

/// <summary>
/// Step 2 of the supervisor funnel (issue #915): decide, from the session's LIVE screen alone, whether the
/// turn that just ended died on a fault - and which class of fault. Pure, free, instant, no model call. Most
/// idle sessions resolve to <see cref="SessionFaultClass.None"/> here and cost nothing.
///
/// THE SCREEN, NOT THE SCROLLBACK. Claude Code, Grok, OpenCode and Copilot hold the terminal ALTERNATE
/// screen for the whole session, and the parser deliberately never commits alternate-screen frames to
/// scrollback - so a buffer read comes back empty for exactly the agents this feature exists for. The live
/// grid is the only place the terminating error is legible.
///
/// THE WINDOW IS WHAT KEEPS THIS NON-INTERRUPTIVE. A fault only counts when it is among the last
/// <see cref="DefaultWindowLines"/> lines of real content on the screen. An error further up - one a later,
/// perfectly healthy turn has already scrolled away from - is not a terminating fault, and a session that
/// merely REMEMBERS an old error must never be sent a "continue". This is the rule that stops the engine
/// re-firing on a session it already rescued.
///
/// THE ASYMMETRY IS DELIBERATE. For the two classes that ACT on a session (transient transport, rate limited)
/// an ordinary English signature is not enough: the same line must also carry an error marker, so a session
/// whose agent happened to PRINT the words "connection refused" while discussing a log is never typed into.
/// Signatures that cannot plausibly occur in ordinary prose - the errno codes, "socket hang up",
/// "rate_limit_error" - stand on their own, because demanding a marker beside them would only lose real
/// faults. For the two classes that only RAISE A HAND (non-recoverable, context full) the signature alone is
/// always enough: they touch nothing, so the safe direction there is to notice more, not less.
/// </summary>
public static class TerminatingFaultClassifier
{
    /// <summary>How many lines of real content at the end of the screen may hold a terminating fault.</summary>
    public const int DefaultWindowLines = 12;

    /// <summary>
    /// Classify the tail of a session's live screen. <paramref name="rows"/> is the resolved grid
    /// (<c>ScreenGridResponse.Rows</c>); null, empty or blank is <see cref="SessionFaultClass.None"/> -
    /// an unreadable screen is never a fault, because acting on one would be guessing.
    /// </summary>
    public static SessionFault Classify(IReadOnlyList<string>? rows, int windowLines = DefaultWindowLines)
    {
        var window = ContentWindow(rows, windowLines);
        if (window.Count == 0) return SessionFault.None;

        // Class-major, in precedence order: a screen carrying BOTH an authentication failure and a dropped
        // connection escalates rather than retrying. The dangerous mistake is retrying something that will
        // never succeed on its own, so the classes that refuse to act are asked first.
        if (FirstMatch(window, NonRecoverableSignatures, requireErrorMarker: false) is { } nonRecoverable)
            return new SessionFault(SessionFaultClass.NonRecoverable, nonRecoverable);
        if (FirstMatch(window, ContextFullSignatures, requireErrorMarker: false) is { } contextFull)
            return new SessionFault(SessionFaultClass.ContextFull, contextFull);
        if (FirstMatch(window, RateLimitedSelfEvident, requireErrorMarker: false) is { } rateLimitedPlain)
            return new SessionFault(SessionFaultClass.RateLimited, rateLimitedPlain);
        if (FirstMatch(window, RateLimitedGeneric, requireErrorMarker: true) is { } rateLimited)
            return new SessionFault(SessionFaultClass.RateLimited, rateLimited);
        if (FirstMatch(window, TransientTransportSelfEvident, requireErrorMarker: false) is { } transientPlain)
            return new SessionFault(SessionFaultClass.TransientTransport, transientPlain);
        if (FirstMatch(window, TransientTransportGeneric, requireErrorMarker: true) is { } transient)
            return new SessionFault(SessionFaultClass.TransientTransport, transient);

        // Nothing recognized. If the turn nonetheless ended on something that announces itself as an error,
        // say so honestly as UNCLASSIFIED - that is the only input step 3 (the model fallback) accepts. A
        // screen with no error banner at all is a clean turn end, and the funnel stops.
        if (FirstMatch(window, ErrorBanners, requireErrorMarker: false) is { } banner)
            return new SessionFault(SessionFaultClass.Unclassified, banner);

        return SessionFault.None;
    }

    /// <summary>
    /// The last <paramref name="windowLines"/> lines of REAL CONTENT on the screen: blank rows, box borders,
    /// the composer line and the agent's mode/shortcut footer are all chrome and are dropped, wherever they
    /// sit. Public so a test can assert exactly which lines are in scope.
    /// </summary>
    public static IReadOnlyList<string> ContentWindow(IReadOnlyList<string>? rows, int windowLines = DefaultWindowLines)
    {
        if (rows is null || rows.Count == 0 || windowLines <= 0) return Array.Empty<string>();
        var content = new List<string>();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row)) continue;
            if (IsChrome(row)) continue;
            content.Add(row);
        }
        if (content.Count <= windowLines) return content;
        return content.GetRange(content.Count - windowLines, windowLines);
    }

    /// <summary>The first signature present in the window, or null. When <paramref name="requireErrorMarker"/>
    /// is set, the matching line must ALSO carry an error marker.</summary>
    private static string? FirstMatch(IReadOnlyList<string> window, string[] signatures, bool requireErrorMarker)
    {
        foreach (var line in window)
        {
            var lower = line.ToLowerInvariant();
            if (requireErrorMarker && !HasErrorMarker(lower)) continue;
            foreach (var signature in signatures)
            {
                if (lower.Contains(signature, StringComparison.Ordinal))
                    return signature;
            }
        }
        return null;
    }

    private static bool HasErrorMarker(string lowerLine)
    {
        foreach (var marker in ErrorMarkers)
        {
            if (lowerLine.Contains(marker, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Screen furniture: a blank row, a pure box border, the composer input line, or the agent's
    /// mode/shortcut footer. None of these can carry a terminating fault.</summary>
    private static bool IsChrome(string row)
    {
        var trimmed = row.Trim();
        if (trimmed.Length == 0) return true;

        var stripped = trimmed.Trim(BorderGlyphs).Trim();
        if (stripped.Length == 0) return true;                                  // border-only row
        if (stripped[0] == '>' || stripped[0] == '❯') return true;         // the composer line

        var lower = stripped.ToLowerInvariant();
        foreach (var anchor in FooterAnchors)
        {
            if (lower.Contains(anchor, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Words that mark a line as an agent-emitted failure rather than prose that merely mentions one.
    /// Required beside a GENERIC signature before either acting class may match.
    ///
    /// Deliberately NOT including "refused", "timed out" or "disconnected": each of those is already part of a
    /// generic signature below, so listing it here would let that signature satisfy its own marker test and
    /// the guard would wave through the very sentence it exists to reject.
    /// </summary>
    private static readonly string[] ErrorMarkers =
    {
        "error", "failed", "failure", "unable", "cannot", "can't", "aborted", "retrying",
    };

    /// <summary>Transport faults that cannot plausibly appear in ordinary prose - an errno code or a runtime's
    /// own error text. These stand on their own. The July 21 incident matched the first one.</summary>
    private static readonly string[] TransientTransportSelfEvident =
    {
        "enotfound", "econnreset", "econnrefused", "etimedout", "eai_again", "epipe",
        "socket hang up", "getaddrinfo", "upstream connect error", "fetch failed",
        "unable to connect to api",
    };

    /// <summary>Transport faults phrased in ordinary English - a session could be DISCUSSING one of these
    /// rather than dying on it, so they need an error marker on the same line.</summary>
    private static readonly string[] TransientTransportGeneric =
    {
        "network error", "connection error", "connection reset", "connection refused",
        "connection closed", "request timed out",
    };

    /// <summary>Provider throttling in the provider's own vocabulary - recoverable after backing off.</summary>
    private static readonly string[] RateLimitedSelfEvident =
    {
        "rate_limit_error", "too many requests", "overloaded_error",
    };

    /// <summary>Throttling phrased in ordinary English, or a bare status number - marker required.</summary>
    private static readonly string[] RateLimitedGeneric =
    {
        "rate limit", "429", "overloaded",
    };

    /// <summary>A full context window. Recoverable only by compacting first, which is phase 2.</summary>
    private static readonly string[] ContextFullSignatures =
    {
        "prompt is too long", "context limit reached", "context window is full", "conversation is too long",
    };

    /// <summary>Faults that mean the work cannot proceed. Never auto-continued (issue #758's class).</summary>
    private static readonly string[] NonRecoverableSignatures =
    {
        "credit balance is too low", "out of credits", "insufficient credits", "usage limit reached",
        "invalid api key", "invalid x-api-key", "api key auth failed", "authentication_error", "authentication failed",
        "oauth token has expired", "please run /login", "permission_error", "403 forbidden",
    };

    /// <summary>A line that announces itself as a failure without naming a class we know. The ONLY thing
    /// that reaches step 3.</summary>
    private static readonly string[] ErrorBanners =
    {
        "api error", "error:", "fatal:", "unhandled exception", "request failed",
    };

    private static readonly string[] FooterAnchors =
    {
        "bypass permissions", "plan mode", "accept edits", "shift+tab to cycle", "? for shortcuts",
    };

    private static readonly char[] BorderGlyphs =
    {
        '│','┃','┆','┇','┊','┋','╎','╏','║',
        '╭','╮','╰','╯','┌','┐','└','┘',
        '╔','╗','╚','╝',
        '─','━','═','┄','┅','┈','┉','|',' ','\t','\r',
    };
}
