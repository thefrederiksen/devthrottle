using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>
/// WHICH library a skill on disk came from: the Gateway that served it, by that Gateway's own stable id, and the
/// tenant on it - the person's own personal account, or one team (devthrottle_internal#2311).
///
/// WHY THE INSTALLED SKILL HAS TO SAY THIS. The skill folders are per USER, not per Director
/// (<see cref="SkillInstallTargets.For"/>): every Director on the computer writes into the same
/// <c>~/.agents/skills</c> and <c>~/.claude/skills</c>. The marker used to say only "DevThrottle installed
/// this", so a Director reconciling its own library deleted every marked folder its library did not hold -
/// including the ones another Director had put there (#2311 live proof, finding F7).
///
/// WHY THE GATEWAY'S ID AND THE TENANT, AND NOT THE DIRECTOR OR THE ADDRESS. One person's two Directors on the
/// same account are served the same library and must SHARE their folders - a Director id would make them fight.
/// The tenant alone is not enough: every self-hosted Gateway's tenant is the same constant, so two self-hosted
/// Gateways would look like one library. And the address is not an identity at all: a move to TLS, a new domain
/// or a changed address is the same Gateway (review finding SK-F3). So the identity is the Gateway's stable id
/// plus the tenant, both as the GATEWAY stated them for this Director's key.
///
/// WHERE IT COMES FROM. The skills register answers it alongside the list, for the key that fetched the list,
/// and <see cref="SkillStoreRefresh"/> records it WITH the store it materialized (<see cref="RecordFileName"/>).
/// So the source stamped on disk is the identity that fetched the library - never a separate local record that
/// can be missing (review finding SK-F2). See <see cref="Establish"/> for when placement refuses.
/// </summary>
/// <param name="GatewayId">The serving Gateway's stable id.</param>
/// <param name="TenantId">The tenant the library belongs to.</param>
/// <param name="TeamId">The team, when the tenant is a team; null for the person's own personal account.</param>
public sealed record SkillSource(
    [property: JsonPropertyName("gatewayId")] string GatewayId,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("teamId")] string? TeamId)
{
    /// <summary>The file beside the store's skills that records which library the store holds.</summary>
    public const string RecordFileName = ".library-source.json";

    /// <summary>The marker line that names the Gateway.</summary>
    public const string GatewayIdKey = "gateway-id=";

    /// <summary>The marker line that names the tenant.</summary>
    public const string TenantKey = "tenant=";

    /// <summary>The marker line that names the kind of account: <c>personal</c> or <c>team:&lt;id&gt;</c>.</summary>
    public const string AccountKey = "account=";

    /// <summary>Marker lines written by earlier forms of the source stamp, removed when a folder is re-stamped.</summary>
    internal static readonly string[] RetiredKeys = { "gateway=" };

    private const string PersonalAccount = "personal";
    private const string TeamPrefix = "team:";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>True when this is the person's own personal account, which wins every name clash.</summary>
    [JsonIgnore]
    public bool IsPersonal => TeamId is null;

    /// <summary>The marker lines this source appends to what it installs.</summary>
    public string MarkerLines() =>
        $"{GatewayIdKey}{GatewayId}\n{TenantKey}{TenantId}\n{AccountKey}{(IsPersonal ? PersonalAccount : TeamPrefix + TeamId)}\n";

    /// <summary>One line for a log or a placement message.</summary>
    public string Describe() =>
        $"{(IsPersonal ? "the personal account" : "team " + TeamId)} (tenant {TenantId}) on Gateway {GatewayId}";

    /// <summary>True when <paramref name="stamp"/> was written by this source.</summary>
    public bool Wrote(SkillSourceStamp stamp) =>
        string.Equals(stamp.GatewayId, GatewayId, StringComparison.Ordinal)
        && string.Equals(stamp.TenantId, TenantId, StringComparison.Ordinal);

    /// <summary>Record which library the store at <paramref name="storeRoot"/> holds.</summary>
    public void WriteTo(string storeRoot)
    {
        Directory.CreateDirectory(storeRoot);
        var path = Path.Combine(storeRoot, RecordFileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Forget which library the store holds - the Gateway did not say.</summary>
    public static void ClearAt(string storeRoot)
    {
        var path = Path.Combine(storeRoot, RecordFileName);
        if (File.Exists(path))
            File.Delete(path);
    }

    /// <summary>
    /// The source placement may install as, or why there is none. It is the record the store refresh wrote from
    /// the Gateway's own answer, and it must AGREE with the team this Director is set up for
    /// (<paramref name="configuredTeam"/>; null - no team file - means the person's own account).
    ///
    /// FAIL CLOSED. No record means the Gateway did not say whose library this is (a Gateway older than this
    /// rule), and disagreement means the library was fetched for one account while this Director believes it
    /// works for another - the partial-enrollment state where the team key was saved and the team file was not
    /// (review finding SK-F2). Either way placement must not guess an owner: it changes nothing.
    /// </summary>
    public static SkillSourceEstablished Establish(string storeRoot, DirectorTeam? configuredTeam)
    {
        var path = Path.Combine(storeRoot, RecordFileName);
        if (!File.Exists(path))
            return SkillSourceEstablished.Refused(SkillPlacementFault.SourceUnknown,
                $"no library source is recorded at {path} - the Gateway did not say which account these skills belong to");

        var recorded = JsonSerializer.Deserialize<SkillSource>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"The library source record {path} is empty.");
        if (string.IsNullOrWhiteSpace(recorded.GatewayId) || string.IsNullOrWhiteSpace(recorded.TenantId))
            throw new InvalidDataException($"The library source record {path} names no Gateway or no tenant.");

        var configuredTeamId = configuredTeam?.TeamId;
        if (!string.Equals(recorded.TeamId, configuredTeamId, StringComparison.Ordinal))
            return SkillSourceEstablished.Refused(SkillPlacementFault.SourceMismatch,
                $"the skills were fetched for {recorded.Describe()}, but this Director is set up for " +
                $"{(configuredTeamId is null ? "the personal account" : "team " + configuredTeamId)}");

        return SkillSourceEstablished.As(recorded);
    }

    /// <summary>
    /// Read who installed a marked folder from its marker's lines. Null when the marker carries no source in this
    /// form - one written before sources were recorded, or by an earlier form that named the Gateway by address -
    /// which <see cref="SkillDirectoryInstaller"/> treats as the personal account's.
    /// </summary>
    public static SkillSourceStamp? ReadStamp(IReadOnlyList<string> markerLines)
    {
        string? gatewayId = null, tenant = null, account = null;
        foreach (var raw in markerLines)
        {
            var line = raw.Trim();
            if (line.StartsWith(GatewayIdKey, StringComparison.Ordinal))
                gatewayId = line[GatewayIdKey.Length..];
            else if (line.StartsWith(TenantKey, StringComparison.Ordinal))
                tenant = line[TenantKey.Length..];
            else if (line.StartsWith(AccountKey, StringComparison.Ordinal))
                account = line[AccountKey.Length..];
        }
        if (string.IsNullOrEmpty(gatewayId) || string.IsNullOrEmpty(tenant) || account is null)
            return null;
        // Anything but "personal" is a team. An account spelled some way this code does not know is therefore
        // never mistaken for the person's own - so it is only ever kept, never taken over by a team.
        return new SkillSourceStamp(gatewayId, tenant, account == PersonalAccount
            ? null
            : account.StartsWith(TeamPrefix, StringComparison.Ordinal) ? account[TeamPrefix.Length..] : account);
    }
}

/// <summary>The source recorded on one installed skill folder.</summary>
public sealed record SkillSourceStamp(string GatewayId, string TenantId, string? TeamId)
{
    /// <summary>True when the personal account installed it.</summary>
    public bool IsPersonal => TeamId is null;

    /// <summary>One line for a log or a placement message.</summary>
    public string Describe() =>
        $"{(IsPersonal ? "the personal account" : "team " + TeamId)} (tenant {TenantId}) on Gateway {GatewayId}";
}

/// <summary>The outcome of <see cref="SkillSource.Establish"/>: a source to install as, or why there is none.</summary>
public sealed record SkillSourceEstablished(SkillSource? Source, SkillPlacementFault? Refusal, string Reason)
{
    public static SkillSourceEstablished As(SkillSource source) => new(source, null, "");

    public static SkillSourceEstablished Refused(SkillPlacementFault why, string reason) => new(null, why, reason);
}
