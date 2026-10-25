using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Messaging;

/// <summary>
/// THE REQUESTS FOR A MESSAGE LINK, AS ALERTS (the Alerts mission). A pure mapping over
/// <see cref="FleetMessageLinkRequestStore"/>, read the way the owner's own list reads it
/// (<see cref="FleetMessageLinkRequestEndpoints.List"/>): a waiting request one of whose sessions has ended is ended
/// first, by that feature's own <see cref="FleetMessageLinkRequestEndpoints.EndIfASessionEnded"/>, so a request nobody
/// can answer never sits on the bell. The detail is the same labelled <see cref="FleetMessageLinkRequestDto"/> the list
/// serves, so the detail view answers through the existing answer route.
/// </summary>
public sealed class MessageRequestAlertSource : IAlertSource
{
    private readonly FleetMessageLinkRequestStore _requests;
    private readonly FleetMessageLinkRecord _record;
    private readonly Func<TenantId, string, SessionDto?> _findSession;
    private readonly Action<TenantId, SessionDto, string> _notify;

    public MessageRequestAlertSource(FleetMessageLinkRequestStore requests, FleetMessageLinkRecord record,
        Func<TenantId, string, SessionDto?> findSession, Action<TenantId, SessionDto, string> notify)
    {
        _requests = requests ?? throw new ArgumentNullException(nameof(requests));
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _findSession = findSession ?? throw new ArgumentNullException(nameof(findSession));
        _notify = notify ?? throw new ArgumentNullException(nameof(notify));
    }

    public string Kind => AlertKinds.MessageRequest;

    public IReadOnlyList<AlertDto> List(TenantId tenant, DateTime nowUtc)
    {
        foreach (var waiting in _requests.List(tenant, nowUtc).Where(r => r.Status == FleetMessageLinkRequestStatuses.Pending))
            FleetMessageLinkRequestEndpoints.EndIfASessionEnded(tenant, waiting, _requests, _record, _findSession, _notify, nowUtc);

        var alerts = _requests.List(tenant, nowUtc - AlertWindow.Earlier)
            .Select(r => Fold(FleetMessageLinkRequestEndpoints.Labelled(tenant, r, _findSession)))
            .Where(a => AlertWindow.Keeps(a.NeedsAnswer, a.UpdatedAtUtc, nowUtc))
            .ToList();
        FileLog.Write($"[MessageRequestAlertSource] List: tenant={tenant.ToLogString()}, alerts={alerts.Count}");
        return alerts;
    }

    /// <summary>One request as the owner reads it. The labels are the session numbers and names the Gateway already
    /// writes for the phone (<see cref="FleetMessageLinkRequestEndpoints.SessionLabel"/>), never an id.</summary>
    internal static AlertDto Fold(FleetMessageLinkRequestDto r)
    {
        var from = r.RequesterLabel ?? throw new ArgumentException($"request {r.RequestId} is not labelled", nameof(r));
        var to = r.TargetLabel ?? throw new ArgumentException($"request {r.RequestId} is not labelled", nameof(r));
        var (status, title, summary) = r.Status switch
        {
            FleetMessageLinkRequestStatuses.Pending => ("Waiting", $"Let {from} message {to}?", $"{from} asks: {r.Reason}"),
            FleetMessageLinkRequestStatuses.Allowed => ("Allowed", $"{from} may message {to}",
                $"You allowed it: {AmountWords(r.Amount)}."),
            FleetMessageLinkRequestStatuses.Declined => ("Declined", $"{from} asked to message {to}",
                "You declined it. Nothing was set up."),
            FleetMessageLinkRequestStatuses.Ended => ("Ended", $"{from} asked to message {to}",
                "A session ended before you answered. Nothing was set up."),
            _ => throw new InvalidOperationException($"message request {r.RequestId} is '{r.Status}', which has no alert words"),
        };
        return new AlertDto
        {
            Id = AlertKinds.IdOf(AlertKinds.MessageRequest, r.RequestId),
            Kind = AlertKinds.MessageRequest,
            NeedsAnswer = r.Status == FleetMessageLinkRequestStatuses.Pending,
            Title = title,
            Summary = summary,
            Status = status,
            CreatedAtUtc = Utc(r.AskedAtUtc),
            UpdatedAtUtc = Utc(AlertWindow.Latest(r.AskedAtUtc, r.AnsweredAtUtc)),
            ExpiresAtUtc = null,
            Detail = r,
        };
    }

    /// <summary>How much talking was allowed, in words.</summary>
    internal static string AmountWords(string? amount) => amount switch
    {
        FleetMessageLinkAmounts.Once => "one message, with no reply",
        FleetMessageLinkAmounts.OnceWithReply => "one message and its reply",
        FleetMessageLinkAmounts.Ongoing => "as many messages as they need, both ways",
        _ => throw new InvalidOperationException($"an allowed request carries the amount '{amount}', which is not one of "
                                                 + string.Join(", ", FleetMessageLinkAmounts.All)),
    };

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
