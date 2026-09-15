using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Wingman;

/// <summary>
/// What the content rule WOULD have decided, written down so the decision can be argued from
/// numbers rather than from impressions. One line of JSON per check, one file per session, at
/// <c>%LOCALAPPDATA%/cc-director/turn-detection-shadow/&lt;sessionId&gt;.jsonl</c>.
///
/// IT IS LOCAL ON PURPOSE. The obvious place for a shadow verdict is the activity ledger, and the
/// activity ledger is the wrong place: its rows live on the hosted Gateway, where a session key is
/// refused, so a verdict written only there is a number nobody on the machine that produced it can
/// read. The comparison this exists for has to be answerable on the owner's own Director.
///
/// ONE ROW PER CHECK, NEVER PER BYTE. A check is scheduled once per burst and pushed out by later
/// bytes inside its window, so a chattering agent costs one row per burst rather than one per
/// write. Each row carries both candidates' verdicts side by side and what the old byte rule would
/// have done, which is the whole point: a row with the byte rule opening and both candidates
/// holding is a phantom turn that would have been suppressed.
///
/// THE RULE SWITCH DEFAULTS OFF AND THIS LOG DEFAULTS ON. That pairing is deliberate - the owner's
/// Director produces the comparison numbers while behaving exactly as it does today.
///
/// Write-only and infrequent, so a single process-wide lock is plenty, and the append happens AFTER
/// the state decision it observes - so it cannot delay that decision, whatever the filesystem does.
/// That is a narrower claim than the one this file used to make, and it is the one the code
/// supports: a synchronous write under a shared lock has no latency bound, so "cannot affect the
/// session" was never true of an append that ran first. Append failures are logged and swallowed.
///
/// IT IS BOUNDED ON DISK. A log with no bound, on a machine running a fleet all day, is a disk leak;
/// the two numbers that bound it are <see cref="DefaultMaxFileBytes"/> and
/// <see cref="DefaultMaxFileAgeDays"/>, and they live here rather than scattered across call sites.
/// </summary>
public static class TurnDetectionShadowLog
{
    /// <summary>Set this to <c>0</c> to stop writing the shadow verdicts.</summary>
    public const string EnabledVariable = "CC_DIRECTOR_TURN_SHADOW";

    /// <summary>
    /// THE RETENTION BOUND, PART ONE: how large one session's file may get before it is rolled over.
    /// A session therefore costs at most twice this on disk - the live file and one rolled
    /// predecessor - and the oldest rows are the ones that go, because the interesting rows are the
    /// recent ones. At roughly four hundred bytes a row this is about ten thousand checks.
    /// </summary>
    public const long DefaultMaxFileBytes = 4L * 1024 * 1024;

    /// <summary>
    /// THE RETENTION BOUND, PART TWO: a file nothing has written to for this long is deleted. It is
    /// what stops finished sessions accumulating for ever - the size bound alone only limits each
    /// one, and a fleet creates new sessions all day.
    /// </summary>
    public const int DefaultMaxFileAgeDays = 14;

    /// <summary>How often the age sweep actually runs. Once an hour per process: the bound is in
    /// days, so sweeping more often only costs a directory listing.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromHours(1);

    /// <summary>The live size bound. Settable so a test can drive a rollover without writing four
    /// megabytes.</summary>
    internal static long MaxFileBytes { get; set; } = DefaultMaxFileBytes;

    /// <summary>The live age bound, in days.</summary>
    internal static int MaxFileAgeDays { get; set; } = DefaultMaxFileAgeDays;

    /// <summary>The live sweep interval.</summary>
    internal static TimeSpan SweepInterval { get; set; } = DefaultSweepInterval;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static DateTime _lastSweepUtc = DateTime.MinValue;

    /// <summary>Resolved per access, not a baked static field, so CC_DIRECTOR_ROOT redirects it
    /// under test. A baked field is captured at type load and no test can undo it - which is how
    /// the state-change log's own test came to write into the real running Director's data.</summary>
    private static string Root => CcStorage.TurnDetectionShadow();

    /// <summary>
    /// ON unless the variable says exactly <c>0</c>. A named function rather than an expression
    /// buried in a field initialiser, because "the shadow log ships on" is a claim about the
    /// PRODUCT that has to be provable by a test - and <see cref="Enabled"/> itself is mutable, so
    /// reading it proves nothing about the default.
    /// </summary>
    internal static bool ResolveEnabled(string? value) => value != "0";

    /// <summary>
    /// What this process started with, kept separately because tests move <see cref="Enabled"/>.
    /// </summary>
    internal static bool InitialEnabled { get; } =
        ResolveEnabled(Environment.GetEnvironmentVariable(EnabledVariable));

    /// <summary>
    /// On by default. The rule it observes is off by default, so out of the box a Director behaves
    /// exactly as it does today AND produces the numbers that decide whether to turn the rule on.
    /// </summary>
    public static bool Enabled { get; set; } = InitialEnabled;

    /// <summary>
    /// One check.
    /// </summary>
    /// <param name="T">ISO-8601 UTC timestamp of the check.</param>
    /// <param name="Agent">Which agent's terminal this was, because the marker list is per-agent.</param>
    /// <param name="Mode">How the check was reached: "byte" for an ordinary agent, "body" for one
    /// whose idle terminal never goes byte-silent.</param>
    /// <param name="Rule">Which candidate was AUTHORITATIVE at the time: "off", "row" or "size".</param>
    /// <param name="ByteRuleOpens">What today's rule would have done. Always true at a check, because
    /// a check only happens when a byte arrived at a settled session - which is exactly why the
    /// column is worth writing down rather than assuming.</param>
    /// <param name="RowRuleOpens">What the row candidate decided.</param>
    /// <param name="RowEvidence">The first row the row candidate called new, verbatim, so a wrong
    /// decision can be read back. Null when it decided nothing was gained.</param>
    /// <param name="SizeRuleOpens">What the size candidate decided, at <paramref name="SizeThreshold"/>.</param>
    /// <param name="ChangedCharacters">The size candidate's magnitude, whatever it decided. Recorded
    /// on every row so the threshold can be re-chosen from the log rather than re-run.</param>
    /// <param name="SizeThreshold">The threshold the size verdict was taken at.</param>
    /// <param name="SettledHash">Hash of the settled screen, and</param>
    /// <param name="CurrentHash">hash of the screen at the check - the pair is what matches a row
    /// back to a saved turn-review capture.</param>
    /// <param name="Bytes">How many bytes arrived in the burst that produced this check.</param>
    /// <param name="Submitted">True when a submission inside the window explains this wake, which
    /// makes the row a control rather than a candidate phantom.</param>
    public sealed record Record(
        string T,
        string Agent,
        string Mode,
        string Rule,
        bool ByteRuleOpens,
        bool RowRuleOpens,
        string? RowEvidence,
        bool SizeRuleOpens,
        int ChangedCharacters,
        int SizeThreshold,
        string? SettledHash,
        string? CurrentHash,
        long Bytes,
        bool Submitted);

    public static void Append(Guid sessionId, Record record)
    {
        if (!Enabled || record is null) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Root);
                var path = Path.Combine(Root, sessionId.ToString("N") + ".jsonl");
                RotateIfOversized(path);
                File.AppendAllText(path, JsonSerializer.Serialize(record, Json) + "\n", Encoding.UTF8);
                SweepAgedFiles();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnDetectionShadowLog] append failed for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// One session's file has reached the size bound: move it aside so the live file starts again.
    /// One predecessor is kept, so the bound per session is twice <see cref="MaxFileBytes"/> and a
    /// rollover never throws away rows that have no replacement yet.
    /// </summary>
    private static void RotateIfOversized(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxFileBytes) return;

        var rolled = Path.Combine(
            Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + ".1.jsonl");
        File.Move(path, rolled, overwrite: true);
        FileLog.Write($"[TurnDetectionShadowLog] rolled over {Path.GetFileName(path)} at {info.Length} bytes");
    }

    /// <summary>
    /// Delete the files nothing has written to for <see cref="MaxFileAgeDays"/>. It names what to
    /// DELETE rather than what to keep: only this directory, only the extension this log writes,
    /// and only a file whose last write time - read now, not cached - is past the bound. A file it
    /// cannot delete is reported with its path and tried again next time, because a bound nobody
    /// can see fail is a bound nobody checks.
    /// </summary>
    private static void SweepAgedFiles()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSweepUtc < SweepInterval) return;
        _lastSweepUtc = now;

        var cutoff = now - TimeSpan.FromDays(MaxFileAgeDays);
        foreach (var file in Directory.EnumerateFiles(Root, "*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
            try
            {
                File.Delete(file);
                FileLog.Write($"[TurnDetectionShadowLog] retention: deleted {Path.GetFileName(file)}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TurnDetectionShadowLog] retention: could not delete {file}: {ex.Message}");
            }
        }
    }
}
