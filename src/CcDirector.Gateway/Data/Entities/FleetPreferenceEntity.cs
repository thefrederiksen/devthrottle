namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One standing preference the owner gave the Fleet Manager - "stop asking me about draft posts, just stage
/// them" - kept for the account so a restarted or moved Fleet Manager still knows it (the Fleet Manager
/// mission, step 3). The text is the owner's words, stored exactly as given.
/// </summary>
public sealed class FleetPreferenceEntity : GatewayMintedKeyEntity
{
    /// <summary>The owner's words, verbatim.</summary>
    public string Text { get; set; } = "";

    /// <summary>When it was kept (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The session id that kept it, or <c>owner</c> when a person's device did.</summary>
    public string CreatedBy { get; set; } = "";
}
