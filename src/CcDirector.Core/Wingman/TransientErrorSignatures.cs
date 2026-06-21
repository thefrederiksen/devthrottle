namespace CcDirector.Core.Wingman;

/// <summary>
/// Pure, deterministic classifier for Anthropic API error text seen in a Claude Code session's
/// terminal (issue #476). It answers exactly one question: is the visible error a TRANSIENT,
/// retryable server-side blip that will usually clear itself in a moment, as opposed to a
/// TERMINAL error the user must fix (bad key, auth, quota/billing, malformed request)?
///
/// The auto-resume scheduler acts ONLY on a transient classification. Terminal errors are never
/// auto-retried - retrying them is pointless and would mask a real problem (scope OUT in the
/// issue). The rule is conservative and content-based:
///
///   transient  = the text matches a known transient signature
///   terminal   = the text matches a known terminal signature
///   decision   = transient AND NOT terminal  (a screen that somehow shows both is treated as
///                terminal, i.e. not auto-resumed - fail closed toward NOT acting)
///
/// All matching is case-insensitive substring matching on the ANSI-stripped resolved screen
/// text; no regex, so there is no catastrophic-backtracking risk and the gate stays fast.
/// </summary>
public static class TransientErrorSignatures
{
    /// <summary>
    /// Substrings that mark a TRANSIENT, retryable Anthropic server error. At minimum the
    /// verbatim field-seen HTTP 500 message, plus the closely-related 529 "Overloaded" /
    /// "try again" transient family (issue A-4). Lower-cased; matched case-insensitively.
    /// </summary>
    public static readonly IReadOnlyList<string> TransientSubstrings = new[]
    {
        // The verbatim field message (Screenshot 2026-06-16 133011.png):
        // "API Error: 500 Internal server error. This is a server-side issue, usually temporary
        //  - try again in a moment. If it persists, check https://status.claude.com"
        "500 internal server error",
        "server-side issue, usually temporary",
        // The 529 overloaded family.
        "529",
        "overloaded",
        "overloaded_error",
        // The shared "try again" wording that accompanies both transient families.
        "try again in a moment",
    };

    /// <summary>
    /// Substrings that mark a TERMINAL (non-transient) error the user must resolve. These VETO
    /// auto-resume even if a transient substring also appears: a wrong key or exhausted quota
    /// will never clear on a retry. Lower-cased; matched case-insensitively.
    /// </summary>
    public static readonly IReadOnlyList<string> TerminalSubstrings = new[]
    {
        "invalid api key",
        "invalid x-api-key",
        "authentication_error",
        "authentication error",
        "401 unauthorized",
        "403 forbidden",
        "permission_error",
        "credit balance is too low",
        "insufficient_quota",
        "quota",
        "billing",
        "invalid_request_error",
        "400 bad request",
        "malformed",
    };

    /// <summary>True when <paramref name="screenText"/> contains a transient signature.</summary>
    public static bool ContainsTransient(string? screenText) =>
        ContainsAny(screenText, TransientSubstrings);

    /// <summary>True when <paramref name="screenText"/> contains a terminal (non-transient)
    /// signature.</summary>
    public static bool ContainsTerminal(string? screenText) =>
        ContainsAny(screenText, TerminalSubstrings);

    /// <summary>
    /// The auto-resume decision: the screen shows a transient error that is NOT also a terminal
    /// error. This is the ONLY signal the scheduler acts on.
    /// </summary>
    public static bool IsRetryableTransient(string? screenText)
    {
        if (string.IsNullOrWhiteSpace(screenText)) return false;
        if (ContainsTerminal(screenText)) return false; // veto - fail closed toward NOT acting
        return ContainsTransient(screenText);
    }

    private static bool ContainsAny(string? text, IReadOnlyList<string> needles)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
