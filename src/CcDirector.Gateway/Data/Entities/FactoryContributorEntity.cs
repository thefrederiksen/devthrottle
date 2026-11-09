namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE FACTORY SENT BY NAME TO ONE CONTRIBUTOR OF ITS TEAM (<c>factory_contributors</c>, Team implementation piece 4.1,
/// devthrottle_internal#2798). A contributor sees nothing until it is shared with them, item by item (design.html, "What a
/// contributor sees"): this row is the share of a whole factory, and it is what puts that factory on the contributor's
/// Factories page as a customer page. Without a row the contributor is not told the factory exists.
///
/// The team IS a tenant, so <c>tenant_id</c> is the team id; the factory is a registered factory of that tenant
/// (<see cref="FactoryRegistryEntity"/>). Subjects only - never an email, a name or a machine. Written only through
/// <see cref="Teams.FactoryContributors"/>; read only through <see cref="Teams.ContributorFactoryAccess"/>, the one check
/// every customer-page read asks. Personally identifying (the subjects): never logged.
/// </summary>
public sealed class FactoryContributorEntity : GatewayMintedKeyEntity
{
    /// <summary>The registered factory sent, in the one spelling of a factory id.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The account subject of the contributor it was sent to.</summary>
    public string ContributorSubject { get; set; } = "";

    /// <summary>The account subject of the member who sent it - the team's Owner in step one.</summary>
    public string SharedBySubject { get; set; } = "";

    /// <summary>When it was sent (UTC), from the injected clock.</summary>
    public DateTime SharedAtUtc { get; set; }
}
