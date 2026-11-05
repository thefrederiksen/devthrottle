namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>
/// THE SECRETARY'S MARK (Factory Inbox mission, phase 1). A Secretary is the product-given seat every factory with a Boss
/// has: nobody defines it. It is recognised by its recorded session history alone - the row's factory, the seat
/// <see cref="Secretary"/>, and a seat it was started as rather than inherited - never by anything a request says. The
/// Gateway's Secretary seat start (phase 2) records exactly this, and the per-session limit at every start door (phase 2)
/// keys on the same mark.
/// </summary>
public static class FactoryInboxSeats
{
    /// <summary>The seat name a Secretary session is recorded with.</summary>
    public const string Secretary = "secretary";
}
