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

    /// <summary>The report breaks the contract's shape (no machine, two active holdings, an account twice, no email).
    /// Unlike the two refusals above, this one is about the MACHINE, so it is recorded where the Licenses page shows
    /// it (<see cref="LicenseStore.RecordRefusal"/>).</summary>
    Malformed,
}

/// <summary>Why a report was refused for what it says: a fixed word, and the reason as a clause in plain English
/// ("it holds alice@example.com twice (once in use and once as a spare)") for the Licenses page.</summary>
public sealed record LicenseReportRefusal(string Code, string Reason);

/// <summary>The words a refused report's reason is recorded by. At most 32 characters each (the switch log's
/// reason column).</summary>
public static class LicenseReportRefusalCodes
{
    public const string NoReport = "no-report";
    public const string NoMachineName = "no-machine-name";
    public const string NoOsUser = "no-os-user";
    public const string NameTooLong = "name-too-long";
    public const string NoHoldingsList = "no-holdings-list";
    public const string TooManyHoldings = "too-many-holdings";
    public const string EmptyHolding = "empty-holding";
    public const string NoAccountEmail = "no-account-email";
    public const string AccountHeldTwice = "account-held-twice";
    public const string MoreThanOneInUse = "more-than-one-in-use";
    public const string SwitchIdShape = "switch-id-shape";
    public const string SwitchedByHandNotActive = "switched-by-hand-not-active";
    public const string UsageNumber = "usage-number";
    public const string ReadTimeAhead = "read-time-ahead";
    public const string ReadTimeTooOld = "read-time-too-old";

    /// <summary>Every word, for the tests that hold each one to its own sentence.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        NoReport, NoMachineName, NoOsUser, NameTooLong, NoHoldingsList, TooManyHoldings, EmptyHolding, NoAccountEmail,
        AccountHeldTwice, MoreThanOneInUse, SwitchIdShape, SwitchedByHandNotActive, UsageNumber, ReadTimeAhead, ReadTimeTooOld,
    };
}

/// <summary>
/// THE USAGE REPORT INGEST (License Switching, contract section 2.1): decides whether one report from a bound Director
/// is taken, and when it is, hands it to <see cref="LicenseStore.ApplyReport"/>. The same gate
/// <c>PushedRepositoryStore.ApplySnapshot</c> applies: only the Director's ACTIVE connection, only a sequence higher
/// than the last one accepted from that connection. A refused report is never an error to the Director.
///
/// TWO KINDS OF REFUSAL. A report from a connection that is not the Director's owner, or with a sequence it has
/// already sent, is BOOKKEEPING: a reconnect, a resend. It is logged and dropped, and says nothing about the machine.
/// A report from the owner that breaks the contract's shape (<see cref="Malformed"/>) is about the MACHINE: its
/// Director is alive and every report it sends is thrown away. That one is RECORDED (<see cref="LicenseStore.RecordRefusal"/>),
/// one row of the Licenses log for each change of reason, and the page shows the machine as refused, with the reason.
/// The shape is judged only once the report is known to come from the owner, so a superseded connection cannot mark
/// a machine refused.
///
/// WHO OWNS A DIRECTOR. This class keeps which connection owns each Director (registered from Hello, with the epoch rule
/// of <c>PushedRepositoryStore</c>, so a superseded connection saying Hello again never reclaims it). Director
/// identifiers are compared without regard to case, as that store compares them.
///
/// THE ORDER OF WRITES, WITHOUT HOLDING HELLO HOSTAGE TO THE DATABASE (pull request 3871 reviews). Two locks per
/// Director: a short ownership lock that Hello, disconnect and the check take, held only over a few fields; and a write
/// gate that only reports take, held over the check AND the database write. So:
/// - a reconnect or a disconnect never waits for a database write;
/// - a report is checked against ownership inside the write gate, immediately before its write, so a connection that was
///   superseded before its turn is refused;
/// - a report that passed its check before a reconnect finishes its write before the new connection's first report can
///   even be checked, so the older facts can never land on top of the newer ones;
/// - the accepted sequence is recorded only if the connection generation (its epoch) is still the one the check saw.
///
/// Clock skew (contract section 5.2): when a report's <c>SentAtUtc</c> is further than
/// <see cref="LicenseContract.ClockSkewLoggedAbove"/> from the receive time, its ages are judged from the receive time,
/// and the skew is logged once per machine (again only after it has cleared and come back).
/// </summary>
public sealed class LicenseUsageIngest
{
    public const int MaxHoldings = 64;

    private sealed class Owner
    {
        public readonly object WriteGate = new();
        public string? ActiveConnectionId;
        public long CurrentEpoch;                 // 0 = no owner
        public long NextEpoch = 1;
        public readonly Dictionary<string, long> ConnectionEpochs = new(StringComparer.Ordinal);
        public long LastSequence = long.MinValue;
    }

    private readonly LicenseStore _store;
    private readonly LicenseUsageHistory _history;
    private readonly Action<string>? _waitingForWriteGate;
    private readonly Action<string>? _insideWrite;
    private readonly ConcurrentDictionary<TenantId, ConcurrentDictionary<string, Owner>> _owners = new();
    private readonly ConcurrentDictionary<(TenantId Tenant, string MachineKey, string UserKey), byte> _skewLogged = new();

    public LicenseUsageIngest(LicenseStore store) : this(store, new LicenseUsageHistory()) { }

    /// <summary><paramref name="history"/> takes the readings of every accepted report, for the forecast's rate of use.</summary>
    public LicenseUsageIngest(LicenseStore store, LicenseUsageHistory history) : this(store, history, null, null) { }

    /// <summary>For tests: <paramref name="waitingForWriteGate"/> runs (with the connection id) just before a report asks
    /// for the write gate, and <paramref name="insideWrite"/> runs inside it, after the ownership check and before the
    /// database write - so a test can line up a reconnect and a second report at exactly those points.</summary>
    internal LicenseUsageIngest(LicenseStore store, Action<string>? waitingForWriteGate, Action<string>? insideWrite)
        : this(store, new LicenseUsageHistory(), waitingForWriteGate, insideWrite) { }

    private LicenseUsageIngest(LicenseStore store, LicenseUsageHistory history, Action<string>? waitingForWriteGate, Action<string>? insideWrite)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _waitingForWriteGate = waitingForWriteGate;
        _insideWrite = insideWrite;
    }

    private ConcurrentDictionary<string, Owner> OwnersFor(TenantId tenant)
        => _owners.GetOrAdd(tenant, _ => new ConcurrentDictionary<string, Owner>(StringComparer.OrdinalIgnoreCase));

    /// <summary>A connection said Hello for this Director. A newer connection takes ownership and starts a fresh sequence
    /// baseline; a superseded one saying Hello again does not reclaim it; the current one keeps its baseline. Never waits
    /// for a database write.</summary>
    public void RegisterConnection(TenantId tenant, string directorId, string connectionId)
    {
        var owner = OwnersFor(tenant).GetOrAdd(directorId, _ => new Owner());
        lock (owner)
        {
            if (!owner.ConnectionEpochs.TryGetValue(connectionId, out var epoch))
            {
                epoch = owner.NextEpoch++;
                owner.ConnectionEpochs[connectionId] = epoch;
            }
            if (string.Equals(owner.ActiveConnectionId, connectionId, StringComparison.Ordinal))
                return;
            if (epoch < owner.CurrentEpoch)
            {
                FileLog.Write($"[LicenseUsageIngest] Hello IGNORED from a superseded connection (epoch {epoch} < current {owner.CurrentEpoch}): director={directorId}");
                return;
            }
            owner.ActiveConnectionId = connectionId;
            owner.CurrentEpoch = epoch;
            owner.LastSequence = long.MinValue;
            FileLog.Write($"[LicenseUsageIngest] RegisterConnection: tenant={tenant.ToLogString()}, director={directorId}, epoch={epoch}");
        }
    }

    /// <summary>A connection ended. When it was the owner, nobody owns the Director until the next Hello. Never waits for
    /// a database write.</summary>
    public void UnregisterConnection(TenantId tenant, string directorId, string connectionId)
    {
        if (!OwnersFor(tenant).TryGetValue(directorId, out var owner)) return;
        lock (owner)
        {
            owner.ConnectionEpochs.Remove(connectionId);
            if (string.Equals(owner.ActiveConnectionId, connectionId, StringComparison.Ordinal))
            {
                owner.ActiveConnectionId = null;
                owner.CurrentEpoch = 0;
            }
        }
        FileLog.Write($"[LicenseUsageIngest] UnregisterConnection: tenant={tenant.ToLogString()}, director={directorId}");
    }

    /// <summary>
    /// Take or refuse one report. The tenant and the Director identifier are the connection's binding, never the payload;
    /// whether <paramref name="connectionId"/> is the owner is decided inside the write gate, just before the write.
    /// </summary>
    public LicenseIngestOutcome Ingest(TenantId tenant, string directorId, string connectionId, long sequence,
        LicenseUsageReport? report, DateTime receivedAtUtc)
    {
        if (!OwnersFor(tenant).TryGetValue(directorId, out var owner))
        {
            FileLog.Write($"[LicenseUsageIngest] report DROPPED (no connection registered): tenant={tenant.ToLogString()}, director={directorId}, seq={sequence}");
            return LicenseIngestOutcome.NotActiveConnection;
        }

        _waitingForWriteGate?.Invoke(connectionId);
        lock (owner.WriteGate)
        {
            long generation;
            lock (owner)
            {
                if (!string.Equals(owner.ActiveConnectionId, connectionId, StringComparison.Ordinal))
                {
                    FileLog.Write($"[LicenseUsageIngest] report DROPPED (not the active connection): tenant={tenant.ToLogString()}, director={directorId}, seq={sequence}");
                    return LicenseIngestOutcome.NotActiveConnection;
                }
                if (sequence <= owner.LastSequence)
                {
                    FileLog.Write($"[LicenseUsageIngest] report DROPPED (stale sequence {sequence} <= last {owner.LastSequence}): tenant={tenant.ToLogString()}, director={directorId}");
                    return LicenseIngestOutcome.StaleSequence;
                }
                generation = owner.CurrentEpoch;
            }

            if (Malformed(report) is { } refusal)
            {
                FileLog.Write($"[LicenseUsageIngest] report REFUSED ({refusal.Code}: {refusal.Reason}): tenant={tenant.ToLogString()}, director={directorId}, seq={sequence}");
                // The license alarm, once it exists, is raised here when the change is not Same.
                _store.RecordRefusal(tenant, directorId, report, refusal, receivedAtUtc);
                return LicenseIngestOutcome.Malformed;
            }

            var shift = ReadAtShift(tenant, report!, receivedAtUtc);
            _insideWrite?.Invoke(connectionId);
            var applied = _store.ApplyReport(tenant, directorId, sequence, report!, receivedAtUtc, shift);
            _store.RecordAccepted(tenant, directorId, applied, report!, receivedAtUtc);
            // Only a report the store took adds to the history: a refused or failed one never reaches this line.
            _history.Record(tenant, report!, shift, receivedAtUtc);
            lock (owner)
            {
                // A reconnect during the write started a fresh baseline for the new connection; this one's number must
                // not be written over it.
                if (owner.CurrentEpoch == generation)
                    owner.LastSequence = sequence;
            }
            FileLog.Write($"[LicenseUsageIngest] report ACCEPTED: tenant={tenant.ToLogString()}, director={directorId}, seq={sequence}, "
                          + $"trigger={report!.Trigger}, holdings={report.Holdings.Count}, added={applied.Added.Count}, removed={applied.Removed.Count}");
            return LicenseIngestOutcome.Accepted;
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
    internal static LicenseReportRefusal? Malformed(LicenseUsageReport? report)
    {
        static LicenseReportRefusal Refused(string code, string reason) => new(code, reason);
        if (report is null) return Refused(LicenseReportRefusalCodes.NoReport, "it sent no report");
        if (string.IsNullOrWhiteSpace(report.MachineName)) return Refused(LicenseReportRefusalCodes.NoMachineName, "it does not name its machine");
        // The user is required to be there, never to be non-empty: an empty user is one the store keys by.
        if (report.OsUser is null) return Refused(LicenseReportRefusalCodes.NoOsUser, "it does not name its operating system user");
        if (report.MachineName.Trim().Length > 128 || report.OsUser.Trim().Length > 128)
            return Refused(LicenseReportRefusalCodes.NameTooLong, "its machine name or user name is longer than 128 characters");
        if (report.Holdings is null) return Refused(LicenseReportRefusalCodes.NoHoldingsList, "it carries no list of sign-ins");
        if (report.Holdings.Count > MaxHoldings)
            return Refused(LicenseReportRefusalCodes.TooManyHoldings, $"it lists {report.Holdings.Count} sign-ins, and at most {MaxHoldings} are taken");
        if (report.Holdings.Any(h => h is null)) return Refused(LicenseReportRefusalCodes.EmptyHolding, "it lists a sign-in that is empty");
        var emails = report.Holdings.Select(h => LicenseStore.NormalizeEmail(h.AccountEmail)).ToList();
        if (emails.Any(e => e.Length == 0 || e.Length > 320))
            return Refused(LicenseReportRefusalCodes.NoAccountEmail, "it lists a sign-in with no account email, or with one longer than 320 characters");
        if (emails.GroupBy(e => e, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
            return Refused(LicenseReportRefusalCodes.AccountHeldTwice, $"it holds {twice.Key} {HeldTwice(report.Holdings.Where(h => LicenseStore.NormalizeEmail(h.AccountEmail) == twice.Key).ToList())}");
        var inUse = report.Holdings.Where(h => h.IsActive).Select(h => LicenseStore.NormalizeEmail(h.AccountEmail)).ToList();
        if (inUse.Count > 1)
            return Refused(LicenseReportRefusalCodes.MoreThanOneInUse,
                $"it says {inUse.Count} sign-ins are in use at once ({string.Join(", ", inUse.Take(inUse.Count - 1))} and {inUse[^1]}), and a machine is on one account at a time");
        if (!string.IsNullOrWhiteSpace(report.SwitchInProgressId) && !LicenseContract.SwitchIdShape.IsMatch(report.SwitchInProgressId.Trim()))
            return Refused(LicenseReportRefusalCodes.SwitchIdShape, "it reports a switch in progress whose identifier is not one the Gateway could have sent");
        if (report.SwitchedByHand is { } hand
            && (LicenseStore.NormalizeEmail(hand.AccountEmail).Length == 0
                || LicenseStore.NormalizeEmail(hand.AccountEmail) != LicenseStore.NormalizeEmail(report.Holdings.FirstOrDefault(h => h.IsActive)?.AccountEmail)))
            return Refused(LicenseReportRefusalCodes.SwitchedByHandNotActive, LicenseStore.NormalizeEmail(hand.AccountEmail) is { Length: > 0 } to
                ? $"it says it was switched by hand to {to}, which is not the sign-in it has in use"
                : "it says it was switched by hand, and names no account it was switched to");
        return report.Holdings.Select(h => UnreadableReading(h, report.SentAtUtc)).FirstOrDefault(why => why is not null);
    }

    // How an account is held more than once, said the way a person reads the machine's card.
    private static string HeldTwice(IReadOnlyList<LicenseHolding> holdings)
    {
        var active = holdings.Count(h => h.IsActive);
        var times = holdings.Count == 2 ? "twice" : $"{holdings.Count} times";
        return (holdings.Count, active) switch
        {
            (2, 1) => $"{times} (once in use and once as a spare)",
            (_, 0) => $"{times} (each time as a spare)",
            (_, var n) when n == holdings.Count => $"{times} (each time in use)",
            _ => $"{times} ({active} in use and {holdings.Count - active} as spares)",
        };
    }

    /// <summary>The most a usage number may be. The provider reports 0 to 100 and can pass 100 a little; nothing real
    /// is near this.</summary>
    public const double MaxUtilization = 1000;

    /// <summary>The longest a reading may be older than the report that carries it. A holding whose read keeps failing
    /// carries its last good read time for as long as that lasts; nothing real is near this.</summary>
    public static readonly TimeSpan MaxReadingAge = TimeSpan.FromDays(3650);

    /// <summary>
    /// Why one holding's numbers or read time cannot be a reading, or null when they can. Everything downstream - the
    /// rate, the forecast, the staleness of an account - does arithmetic on these, so what is refused here cannot
    /// become an infinite rate, a date past the end of the calendar or a reading that is never stale:
    /// a usage number that is not a number, is below 0 or is above <see cref="MaxUtilization"/>; a read time later than
    /// the report's own sent time by more than <see cref="LicenseContract.ClockSkewLoggedAbove"/>; and a read time more
    /// than <see cref="MaxReadingAge"/> before it. Both times are judged against the report's sent time, never a clock.
    ///
    /// WHY A READ TIME A LITTLE AHEAD OF THE SENT TIME IS ALLOWED. One clock wrote both, but not at one moment: a
    /// spare is read every ten minutes and its read time is kept until the next read. When time synchronisation
    /// steps the machine's clock BACK, that kept read time is ahead of every report sent for up to ten minutes. The
    /// whole report is what is refused here, so refusing those would leave a healthy machine silent, then stale, then
    /// unable to be switched. A step of up to two minutes is an ordinary correction - the same amount of skew the
    /// ingest ignores between the machine's clock and the Gateway's. Further ahead than that is not a reading.
    /// </summary>
    internal static LicenseReportRefusal? UnreadableReading(LicenseHolding holding, DateTime sentAtUtc)
    {
        static bool Bad(double? value) => value is { } v && (!double.IsFinite(v) || v < 0 || v > MaxUtilization);
        var email = LicenseStore.NormalizeEmail(holding.AccountEmail);
        if (Bad(holding.FiveHour?.Utilization) || Bad(holding.SevenDay?.Utilization) || Bad(holding.ExtraUsage?.Utilization))
            return new(LicenseReportRefusalCodes.UsageNumber, $"it gives a usage number for {email} that is not between 0 and {MaxUtilization}");
        if (holding.ReadAtUtc is not { } readAt)
            return null;
        var read = Utc(readAt);
        var sent = Utc(sentAtUtc);
        if (read - sent > LicenseContract.ClockSkewLoggedAbove)
            return new(LicenseReportRefusalCodes.ReadTimeAhead, $"it gives a read time for {email} more than two minutes later than the time the report was sent");
        if (sent - read > MaxReadingAge)
            return new(LicenseReportRefusalCodes.ReadTimeTooOld, $"it gives a read time for {email} more than ten years before the time the report was sent");
        return null;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
