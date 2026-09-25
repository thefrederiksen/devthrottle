namespace CcDirector.Gateway.Contracts;

/// <summary>
/// HOW LONG A DELIVERY ID IS HONOURED, stated once for the Gateway and the Director (Voice Delivery mission,
/// phase 1, review finding 3).
///
/// THE RULE: the Director's record of a delivery outlives every window in which the Gateway still lets that
/// recording be sent with its delivery id. The Director refuses a second copy only while it remembers the first;
/// if the Gateway could still attach a delivery id after the Director had forgotten it, the Director would answer
/// "unknown" and type the words a second time. So the two windows are not chosen separately - both come from
/// here, and the Director's is the Gateway's plus a margin.
///
/// Before this, the Director kept seven days and the Gateway kept its records thirty, so a "Send anyway" pressed
/// on a device that still held a shown-back recording after a week reached a Director that had forgotten it.
/// </summary>
public static class DeliveryRetention
{
    /// <summary>
    /// How long after a recording was RESOLVED - delivered, judged moved-on, or given up - the Gateway still
    /// turns a "Send anyway" claim for it into its delivery id. Past it, the claim is dropped (written to the
    /// recording's decision log and the Gateway log) and the words are typed as the owner asked, exactly as
    /// "Send anyway" did before delivery ids existed.
    ///
    /// THIRTY DAYS, because a shown-back recording lives in the owner's browser with no limit of its own (on
    /// 25 September 2026 two were five days old), and thirty days is the age the Gateway already kept a resolved
    /// recording's record for. Measured from the resolution, never from the acknowledgement, which can come days
    /// later and would stretch the window past the Director's.
    ///
    /// The Gateway's tombstone sweep keeps a resolved record at least this long, so a claim inside the window
    /// always finds its record.
    /// </summary>
    public static readonly TimeSpan ClaimWindow = TimeSpan.FromDays(30);

    /// <summary>
    /// How much longer than <see cref="ClaimWindow"/> the Director keeps an entry: one day, for the difference
    /// between the Gateway's clock and the Director's machine clock, and for the minutes between the Director
    /// typing the words and the Gateway writing the recording's resolution.
    /// </summary>
    public static readonly TimeSpan DirectorMargin = TimeSpan.FromDays(1);

    /// <summary>How long the Director keeps a delivery id's entry: <see cref="ClaimWindow"/> plus <see cref="DirectorMargin"/>.</summary>
    public static TimeSpan DirectorRetention => ClaimWindow + DirectorMargin;
}
