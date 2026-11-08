using CcDirector.Core.Machine;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Stats.Data;
using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats;

/// <summary>Which notice a drive's window calls for.</summary>
public enum DiskSpaceNoticeKind
{
    /// <summary>The drive went under the red line (10 GB free).</summary>
    Red = 1,

    /// <summary>The drive was red, the owner was told, and it is back above the red line.</summary>
    Recovered = 2,
}

/// <summary>Where one drive stands for the notices: the persisted state the rules read and write.</summary>
/// <param name="IsRed">True while the drive is under the red line.</param>
/// <param name="LastRedNoticeUtc">When the last red notice for this drive was written; null when none ever was.</param>
/// <param name="RedSpellNoticed">True when the current red spell was told to the owner.</param>
public sealed record DiskSpaceNoticeState(bool IsRed, DateTime? LastRedNoticeUtc, bool RedSpellNoticed)
{
    /// <summary>A drive never judged before.</summary>
    public static readonly DiskSpaceNoticeState Never = new(false, null, false);
}

/// <summary>
/// THE ONE PLACE A DRIVE'S UPLOADED WINDOW BECOMES A NOTICE (the owner's answer of 7 November 2026): "one inbox message
/// when a drive goes red, at most once a day per drive, and one when it recovers". A pure function, so every branch is
/// pinned by a unit test with no database.
///
/// RED IS A TRANSITION, NOT A CONDITION. The notice is "this drive just went under 10 GB", so a drive that stays red for
/// three days is told once, not three times - the Cockpit and the meter show it is still red.
///
/// AT MOST ONCE A DAY PER DRIVE. A drive bouncing around 10 GB free (a build that writes and cleans up) would otherwise
/// write a red notice every few minutes. A red spell that starts within a day of the last red notice is not told.
///
/// A RECOVERY ONLY FOLLOWS A RED NOTICE THE OWNER SAW. A recovery for a red spell that was held back would be a message
/// about a problem the owner was never told about - so the two always come in pairs, at most one pair a day.
///
/// One window gives at most one notice. Time is the window's own end, so a day is measured on the machine's timeline and
/// a batch of windows resent after a Director was offline is judged in order, as it happened.
/// </summary>
public static class DiskSpaceNoticeRules
{
    /// <summary>The shortest time between two red notices for the same drive: one day.</summary>
    public static readonly TimeSpan RedNoticeEvery = TimeSpan.FromDays(1);

    /// <summary>
    /// Judges one drive's window against where the drive stood. Returns the new state and the notice to write, if any.
    /// A drive that could not be read in the window is not judged: not knowing is neither red nor a recovery.
    /// </summary>
    public static (DiskSpaceNoticeState State, DiskSpaceNoticeKind? Notice) Judge(
        DiskSpaceNoticeState state, ResourceDriveSummary drive, DateTime windowEndUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(drive);
        if (!drive.CouldRead || drive.LowestFreeBytes is not { } lowest || drive.FreeBytesAtEnd is not { } atEnd)
            return (state, null);

        var red = (long)ResourceSummaryContract.DiskRedBelowBytes;
        var wentRed = lowest < red;
        var endsRed = atEnd < red;

        if (wentRed && !state.IsRed)
        {
            var allowed = state.LastRedNoticeUtc is not { } last || windowEndUtc - last >= RedNoticeEvery;
            return allowed
                ? (new DiskSpaceNoticeState(true, windowEndUtc, true), DiskSpaceNoticeKind.Red)
                : (state with { IsRed = true, RedSpellNoticed = false }, null);
        }

        if (state.IsRed && !endsRed)
        {
            var told = state.RedSpellNoticed;
            return (state with { IsRed = false, RedSpellNoticed = false }, told ? DiskSpaceNoticeKind.Recovered : null);
        }

        return (state, null);
    }

    /// <summary>The words of a notice: subject and body, plain English, the figures from the window.</summary>
    public static (string Subject, string Body) Words(DiskSpaceNoticeKind kind, string machine, ResourceDriveSummary drive, DateTime windowEndUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machine);
        ArgumentNullException.ThrowIfNull(drive);
        if (drive.FreeBytesAtEnd is not { } free || drive.TotalBytes is not { } total || drive.LowestFreeBytes is not { } lowest)
            throw new ArgumentException($"A notice needs a drive that was read; drive {drive.Name} on {machine} was not.", nameof(drive));

        var freeText = ResourceViewText.Bytes((ulong)free);
        var totalText = ResourceViewText.Bytes((ulong)total);
        var at = $"{DateTime.SpecifyKind(windowEndUtc, DateTimeKind.Utc):yyyy-MM-dd HH:mm} UTC";
        const string Rule = "You are told at most once a day for each drive when it goes under 10 GB free, and once when it is back above 10 GB.";
        const string Where = "The machine's page in the Cockpit shows every drive and its free space over the last day.";

        if (kind == DiskSpaceNoticeKind.Red)
        {
            var dip = lowest < free ? $" It fell as low as {ResourceViewText.Bytes((ulong)lowest)} in the five minutes to {at}." : "";
            return ($"Disk space: drive {drive.Name} on {machine} is under 10 GB free",
                string.Join("\n",
                    $"Drive {drive.Name} on {machine} has {freeText} free of {totalText}, under the 10 GB red line, as of {at}.{dip}",
                    "",
                    "When a drive runs out, programs on that machine cannot save their files - agents and their tools stop working. Free some space on that drive.",
                    "",
                    Where,
                    Rule));
        }

        return ($"Disk space: drive {drive.Name} on {machine} is back above 10 GB free",
            string.Join("\n",
                $"Drive {drive.Name} on {machine} has {freeText} free of {totalText} as of {at}, back above the 10 GB red line.",
                "",
                Where,
                Rule));
    }
}

/// <summary>
/// Tells the owner, in Your inbox, when a drive on one of their machines goes under 10 GB free and when it comes back
/// (Disk Space on the Meter, 7 November 2026). Fed by the Director hub with each batch of five-minute windows the
/// store has just accepted; the rules are <see cref="DiskSpaceNoticeRules"/>, and where each drive stands is kept in the
/// statistics store's <c>resource_disk_notice</c> table so a Gateway restart neither repeats a notice nor loses a spell.
///
/// The same owner inbox path the schedule failure notices use (<see cref="YourInboxStore.Add"/>), from the Gateway,
/// seat <see cref="YourInboxWords.DiskSpaceSeat"/>. Never an email.
///
/// A window already judged is not judged again: each drive's row remembers the newest window start it judged, so a
/// resend after a lost acknowledgement writes nothing.
/// </summary>
public sealed class DiskSpaceNotifier
{
    private readonly Func<IDbContextFactory<GatewayStatsDbContext>?> _factory;
    private readonly Action<TenantId, YourInboxMessageEntity> _inbox;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    /// <summary>Creates the notifier.</summary>
    /// <param name="factory">The statistics store; null while it is not open, and then nothing is judged.</param>
    /// <param name="inbox">Writes one message into the owner's Your inbox.</param>
    /// <param name="time">The Gateway's clock, for the message's sent time.</param>
    public DiskSpaceNotifier(Func<IDbContextFactory<GatewayStatsDbContext>?> factory, Action<TenantId, YourInboxMessageEntity> inbox, TimeProvider time)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        FileLog.Write("[DiskSpaceNotifier] constructed: a drive under 10 GB free is told to the owner, at most once a day per drive");
    }

    /// <summary>
    /// Judges every drive of every window, oldest window first, and writes the notices they call for. Returns how many
    /// were written. A window with no drives - from a Director older than 7 November 2026 - is skipped.
    /// </summary>
    public int Observe(TenantId tenant, IReadOnlyList<ResourceSummaryWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var withDrives = windows.Where(w => w?.Drives is { Count: > 0 }).OrderBy(w => w.WindowStartUtc).ToList();
        if (withDrives.Count == 0) return 0;

        var factory = _factory();
        if (factory is null)
        {
            FileLog.Write($"[DiskSpaceNotifier] Observe: tenant={tenant.ToLogString()} - the statistics store is not open; no drive judged");
            return 0;
        }

        var written = 0;
        lock (_gate)
        {
            using var db = factory.CreateDbContext();
            foreach (var window in withDrives)
            {
                var start = ResourceSummaryStore.Utc(window.WindowStartUtc);
                var end = start.AddSeconds(window.WindowSeconds);
                var machine = window.MachineName.Trim();
                foreach (var drive in window.Drives.Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Name)))
                {
                    var row = db.ResourceDiskNotices.Find(tenant.Value, machine, drive.Name);
                    if (row is not null && start <= DateTime.SpecifyKind(row.LastWindowStartUtc, DateTimeKind.Utc)) continue;

                    var before = row is null
                        ? DiskSpaceNoticeState.Never
                        : new DiskSpaceNoticeState(row.IsRed, row.LastRedNoticeUtc, row.RedSpellNoticed);
                    var (after, notice) = DiskSpaceNoticeRules.Judge(before, drive, end);

                    if (row is null)
                    {
                        row = new ResourceDiskNoticeEntity { Tenant = tenant.Value, MachineName = machine, Drive = drive.Name };
                        db.ResourceDiskNotices.Add(row);
                    }
                    row.IsRed = after.IsRed;
                    row.LastRedNoticeUtc = after.LastRedNoticeUtc;
                    row.RedSpellNoticed = after.RedSpellNoticed;
                    row.LastWindowStartUtc = start;

                    if (notice is not { } kind) continue;

                    // The message goes in BEFORE the state is saved: if the save then fails, the next window is judged
                    // against the old state and the owner may hear twice - never not at all.
                    var (subject, body) = DiskSpaceNoticeRules.Words(kind, machine, drive, end);
                    var message = YourInboxStore.ForDiskSpace(tenant, subject, body, _time.GetUtcNow().UtcDateTime);
                    _inbox(tenant, message);
                    written++;
                    FileLog.Write($"[DiskSpaceNotifier] Observe: tenant={tenant.ToLogString()} machine={machine} drive={drive.Name} {kind} notice written ({message.Id}), window ending {end:O}");
                }
            }
            db.SaveChanges();
        }
        return written;
    }
}
