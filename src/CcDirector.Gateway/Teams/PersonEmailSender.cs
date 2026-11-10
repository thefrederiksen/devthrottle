using System.Net.Http;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.Teams;

/// <summary>What became of one email to a person.</summary>
/// <param name="Sent">True only when the DevThrottle email service confirmed the send.</param>
/// <param name="Detail">One plain sentence saying why it was not sent; null when it was.</param>
/// <param name="RetryAfter">How long the email service asked this Gateway to wait, when it said.</param>
/// <param name="Permanent">True when trying again cannot help - the person has no account on this Gateway, or the email
/// service refused the email with an answer from 400 to 499 other than 429: the caller stops trying.</param>
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
/// Gateway there is nobody to address, and that is permanent. The email service's answers from 400 to 499 other than 429
/// (too many requests) are permanent too: the same request would be refused the same way, so trying again only repeats
/// the refusal. 429, every answer from 500 up and an unreachable service are tried again by the caller.
/// </summary>
public sealed class PersonEmailSender : IPersonEmailSender
{
    internal const string NotSetUp = "This DevThrottle Gateway is not set up to send email.";
    internal const string Unreachable = "The DevThrottle email service could not be reached.";
    internal const string NoAccount = "The person has no account on this DevThrottle Gateway, so there is nobody to email.";

    /// <summary>The sentence for a refusal trying again cannot change. It names the status only, never the service's own
    /// text, so nothing the service says about the person reaches the log.</summary>
    internal static string Refused(int status) =>
        $"The DevThrottle email service refused the email (status {status}), and trying again would be refused the same way.";

    /// <summary>True for an answer from 400 to 499 other than 429.</summary>
    internal static bool IsPermanentRefusal(int status) => status is >= 400 and < 500 and not 429;

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
            if (IsPermanentRefusal(result.StatusCode))
            {
                FileLog.Write($"[PersonEmailSender] SendAsync: tenant {own.ToLogString()} - NOT sent, refused for good (status {result.StatusCode}, code {result.ErrorCode ?? "<none>"})");
                return new PersonEmailResult(false, Refused(result.StatusCode), Permanent: true);
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
