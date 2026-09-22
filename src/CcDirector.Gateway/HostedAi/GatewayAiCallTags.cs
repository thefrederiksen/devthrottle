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

        var token = UsableServiceToken();
        if (token is null)
            throw new InvalidOperationException(MissingCredentialMessage);
        return new AiCallTag(feature, tenant.Value, token);
    }

    /// <summary>The website refuses a service credential shorter than this (its MIN_SERVICE_TOKEN_LENGTH), so a
    /// shorter one - a placeholder left during a rotation - is treated here exactly as a missing one.</summary>
    public const int MinServiceTokenLength = 32;

    private static string? UsableServiceToken()
    {
        var token = AccountNotifyByTenantClient.ResolveServiceToken()?.Trim();
        return token is { Length: >= MinServiceTokenLength } ? token : null;
    }

    /// <summary>The one message for a hosted Gateway without the credential: thrown per call, logged at startup.</summary>
    public static string MissingCredentialMessage =>
        $"This Gateway serves account tenants but its {AccountNotifyByTenantClient.ServiceTokenEnvVar} setting is " +
        $"missing or shorter than {MinServiceTokenLength} characters, so it cannot record which account an AI call is " +
        "for and makes no AI call for an account. Set it to the same secret as the website's GATEWAY_SERVICE_TOKEN.";

    /// <summary>Whether AI calls can be made for account tenants: always on a self-hosted Gateway (it names no
    /// account), and on a hosted one only with the service credential. /healthz reports it as a subsystem, so
    /// the deploy fails on a Gateway that lost the setting instead of every AI feature failing call by call.</summary>
    public static bool AttributionReady(bool hosted)
        => !hosted || UsableServiceToken() is not null;

    /// <summary>The tag for a call with an optional tenant: null means the self-host single tenant.</summary>
    public static AiCallTag For(TenantId? tenant, string feature)
        => tenant is { } t ? For(t, feature) : new AiCallTag(feature);
}
