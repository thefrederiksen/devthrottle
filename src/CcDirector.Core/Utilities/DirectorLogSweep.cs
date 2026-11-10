namespace CcDirector.Core.Utilities;

/// <summary>One file in the log directory, as the sweep needs to judge it.</summary>
/// <param name="Name">The file name alone, never a path.</param>
/// <param name="LastWriteUtc">When it was last written.</param>
/// <param name="Bytes">Its size, reported so the sweep can say what it reclaimed.</param>
public sealed record LogFileFact(string Name, DateTime LastWriteUtc, long Bytes);

/// <summary>What a sweep decided, before anything is deleted.</summary>
/// <param name="Delete">The file names positively proven disposable. Everything else survives.</param>
/// <param name="BytesToReclaim">The total size of <paramref name="Delete"/>.</param>
/// <param name="Kept">How many files the sweep left alone, for the one line it logs.</param>
public sealed record LogSweepPlan(IReadOnlyList<string> Delete, long BytesToReclaim, int Kept);

/// <summary>
/// Removes the Director's own OLD process logs, so the log folder stops growing for ever (issue
/// #4853: 409 MB in 85 files on devthrottle-mac-mini, the oldest three weeks old, and nothing had
/// ever removed one). One file is written per Director process, so a machine that restarts its
/// Director a few times a day adds a file each time, in
/// <c>~/Library/Application Support/cc-director/.../logs/director</c> - a path nobody thinks to look
/// in, which is why it went unnoticed until the disk figure started being reported.
///
/// <para>IT IS AN ALLOW-LIST, DELIBERATELY. A destructive sweep acts only on what it can positively
/// prove is disposable, because a false delete is unrecoverable while a false keep is merely disk.
/// So <see cref="Plan"/> ENUMERATES WHAT TO DELETE and never what to skip: a file is removed only
/// when its name is exactly a Director process log (<c>{prefix}-yyyy-MM-dd-{instance}.log</c>, with a
/// date that really parses), it is not the file this process is writing, and it has not been written
/// for longer than the keep window. Anything the rule cannot classify - a subdirectory, another
/// tool's file, a half-written name, a date that does not parse - SURVIVES. That survival is the
/// boundary working, not the boundary leaking.</para>
///
/// <para>WHY THIS IS NOT <c>HostedLogRetention</c>, which bounds the hosted Gateway's log folder and
/// was merged days before this. The two look alike and the policies are deliberately different. That
/// one considers EVERY <c>*.log</c> in its folder and protects the live file only by an hour-wide
/// last-write window, which is right for a container's own private share where every file in the
/// folder belongs to it. This folder is on a person's machine and is shared - a self-hosted Gateway on
/// the same machine logs into THIS folder too, because only the launcher calls UseLogDirectory (the
/// launcher itself writes to a sibling folder, logs/launcher, which nothing sweeps) - so this one admits only
/// names it can positively prove are spent process logs, and protects the live file BY NAME rather
/// than by age. Reusing the hosted policy here would make the Director's sweep less safe, not more.
/// Carrying two retention policies is still a cost, and it is filed rather than glossed.</para>
///
/// <para>SCOPE, stated rather than implied: any <c>{prefix}-yyyy-MM-dd-{instance}.log</c> in this
/// folder is a candidate, which includes files written by OTHER Director slots and by a self-hosted
/// Gateway on this machine. That is intended - those are spent process logs of the same shape, and
/// age plus the live-file exclusion is what makes removing them safe. It does NOT cover
/// <c>logs/launcher</c>, which no process sweeps today.</para>
///
/// <para>THE SAFETY SIGNAL IS REFRESHED BY THE THING THAT MAKES A FILE LIVE. Age is judged on last
/// write, and a log file is live exactly when something is writing to it - so every successful
/// operation on a live file touches the signal that protects it. That is the property a sweep needs
/// and the one an upload-directory sweep once lacked, where a successful resume wrote nothing and an
/// in-use directory aged out. Here there is no such gap: a Director that is running is logging, and
/// a file inside the window is never a candidate however old its name's date is.</para>
/// </summary>
public static class DirectorLogSweep
{
    /// <summary>
    /// How long a log is kept after its last write. Long enough to investigate a stop days later -
    /// the whole reason these files exist - and short enough to bound the folder. A Director that
    /// stopped without shutting down is usually looked at the same day or the next; two weeks is
    /// several times that.
    /// </summary>
    public static readonly TimeSpan DefaultKeepWindow = TimeSpan.FromDays(14);

    /// <summary>
    /// True when <paramref name="fileName"/> is EXACTLY a process log of <paramref name="filePrefix"/>:
    /// <c>{prefix}-yyyy-MM-dd-{instance}.log</c>, where the date parses as a real date and the instance
    /// discriminator (a process id, or a hex token in a container) is non-empty and alphanumeric.
    ///
    /// <para>This is the canonical-identifier check the sweep's safety rests on. It is written as a
    /// positive match so that a name nobody anticipated cannot pass it.</para>
    /// </summary>
    public static bool IsProcessLogName(string? fileName, string filePrefix)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(filePrefix))
            return false;
        if (!fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            return false;

        var stem = fileName[..^4];
        var head = filePrefix + "-";
        if (!stem.StartsWith(head, StringComparison.Ordinal))
            return false;

        // {yyyy}-{MM}-{dd}-{instance}
        var rest = stem[head.Length..];
        var parts = rest.Split('-');
        if (parts.Length != 4)
            return false;

        if (parts[0].Length != 4 || parts[1].Length != 2 || parts[2].Length != 2)
            return false;
        if (!int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month) || !int.TryParse(parts[2], out var day))
            return false;
        // A date that does not exist is not a name we wrote, so it is not ours to delete.
        if (year < 1 || year > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;

        var instance = parts[3];
        return instance.Length > 0 && instance.All(char.IsLetterOrDigit);
    }

    /// <summary>
    /// Decide what to delete. Pure: no clock, no disk, no process - so the boundary is testable on its
    /// own. <paramref name="liveFileName"/> is the file this process is writing and is never a
    /// candidate, whatever its age.
    /// </summary>
    public static LogSweepPlan Plan(
        IEnumerable<LogFileFact> files,
        string filePrefix,
        string? liveFileName,
        DateTime nowUtc,
        TimeSpan keepWindow)
    {
        ArgumentNullException.ThrowIfNull(files);

        var horizon = nowUtc - keepWindow;
        var delete = new List<string>();
        long bytes = 0;
        var kept = 0;

        foreach (var f in files)
        {
            var disposable =
                IsProcessLogName(f.Name, filePrefix)
                && !string.Equals(f.Name, liveFileName, StringComparison.OrdinalIgnoreCase)
                && f.LastWriteUtc < horizon;

            if (disposable)
            {
                delete.Add(f.Name);
                bytes += Math.Max(0, f.Bytes);
            }
            else
            {
                kept++;
            }
        }

        return new LogSweepPlan(delete, bytes, kept);
    }

    /// <summary>
    /// Sweep <paramref name="logDirectory"/> and log one line saying what went. Best effort: this runs
    /// at start-up and housekeeping must never be the reason a Director fails to start, so a directory
    /// that cannot be read or a file that cannot be deleted is reported and the start continues. A
    /// file that cannot be deleted is simply kept - the next start tries again.
    /// </summary>
    /// <returns>The plan that was carried out, for the caller's own reporting and for tests.</returns>
    public static LogSweepPlan Run(
        string logDirectory,
        string filePrefix,
        string? liveFilePath,
        DateTime nowUtc,
        TimeSpan? keepWindow = null)
    {
        var window = keepWindow ?? DefaultKeepWindow;
        FileLog.Write($"[DirectorLogSweep] Run: dir={logDirectory}, prefix={filePrefix}, keep={window.TotalDays:0.#} days");

        var empty = new LogSweepPlan(Array.Empty<string>(), 0, 0);
        try
        {
            if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
            {
                FileLog.Write($"[DirectorLogSweep] nothing to sweep - {logDirectory} does not exist");
                return empty;
            }

            // TOP LEVEL ONLY, and files only. A subdirectory is never a process log, so it is never a
            // candidate and is not descended into.
            var facts = new List<LogFileFact>();
            foreach (var path in Directory.EnumerateFiles(logDirectory))
            {
                FileInfo info;
                try { info = new FileInfo(path); if (!info.Exists) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file the sweep cannot read is one it cannot prove is spent, so it survives -
                    // the allow-list working rather than a fault.
                    // not-an-error: the file is KEPT, so nothing is lost and nothing is wrong
                    FileLog.Write($"[DirectorLogSweep] could not read {path} - kept: {ex.Message}");
                    continue;
                }
                facts.Add(new LogFileFact(info.Name, info.LastWriteTimeUtc, info.Length));
            }

            var liveName = string.IsNullOrWhiteSpace(liveFilePath) ? null : Path.GetFileName(liveFilePath);
            var plan = Plan(facts, filePrefix, liveName, nowUtc, window);

            if (plan.Delete.Count == 0)
            {
                FileLog.Write($"[DirectorLogSweep] removed 0 logs; kept {plan.Kept}");
                return plan;
            }

            var removed = new List<string>();
            long reclaimed = 0;
            foreach (var name in plan.Delete)
            {
                var full = Path.Combine(logDirectory, name);
                try
                {
                    var size = new FileInfo(full).Length;
                    File.Delete(full);
                    removed.Add(name);
                    reclaimed += size;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // REPORTED AS A FAULT, deliberately (review). A single failure costs nothing - the
                    // file is kept and the next start tries again. But this recurring every start is the
                    // bound this class exists to provide NOT working, and the folder growing without
                    // limit again, which is invisible from anywhere else. So it reaches the Gateway.
                    FileLog.Write($"[DirectorLogSweep] Run FAILED to delete {name} - kept, will retry next start: {ex.Message}");
                }
            }

            FileLog.Write(
                $"[DirectorLogSweep] removed {removed.Count} log(s) older than {window.TotalDays:0.#} days, " +
                $"reclaimed {reclaimed / 1024 / 1024} MB; kept {plan.Kept}");
            return plan with { Delete = removed, BytesToReclaim = reclaimed };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // This is an entry point - it runs on the Director's start-up path, and housekeeping must
            // never be the reason a Director fails to start. Narrowed to the two faults a filesystem
            // actually raises here, so a defect in this class's own logic still throws and is seen.
            FileLog.Write($"[DirectorLogSweep] Run FAILED: {ex.Message}");
            return empty;
        }
    }
}
