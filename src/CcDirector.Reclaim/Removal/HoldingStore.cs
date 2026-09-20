using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Removal;

/// <summary>One entry in a holding root, as a listing shows it.</summary>
/// <param name="EntryId">The entry folder's name: the date of the move and eight hexadecimal characters.</param>
/// <param name="EntryPath">The entry folder's full path.</param>
/// <param name="Record">The record inside it.</param>
public sealed record HoldingEntry(string EntryId, string EntryPath, HoldingRecord Record);

/// <summary>
/// What one listing of a holding root found. A holding root that does not exist is an honest empty
/// answer, not an error; a holding root that exists but cannot be read is an error naming why, never
/// an empty list - an unreadable holding and an empty holding look identical from the outside, and
/// only one of them is safe to act on.
/// </summary>
public sealed record HoldingListing
{
    /// <summary>True when the holding root exists on the disk.</summary>
    public required bool RootExists { get; init; }

    /// <summary>Entries whose records say the move completed.</summary>
    public required IReadOnlyList<HoldingEntry> Complete { get; init; }

    /// <summary>
    /// Entries whose records say the move never completed. They are listed separately and named,
    /// never silently counted among the held and never silently dropped.
    /// </summary>
    public required IReadOnlyList<HoldingEntry> Incomplete { get; init; }

    /// <summary>Why the root could not be read, when it could not. Never null alongside an empty list.</summary>
    public required string? UnreadableReason { get; init; }
}

/// <summary>What moving one item into holding did.</summary>
public sealed record HoldOutcome
{
    /// <summary>The entry's id, when the move happened.</summary>
    public required string? EntryId { get; init; }

    /// <summary>True when the item is in holding.</summary>
    public required bool Held { get; init; }

    /// <summary>
    /// Why the item was not moved and is still where it was, or null when it moved. A refusal always
    /// leaves the item in place - a holding that refuses is keeping something, never losing it.
    /// </summary>
    public required string? RefusalReason { get; init; }

    /// <summary>
    /// A warning about an entry that moved but whose record could not be finalized, or null. The item
    /// IS in holding and the entry IS incomplete: it is named by every listing and never purged until
    /// restore resolves it.
    /// </summary>
    public required string? IncompleteReason { get; init; }
}

/// <summary>What restoring one entry did.</summary>
public sealed record RestoreOutcome
{
    /// <summary>The entry that was asked for.</summary>
    public required string EntryId { get; init; }

    /// <summary>Where the record says the item came from, when the record could be read.</summary>
    public required string? OriginalPath { get; init; }

    /// <summary>True when the item was moved back to its original path.</summary>
    public required bool Restored { get; init; }

    /// <summary>
    /// True when the entry's record said the move never happened and the item was still at its
    /// original path: nothing needed to move, and the entry is cleared.
    /// </summary>
    public required bool AlreadyHome { get; init; }

    /// <summary>Why nothing was restored, or null when something was. Restore never overwrites.</summary>
    public required string? RefusalReason { get; init; }
}

/// <summary>What one purge call decided, entry by entry.</summary>
public sealed record PurgeOutcome
{
    /// <summary>True when entries were actually removed; false when this was the dry run.</summary>
    public required bool Applied { get; init; }

    /// <summary>Entries past their holding period, which an apply removes.</summary>
    public required IReadOnlyList<HoldingEntry> Purgeable { get; init; }

    /// <summary>Entries not yet past their holding period, which no apply touches.</summary>
    public required IReadOnlyList<HoldingEntry> NotYetPurgeable { get; init; }

    /// <summary>Entries whose records say the move never completed. Purge always refuses them.</summary>
    public required IReadOnlyList<HoldingEntry> Incomplete { get; init; }

    /// <summary>Entries that could not be answered and were kept, each with why.</summary>
    public required IReadOnlyList<(HoldingEntry Entry, string Reason)> Kept { get; init; }

    /// <summary>Entries this call removed, when it applied.</summary>
    public required IReadOnlyList<string> PurgedEntryIds { get; init; }
}

/// <summary>
/// A holding folder: where removal puts what it moves, and the record that makes each move
/// answerable. One holding root serves one volume, because removal is a MOVE on the same volume - a
/// move across volumes is a copy and a delete, which is not what this is.
///
/// The engine takes the holding root as a parameter and holds no default of its own. Only the command
/// line computes the per-volume default, so no test ever writes to the root of a real volume: every
/// test keeps its holding inside a fixture tree it built itself.
///
/// Every method here leans to keep. A record that cannot be read, an entry that cannot be listed, a
/// move the file system refuses - each is a refusal or a kept entry with the reason named, never an
/// assumption and never a fallback location somewhere else.
/// </summary>
public sealed class HoldingStore
{
    /// <summary>The default holding period, in days.</summary>
    public const int DefaultHoldingPeriodDays = 30;

    private readonly string _root;

    /// <summary>
    /// Build a store over one holding root.
    /// </summary>
    /// <param name="holdingRoot">The holding root's full path. It is created when something is held, not here.</param>
    public HoldingStore(string holdingRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(holdingRoot);
        _root = holdingRoot;
        FileLog.Write($"[HoldingStore] HoldingStore: root={_root}");
    }

    /// <summary>The holding root this store serves.</summary>
    public string Root => _root;

    /// <summary>
    /// Move one item into holding. The record is written in the same breath as the move: the entry
    /// folder is created, the record is written saying the move is in progress, the item moves, and
    /// the record is rewritten saying the move completed. A crash between the record and the move
    /// leaves an incomplete entry that every reader names and nothing purges.
    /// </summary>
    /// <param name="candidate">The item to hold, with the bytes and newest write measured at the move.</param>
    /// <param name="ruleId">The rule that proved the item disposable.</param>
    /// <param name="movedAtUtc">When the move happens.</param>
    /// <param name="holdingPeriodDays">How long the entry must stay before it is purgeable.</param>
    public HoldOutcome Hold(
        ReclaimCandidate candidate,
        string ruleId,
        DateTimeOffset movedAtUtc,
        int holdingPeriodDays = DefaultHoldingPeriodDays)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        ArgumentOutOfRangeException.ThrowIfNegative(holdingPeriodDays);
        FileLog.Write($"[HoldingStore] Hold: path={candidate.Path}, rule={ruleId}");

        var isDirectory = Directory.Exists(candidate.Path);
        var isFile = File.Exists(candidate.Path);
        if (!isDirectory && !isFile)
        {
            FileLog.Write($"[HoldingStore] Hold refused: path={candidate.Path}, the item is not there");
            return new HoldOutcome
            {
                EntryId = null,
                Held = false,
                RefusalReason = $"the item {candidate.Path} is not there any more, so there is nothing to move",
                IncompleteReason = null
            };
        }

        try
        {
            Directory.CreateDirectory(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[HoldingStore] Hold FAILED: the holding root {_root} could not be created, code={ex.HResult}");
            return new HoldOutcome
            {
                EntryId = null,
                Held = false,
                RefusalReason =
                    $"the holding root {_root} cannot be created or written, so nothing can move into it " +
                    $"(code {ex.HResult.ToString(CultureInfo.InvariantCulture)}); the item stays where it is",
                IncompleteReason = null
            };
        }

        string entryPath;
        try
        {
            entryPath = CreateEntryFolder(movedAtUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[HoldingStore] Hold FAILED: an entry folder under {_root} could not be created, code={ex.HResult}");
            return new HoldOutcome
            {
                EntryId = null,
                Held = false,
                RefusalReason =
                    $"an entry folder under {_root} could not be created (code " +
                    $"{ex.HResult.ToString(CultureInfo.InvariantCulture)}); the item stays where it is",
                IncompleteReason = null
            };
        }

        var entryId = Path.GetFileName(entryPath);
        var name = isDirectory
            ? new DirectoryInfo(candidate.Path).Name
            : new FileInfo(candidate.Path).Name;

        var record = new HoldingRecord
        {
            OriginalPath = Path.GetFullPath(candidate.Path),
            Name = name,
            Bytes = candidate.Bytes,
            LastWrittenUtc = candidate.LastWrittenUtc,
            Rule = ruleId,
            MovedAtUtc = movedAtUtc,
            PurgeNotBeforeUtc = movedAtUtc.AddDays(holdingPeriodDays),
            State = HoldingState.Moving
        };

        // The record saying the move is in progress, written before the move: this is what makes a
        // crash between the two answerable rather than a mystery item in holding that nothing knows.
        if (!TryWriteRecord(entryPath, record))
        {
            TryDeleteEntryFolder(entryPath);
            return new HoldOutcome
            {
                EntryId = null,
                Held = false,
                RefusalReason = $"the record for the move into {entryPath} could not be written; the item stays where it is",
                IncompleteReason = null
            };
        }

        var destination = Path.Combine(entryPath, name);
        try
        {
            if (isDirectory) Directory.Move(candidate.Path, destination);
            else File.Move(candidate.Path, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The move did not happen. The entry folder holds nothing but a record for a move that
            // never occurred, and leaving it behind would be an incomplete entry for a refusal, so
            // it is taken away again and the item is simply kept.
            FileLog.Write(
                $"[HoldingStore] Hold FAILED: the move of {candidate.Path} into {destination} was refused, code={ex.HResult}");
            TryDeleteEntryFolder(entryPath);
            return new HoldOutcome
            {
                EntryId = null,
                Held = false,
                RefusalReason =
                    $"the file system refused to move {candidate.Path} into holding (code " +
                    $"{ex.HResult.ToString(CultureInfo.InvariantCulture)}); the item stays where it is",
                IncompleteReason = null
            };
        }

        var held = record with { State = HoldingState.Held };
        if (!TryWriteRecord(entryPath, held))
        {
            FileLog.Write($"[HoldingStore] Hold: the item moved but the record at {entryPath} could not be finalized");
            return new HoldOutcome
            {
                EntryId = entryId,
                Held = true,
                RefusalReason = null,
                IncompleteReason =
                    $"the item moved into {entryPath} but its record could not be finalized, so the entry is " +
                    "incomplete: it is named by every listing, never purged, and restore resolves it"
            };
        }

        FileLog.Write($"[HoldingStore] Hold done: path={candidate.Path}, entry={entryId}, bytes={candidate.Bytes}");
        return new HoldOutcome { EntryId = entryId, Held = true, RefusalReason = null, IncompleteReason = null };
    }

    /// <summary>
    /// List everything in this holding root. Incomplete entries are listed separately and named. A
    /// root that does not exist is an honest empty answer; a root that exists but cannot be read is an
    /// error naming why.
    /// </summary>
    public HoldingListing List()
    {
        FileLog.Write($"[HoldingStore] List: root={_root}");

        if (!Directory.Exists(_root))
        {
            FileLog.Write($"[HoldingStore] List: root={_root}, result=not there, count: 0");
            return new HoldingListing
            {
                RootExists = false,
                Complete = [],
                Incomplete = [],
                UnreadableReason = null
            };
        }

        string[] entries;
        try
        {
            entries = Directory.GetDirectories(_root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[HoldingStore] List FAILED: root={_root}, code={ex.HResult}");
            return new HoldingListing
            {
                RootExists = true,
                Complete = [],
                Incomplete = [],
                UnreadableReason =
                    $"the holding root {_root} exists but cannot be read (code " +
                    $"{ex.HResult.ToString(CultureInfo.InvariantCulture)}), so its entries are neither known nor safe to touch"
            };
        }

        var complete = new List<HoldingEntry>();
        var incomplete = new List<HoldingEntry>();
        foreach (var entryPath in entries)
        {
            if (TryReadRecord(entryPath, out var record))
            {
                if (record.State == HoldingState.Held) complete.Add(new HoldingEntry(Path.GetFileName(entryPath), entryPath, record));
                else incomplete.Add(new HoldingEntry(Path.GetFileName(entryPath), entryPath, record));
            }
            else
            {
                incomplete.Add(new HoldingEntry(
                    Path.GetFileName(entryPath),
                    entryPath,
                    new HoldingRecord
                    {
                        OriginalPath = string.Empty,
                        Name = string.Empty,
                        Bytes = 0,
                        LastWrittenUtc = DateTimeOffset.UnixEpoch,
                        Rule = string.Empty,
                        MovedAtUtc = DateTimeOffset.UnixEpoch,
                        PurgeNotBeforeUtc = DateTimeOffset.UnixEpoch,
                        State = HoldingState.Moving
                    }));
            }
        }

        FileLog.Write(
            $"[HoldingStore] List done: root={_root}, complete={complete.Count}, incomplete={incomplete.Count}");
        return new HoldingListing
        {
            RootExists = true,
            Complete = complete,
            Incomplete = incomplete,
            UnreadableReason = null
        };
    }

    /// <summary>
    /// Move one entry's item back to the path its record says it came from. Restoring is never a dry
    /// run: it moves only within holding, back to where the record says the item came from, and it is
    /// refused by any check it cannot answer. It never overwrites anything: when something now stands
    /// at the original path the restore is refused, the reason says so, and the entry stays.
    /// </summary>
    /// <param name="entryId">The entry folder's name, as a listing showed it.</param>
    public RestoreOutcome Restore(string entryId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        FileLog.Write($"[HoldingStore] Restore: entry={entryId}");

        var entryPath = Path.Combine(_root, entryId);
        if (!Directory.Exists(entryPath))
        {
            FileLog.Write($"[HoldingStore] Restore refused: entry={entryId}, there is no such entry");
            return new RestoreOutcome
            {
                EntryId = entryId,
                OriginalPath = null,
                Restored = false,
                AlreadyHome = false,
                RefusalReason = $"there is no entry {entryId} in {_root}"
            };
        }

        if (!TryReadRecord(entryPath, out var record))
        {
            FileLog.Write($"[HoldingStore] Restore refused: entry={entryId}, the record cannot be read");
            return new RestoreOutcome
            {
                EntryId = entryId,
                OriginalPath = null,
                Restored = false,
                AlreadyHome = false,
                RefusalReason = $"the record of entry {entryId} cannot be read, so there is nothing to answer with; the entry stays"
            };
        }

        // An entry whose move never happened, with the item still sitting at its original path, is
        // resolved by saying so and clearing the entry - there is nothing to move.
        if (record.State == HoldingState.Moving && PathExists(record.OriginalPath))
        {
            if (!TryDeleteEntryFolder(entryPath))
            {
                return new RestoreOutcome
                {
                    EntryId = entryId,
                    OriginalPath = record.OriginalPath,
                    Restored = false,
                    AlreadyHome = false,
                    RefusalReason = $"the item is already home at {record.OriginalPath} but the entry {entryId} could not be cleared"
                };
            }

            FileLog.Write($"[HoldingStore] Restore done: entry={entryId}, result=already home");
            return new RestoreOutcome
            {
                EntryId = entryId,
                OriginalPath = record.OriginalPath,
                Restored = false,
                AlreadyHome = true,
                RefusalReason = null
            };
        }

        var parent = Path.GetDirectoryName(record.OriginalPath);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            FileLog.Write($"[HoldingStore] Restore refused: entry={entryId}, the original parent is gone");
            return new RestoreOutcome
            {
                EntryId = entryId,
                OriginalPath = record.OriginalPath,
                Restored = false,
                AlreadyHome = false,
                RefusalReason = $"the folder {parent} no longer exists, so the item from {entryId} has nowhere to go back to; the entry stays"
            };
        }

        if (PathExists(record.OriginalPath))
        {
            FileLog.Write($"[HoldingStore] Restore refused: entry={entryId}, something stands at the original path");
            return new RestoreOutcome
            {
                EntryId = entryId,
                OriginalPath = record.OriginalPath,
                Restored = false,
                AlreadyHome = false,
                RefusalReason = $"something now stands at {record.OriginalPath}, and restore never overwrites; the entry stays"
            };
        }

        var heldItem = Path.Combine(entryPath, record.Name);
        if (!PathExists(heldItem))
        {
            FileLog.Write($"[HoldingStore] Restore refused: entry={entryId}, the entry holds no such item");
            return new RestoreOutcome
            {
                EntryId = entryId,
                OriginalPath = record.OriginalPath,
                Restored = false,
                AlreadyHome = false,
                RefusalReason = $"the entry {entryId} does not hold an item named {record.Name}, so there is nothing to put back; the entry stays"
            };
        }

        try
        {
            if (Directory.Exists(heldItem)) Directory.Move(heldItem, record.OriginalPath);
            else File.Move(heldItem, record.OriginalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[HoldingStore] Restore FAILED: entry={entryId}, code={ex.HResult}");
            return new RestoreOutcome
            {
                EntryId = entryId,
                OriginalPath = record.OriginalPath,
                Restored = false,
                AlreadyHome = false,
                RefusalReason = $"the file system refused to move the item back to {record.OriginalPath} (code {ex.HResult.ToString(CultureInfo.InvariantCulture)}); the entry stays"
            };
        }

        if (!TryDeleteEntryFolder(entryPath))
        {
            FileLog.Write($"[HoldingStore] Restore: entry={entryId}, the item returned but the entry could not be cleared");
            return new RestoreOutcome
            {
                EntryId = entryId,
                OriginalPath = record.OriginalPath,
                Restored = true,
                AlreadyHome = false,
                RefusalReason = $"the item returned to {record.OriginalPath}, but the entry folder {entryPath} could not be cleared"
            };
        }

        FileLog.Write($"[HoldingStore] Restore done: entry={entryId}, result=restored");
        return new RestoreOutcome
        {
            EntryId = entryId,
            OriginalPath = record.OriginalPath,
            Restored = true,
            AlreadyHome = false,
            RefusalReason = null
        };
    }

    /// <summary>
    /// Purge the entries whose holding period has passed. A dry run by default: it names exactly
    /// which entries have passed their period, and with <paramref name="apply"/> it removes them,
    /// re-reading each record immediately before removing it. A record that cannot be read, or an
    /// entry that cannot be listed or removed, is kept and named, never assumed. Entries whose
    /// records say the move never completed are always refused.
    /// </summary>
    /// <param name="nowUtc">The moment the holding periods are judged against.</param>
    /// <param name="apply">True to remove; false to report what would be removed.</param>
    /// <param name="daysOverride">
    /// A holding period for this call only, in days, or null to use each record's own
    /// purge-not-before moment. The default period is thirty days.
    /// </param>
    public PurgeOutcome Purge(DateTimeOffset nowUtc, bool apply, int? daysOverride = null)
    {
        if (daysOverride is < 0)
            throw new ArgumentOutOfRangeException(nameof(daysOverride), daysOverride, "A holding period cannot be negative.");
        FileLog.Write($"[HoldingStore] Purge: root={_root}, apply={apply}, days={daysOverride?.ToString(CultureInfo.InvariantCulture) ?? "each record's own"}");

        var listing = List();
        var purgeable = new List<HoldingEntry>();
        var notYet = new List<HoldingEntry>();
        var incomplete = new List<HoldingEntry>();
        var kept = new List<(HoldingEntry Entry, string Reason)>();
        var purged = new List<string>();

        if (!listing.RootExists)
        {
            FileLog.Write($"[HoldingStore] Purge done: root={_root}, result=no holding root, count: 0");
            return new PurgeOutcome
            {
                Applied = apply,
                Purgeable = [],
                NotYetPurgeable = [],
                Incomplete = [],
                Kept = [],
                PurgedEntryIds = []
            };
        }

        foreach (var entry in listing.Complete)
        {
            if (IsPastItsPeriod(entry.Record, nowUtc, daysOverride)) purgeable.Add(entry);
            else notYet.Add(entry);
        }

        incomplete.AddRange(listing.Incomplete);

        if (apply)
        {
            foreach (var entry in purgeable.ToList())
            {
                // Re-read the record immediately before removing the entry: the disk changes under
                // every step, and a record that has become unreadable between the listing and the
                // removal is kept rather than assumed.
                if (!TryReadRecord(entry.EntryPath, out var fresh) || fresh.State != HoldingState.Held)
                {
                    kept.Add((entry, $"the record of entry {entry.EntryId} could not be re-read immediately before its removal, so the entry is kept"));
                    purgeable.Remove(entry);
                    continue;
                }

                if (!TryDeleteEntryFolder(entry.EntryPath))
                {
                    kept.Add((entry, $"the entry folder {entry.EntryPath} could not be removed, so the entry is kept"));
                    purgeable.Remove(entry);
                    continue;
                }

                purged.Add(entry.EntryId);
            }
        }

        FileLog.Write(
            $"[HoldingStore] Purge done: root={_root}, apply={apply}, purgeable={purgeable.Count}, " +
            $"notYet={notYet.Count}, incomplete={incomplete.Count}, kept={kept.Count}, purged={purged.Count}");
        return new PurgeOutcome
        {
            Applied = apply,
            Purgeable = purgeable,
            NotYetPurgeable = notYet,
            Incomplete = incomplete,
            Kept = kept,
            PurgedEntryIds = purged
        };
    }

    /// <summary>An entry is past its holding period when the moment asked about has reached its gate.</summary>
    private static bool IsPastItsPeriod(HoldingRecord record, DateTimeOffset nowUtc, int? daysOverride)
    {
        var gate = daysOverride is null
            ? record.PurgeNotBeforeUtc
            : record.MovedAtUtc.AddDays(daysOverride.Value);
        return nowUtc >= gate;
    }

    private string CreateEntryFolder(DateTimeOffset movedAtUtc)
    {
        // The date a person can read in a directory listing, and eight hexadecimal characters so no
        // two entries ever share a folder. A collision is retried, and a machine that cannot offer a
        // unique name after many tries is refused loudly rather than reusing one.
        var date = movedAtUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
            var candidate = Path.Combine(_root, $"{date}-{suffix}");
            if (!Directory.Exists(candidate))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }

        throw new IOException($"Sixty-four attempts to name an entry folder under {_root} all collided.");
    }

    private static bool TryWriteRecord(string entryPath, HoldingRecord record)
    {
        try
        {
            var path = Path.Combine(entryPath, HoldingRecord.RecordFileName);
            File.WriteAllText(path, JsonSerializer.Serialize(record, HoldingRecord.Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[HoldingStore] TryWriteRecord FAILED: entry={entryPath}, code={ex.HResult}");
            return false;
        }
    }

    private static bool TryReadRecord(string entryPath, out HoldingRecord record)
    {
        try
        {
            var path = Path.Combine(entryPath, HoldingRecord.RecordFileName);
            if (!File.Exists(path))
            {
                record = EmptyRecord();
                return false;
            }

            var read = JsonSerializer.Deserialize<HoldingRecord>(File.ReadAllText(path), HoldingRecord.Json);
            if (read is null)
            {
                record = EmptyRecord();
                return false;
            }

            if (string.IsNullOrWhiteSpace(read.OriginalPath) || string.IsNullOrWhiteSpace(read.Name))
            {
                record = EmptyRecord();
                return false;
            }

            record = read;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            FileLog.Write($"[HoldingStore] TryReadRecord FAILED: entry={entryPath}, code={ex.HResult}");
            record = EmptyRecord();
            return false;
        }
    }

    private static HoldingRecord EmptyRecord() => new()
    {
        OriginalPath = string.Empty,
        Name = string.Empty,
        Bytes = 0,
        LastWrittenUtc = DateTimeOffset.UnixEpoch,
        Rule = string.Empty,
        MovedAtUtc = DateTimeOffset.UnixEpoch,
        PurgeNotBeforeUtc = DateTimeOffset.UnixEpoch,
        State = HoldingState.Moving
    };

    private static bool TryDeleteEntryFolder(string entryPath)
    {
        // An entry folder is only ever removed by the code that has just decided what to do with it:
        // a refusal that never moved anything takes away the record it wrote for the move that did
        // not happen, and a completed restore or purge takes away an entry it has fully accounted for.
        try
        {
            Directory.Delete(entryPath, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[HoldingStore] TryDeleteEntryFolder FAILED: entry={entryPath}, code={ex.HResult}");
            return false;
        }
    }

    private static bool PathExists(string path) =>
        !string.IsNullOrWhiteSpace(path) && (Directory.Exists(path) || File.Exists(path));
}
