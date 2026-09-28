namespace CcDirector.Gateway.History;

/// <summary>
/// The answer to "which factory does this session belong to", read from the session's history row
/// (Factory Memory mission, phase 1). Three states, and keeping them apart is the point (review finding 4):
/// a session the Gateway has no row for yet is NOT a session in no factory, and treating the two the same
/// is how a child spawned in the seconds after a Gateway restart would be copied an empty factory and stay
/// outside its factory for the rest of its life.
/// </summary>
public readonly record struct SessionFactoryLookup
{
    private SessionFactoryLookup(bool known, string? factory)
    {
        IsKnown = known;
        Factory = factory;
    }

    /// <summary>False when the Gateway holds no history row for the session yet - it is younger than its
    /// first push. Nothing can be concluded about its membership, in either direction.</summary>
    public bool IsKnown { get; }

    /// <summary>The factory id when the session is in one; null when the row says it is in none.</summary>
    public string? Factory { get; }

    /// <summary>True when the session is known AND in a factory.</summary>
    public bool IsInAFactory => IsKnown && !string.IsNullOrWhiteSpace(Factory);

    /// <summary>No row for this session yet: "try again in a moment", never "you are in no factory".</summary>
    public static readonly SessionFactoryLookup NotKnown = new(false, null);

    /// <summary>A row exists and says the session belongs to no factory. A settled answer.</summary>
    public static readonly SessionFactoryLookup InNoFactory = new(true, null);

    /// <summary>A row exists and names the factory.</summary>
    public static SessionFactoryLookup In(string factory) => new(true, factory);
}
