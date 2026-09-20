using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Rules;

namespace CcDirector.Reclaim.Windows;

/// <summary>
/// The recycle bin on one volume.
///
/// This is the first proof kind, and the record is the bin's own. For every item it holds, the
/// recycle bin keeps a record that says where the file came from and when the owner deleted it: on
/// this system that record is the file whose name starts with the two characters dollar-I, and it
/// is paired with the item's own data, whose name starts with dollar-R and carries the same
/// identity. The pair is offered as one thing, because a record without its data is a deletion
/// nobody can undo and data without a record is a thing nobody has proven was deleted at all.
///
/// Everything else in the bin is declined and counted: another account's bin, which refuses its
/// listing to this one; data with no record; records with no data; entries the system does not
/// explain. On the machine this rule was measured on, the bin folders hold more than half of their
/// bytes in entries without records, and Windows' own view of the bin says it is empty - so the
/// rule offers nothing there, and says so with its controls, because an entry this rule cannot
/// positively prove disposable survives. That is the boundary working, not the boundary leaking.
///
/// The age gate is judged on the deletion time in the record, which is the moment the owner's
/// deletion happened, not on any file's write time.
///
/// The mandate's own words on this place: the recycle bin is the one place on the machine that is
/// already a holding folder, so emptying it is the end of the line, and "what is lost" says so.
/// </summary>
public sealed class RecycleBinRule : IReclaimRule
{
    /// <summary>How long ago the owner must have deleted an item before this rule will offer it.</summary>
    public const int DefaultAgeGateDays = 30;

    private static readonly string RecordPrefix = "$I";
    private static readonly string DataPrefix = "$R";

    private readonly string _recycleBinFolderPath;
    private readonly string _volumeWord;

    /// <summary>
    /// Build the rule for one volume's bin.
    /// </summary>
    /// <param name="recycleBinFolderPath">The volume's recycle bin folder, for example C:\$RECYCLE.BIN.</param>
    /// <param name="ageGateDays">How long ago the deletion must be before the item is offered. Thirty days by default.</param>
    public RecycleBinRule(string recycleBinFolderPath, int ageGateDays = DefaultAgeGateDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recycleBinFolderPath);
        ArgumentOutOfRangeException.ThrowIfNegative(ageGateDays);

        _recycleBinFolderPath = recycleBinFolderPath;
        AgeGateDays = ageGateDays;

        var root = Path.GetPathRoot(Path.GetFullPath(recycleBinFolderPath)) ?? recycleBinFolderPath;
        _volumeWord = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>The rule's name as an identifier a machine matches on.</summary>
    public string Id => $"windows-recycle-bin-on-{Letter}";

    /// <summary>The rule's name as a person reads it.</summary>
    public string Name => $"The recycle bin on the {_volumeWord} volume";

    private string Letter =>
        _volumeWord.Length > 0 && char.IsLetter(_volumeWord[0])
            ? char.ToLowerInvariant(_volumeWord[0]).ToString(CultureInfo.InvariantCulture)
            : _volumeWord;

    /// <summary>A record the system keeps says nothing needs it.</summary>
    public ProofKind Proof => ProofKind.SystemRecord;

    /// <summary>What this rule removes.</summary>
    public string WhatItRemoves =>
        $"items in {_recycleBinFolderPath} that carry the bin's own deletion record: the record and its " +
        "data are offered together as one thing, and an entry the system gives no record for is left alone";

    /// <summary>Why removing it is safe.</summary>
    public string WhyItIsSafe =>
        "the recycle bin keeps its own record for every item it holds, and the record says where the file " +
        "came from and when the owner deleted it: the owner's own deletion is the system's own record that " +
        "nothing needs the item, and an entry without one is a thing nobody has proven was deleted, so it stays";

    /// <summary>
    /// What is lost. It says plainly that emptying the bin is the end of the line, because the bin
    /// is the one place on the machine that is already a holding folder.
    /// </summary>
    public string WhatIsLost =>
        "the deleted files themselves, and emptying the bin is the end of the line: the recycle bin is " +
        "already the holding folder, so once it is emptied there is no further holding and no way back. " +
        "Until then the files can be restored exactly as they were";

    /// <summary>How to get it back.</summary>
    public string HowToGetItBack =>
        "from the bin's own restore, until it is emptied. After that there is no way back: nothing else " +
        "holds these files, and the deletion the record describes becomes final";

    /// <summary>How long ago the owner must have deleted an item before this rule will offer it.</summary>
    public int AgeGateDays { get; }

    /// <summary>
    /// False. Only the account's own bins are offered, and an account may empty its own bin. Another
    /// account's bin refuses its listing and is counted, not offered.
    /// </summary>
    public bool NeedsAdministrator => false;

    /// <summary>There is no command to print: the bin is emptied by the owner, or by the tool that owns removal.</summary>
    public string? CommandToRun => null;

    /// <summary>The volume's recycle bin folder this rule looks in.</summary>
    public string LooksIn => _recycleBinFolderPath;

    /// <summary>Look, count, and report. It reads; it changes nothing.</summary>
    /// <param name="context">What is being asked about, and the moment the age gate is judged against.</param>
    public RuleAnswer Examine(RuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        FileLog.Write($"[RecycleBinRule] Examine: folder={_recycleBinFolderPath}");

        // The bin folder is probed by ATTEMPTING ITS LISTING, never by an existence question.
        //
        // Directory.Exists answers false for a folder that is not there AND for one that cannot be
        // told - access denied, an input-output error, a path it cannot evaluate - and it swallows
        // the reason, so the two arrive indistinguishable. That matters here more than almost
        // anywhere: the absent answer below carries no control capable of alarming, because there is
        // genuinely nothing to count, so if "could not tell" reached it the fold could not catch it
        // and the rule would report "nothing to remove" about a bin it never managed to look at.
        //
        // Attempting the listing separates them. Not-found is the honest absent answer; every other
        // failure names itself and reports the rule broken.
        List<DirectoryInfo> accountBins;
        try
        {
            accountBins = new DirectoryInfo(_recycleBinFolderPath)
                .EnumerateDirectories("*", SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch (DirectoryNotFoundException)
        {
            // A volume with no recycle bin folder is a volume that has never held a deleted item or
            // has recycling switched off. That is a real answer, not a failure: counted, offered
            // nothing, not called broken. This catch must stay ABOVE the IOException one, because
            // DirectoryNotFoundException derives from it and would otherwise be reported as broken.
            FileLog.Write($"[RecycleBinRule] Examine done: the bin folder is not there");
            return new RuleAnswer(
                [
                    new RuleControl("bins-looked-for", 1, MustNotBeEmpty: true),
                    new RuleControl("bins-found", 0, MustNotBeEmpty: false),
                    new RuleControl("records-read", 0, MustNotBeEmpty: false)
                ],
                []);
        }
        catch (UnauthorizedAccessException)
        {
            return new RuleAnswer([], [],
                $"the recycle bin folder {_recycleBinFolderPath} would not be listed, so this rule could not do its work");
        }
        catch (IOException)
        {
            return new RuleAnswer([], [],
                $"the recycle bin folder {_recycleBinFolderPath} would not be listed, so this rule could not do its work");
        }

        var oldEnough = context.NowUtc.AddDays(-AgeGateDays);
        var candidates = new List<ReclaimCandidate>();
        var binsFound = accountBins.Count;
        var binsListed = 0;
        var binsThatWouldNotBeListed = 0;
        var recordsRead = 0;
        var recordsTooYoung = 0;
        var recordsWithoutTheirData = 0;
        var recordsThatWouldNotBeRead = 0;
        var pairsThatWouldNotBeRead = 0;
        var entriesWithoutARecord = 0;

        foreach (var bin in accountBins)
        {
            // One account's bin is one thing, whatever is in it, and the candidates below are one
            // per bin: per-user and per-volume bins are treated as what they are.
            List<FileSystemInfo> entries;
            try
            {
                entries = bin.EnumerateFileSystemInfos("*", SearchOption.TopDirectoryOnly).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                binsThatWouldNotBeListed++;
                continue;
            }
            catch (IOException)
            {
                binsThatWouldNotBeListed++;
                continue;
            }

            binsListed++;

            // The data entries, indexed by the identity they carry after the dollar-R, so each
            // record can find its own data in one step.
            var dataById = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!entry.Name.StartsWith(DataPrefix, StringComparison.Ordinal)) continue;

                var id = IdentityOf(entry.Name);
                if (id.Length > 0) dataById[id] = entry;
            }

            long offeredBytes = 0;
            var offeredRecords = 0;
            var newestDeletion = DateTimeOffset.UnixEpoch;
            var claimedData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (!entry.Name.StartsWith(RecordPrefix, StringComparison.Ordinal))
                {
                    // Everything that is not a record is a thing the system has not explained: the
                    // bin's own settings file, entries no record names. None of it is positively
                    // proven disposable, so all of it survives and is counted. Data entries are
                    // counted after the loop, once the records have said which of them they claim.
                    if (!entry.Name.StartsWith(DataPrefix, StringComparison.Ordinal))
                        entriesWithoutARecord++;
                    continue;
                }

                var record = ReadRecord(entry.FullName);
                if (record is null)
                {
                    recordsThatWouldNotBeRead++;
                    continue;
                }

                recordsRead++;

                var id = IdentityOf(entry.Name);
                if (!dataById.TryGetValue(id, out var data))
                {
                    recordsWithoutTheirData++;
                    continue;
                }

                if (record.DeletedAtUtc > oldEnough)
                {
                    recordsTooYoung++;
                    continue;
                }

                var measured = MeasureData(data);
                if (measured is null)
                {
                    pairsThatWouldNotBeRead++;
                    continue;
                }

                claimedData.Add(id);
                offeredRecords++;
                offeredBytes += measured.Value;
                if (record.DeletedAtUtc > newestDeletion) newestDeletion = record.DeletedAtUtc;
            }

            // Data no record claims is a thing nobody has proven was deleted, so it is counted and
            // never offered, exactly like the bin's other unexplained entries.
            foreach (var (id, _) in dataById)
            {
                if (!claimedData.Contains(id)) entriesWithoutARecord++;
            }

            if (offeredRecords == 0) continue;

            candidates.Add(new ReclaimCandidate(
                bin.FullName,
                offeredBytes,
                newestDeletion,
                $"the account's own bin, holding {offeredRecords.ToString(CultureInfo.InvariantCulture)} " +
                "deleted items whose own records say where each came from and when the owner deleted it, " +
                "each longer ago than the age gate"));
        }

        var controls = new List<RuleControl>
        {
            // The bin this rule is about. Nought here is a rule constructed with no bin at all,
            // which is a defect rather than an answer, and the fold refuses it.
            new("bins-looked-for", 1, MustNotBeEmpty: true),
            new("bins-found", binsFound, MustNotBeEmpty: false),

            // The bins whose listings succeeded. The bin folder exists and holds account bins, so
            // nought here means every one of them refused its listing, which means this rule could
            // not do its work rather than that there is nothing to remove.
            new("bins-listed", binsListed, MustNotBeEmpty: true),
            new("bins-that-would-not-be-listed", binsThatWouldNotBeListed, MustNotBeEmpty: false),
            new("records-read", recordsRead, MustNotBeEmpty: false),
            new("records-too-young-to-offer", recordsTooYoung, MustNotBeEmpty: false),
            new("records-without-their-data", recordsWithoutTheirData, MustNotBeEmpty: false),
            new("records-that-would-not-be-read", recordsThatWouldNotBeRead, MustNotBeEmpty: false),
            new("pairs-that-would-not-be-read", pairsThatWouldNotBeRead, MustNotBeEmpty: false),
            new("entries-without-a-record", entriesWithoutARecord, MustNotBeEmpty: false)
        };

        FileLog.Write(
            $"[RecycleBinRule] Examine done: found={binsFound}, listed={binsListed}, refused={binsThatWouldNotBeListed}, " +
            $"records={recordsRead}, tooYoung={recordsTooYoung}, withoutData={recordsWithoutTheirData}, " +
            $"unrecorded={entriesWithoutARecord}, offered={candidates.Count}");
        return new RuleAnswer(controls, candidates);
    }

    // The identity an entry carries after its prefix, up to the first full stop: the part a record
    // and its data share.
    private static string IdentityOf(string name)
    {
        var withoutPrefix = name.Length > 2 ? name[2..] : string.Empty;
        var dot = withoutPrefix.IndexOf('.');
        return dot < 0 ? withoutPrefix : withoutPrefix[..dot];
    }

    // Read one deletion record. Null when the record will not read or does not hold what this
    // system's records hold, because a record this rule cannot prove is a record it declines.
    private static DeletionRecord? ReadRecord(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        if (bytes.Length < 24) return null;

        var version = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        try
        {
            var deletedAt = DateTimeOffset.FromFileTime(
                BinaryPrimitives.ReadInt64LittleEndian(bytes[16..])).ToUniversalTime();

            if (deletedAt < DateTimeOffset.UnixEpoch - TimeSpan.FromDays(1)) return null;

            return version is 1 or 2
                ? new DeletionRecord(deletedAt)
                : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // A file time outside the range the system understands is not a deletion moment this
            // rule can judge, so the record is declined rather than guessed at.
            return null;
        }
    }

    // Measure the data of one deleted item. Null when the data will not be read, because a size
    // short by an unknown amount is not a measurement.
    private static long? MeasureData(FileSystemInfo data)
    {
        if (data is FileInfo file) return file.Length;

        var measured = FolderMeasurer.Measure(data.FullName);
        return measured.UnreadableFolders > 0 ? null : measured.Bytes;
    }

    private sealed record DeletionRecord(DateTimeOffset DeletedAtUtc);
}
