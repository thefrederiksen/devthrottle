using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Alerts;

/// <summary>What one clear did.</summary>
public enum AlertClearOutcome
{
    /// <summary>The alert is cleared (or already was).</summary>
    Cleared,

    /// <summary>No current alert in this account has that id.</summary>
    NotFound,

    /// <summary>The alert still needs an answer, so it was not cleared.</summary>
    NeedsAnswer,
}

/// <summary>
/// ONE LIST OF EVERYTHING THAT NEEDS THE OWNER, per account, composed at read time (the Alerts mission, section 5.1).
/// Every <see cref="IAlertSource"/> is read, the owner's marks are applied - a cleared alert is not listed, a seen one is
/// flagged - and the result is ordered: needs-answer first, newest first within each half.
///
/// NOTHING HERE ANSWERS ANYTHING. Accepting a restart, approving a transfer or allowing a message keeps going through its
/// feature's own routes and their checks; this service only reads, and writes the marks.
/// </summary>
public sealed class AlertsService
{
    /// <summary>The sentence a refused clear carries (section 5.2).</summary>
    public const string NeedsAnswerSentence = "This still needs your answer; it leaves when you answer it or when it expires.";

    private readonly IReadOnlyList<IAlertSource> _sources;
    private readonly AlertMarkStore _marks;
    private readonly Func<DateTime> _nowUtc;

    public AlertsService(IReadOnlyList<IAlertSource> sources, AlertMarkStore marks, Func<DateTime> nowUtc)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _marks = marks ?? throw new ArgumentNullException(nameof(marks));
        _nowUtc = nowUtc ?? throw new ArgumentNullException(nameof(nowUtc));
        var kinds = _sources.Select(s => s.Kind).ToList();
        if (kinds.Distinct(StringComparer.Ordinal).Count() != kinds.Count)
            throw new ArgumentException($"two alert sources share a kind: {string.Join(", ", kinds)}", nameof(sources));
    }

    /// <summary>True when <paramref name="id"/> has the shape of an alert id: a known kind, a colon, and a source id.</summary>
    public static bool IsWellFormedId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var colon = id.IndexOf(':');
        if (colon <= 0 || colon == id.Length - 1) return false;
        return AlertKinds.All.Contains(id[..colon], StringComparer.Ordinal);
    }

    /// <summary>The account's alerts as the bell and the tray show them.</summary>
    public AlertListResponse List(TenantId tenant)
    {
        FileLog.Write($"[AlertsService] List: tenant={tenant.ToLogString()}");
        try
        {
            var now = _nowUtc();
            var all = Compose(tenant, now);
            var marks = _marks.All(tenant);
            _marks.Sweep(tenant, all.Select(a => a.Id).ToHashSet(StringComparer.Ordinal), AlertWindow.Earlier, now);

            var shown = new List<AlertDto>();
            foreach (var alert in all)
            {
                marks.TryGetValue(alert.Id, out var mark);
                if (mark?.ClearedAtUtc is not null && !alert.NeedsAnswer) continue;
                // Compared at the precision the table keeps, the precision the version was stored at.
                alert.Seen = mark?.SeenVersionUtc is { } seen
                             && AlertMarkStore.ToStoredPrecision(DateTime.SpecifyKind(seen, DateTimeKind.Utc))
                                >= AlertMarkStore.ToStoredPrecision(alert.UpdatedAtUtc);
                shown.Add(alert);
            }
            var ordered = shown
                .OrderByDescending(a => a.NeedsAnswer)
                .ThenByDescending(a => a.UpdatedAtUtc)
                .ThenBy(a => a.Id, StringComparer.Ordinal)
                .ToList();
            var response = new AlertListResponse { Alerts = ordered, NeedsAnswerCount = ordered.Count(a => a.NeedsAnswer) };
            FileLog.Write($"[AlertsService] List: tenant={tenant.ToLogString()}, alerts={ordered.Count}, needsAnswer={response.NeedsAnswerCount}");
            return response;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AlertsService] List FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Mark the given alerts seen at their current version. Ids that name no current alert are skipped.
    /// Returns how many were marked.</summary>
    public int MarkSeen(TenantId tenant, IReadOnlyCollection<string> ids)
    {
        FileLog.Write($"[AlertsService] MarkSeen: tenant={tenant.ToLogString()}, ids={ids.Count}");
        var now = _nowUtc();
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var seen = Compose(tenant, now)
            .Where(a => wanted.Contains(a.Id))
            .Select(a => (a.Id, a.UpdatedAtUtc))
            .ToList();
        _marks.MarkSeen(tenant, seen, now);
        return seen.Count;
    }

    /// <summary>Clear one alert, unless it still needs an answer.</summary>
    public AlertClearOutcome Clear(TenantId tenant, string id)
    {
        FileLog.Write($"[AlertsService] Clear: tenant={tenant.ToLogString()}, id={id}");
        var now = _nowUtc();
        var alert = Compose(tenant, now).FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal));
        if (alert is null) return AlertClearOutcome.NotFound;
        if (alert.NeedsAnswer) return AlertClearOutcome.NeedsAnswer;
        _marks.MarkCleared(tenant, new[] { alert.Id }, now);
        return AlertClearOutcome.Cleared;
    }

    /// <summary>Clear every alert that does not need an answer and is not cleared already. Returns how many.</summary>
    public int ClearAll(TenantId tenant)
    {
        FileLog.Write($"[AlertsService] ClearAll: tenant={tenant.ToLogString()}");
        var now = _nowUtc();
        var marks = _marks.All(tenant);
        var ids = Compose(tenant, now)
            .Where(a => !a.NeedsAnswer && !(marks.TryGetValue(a.Id, out var m) && m.ClearedAtUtc is not null))
            .Select(a => a.Id)
            .ToList();
        _marks.MarkCleared(tenant, ids, now);
        FileLog.Write($"[AlertsService] ClearAll: tenant={tenant.ToLogString()}, cleared={ids.Count}");
        return ids.Count;
    }

    private List<AlertDto> Compose(TenantId tenant, DateTime now)
        => _sources.SelectMany(s => s.List(tenant, now)).ToList();
}
