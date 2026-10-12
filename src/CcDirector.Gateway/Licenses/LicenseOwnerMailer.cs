using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Licenses;

/// <summary>What became of one attempt to email the owner.</summary>
public enum LicenseOwnerEmailOutcome
{
    /// <summary>The mail service confirmed it sent the email.</summary>
    Sent,

    /// <summary>Not sent this time; it may be tried again on a later minute check.</summary>
    Failed,

    /// <summary>This Gateway has no DevThrottle account to email (a self-hosted Gateway). Trying again cannot help.</summary>
    NoAccount,
}

/// <summary>The result of one attempt to email the owner.</summary>
/// <param name="Outcome">Sent, failed (try again later), or no account to email.</param>
/// <param name="Detail">One plain sentence saying why it was not sent; null when it was.</param>
/// <param name="RetryAfter">How long the mail service asked this Gateway to wait before trying again, when it said.</param>
public sealed record LicenseOwnerEmailResult(LicenseOwnerEmailOutcome Outcome, string? Detail, TimeSpan? RetryAfter = null);

/// <summary>Sends one owner email for one tenant. The recipient is never named: the mail service resolves the tenant's owner.</summary>
public delegate Task<LicenseOwnerEmailResult> LicenseOwnerEmailSender(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct);

/// <summary>
/// THE OWNER EMAIL for License Switching - "no account has room" - sent the way <c>AccountEmailEndpoint</c> sends on the
/// hosted Gateway: <see cref="AccountNotifyByTenantClient.SendOwnerForTenantAsync"/> names the TENANT and the
/// DevThrottle account service resolves the owner's address. This Gateway never holds or names an address, and the
/// minute check that calls this holds no account access token for any tenant, which is why the token-based
/// <c>AccountNotifyClient</c> cannot be used here.
///
/// A SELF-HOSTED GATEWAY HAS NO TENANT ACCOUNT. Its one tenant is <see cref="TenantId.Local"/>, which the account
/// service knows nothing about, so there is nobody for the by-tenant sender to resolve. That is reported as
/// <see cref="LicenseOwnerEmailOutcome.NoAccount"/>, logged, and nothing is sent.
///
/// A hosted Gateway whose <see cref="AccountNotifyByTenantClient.ServiceTokenEnvVar"/> setting is missing does not call
/// the service unauthenticated (the same rule <c>AccountEmailEndpoint</c> keeps, so that a 401 from the service can only
/// ever mean the secret is WRONG): it reports <see cref="LicenseOwnerEmailOutcome.Failed"/>, and the email goes out on
/// the first minute check after the setting is fixed.
/// </summary>
public sealed class LicenseOwnerMailer
{
    private readonly bool _hosted;
    private readonly AccountNotifyByTenantClient _client;
    private readonly Func<TenantId, string?> _accountSubjectOf;
    private readonly Func<string?> _serviceToken;

    /// <param name="hosted">True on the hosted Gateway, where every tenant is a DevThrottle account.</param>
    /// <param name="client">The tenant-addressed sender.</param>
    /// <param name="accountSubjectOf">The account subject recorded on the tenant (<c>TenantRegistry.SubjectForTenant</c>),
    /// sent as the service's cross-check so a wrong tenant id is refused rather than delivered to somebody else.</param>
    /// <param name="serviceToken">The Gateway's service secret, or null when it is not set
    /// (<see cref="AccountNotifyByTenantClient.ResolveServiceToken"/>).</param>
    public LicenseOwnerMailer(bool hosted, AccountNotifyByTenantClient client, Func<TenantId, string?> accountSubjectOf,
        Func<string?> serviceToken)
    {
        _hosted = hosted;
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _accountSubjectOf = accountSubjectOf ?? throw new ArgumentNullException(nameof(accountSubjectOf));
        _serviceToken = serviceToken ?? throw new ArgumentNullException(nameof(serviceToken));
    }

    /// <summary>Send <paramref name="email"/> to the owner of <paramref name="tenant"/>. Never throws for a send that
    /// did not happen: an unreachable service is <see cref="LicenseOwnerEmailOutcome.Failed"/>, to be tried again.</summary>
    public async Task<LicenseOwnerEmailResult> SendAsync(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);
        FileLog.Write($"[LicenseOwnerMailer] SendAsync: tenant={tenant.ToLogString()}, hosted={_hosted}");
        if (!_hosted)
        {
            FileLog.Write("[LicenseOwnerMailer] SendAsync: this self-hosted Gateway has no DevThrottle account to email - NOT sent");
            return new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.NoAccount,
                "this self-hosted Gateway has no DevThrottle account to email");
        }
        var serviceToken = _serviceToken();
        if (serviceToken is null)
        {
            FileLog.Write($"[LicenseOwnerMailer] SendAsync: {AccountNotifyByTenantClient.ServiceTokenEnvVar} is not set on this Gateway - NOT sent");
            return new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed,
                $"this Gateway's {AccountNotifyByTenantClient.ServiceTokenEnvVar} setting is missing");
        }

        TenantNotifyResult result;
        try
        {
            // The one place a transport failure is caught: the service being unreachable is an expected outcome of a
            // send, reported as "not sent, try again", never as a crash of the minute check.
            result = await _client.SendOwnerForTenantAsync(serviceToken, tenant.Value, _accountSubjectOf(tenant),
                email.Subject, email.BodyText, null, null, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            FileLog.Write($"[LicenseOwnerMailer] SendAsync FAILED: the account service could not be reached: {ex.Message}");
            return new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service could not be reached");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            FileLog.Write("[LicenseOwnerMailer] SendAsync FAILED: the account service did not answer in time");
            return new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed, "the DevThrottle account service did not answer in time");
        }

        if (result.Sent)
        {
            FileLog.Write("[LicenseOwnerMailer] SendAsync: sent to the tenant's owner");
            return new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null);
        }
        FileLog.Write($"[LicenseOwnerMailer] SendAsync: NOT sent (service status {result.StatusCode}, code {result.ErrorCode ?? "<none>"})");
        return new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Failed,
            result.Error ?? $"the DevThrottle account service returned {result.StatusCode}",
            result.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
    }
}
