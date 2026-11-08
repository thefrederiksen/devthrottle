using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Inbox;

namespace CcDirector.Gateway.Licenses;

/// <summary>What became of one attempt to tell the owner (the names say email: they are older than Your inbox).</summary>
public enum LicenseOwnerEmailOutcome
{
    /// <summary>The message is in the owner's Your inbox.</summary>
    Sent,

    /// <summary>Not written this time; it may be tried again on a later minute check.</summary>
    Failed,
}

/// <summary>The result of one attempt to tell the owner.</summary>
/// <param name="Outcome">Sent, or failed (try again later).</param>
/// <param name="Detail">One plain sentence saying why it was not sent; null when it was.</param>
/// <param name="RetryAfter">How long the channel asked this Gateway to wait before trying again, when it said.</param>
public sealed record LicenseOwnerEmailResult(LicenseOwnerEmailOutcome Outcome, string? Detail, TimeSpan? RetryAfter = null);

/// <summary>Tells the owner of one tenant one license alert (<see cref="LicenseOwnerInbox.SendAsync"/>). The name is
/// older than Your inbox: nothing behind it sends an email any more.</summary>
public delegate Task<LicenseOwnerEmailResult> LicenseOwnerEmailSender(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct);

/// <summary>
/// THE LICENSE ALERTS GO TO YOUR INBOX, NEVER BY EMAIL (the owner's ruling on the last three alerts that emailed him:
/// "Move them all to Your inbox, with a push to the phone"). Until then this was <c>LicenseOwnerMailer</c>, which sent
/// the owner an email through the DevThrottle account service. The license checks that tell the owner - an alarm that
/// opened (<see cref="LicenseAlarmService"/>) and a sign-in whose renewal keeps failing
/// (<see cref="LicenseRenewalEmailService"/>) - now each write ONE message into the owner's Your inbox, from
/// "Licenses", with no Reply. This class holds no mail sender of any kind, so it cannot send an email.
///
/// EVERY LIMIT STAYS WITH THE CHECKS. Once for each alarm, once for each failing streak, one message for each check of
/// one account, the hold-down, the waits between tries: all of it is in the checks, which still record a message as
/// told only when this answers <see cref="LicenseOwnerEmailOutcome.Sent"/>. A message that cannot be written throws,
/// and the checks count that as a failed try and try again later.
///
/// THE PUSH. An alarm that opens is already pushed by its own push (<c>Push.LicenseAlarmPushNotifier</c>, which opens
/// the Licenses page), so <see cref="SendAsync"/> does not push again; the failing renewal had no push, so the host
/// hands that check <see cref="ThenPush"/> around the same sender.
///
/// A self-hosted Gateway has a Your inbox too, so the owner of a self-hosted Gateway is now told where before nothing
/// was sent.
/// </summary>
public sealed class LicenseOwnerInbox
{
    private readonly YourInboxWriter _write;
    private readonly Func<DateTime> _nowUtc;

    /// <param name="write">Writes the message (<see cref="YourInboxStore.Add"/>).</param>
    /// <param name="nowUtc">The time the message is sent at.</param>
    public LicenseOwnerInbox(YourInboxWriter write, Func<DateTime> nowUtc)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
    }

    /// <summary>Write <paramref name="email"/> into the Your inbox of the owner of <paramref name="tenant"/>, from
    /// "Licenses". Throws when the message cannot be written.</summary>
    public Task<LicenseOwnerEmailResult> SendAsync(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(email);
        ct.ThrowIfCancellationRequested();
        FileLog.Write($"[LicenseOwnerInbox] SendAsync: tenant={tenant.ToLogString()}");
        var row = YourInboxStore.ForSystemAlert(tenant, YourInboxWords.LicensesSeat, email.Subject, email.BodyText, _nowUtc());
        _write(tenant, row);
        FileLog.Write($"[LicenseOwnerInbox] SendAsync: wrote to Your inbox, message={row.Id:D}");
        return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
    }

    /// <summary>
    /// <paramref name="send"/>, followed by one push to the owner's phones when it answered
    /// <see cref="LicenseOwnerEmailOutcome.Sent"/>. Nothing is pushed for a message that was not written. The push is
    /// best effort AFTER the write (<see cref="SystemAlertInbox.PushBestEffortAsync"/>): a push that throws or is
    /// cancelled never turns a written message into a failure, so the check marks it told and never writes it again.
    /// The push names no one message (the sender does not return it), so a tap opens Your inbox.
    /// </summary>
    public static LicenseOwnerEmailSender ThenPush(LicenseOwnerEmailSender send, SystemAlertPusher push)
    {
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(push);
        return async (tenant, email, ct) =>
        {
            var result = await send(tenant, email, ct).ConfigureAwait(false);
            if (result.Outcome == LicenseOwnerEmailOutcome.Sent)
                await SystemAlertInbox.PushBestEffortAsync(push, tenant, YourInboxWords.LicensesSeat, email.Subject, null, ct)
                    .ConfigureAwait(false);
            return result;
        };
    }
}
