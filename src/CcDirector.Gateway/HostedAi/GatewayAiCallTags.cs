using CcDirector.Core.Account;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Tenancy;

namespace CcDirector.Gateway.HostedAi;

/// <summary>
/// The one place the Gateway decides which account a hosted AI call is recorded under.
///
/// The Gateway calls the DevThrottle API with ONE key for every tenant it serves, so without this every
/// account's AI usage was recorded under the key's owner. A real account tenant is now named on every call,
/// proven with the Gateway's service credential (the same one the tenant-addressed owner email presents).
///
/// The self-host single tenant (<see cref="TenantId.Local"/>) and the reserved <see cref="TenantId.System"/>
/// name no account: a self-hosted Gateway calls with its owner's own key, which IS the right account.
///
/// A hosted Gateway serving a real tenant WITHOUT the credential fails loudly here rather than quietly
/// recording the call under the key's owner - that silent misattribution is the defect this exists to end.
/// </summary>
public static class GatewayAiCallTags
{
    /// <summary>The tag for one call made on behalf of <paramref name="tenant"/>.</summary>
    public static AiCallTag For(TenantId tenant, string feature)
    {
        if (!tenant.IsValid || tenant.IsLocal || tenant.IsSystem)
            return new AiCallTag(feature);

        var token = AccountNotifyByTenantClient.ResolveServiceToken();
        if (token is null)
            throw new InvalidOperationException(
                $"This Gateway serves account tenants but has no {AccountNotifyByTenantClient.ServiceTokenEnvVar} setting, " +
                "so it cannot record which account an AI call is for. Set it to the same secret as the website's " +
                "GATEWAY_SERVICE_TOKEN.");
        return new AiCallTag(feature, tenant.Value, token);
    }

    /// <summary>The tag for a call with an optional tenant: null means the self-host single tenant.</summary>
    public static AiCallTag For(TenantId? tenant, string feature)
        => tenant is { } t ? For(t, feature) : new AiCallTag(feature);
}
