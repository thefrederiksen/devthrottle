using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Reclaim.Indexing;

namespace CcDirector.Reclaim.Background;

/// <summary>
/// The record of one background scan, exactly as it is written to disk.
///
/// Like the saved scan, it is a contract between two programs that never run at the same time - the
/// Launcher writes it, the Director reads it - so it carries its own name and version.
/// </summary>
public sealed record BackgroundScanRecord
{
    /// <summary>What this file is.</summary>
    public const string FormatName = "cc-cleanup-storage-background-scan";

    /// <summary>The version this library writes and the only one it reads.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>The state word for a scan that has started and not yet ended.</summary>
    public const string RunningWord = "running";

    /// <summary>The state word for a scan that finished with a result.</summary>
    public const string CompletedWord = "completed";

    /// <summary>The state word for a scan that ended without one.</summary>
    public const string FailedWord = "failed";

    /// <summary>The format name, always <see cref="FormatName"/>.</summary>
    public required string Format { get; init; }

    /// <summary>The format version.</summary>
    public required int FormatVersion { get; init; }

    /// <summary>The folder the scan is of.</summary>
    public required string RootPath { get; init; }

    /// <summary>One of the three state words above. "Never run" is the absence of this file.</summary>
    public required string State { get; init; }

    /// <summary>When the scan started.</summary>
    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>When the scan ended, or null while it runs.</summary>
    public DateTimeOffset? FinishedUtc { get; init; }

    /// <summary>The process that ran the scan.</summary>
    public required int HolderProcessId { get; init; }

    /// <summary>
    /// When that process started. A process number alone is not an identity - the operating system
    /// hands the same number to a later process - so the two together are what "still alive" is
    /// asked of.
    /// </summary>
    public required DateTimeOffset HolderStartedUtc { get; init; }

    /// <summary>Why the scan failed, or null when it did not.</summary>
    public string? FailureReason { get; init; }

    /// <summary>When a scan of this folder last finished with a result, carried from record to record.</summary>
    public DateTimeOffset? LastCompletedUtc { get; init; }

    /// <summary>The saved scan the last completed scan wrote.</summary>
    public string? IndexPath { get; init; }

    /// <summary>The saved recommendations the last completed scan wrote.</summary>
    public string? RecommendationPath { get; init; }
}

/// <summary>
/// Reads and writes the state of the background scan, and decides what a record on the disk means.
/// </summary>
public static class BackgroundScanStatusStore
{
    private static readonly JsonSerializerOptions JsonFormat = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Where everything the background scan writes lives: one folder for the whole machine, the
    /// parent of <see cref="ScanIndexStore.DefaultIndexDirectory"/>.
    /// </summary>
    public static string DefaultStoreDirectory() => Path.Combine(CcStorage.MachineRoot(), "reclaim");

    /// <summary>The folder saved scans live in, inside a store folder.</summary>
    /// <param name="storeDirectory">The store folder.</param>
    public static string IndexDirectory(string storeDirectory) => Path.Combine(storeDirectory, "index");

    /// <summary>The folder saved recommendations live in, inside a store folder.</summary>
    /// <param name="storeDirectory">The store folder.</param>
    public static string RecommendationDirectory(string storeDirectory) => Path.Combine(storeDirectory, "recommendations");

    /// <summary>The folder background scan records live in, inside a store folder.</summary>
    /// <param name="storeDirectory">The store folder.</param>
    public static string StatusDirectory(string storeDirectory) => Path.Combine(storeDirectory, "background");

    /// <summary>
    /// The file one folder's record is written to. Named by the same rule as a saved scan, so one
    /// folder spelled two ways is one record.
    /// </summary>
    /// <param name="storeDirectory">The store folder.</param>
    /// <param name="rootPath">The folder the scan is of.</param>
    public static string PathFor(string storeDirectory, string rootPath) =>
        ScanIndexStore.PathFor(StatusDirectory(storeDirectory), rootPath);

    /// <summary>
    /// Where the background scan of one folder stands.
    ///
    /// Three answers here are decided rather than read. No record at all is "never run". A record
    /// that cannot be read is "failed", never "never run", because something WAS written there. And
    /// a record that says "running" whose process is gone is "failed": the scan was cut short, and a
    /// reader told "running" would wait for ever for a result that is not coming.
    /// </summary>
    /// <param name="storeDirectory">The store folder.</param>
    /// <param name="rootPath">The folder the scan is of.</param>
    public static BackgroundScanStatus Read(string storeDirectory, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory))
            throw new ArgumentException("A store directory cannot be blank.", nameof(storeDirectory));

        var recordPath = PathFor(storeDirectory, rootPath);
        var canonicalRoot = Scanning.DirectoryScanner.Canonical(rootPath);
        FileLog.Write($"[BackgroundScanStatusStore] Read: root={canonicalRoot}, record={recordPath}");

        if (!File.Exists(recordPath))
        {
            FileLog.Write($"[BackgroundScanStatusStore] Read done: root={canonicalRoot}, state=never-run");
            return Finish(new BackgroundScanStatus
            {
                RootPath = canonicalRoot,
                State = BackgroundScanState.NeverRun,
                StartedUtc = null,
                FinishedUtc = null,
                FailureReason = null,
                LastCompletedUtc = null,
                IndexPath = null,
                RecommendationPath = null,
                Lines = []
            });
        }

        var (record, unreadableReason) = ReadRecord(recordPath);
        if (record is null)
        {
            FileLog.Write($"[BackgroundScanStatusStore] Read done: root={canonicalRoot}, state=failed, reason={unreadableReason}");
            return Finish(new BackgroundScanStatus
            {
                RootPath = canonicalRoot,
                State = BackgroundScanState.Failed,
                StartedUtc = null,
                FinishedUtc = null,
                FailureReason = unreadableReason,
                LastCompletedUtc = null,
                IndexPath = null,
                RecommendationPath = null,
                Lines = []
            });
        }

        var state = record.State switch
        {
            BackgroundScanRecord.RunningWord => BackgroundScanState.Running,
            BackgroundScanRecord.CompletedWord => BackgroundScanState.Completed,
            BackgroundScanRecord.FailedWord => BackgroundScanState.Failed,
            _ => (BackgroundScanState?)null
        };

        var failureReason = record.FailureReason;

        if (state is null)
        {
            state = BackgroundScanState.Failed;
            failureReason = $"the record at {recordPath} names a state this version does not know: {record.State}";
        }
        else if (state == BackgroundScanState.Running && !HolderIsAlive(record.HolderProcessId, record.HolderStartedUtc))
        {
            state = BackgroundScanState.Failed;
            failureReason =
                "the scan was cut short: the process that was running it, number " +
                $"{record.HolderProcessId.ToString(CultureInfo.InvariantCulture)}, is no longer running, " +
                "and it never recorded an end";
        }
        else if (state == BackgroundScanState.Failed && string.IsNullOrWhiteSpace(failureReason))
        {
            failureReason = "the scan recorded that it failed and did not record why";
        }

        FileLog.Write($"[BackgroundScanStatusStore] Read done: root={canonicalRoot}, state={state}");
        return Finish(new BackgroundScanStatus
        {
            RootPath = record.RootPath,
            State = state.Value,
            StartedUtc = record.StartedUtc,
            FinishedUtc = record.FinishedUtc,
            FailureReason = state ==BackgroundScanState.Failed ? failureReason : null,
            LastCompletedUtc = record.LastCompletedUtc,
            IndexPath = record.IndexPath,
            RecommendationPath = record.RecommendationPath,
            Lines = []
        });
    }

    /// <summary>Record that a scan has started, keeping what is known of the last one that finished.</summary>
    /// <param name="storeDirectory">The store folder.</param>
    /// <param name="rootPath">The folder the scan is of, in canonical form.</param>
    /// <param name="startedUtc">When it started.</param>
    /// <param name="previous">The status read just before, whose last result is carried forward.</param>
    internal static BackgroundScanRecord WriteRunning(
        string storeDirectory, string rootPath, DateTimeOffset startedUtc, BackgroundScanStatus previous)
    {
        using var self = Process.GetCurrentProcess();
        var record = new BackgroundScanRecord
        {
            Format = BackgroundScanRecord.FormatName,
            FormatVersion = BackgroundScanRecord.CurrentFormatVersion,
            RootPath = rootPath,
            State = BackgroundScanRecord.RunningWord,
            StartedUtc = startedUtc,
            HolderProcessId = self.Id,
            HolderStartedUtc = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero),
            LastCompletedUtc = previous.LastCompletedUtc,
            IndexPath = previous.IndexPath,
            RecommendationPath = previous.RecommendationPath
        };
        Write(storeDirectory, record);
        return record;
    }

    /// <summary>Record that the running scan finished with a result.</summary>
    internal static void WriteCompleted(
        string storeDirectory, BackgroundScanRecord running, DateTimeOffset finishedUtc,
        string indexPath, string recommendationPath) =>
        Write(storeDirectory, running with
        {
            State = BackgroundScanRecord.CompletedWord,
            FinishedUtc = finishedUtc,
            FailureReason = null,
            LastCompletedUtc = finishedUtc,
            IndexPath = indexPath,
            RecommendationPath = recommendationPath
        });

    /// <summary>Record that the running scan ended without a result, and why.</summary>
    internal static void WriteFailed(
        string storeDirectory, BackgroundScanRecord running, DateTimeOffset finishedUtc, string reason) =>
        Write(storeDirectory, running with
        {
            State = BackgroundScanRecord.FailedWord,
            FinishedUtc = finishedUtc,
            FailureReason = reason
        });

    private static void Write(string storeDirectory, BackgroundScanRecord record)
    {
        var path = PathFor(storeDirectory, record.RootPath);
        WholeFileWriter.Write(path, JsonSerializer.Serialize(record, JsonFormat));
        FileLog.Write($"[BackgroundScanStatusStore] Write: root={record.RootPath}, state={record.State}, record={path}");
    }

    // A record that cannot be read comes back as a reason, never as an exception and never as a
    // missing record: something was written there, and saying "never run" about it would be the one
    // wrong answer.
    private static (BackgroundScanRecord? Record, string? Reason) ReadRecord(string recordPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(recordPath);
        }
        catch (IOException ex)
        {
            return (null, $"the record at {recordPath} could not be read, code {ex.HResult.ToString(CultureInfo.InvariantCulture)}");
        }

        BackgroundScanRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<BackgroundScanRecord>(text, JsonFormat);
        }
        catch (JsonException)
        {
            return (null, $"the record at {recordPath} is not a background scan record this version can read");
        }

        if (record is null)
            return (null, $"the record at {recordPath} holds nothing");

        if (!string.Equals(record.Format, BackgroundScanRecord.FormatName, StringComparison.Ordinal) ||
            record.FormatVersion != BackgroundScanRecord.CurrentFormatVersion)
        {
            return (null,
                $"the record at {recordPath} says it is {record.Format} version " +
                $"{record.FormatVersion.ToString(CultureInfo.InvariantCulture)}, and this version reads " +
                $"{BackgroundScanRecord.FormatName} version " +
                $"{BackgroundScanRecord.CurrentFormatVersion.ToString(CultureInfo.InvariantCulture)} only");
        }

        return (record, null);
    }

    // Is the process that wrote "running" still the one running? The number must name a live process
    // AND that process must have started when the record says its holder did, because the operating
    // system reuses numbers. A process that exists but will not say when it started cannot be shown
    // to be somebody else, so the record's own word stands.
    private static bool HolderIsAlive(int processId, DateTimeOffset holderStartedUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;

            var startedUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return (startedUtc - holderStartedUtc).Duration() < TimeSpan.FromSeconds(2);
        }
        catch (ArgumentException)
        {
            return false; // no process has that number
        }
        catch (InvalidOperationException)
        {
            return false; // it exited between the two questions
        }
        catch (Win32Exception ex)
        {
            FileLog.Write($"[BackgroundScanStatusStore] HolderIsAlive: process {processId} exists and would not say when it started: {ex.Message}");
            return true;
        }
    }

    private static BackgroundScanStatus Finish(BackgroundScanStatus status) =>
        status with { Lines = BackgroundScanStatus.Describe(status) };
}
