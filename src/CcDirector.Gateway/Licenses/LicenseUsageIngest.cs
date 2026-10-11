using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Licenses;

/// <summary>Why a usage report was or was not taken.</summary>
public enum LicenseIngestOutcome
{
    Accepted,

    /// <summary>The connection is not the Director's active one - a superseded connection never overwrites current facts.</summary>
    NotActiveConnection,

    /// <summary>The sequence is not higher than the last one accepted from this Director's connection.</summary>
    StaleSequence,

    /// <summary>The report breaks the contract's shape (no machine, two active holdings, an account twice, no email).</summary>
    Malformed,
}

/// <summary>
/// THE USAGE REPORT INGEST (License Switching, contract section 2.1): decides whether one report from a bound Director
/// is taken, and when it is, hands it to <see cref="LicenseStore.ApplyReport"/>. The same gate
/// <c>PushedRepositoryStore.ApplySnapshot</c> applies: only the Director's ACTIVE connection, only a sequence higher
/// than the last one accepted from that connection (a new connection starts a fresh baseline, as the Director's own
/// sequence does). A refused report is logged and dropped; it is never an error to the Director.
///
/// Clock skew (contract section 5.2): when a report's <c>SentAtUtc</c> is further than
/// <see cref="LicenseContract.ClockSkewLoggedAbove"/> from the receive time, its ages are judged from the receive time,
/// and the skew is logged once per machine (again only after it has cleared and come back).
/// </summary>
public sealed class LicenseUsageIngest
{
    public const int MaxHoldings = 64;

    private sealed class SequenceGate
    {
        public string ConnectionId = "";
        public long LastSequence = long.MinValue;
    }

    private readonly LicenseStore _store;
    private readonly ConcurrentDictionary<(TenantId Tenant, string DirectorId), SequenceGate> _gates = new();
    private readonly ConcurrentDictionary<(TenantId Tenant, string MachineKey, string UserKey), byte> _skewLogged = new();

    public LicenseUsageIngest(LicenseStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Take or refuse one report. <paramref name="isActiveConnection"/> is the hub's answer to "is this the Director's
    /// current connection"; the tenant and the Director identifier are the connection's binding, never the payload.
    /// </summary>
    public LicenseIngestOutcome Ingest(TenantId tenant, string directorId, string connectionId, bool isActiveConnection,
        long sequence, LicenseUsageReport? report, DateTime receivedAtUtc)
    {
        if (!isActiveConnection)
        {
            FileLog.Write($"[LicenseUsageIngest] report DROPPED (not the active connection): tenant={tenant.ToLogString()}, director={directorId}, seq={sequence}");
            return LicenseIngestOutcome.NotActiveConnection;
        }
        if (Malformed(report) is { } why)
        {
            FileLog.Write($"[LicenseUsageIngest] report DROPPED (malformed: {why}): tenant={tenant.ToLogString()}, director={directorId}, seq={sequence}");
            return LicenseIngestOutcome.Malformed;
        }

        var gate = _gates.GetOrAdd((tenant, directorId), _ => new SequenceGate());
        lock (gate)
        {
            if (!string.Equals(gate.ConnectionId, connectionId, StringComparison.Ordinal))
            {
                gate.ConnectionId = connectionId;
                gate.LastSequence = long.MinValue;
            }
            if (sequence <= gate.LastSequence)
            {
                FileLog.Write($"[LicenseUsageIngest] report DROPPED (stale sequence {sequence} <= last {gate.LastSequence}): tenant={tenant.ToLogString()}, director={directorId}");
                return LicenseIngestOutcome.StaleSequence;
            }

            // Applied under the gate, so two reports from one Director cannot land out of order.
            var shift = ReadAtShift(tenant, report!, receivedAtUtc);
            var applied = _store.ApplyReport(tenant, directorId, sequence, report!, receivedAtUtc, shift);
            gate.LastSequence = sequence;
            FileLog.Write($"[LicenseUsageIngest] report ACCEPTED: tenant={tenant.ToLogString()}, director={directorId}, seq={sequence}, "
                          + $"trigger={report!.Trigger}, holdings={report.Holdings.Count}, added={applied.Added.Count}, removed={applied.Removed.Count}");
            return LicenseIngestOutcome.Accepted;
        }
    }

    /// <summary>Forget a Director's sequence baseline when its connection ends, so the map does not grow forever.</summary>
    public void ForgetConnection(TenantId tenant, string directorId, string connectionId)
    {
        if (_gates.TryGetValue((tenant, directorId), out var gate))
        {
            lock (gate)
            {
                if (string.Equals(gate.ConnectionId, connectionId, StringComparison.Ordinal))
                    _gates.TryRemove(new KeyValuePair<(TenantId, string), SequenceGate>((tenant, directorId), gate));
            }
        }
    }

    /// <summary>
    /// What to add to each <c>ReadAtUtc</c> to get the time staleness is judged by: zero when the report's clock is
    /// within the skew limit; otherwise receive time minus <c>SentAtUtc</c>, so a reading's AGE (SentAt minus ReadAt)
    /// is kept and measured from when the Gateway received it.
    /// </summary>
    public TimeSpan ReadAtShift(TenantId tenant, LicenseUsageReport report, DateTime receivedAtUtc)
    {
        var skew = receivedAtUtc - report.SentAtUtc;
        var key = (tenant, report.MachineName.Trim().ToLowerInvariant(), report.OsUser.Trim().ToLowerInvariant());
        if (skew.Duration() <= LicenseContract.ClockSkewLoggedAbove)
        {
            _skewLogged.TryRemove(key, out _);
            return TimeSpan.Zero;
        }
        if (_skewLogged.TryAdd(key, 0))
            FileLog.Write($"[LicenseUsageIngest] CLOCK SKEW: machine={report.MachineName}, user={report.OsUser}, "
                          + $"skew={skew.TotalSeconds:F0}s (receive time minus sent time); ages are judged from the receive time");
        return skew;
    }

    /// <summary>Why the report breaks the contract's shape, or null when it does not.</summary>
    internal static string? Malformed(LicenseUsageReport? report)
    {
        if (report is null) return "no report";
        if (string.IsNullOrWhiteSpace(report.MachineName)) return "no machine name";
        if (report.MachineName.Trim().Length > 128 || report.OsUser.Trim().Length > 128) return "machine or user name too long";
        if (report.Holdings is null) return "no holdings list";
        if (report.Holdings.Count > MaxHoldings) return $"more than {MaxHoldings} holdings";
        if (report.Holdings.Any(h => h is null)) return "a null holding";
        var emails = report.Holdings.Select(h => LicenseStore.NormalizeEmail(h.AccountEmail)).ToList();
        if (emails.Any(e => e.Length == 0 || e.Length > 320)) return "a holding with no account email, or one too long";
        if (emails.Distinct(StringComparer.Ordinal).Count() != emails.Count) return "an account held twice";
        if (report.Holdings.Count(h => h.IsActive) > 1) return "more than one active holding";
        if (!string.IsNullOrWhiteSpace(report.SwitchInProgressId) && !LicenseContract.SwitchIdShape.IsMatch(report.SwitchInProgressId.Trim()))
            return "a switch in progress id of the wrong shape";
        return null;
    }
}
