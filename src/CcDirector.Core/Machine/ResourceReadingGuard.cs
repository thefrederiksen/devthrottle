namespace CcDirector.Core.Machine;

/// <summary>
/// The one check every <c>Unreadable</c> factory makes: the reason is the whole point of an unreadable
/// reading, so an empty one is refused at construction rather than shown to the owner as "cannot read ()".
/// </summary>
internal static class ResourceReadingGuard
{
    /// <summary>Returns the reason unchanged, or throws when it is missing or blank.</summary>
    public static string Reason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("An unreadable reading must say why, in plain English.", nameof(reason));
        return reason;
    }
}
