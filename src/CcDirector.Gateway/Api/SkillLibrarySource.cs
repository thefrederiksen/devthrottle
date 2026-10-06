using System.Text.Json.Serialization;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Which library a skills register answer is (devthrottle_internal#2311, review findings SK-F2 and SK-F3): the
/// stable id of THIS Gateway, the tenant the caller's key is bound to, and the team when that tenant is a team.
/// Never the address the caller used - an address changes, these do not.
/// </summary>
/// <param name="GatewayId">This Gateway's own id, created once per database (<c>GatewayInstanceIdentity</c>).</param>
/// <param name="TenantId">The tenant the caller's key is bound to - the person's own, or a team's.</param>
/// <param name="TeamId">The team when <paramref name="TenantId"/> is a team (a team's id IS its tenant id); null for
/// the person's own account.</param>
public sealed record SkillLibrarySource(
    [property: JsonPropertyName("gatewayId")] string GatewayId,
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("teamId")] string? TeamId);
