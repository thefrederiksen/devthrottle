using CcDirector.Core.Account;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Factory.Email;

/// <summary>What a factory email send came to.</summary>
/// <param name="Sent">True only when the owner-email service accepted and sent it.</param>
/// <param name="ProviderId">The provider's message id, when one came back.</param>
/// <param name="Error">Why it was not sent, in words; null when sent.</param>
/// <param name="Final">True when trying again cannot help (a team account); the slot is closed rather than retried.</param>
public sealed record FactoryEmailSendResult(bool Sent, string? ProviderId, string? Error, bool Final)
{
    public static FactoryEmailSendResult Ok(string? providerId) => new(true, providerId, null, false);
    public static FactoryEmailSendResult Failed(string error) => new(false, null, error, false);
    public static FactoryEmailSendResult NotSupported(string error) => new(false, null, error, true);
}

/// <summary>Sends one factory email to the account that set up the factories. The seam the sweep's tests replace.</summary>
public interface IFactoryEmailSender
{
    /// <summary>Send it. Never names an address: the owner-email service resolves the recipient itself.</summary>
    Task<FactoryEmailSendResult> SendAsync(TenantId tenant, string subject, string text, string html, CancellationToken ct);
}

/// <summary>
/// THE FACTORY EMAIL GOES TO THE ACCOUNT THAT SET UP THE FACTORY, NEVER A FIXED ADDRESS (the owner, 10 October 2026).
/// It is sent through the existing "email the account owner" primitive, which cannot address anyone: the Gateway names
/// an account, and the DevThrottle account service resolves its address.
///
///   - HOSTED: <see cref="AccountNotifyByTenantClient"/> (<c>POST /api/v1/account/notify-owner-by-tenant</c>). The
///     Gateway names the TENANT, with its account subject as a cross-check, authenticated by
///     <c>NOTIFY_OWNER_SERVICE_TOKEN</c>. The hosted Gateway holds no account token, by design, and does not start
///     holding one here.
///   - SELF-HOST: <see cref="AccountNotifyClient"/> (<c>POST /api/v1/account/notify-owner</c>) with the account token
///     this Gateway is signed in with - the same send <c>NetDiagAlertService</c> already makes on a timer.
///
/// A personal account's tenant IS the account that set up its factories, so the tenant's owner is exactly the right
/// person. A TEAM is a tenant too, but it has no account row of its own, and the member who set a factory up need not
/// be the team's owner - so a team account is told it is not supported yet, plainly, and no address is guessed.
/// </summary>
public sealed class FactoryEmailSender : IFactoryEmailSender
{
    /// <summary>What a team account is told.</summary>
    public const string TeamNotSupported =
        "This is a team account. The factory email goes to the account that set up the factory, and the Gateway "
        + "cannot address one member of a team yet, so no email was sent.";

    private readonly bool _hosted;
    private readonly Func<TenantId, bool> _isTeam;
    private readonly Func<TenantId, string?> _subjectOf;
    private readonly Func<string?> _serviceToken;
    private readonly Func<string?> _accountToken;
    private readonly AccountNotifyByTenantClient _byTenant;
    private readonly AccountNotifyClient _byToken;

    /// <param name="hosted">True on the hosted Gateway.</param>
    /// <param name="isTeam">Whether a tenant is a team.</param>
    /// <param name="subjectOf">A tenant's account subject (<c>TenantRegistry.SubjectForTenant</c>), sent as the cross-check.</param>
    /// <param name="serviceToken">The hosted service token (<see cref="AccountNotifyByTenantClient.ResolveServiceToken"/>).</param>
    /// <param name="accountToken">The self-host account token (<c>DevThrottleAccountService.GetAccessTokenForForwarding</c>).</param>
    /// <param name="byTenant">The hosted owner-email client.</param>
    /// <param name="byToken">The self-host owner-email client.</param>
    public FactoryEmailSender(bool hosted, Func<TenantId, bool> isTeam, Func<TenantId, string?> subjectOf,
        Func<string?> serviceToken, Func<string?> accountToken, AccountNotifyByTenantClient byTenant, AccountNotifyClient byToken)
    {
        _hosted = hosted;
        _isTeam = isTeam ?? throw new ArgumentNullException(nameof(isTeam));
        _subjectOf = subjectOf ?? throw new ArgumentNullException(nameof(subjectOf));
        _serviceToken = serviceToken ?? throw new ArgumentNullException(nameof(serviceToken));
        _accountToken = accountToken ?? throw new ArgumentNullException(nameof(accountToken));
        _byTenant = byTenant ?? throw new ArgumentNullException(nameof(byTenant));
        _byToken = byToken ?? throw new ArgumentNullException(nameof(byToken));
    }

    /// <inheritdoc />
    public async Task<FactoryEmailSendResult> SendAsync(TenantId tenant, string subject, string text, string html, CancellationToken ct)
    {
        FileLog.Write($"[FactoryEmailSender] SendAsync: tenant={tenant.ToLogString()}, hosted={_hosted}");
        if (_isTeam(tenant))
        {
            FileLog.Write($"[FactoryEmailSender] SendAsync: tenant={tenant.ToLogString()} is a team - not supported, nothing sent");
            return FactoryEmailSendResult.NotSupported(TeamNotSupported);
        }

        if (_hosted)
        {
            var serviceToken = _serviceToken();
            if (serviceToken is null)
                return Failed($"This hosted Gateway has no {AccountNotifyByTenantClient.ServiceTokenEnvVar} setting, so it cannot "
                    + "send owner email. It is a Gateway configuration step. No email was sent.");
            var accountSubject = _subjectOf(tenant);
            if (accountSubject is null)
                return Failed("No account is recorded for this tenant, so there is nobody to send the factory email to. No email was sent.");

            var r = await _byTenant.SendOwnerForTenantAsync(serviceToken, tenant.Value, accountSubject, subject, text, html, null, ct)
                .ConfigureAwait(false);
            if (r.Sent)
            {
                FileLog.Write($"[FactoryEmailSender] SendAsync: sent (hosted), tenant={tenant.ToLogString()}");
                return FactoryEmailSendResult.Ok(r.ProviderId);
            }
            return Failed($"The owner-email service did not send it (status {r.StatusCode}, {r.ErrorCode ?? "no code"}): {r.Error}");
        }

        var token = _accountToken();
        if (string.IsNullOrWhiteSpace(token))
            return Failed("This Gateway is not signed in to DevThrottle, so it cannot send owner email. Sign in from the "
                + "Gateway tray. No email was sent.");
        var result = await _byToken.SendOwnerAsync(token, subject, text, html, null, ct).ConfigureAwait(false);
        if (result.Sent)
        {
            FileLog.Write("[FactoryEmailSender] SendAsync: sent (self-host)");
            return FactoryEmailSendResult.Ok(result.ProviderId);
        }
        return Failed($"The owner-email service did not send it (status {result.StatusCode}): {result.Error}");
    }

    private static FactoryEmailSendResult Failed(string error)
    {
        FileLog.Write($"[FactoryEmailSender] SendAsync FAILED: {error}");
        return FactoryEmailSendResult.Failed(error);
    }
}
