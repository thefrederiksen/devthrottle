using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// READING ONE OF THESE FILES MUST NOT COST A ROW, AND IT USED TO. The append opened the file in a
/// way that denied concurrent readers, and every ordinary reader opens in a way that denies
/// concurrent writers, so whichever lost the race failed. The reader's failure was loud; the
/// writer's was swallowed, and the row was gone for good because the check that produced it had
/// already been taken off the books. See <see cref="AppendContentionBudget"/>.
///
/// THE LOCK IS PROCESS-WIDE AND THAT IS ENOUGH, for a reason worth writing down because an
/// inspection correctly pointed out that a process-wide lock does not exclude a second process. Two
/// Director processes never share this directory at all. Every path here is resolved through
/// <c>CcStorage</c>, whose root is the CC_DIRECTOR_ROOT environment variable whenever that variable
/// is set, and it is set PER DIRECTOR INSTANCE. Checked rather than assumed on 15 September 2026:
/// five instance roots existed side by side under the per-user data directory
/// (default, devthrottledemo, slot-1, slot-5, slot-7), the running session's own variable read the
/// first of them, and CcStorage.TurnDetectionShadow composes this directory under that root. So
/// each Director has its OWN shadow directory and there is no shared file for two processes to race
/// over. The per-session filename is a SECOND, independent reason - a session belongs to exactly one
/// Director process - rather than the only one.
///
/// Neither of those is a claim about the lock. The lock serialises the threads inside ONE process,
/// which is all it is claimed to do; the file comment used to imply it covered more.
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
    ///
    /// STATED HONESTLY, BECAUSE THE ALGORITHM IS NOT TIGHTER THAN THIS. The size is tested BEFORE
    /// the next record is appended, so a live file may reach this bound PLUS AT MOST ONE RECORD, and
    /// a file rolled at that moment preserves the same overshoot. The cost per session is therefore
    /// twice this plus at most two records, not twice this exactly. The alternative - serialise the
    /// record, measure it, then decide - buys a tighter number nothing needs at the price of a second
    /// measurement that can disagree with the first. One record is roughly four hundred bytes against
    /// a four megabyte bound.
    ///
    /// The oldest rows are the ones that go, because the interesting rows are the recent ones. At
    /// roughly four hundred bytes a row this is about ten thousand checks.
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

    /// <summary>
    /// HOW LONG AN APPEND KEEPS TRYING WHEN SOMETHING ELSE HAS THE FILE OPEN.
    ///
    /// These files exist to be READ - that is the whole reason the verdicts are written locally
    /// rather than to the hosted Gateway. And reading one used to cost a row, because the ordinary
    /// way to read a file on Windows (File.ReadAllLines, Get-Content, a scoring script, a backup or
    /// antivirus scan) opens it in a way that permits other readers and DENIES writers. The append
    /// then failed, and that failure is swallowed - so the act of looking at the measurement
    /// silently thinned it, with nothing on screen to say so.
    ///
    /// Observed rather than reasoned about: on 15 September 2026 the full Core suite failed twice on
    /// exactly this, once in each direction. The reader that lost the race threw the sharing
    /// violation outright; the writer that lost it dropped a shadow row nothing ever wrote again,
    /// because the check that produced it had already been taken off the books.
    ///
    /// WHAT THIS DOES NOT FIX, said plainly: the suite failure itself was the TEST's reader denying
    /// this writer, and it is fixed in the test by reading in a way that permits a writer. This
    /// budget is for the readers nobody controls - the owner running Get-Content over a shadow file,
    /// a scoring script, a scanner - where the only symptom is a measurement that is quietly a few
    /// rows short.
    ///
    /// A reader's hold is brief, so a short wait covers it. It is BOUNDED because it runs under the
    /// process-wide lock: an unbounded wait would stall every other session's append behind one
    /// stuck handle. Past the bound the row is still lost and still logged - which is stated here
    /// rather than wished away.
    /// </summary>
    internal static TimeSpan AppendContentionBudget { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long to wait between attempts inside <see cref="AppendContentionBudget"/>.</summary>
    internal static TimeSpan AppendRetryPause { get; set; } = TimeSpan.FromMilliseconds(15);

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

                // A FAILED ROLLOVER MUST NEVER COST THE ROW. Rotation used to sit inside the outer
                // try, so a File.Move that threw skipped the append entirely and the observation was
                // gone - visible only in FileLog, never in the shadow file the whole comparison is
                // read from. An oversized file is a far smaller harm than a missing observation, so
                // the append goes ahead regardless and the failure is said out loud.
                try
                {
                    RotateIfOversized(path);
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[TurnDetectionShadowLog] rollover failed for {Path.GetFileName(path)}: {ex.Message}; appending to the oversized file rather than losing the row");
                }

                AppendLine(path, JsonSerializer.Serialize(record, Json) + "\n");
                SweepAgedFiles();
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnDetectionShadowLog] append failed for {sessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// Put one line on the end of the file, in a way that neither locks a reader out nor loses the
    /// row to one.
    ///
    /// THE RETRY IS THE WHOLE FIX, and the share mode deliberately is NOT changed. Widening what WE
    /// permit looks like half the answer and is none of it: a reader that opened with
    /// FileShare.Read denies our write whatever we ask for, and our own share flags cannot reach
    /// that. Measured rather than assumed - a guard written to pin a widened share mode passed
    /// identically with the old append and was deleted rather than kept as decoration. So the open
    /// stays byte for byte what File.AppendAllText did, and the retry is what survives a reader.
    ///
    /// The byte-order mark goes on only when the file is empty, which is exactly what
    /// File.AppendAllText did before this, so the file on disk is unchanged in shape.
    ///
    /// A reader that permits writers may see a line that is still being written - that was already
    /// true of File.AppendAllText and is not changed here. It is handled where it belongs: a reader
    /// counts only newline-terminated lines, so a half-written last row is not a row yet.
    /// </summary>
    private static void AppendLine(string path, string line)
    {
        var payload = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(line);
        var deadline = DateTime.UtcNow + AppendContentionBudget;
        while (true)
        {
            try
            {
                using var stream = new FileStream(
                    path, FileMode.Append, FileAccess.Write, FileShare.Read);
                if (stream.Position == 0)
                {
                    var preamble = Encoding.UTF8.GetPreamble();
                    stream.Write(preamble, 0, preamble.Length);
                }
                stream.Write(payload, 0, payload.Length);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Somebody has it open and does not permit writers. Their hold is brief; ours is
                // bounded. Past the deadline the exception escapes to Append, which logs it.
                Thread.Sleep(AppendRetryPause);
            }
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
    /// The two file-name shapes this log writes, and the ONLY two the sweep will ever delete: a
    /// thirty-two character lower-case hexadecimal session id, optionally followed by the rolled
    /// predecessor's <c>.1</c>, then <c>.jsonl</c>. Anchored at both ends and case-sensitive,
    /// because what is being asked is not "does this look like one of ours" but "is this exactly a
    /// name this writer produces".
    /// </summary>
    private static readonly Regex OwnedFileName =
        new(@"^[0-9a-f]{32}(\.1)?\.jsonl$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True when this file name is one this log can prove it wrote. <see cref="Append"/> composes
    /// the live name as <c>sessionId.ToString("N") + ".jsonl"</c> and
    /// <see cref="RotateIfOversized"/> composes the predecessor by inserting <c>.1</c>, so those two
    /// shapes are the complete inventory of what this type creates.
    /// </summary>
    internal static bool IsOwnedFileName(string fileName) => OwnedFileName.IsMatch(fileName);

    /// <summary>
    /// Delete the files nothing has written to for <see cref="MaxFileAgeDays"/>.
    ///
    /// IT ENUMERATES WHAT TO DELETE AND NEVER WHAT TO SKIP. It used to delete on age alone from
    /// every <c>*.jsonl</c> in the directory, and an inspection demonstrated the consequence: it
    /// dropped an aged <c>owner-notes.jsonl</c> into the shadow directory and the very next append
    /// deleted it. An extension is a DENY boundary - it rules some things out - and a deny boundary
    /// is not proof of ownership. The test now is positive: the name must be exactly a shape this
    /// writer produces (see <see cref="OwnedFileName"/>), the directory must be this one, and the
    /// last write time - read now, not cached - must be past the bound.
    ///
    /// ANYTHING IT DOES NOT RECOGNISE IS LEFT ALONE AND SAID SO, ONCE. Silence would make a
    /// directory quietly filling with files this log will never touch indistinguishable from a
    /// directory doing exactly what it should; one line per sweep says which. A file it cannot
    /// delete is reported with its path and tried again next time, because a bound nobody can see
    /// fail is a bound nobody checks.
    /// </summary>
    private static void SweepAgedFiles()
    {
        var now = DateTime.UtcNow;
        if (now - _lastSweepUtc < SweepInterval) return;
        _lastSweepUtc = now;

        var cutoff = now - TimeSpan.FromDays(MaxFileAgeDays);
        int strangers = 0;
        foreach (var file in Directory.EnumerateFiles(Root))
        {
            var name = Path.GetFileName(file);
            if (!IsOwnedFileName(name))
            {
                strangers++;
                continue;
            }

            if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
            try
            {
                File.Delete(file);
                FileLog.Write($"[TurnDetectionShadowLog] retention: deleted {name}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[TurnDetectionShadowLog] retention: could not delete {file}: {ex.Message}");
            }
        }

        if (strangers > 0)
            FileLog.Write($"[TurnDetectionShadowLog] retention: left {strangers} file(s) in {Root} alone - this log did not write them and does not delete them");
    }
}
