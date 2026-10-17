using System.Text.Json;
using System.Text.Json.Nodes;

namespace CcDirector.Core.Configuration;

/// <summary>
/// License Switching phase 6 (Track C): whether this machine offers its browser profiles as approvers. Stored in
/// config.json beside the setting that turns license switching on, under "licenseSwitching" -&gt; "offerApprovers".
/// OFF by default, and only an explicit <c>true</c> turns it on.
///
/// On, the Director's daily approver check also looks at every one of this Director's own browser profiles - asking
/// each which account it is signed in to claude.ai as - and the Gateway may register a profile proven able to approve
/// as that account's approver. Off, the check still looks at the profiles the Gateway's approver records already name
/// for this machine, and registers nothing new. Either way it does nothing while license switching is off. It is read
/// when the Director starts, so changing it takes a restart, as <see cref="LicenseSwitchingSetting"/> does.
/// </summary>
public static class LicenseApproverOfferSetting
{
    /// <summary>Default posture: off. A missing file, section, key or a value that is not a boolean is off.</summary>
    public const bool Default = false;

    /// <summary>The effective value of <c>licenseSwitching.offerApprovers</c> in config.json.</summary>
    public static bool Get() => ReadFrom(CcDirectorConfigService.ReadRaw());

    /// <summary>The value from an already-loaded config root; the same rules as <see cref="Get"/>.</summary>
    public static bool ReadFrom(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root["licenseSwitching"] is not JsonObject section) return Default;
        return section["offerApprovers"] is JsonValue value && value.GetValueKind() == JsonValueKind.True;
    }

    /// <summary>Persists <paramref name="offer"/>, deep-merging into config.json so every other section - and the
    /// license switching setting beside it - is kept.</summary>
    public static void Set(bool offer) => CcDirectorConfigService.MergePatch(BuildPatch(offer));

    /// <summary>The partial patch that sets <c>licenseSwitching.offerApprovers</c>.</summary>
    public static JsonObject BuildPatch(bool offer) => new()
    {
        ["licenseSwitching"] = new JsonObject { ["offerApprovers"] = offer },
    };
}
