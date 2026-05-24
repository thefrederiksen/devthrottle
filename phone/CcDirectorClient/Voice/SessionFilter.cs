namespace CcDirectorClient.Voice;

/// <summary>
/// Decides which sessions the all-sessions conductor speaks for. Pure logic, no
/// MAUI/Android dependency, so it is unit tested off-device.
///
/// The conductor only speaks for sessions that NEED the user. The owning
/// Director's wingman already encodes exactly that in the authoritative
/// <see cref="SessionInfo.StatusColor"/> field: "red" means the session needs
/// the user (a pending question, an error, or a permission gate). We filter on
/// that field verbatim rather than re-deriving "needs you" from activity state,
/// which would risk disagreeing with what the desktop and the wingman show.
///
/// Sessions that are working (blue), idle/clean (green), or carrying only a soft
/// warning (yellow) are deliberately skipped: the locked decision is to stay
/// silent on sessions still working or idle, with no "still working" roll-call.
/// </summary>
public static class SessionFilter
{
    /// <summary>The status color that means "this session needs the user".</summary>
    public const string NeedsAttentionColor = "red";

    /// <summary>True when the conductor should speak for this session.</summary>
    public static bool NeedsAttention(SessionInfo s)
        => s is not null
           && string.Equals(s.StatusColor, NeedsAttentionColor, StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(s.TailnetEndpoint);

    /// <summary>
    /// The conductor queue: every session that needs the user, in a stable order
    /// (by display name, then id) so repeated polls keep the same rotation and a
    /// session does not jump around in the queue between refreshes.
    /// </summary>
    public static List<SessionInfo> AttentionQueue(IEnumerable<SessionInfo> roster)
    {
        if (roster is null) return new List<SessionInfo>();
        return roster
            .Where(NeedsAttention)
            .OrderBy(s => s.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.SessionId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
