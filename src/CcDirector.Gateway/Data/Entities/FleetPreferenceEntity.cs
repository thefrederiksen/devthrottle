namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// One standing preference the owner gave the Fleet Manager - "stop asking me about draft posts, just stage
/// them" - kept for the account so a restarted or moved Fleet Manager still knows it (the Fleet Manager
/// mission, step 3). The text is the owner's words, stored exactly as given.
///
/// A row is one of two kinds (issue #3559): a standing PREFERENCE - how the owner wants things done - or a LESSON -
/// the owner's correction of a mistake the Fleet Manager made, so no later Fleet Manager makes it again. The two are
/// kept apart everywhere they are read; only a lesson carries <see cref="Mistake"/> and
/// <see cref="ConfirmedByOwnerAtUtc"/>.
/// </summary>
public sealed class FleetPreferenceEntity : GatewayMintedKeyEntity
{
    /// <summary>The owner's words, verbatim.</summary>
    public string Text { get; set; } = "";

    /// <summary>When it was kept (UTC).</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The session id that kept it, or <c>owner</c> when a person's device did.</summary>
    public string CreatedBy { get; set; } = "";

    /// <summary><c>preference</c> or <c>lesson</c> (<see cref="Fleet.FleetPreferenceStore.KindPreference"/>,
    /// <see cref="Fleet.FleetPreferenceStore.KindLesson"/>). Every row written before lessons existed is a
    /// preference.</summary>
    public string Kind { get; set; } = "preference";

    /// <summary>On a lesson: one line saying what went wrong, or null. Always null on a preference.</summary>
    public string? Mistake { get; set; }

    /// <summary>On a lesson: when the owner kept it or confirmed it, or null while it waits for the owner. Only a
    /// confirmed lesson is given to a Fleet Manager as one it must obey. Always null on a preference.</summary>
    public DateTime? ConfirmedByOwnerAtUtc { get; set; }
}
