using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE RESTART REQUESTS, AS ALERTS (the Alerts mission). A pure mapping over <see cref="DirectorRestartRequestStore"/>:
/// its own <see cref="DirectorRestartRequestStore.List"/> applies the expiry and the sweep, and this folds each record
/// into one title and one sentence for the owner.
///
/// ONLY A PENDING REQUEST NEEDS AN ANSWER, and only while <see cref="DirectorRestartRequestDto.CanAccept"/> says the
/// store would honour an accept. An accepted request is "Earlier" at once, with the Director's latest progress line as
/// its summary, updating in place until the Director reports how it ended - it is never a red card for three hours.
///
/// The capability check's own reasons are internal diagnostics: they stay in the detail for the collapsed Details,
/// never in the title or the summary.
/// </summary>
public sealed class RestartRequestAlertSource : IAlertSource
{
    private readonly DirectorRestartRequestStore _store;

    public RestartRequestAlertSource(DirectorRestartRequestStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public string Kind => AlertKinds.RestartRequest;

    public IReadOnlyList<AlertDto> List(TenantId tenant, DateTime nowUtc)
    {
        var alerts = _store.List(tenant)
            .Select(Fold)
            .Where(a => AlertWindow.Keeps(a.NeedsAnswer, a.UpdatedAtUtc, nowUtc))
            .ToList();
        FileLog.Write($"[RestartRequestAlertSource] List: tenant={tenant.ToLogString()}, alerts={alerts.Count}");
        return alerts;
    }

    /// <summary>One request as the owner reads it. Every state of <see cref="DirectorRestartRequestState"/> is named here,
    /// and a state added later throws rather than render a sentence nobody wrote.</summary>
    internal static AlertDto Fold(DirectorRestartRequestDto r)
    {
        var machine = r.Machine;
        var (status, tone, title, summary) = r.State switch
        {
            DirectorRestartRequestState.Pending => ("Waiting", AlertTones.Neutral, $"Restart the Director on {machine}?", AskedBy(r)),
            DirectorRestartRequestState.Accepted => ("Accepted", AlertTones.Neutral, $"Restarting the Director on {machine}",
                string.IsNullOrWhiteSpace(r.Progress) ? "Accepted - waiting for the Director." : Sentence(r.Progress)),
            DirectorRestartRequestState.Declined => ("Declined", AlertTones.Neutral, $"Restart of the Director on {machine}",
                "You declined it. Nothing was restarted."),
            DirectorRestartRequestState.Expired => r.AcceptedAtUtc is null
                ? ("Expired", AlertTones.Neutral, $"Restart of the Director on {machine}",
                    "Nobody accepted it within 30 minutes. Nothing was restarted.")
                : ("Expired", AlertTones.Bad, $"Restart of the Director on {machine}",
                    "The Director did not say how the restart ended within 3 hours. Check that machine before assuming it restarted."),
            DirectorRestartRequestState.Abandoned => ("Stopped", AlertTones.Bad, $"Restart of the Director on {machine}",
                "The Director stopped without restarting. Open Details for its reason."),
            DirectorRestartRequestState.Completed => ("Restarted", AlertTones.Good, $"The Director on {machine} restarted",
                "The restart finished."),
            _ => throw new InvalidOperationException($"restart request {r.Id} is in state {r.State}, which has no alert words"),
        };
        return new AlertDto
        {
            Id = AlertKinds.IdOf(AlertKinds.RestartRequest, r.Id),
            Kind = AlertKinds.RestartRequest,
            NeedsAnswer = r.State == DirectorRestartRequestState.Pending && r.CanAccept,
            Title = title,
            Summary = summary,
            Status = status,
            Tone = tone,
            CreatedAtUtc = Utc(r.RequestedAtUtc),
            UpdatedAtUtc = Utc(AlertWindow.Latest(r.RequestedAtUtc, r.AcceptedAtUtc, r.ProgressAtUtc, r.ClosedAtUtc)),
            ExpiresAtUtc = r.State == DirectorRestartRequestState.Pending ? Utc(r.ExpiresAtUtc) : null,
            Detail = r,
        };
    }

    /// <summary>Who asked and why, in one sentence: "Release Manager asks: the Mac Tester can run the v2.31.0 checks". The
    /// request's own <see cref="DirectorRestartRequestDto.AskedBySentence"/> names the session with a short id, which
    /// the owner cannot act on; this names it by its mission and role (<see cref="AlertWords.FromRestartRequester"/>).</summary>
    private static string AskedBy(DirectorRestartRequestDto r)
    {
        if (string.IsNullOrWhiteSpace(r.RequestedBySessionName))
            throw new InvalidOperationException($"restart request {r.Id} names no requester");
        return $"{AlertWords.FromRestartRequester(r.RequestedBySessionName)} asks: {r.Reason}";
    }

    /// <summary>The Director's progress line as a sentence: it writes "accepted; telling the Director to begin".</summary>
    private static string Sentence(string text)
    {
        var t = text.Trim();
        t = char.ToUpperInvariant(t[0]) + t[1..];
        return t.EndsWith('.') ? t : t + ".";
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
