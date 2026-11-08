using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data.Entities;

namespace CcDirector.Gateway.Inbox;

/// <summary>Writes one message into an account owner's Your inbox (<see cref="YourInboxStore.Add"/>).</summary>
public delegate void YourInboxWriter(TenantId tenant, YourInboxMessageEntity message);

/// <summary>Pushes one system alert to the account's phones (<c>Push.YourInboxAlertPushNotifier.NotifyAsync</c>).
/// Never throws.</summary>
public delegate Task SystemAlertPusher(TenantId tenant, string seat, string subject, Guid? messageId, CancellationToken ct);

/// <summary>
/// HOW THE GATEWAY TELLS THE OWNER ABOUT ITSELF (the owner's ruling: "Move them all to Your inbox, with a push to the
/// phone"). The network alert and the session supervisor's escalation were emails; each is now one message in the
/// owner's Your inbox, from the Gateway under a plain name (<see cref="YourInboxWords.GatewayFromLabel"/>) and with no
/// Reply, followed by one push to the owner's phones. This class holds no mail sender of any kind, so it cannot send an
/// email. Each caller keeps its own limits (per day, per episode); this class only writes and pushes.
///
/// A message that cannot be written THROWS, and nothing is pushed: a push about a message that is not there would send
/// the owner to an empty inbox. A push that fails is logged by the pusher and never undoes the message.
/// </summary>
public sealed class SystemAlertInbox
{
    private readonly YourInboxWriter _write;
    private readonly SystemAlertPusher _push;
    private readonly Func<DateTime> _nowUtc;

    public SystemAlertInbox(YourInboxWriter write, SystemAlertPusher push, Func<DateTime>? nowUtc = null)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _push = push ?? throw new ArgumentNullException(nameof(push));
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <summary>Write one alert into the owner's Your inbox, then push it to the phones, and return the message written
    /// (<see cref="YourInboxStore.ForSystemAlert"/> builds the row).</summary>
    public async Task<YourInboxMessageEntity> TellAsync(TenantId tenant, string seat, string subject, string body, CancellationToken ct,
        string? sessionId = null, int? sessionNumber = null, string? sessionName = null)
    {
        FileLog.Write($"[SystemAlertInbox] TellAsync: tenant={tenant.ToLogString()}, seat={seat}, session={sessionId ?? "(none)"}");
        var row = YourInboxStore.ForSystemAlert(tenant, seat, subject, body, _nowUtc(), sessionId, sessionNumber, sessionName);
        try
        {
            _write(tenant, row);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SystemAlertInbox] TellAsync FAILED: the Your inbox message was not written, nothing pushed: {ex.Message}");
            throw;
        }
        FileLog.Write($"[SystemAlertInbox] wrote to Your inbox: message={row.Id:D}, seat={seat}");
        await _push(tenant, seat, row.Subject, row.Id, ct).ConfigureAwait(false);
        return row;
    }
}
