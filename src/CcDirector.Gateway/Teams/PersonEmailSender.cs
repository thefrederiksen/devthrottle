using System.Net.Http;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams;

/// <summary>What became of one email to a person.</summary>
/// <param name="Sent">True only when the DevThrottle email service confirmed the send.</param>
/// <param name="Detail">One plain sentence saying why it was not sent; null when it was.</param>
/// <param name="RetryAfter">How long the email service asked this Gateway to wait, when it said.</param>
/// <param name="Permanent">True when trying again cannot help (the person has no account on this Gateway): the caller
/// stops trying.</param>
public sealed record PersonEmailResult(bool Sent, string? Detail, TimeSpan? RetryAfter = null, bool Permanent = false);

/// <summary>Sends one finished email to a person named by their account subject. The address is never the caller's to
/// give: the DevThrottle email service reads it from the person's own account.</summary>
public interface IPersonEmailSender
{
    /// <summary>Send <paramref name="email"/> to the person whose account subject is <paramref name="accountSubject"/>.
    /// Never throws for a refusal or an unreachable service; the result says the email was not sent, and why.</summary>
    Task<PersonEmailResult> SendAsync(string accountSubject, PersonEmail email, CancellationToken ct = default);
}

/// <summary>
/// THE ONE WAY THE GATEWAY EMAILS A PERSON BY ACCOUNT (Team implementation piece 4.4; reused by every later email to one
/// person, such as a factory starting or finishing). It is NOT a second sender: it is the existing tenant-addressed
/// owner email (<see cref="AccountNotifyByTenantClient"/>, <c>POST /api/v1/account/notify-owner-by-tenant</c>) pointed at
/// the person's OWN tenant - the one their phone or browser key is bound to. So the website resolves the address from the
/// person's own account exactly as it does for every owner email, the Gateway never holds or names an address, and the
/// same <c>NOTIFY_OWNER_SERVICE_TOKEN</c> that already sends owner email is the only credential.
///
/// Fails closed: with no service credential it calls nothing and says so; with a person who has no tenant on this
/// Gateway there is nobody to address, and that is permanent.
/// </summary>
public sealed class PersonEmailSender : IPersonEmailSender
{
    internal const string NotSetUp = "This DevThrottle Gateway is not set up to send email.";
    internal const string Unreachable = "The DevThrottle email service could not be reached.";
    internal const string NoAccount = "The person has no account on this DevThrottle Gateway, so there is nobody to email.";

    private readonly AccountNotifyByTenantClient _client;
    private readonly TenantRegistry _tenants;
    private readonly Func<string?> _serviceToken;

    /// <param name="client">The tenant-addressed owner email client.</param>
    /// <param name="tenants">Finds the person's own tenant from their account subject.</param>
    /// <param name="serviceToken">Resolves the Gateway service secret; the owner-email setting when omitted.</param>
    public PersonEmailSender(AccountNotifyByTenantClient client, TenantRegistry tenants, Func<string?>? serviceToken = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
        _serviceToken = serviceToken ?? AccountNotifyByTenantClient.ResolveServiceToken;
    }

    public async Task<PersonEmailResult> SendAsync(string accountSubject, PersonEmail email, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountSubject);
        ArgumentNullException.ThrowIfNull(email);

        var token = _serviceToken();
        if (token is null)
        {
            FileLog.Write($"[PersonEmailSender] SendAsync: {AccountNotifyByTenantClient.ServiceTokenEnvVar} is not set on this Gateway - the email was NOT sent");
            return new PersonEmailResult(false, NotSetUp);
        }

        if (_tenants.LookupBySubject(accountSubject) is not { } own)
        {
            FileLog.Write("[PersonEmailSender] SendAsync: the person has no tenant on this Gateway - the email was NOT sent");
            return new PersonEmailResult(false, NoAccount, Permanent: true);
        }

        try
        {
            var result = await _client.SendOwnerForTenantAsync(token, own.Value, accountSubject, email.Subject, email.Text, email.Html,
                null, ct).ConfigureAwait(false);
            if (result.Sent)
            {
                FileLog.Write($"[PersonEmailSender] SendAsync: tenant {own.ToLogString()} - sent");
                return new PersonEmailResult(true, null);
            }
            FileLog.Write($"[PersonEmailSender] SendAsync: tenant {own.ToLogString()} - NOT sent (status {result.StatusCode}, code {result.ErrorCode ?? "<none>"})");
            return new PersonEmailResult(false, result.Error,
                result.RetryAfterSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            FileLog.Write($"[PersonEmailSender] SendAsync: the email service could not be reached ({ex.GetType().Name}) - the email was NOT sent");
            return new PersonEmailResult(false, Unreachable);
        }
    }
}
