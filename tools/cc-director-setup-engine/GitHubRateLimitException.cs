namespace CcDirector.Setup.Engine;

/// <summary>
/// Raised when the GitHub REST API rejects the release-information fetch with an
/// HTTP 403 (or 429) rate-limit response and the bounded retry has been exhausted.
/// The setup wizards recognise this type to show a plain-English message that names
/// the cause (a shared-network-address GitHub rate limit) and tells the user what to
/// do next, instead of surfacing a raw "status code does not indicate success: 403".
/// </summary>
public sealed class GitHubRateLimitException : Exception
{
    /// <summary>
    /// The moment GitHub says the rate-limit budget resets, when the response carried
    /// a usable hint (the Retry-After header or the X-RateLimit-Reset header). Null when
    /// GitHub gave no reset hint.
    /// </summary>
    public DateTimeOffset? ResetsAtUtc { get; }

    /// <summary>How many fetch attempts were made before giving up.</summary>
    public int Attempts { get; }

    public GitHubRateLimitException(string message, int attempts, DateTimeOffset? resetsAtUtc)
        : base(message)
    {
        Attempts = attempts;
        ResetsAtUtc = resetsAtUtc;
    }

    /// <summary>
    /// The plain-English, ASCII-only message both setup wizards show at the Install step when the
    /// release fetch is rate-limited and retries are exhausted. It names the cause and tells the
    /// user what to do next, replacing the bare "ERROR: Could not fetch release info from GitHub."
    /// </summary>
    public string UserMessage()
    {
        var when = ResetsAtUtc is { } r
            ? $" The limit resets around {r.ToLocalTime():HH:mm} local time."
            : "";
        return
            "GitHub is temporarily blocking release downloads from your network because too many " +
            "requests came from your internet address (this is common on shared home, office, or VPN " +
            "networks)." + when + " Please wait a few minutes and click Retry, or try again from a " +
            "different network.";
    }
}
