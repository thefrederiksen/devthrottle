using System.Globalization;
using System.Text.Json;

namespace ResourceLoadProof;

/// <summary>A resource's level as the Director's meter reports it. The same ladder as Core's ResourceLevel.</summary>
public enum MeterLevel
{
    /// <summary>The Director could not read it. Never treated as Normal.</summary>
    Unknown = 0,

    /// <summary>Room to work.</summary>
    Normal = 1,

    /// <summary>Amber.</summary>
    Tight = 2,

    /// <summary>Red.</summary>
    Critical = 3,
}

/// <summary>One bar of the meter as the Director reported it.</summary>
/// <param name="CouldRead">False when the Director showed "cannot read".</param>
/// <param name="Percent">The figure on the bar, 0 to 100. Null when it could not be read.</param>
/// <param name="Level">The bar's colour.</param>
public sealed record MeterBar(bool CouldRead, double? Percent, MeterLevel Level);

/// <summary>What the Director showed at one moment: the two bars and its diagnosis.</summary>
/// <param name="Cpu">The CPU bar.</param>
/// <param name="Memory">The MEM bar.</param>
/// <param name="CouldDiagnose">False when the Director said it could not tell - which means not known, never fine.</param>
/// <param name="IsSlow">The diagnosis: is the machine slow.</param>
/// <param name="Cause">The diagnosis's cause, as the Director names it, for example "Cpu" or "Cpu, Disk".</param>
/// <param name="Raw">The JSON the figures were read from, kept for the report.</param>
/// <summary>
/// What the recorded window says about one wait. <see cref="Elapsed"/> is measured on the recording's own
/// monotonic timeline; see <see cref="ProofChecks.JudgeWarning"/> for why nothing here is measured on the
/// wall clock or on how long the harness took to look.
/// </summary>
/// <param name="Seen">A sample meeting the condition was recorded at or after the change.</param>
/// <param name="Met">It was seen, within the limit, and the span is a sound measurement.</param>
/// <param name="Elapsed">From the first sample at or after the change to the one that met it.</param>
/// <param name="Sample">The sample that met the condition, or null when none did.</param>
/// <param name="Baseline">The first sample at or after the change, which the elapsed time is measured from.</param>
/// <param name="ClockStepped">The recorder marked a wall-clock step on a line inside the span. The figure is a monotonic measurement of the span it reports, but when the step fell BEFORE the change the span itself can start too early, so the figure can OVERSTATE - see <see cref="ProofChecks.StampCorrectedByItsOwnStep"/> for the bound. Never a second look worth skipping.</param>
/// <param name="DirectorRestarted">The span crosses a Director restart, where the recorder bridges the gap with the WALL clock. The figure is then NOT a monotonic measurement, so it never counts as met.</param>
public sealed record WarningVerdict(
    bool Seen, bool Met, TimeSpan Elapsed, MeterObservation? Sample, MeterObservation? Baseline,
    bool ClockStepped, bool DirectorRestarted)
{
    /// <summary>Not one sample was recorded at or after the change: a stopped or stuck Director.</summary>
    public static readonly WarningVerdict NothingRecorded =
        new(Seen: false, Met: false, TimeSpan.Zero, null, null, ClockStepped: false, DirectorRestarted: false);

    /// <summary>Samples were recorded, none met the condition.</summary>
    public static WarningVerdict NotSeen(MeterObservation baseline) =>
        new(Seen: false, Met: false, TimeSpan.Zero, null, baseline, ClockStepped: false, DirectorRestarted: false);
}

/// <param name="TakenAtUtc">When the Director took the sample, on the WALL clock. Used to SELECT samples and to print them, never to measure an interval - see <see cref="RecordingSeconds"/>.</param>
/// <param name="RecordingSeconds">
/// The sample's position on the RECORDING'S OWN TIMELINE, which the recorder advances with the monotonic
/// clock inside one run. This is what every time limit is measured on: a wall-clock step during a run
/// cannot stretch, shrink or reorder it.
/// </param>
/// <param name="RunId">The Director run the sample belongs to. <see cref="RecordingSeconds"/> only advances monotonically WITHIN one run; across a restart the recorder bridges the gap with the wall clock.</param>
/// <param name="ClockStepSeconds">Non-null when the recorder saw the wall clock move by more than its tolerance ON THIS LINE. The recorder detects a step of about two seconds, so this is the honest clock-step detector.</param>
public sealed record MeterObservation(
    MeterBar Cpu, MeterBar Memory, bool CouldDiagnose, bool IsSlow, string Cause,
    DateTime TakenAtUtc, double RecordingSeconds, string RunId, double? ClockStepSeconds, string Raw);

/// <summary>The operating system's own figure for one resource, and where it came from.</summary>
/// <param name="Percent">The figure, 0 to 100.</param>
/// <param name="Source">"free -b", "vmstat", or "typed by the operator from Task Manager" - so the report says how it was obtained.</param>
public sealed record OsFigure(double Percent, string Source);

/// <summary>
/// Every judgement the harness makes, as pure functions - no clock, no process, no file - so each is pinned
/// by a unit test and the harness's verdicts cannot drift from what the report claims they mean.
/// </summary>
public static class ProofChecks
{
    /// <summary>The meter must agree with the operating system's own monitor within this many percentage points (mission section 3).</summary>
    public const double AgreementPoints = 5.0;

    /// <summary>A warning must show within this long of the load starting (mission section 3).</summary>
    public static readonly TimeSpan WarningDeadline = TimeSpan.FromSeconds(70);

    /// <summary>
    /// How long the harness polls for a recorded sample before giving up: the 70-second limit, plus the recorder's
    /// one-minute write interval (the sample that proves it may be written up to a minute after it was taken), plus
    /// three polls. This budget bounds how long the harness WAITS and decides nothing: the pass is judged
    /// on the recording's own timeline by <see cref="JudgeWarning"/>, so a sample the product recorded in
    /// time still passes however late the harness managed to see it.
    /// </summary>
    public static readonly TimeSpan PollDeadline = TimeSpan.FromSeconds(70 + 60 + 15);

    /// <summary>
    /// WHETHER THE WARNING ARRIVED IN TIME, AND HOW LONG IT TOOK, decided here as a pure function of the
    /// recorded window so that it can be tested. The caller only supplies the window and the condition.
    ///
    /// THE MEASUREMENT IS THE DIFFERENCE OF TWO <c>recordingSeconds</c>, from the first sample taken at or
    /// after the change to the first one that meets the condition. The recorder advances that figure with
    /// the MONOTONIC clock inside a run, so no wall-clock step can stretch, shrink or reorder it, and the
    /// recording's own documentation says the last hour is measured on it for exactly this reason.
    ///
    /// THREE THINGS IT DELIBERATELY IS NOT:
    ///
    /// Not the difference of two <c>takenAtUtc</c> stamps. <c>ResourceSample.TakenAtUtc</c> says on its own
    /// type that comparing two of these is the defect that interface was changed to remove: a time
    /// synchronisation step of ten seconds during a run turns a real 68-second warning into 58 or 78, and
    /// this is the instrument that decides whether the product met a success criterion.
    ///
    /// Not the time the HARNESS took to see it. That was the first attempt and it could not pass a correct
    /// product. The sampler reads every five seconds and the processor only turns Critical once a whole
    /// sustained window is over the limit, so the product's own red arrives 65 to 70 seconds after the load
    /// by construction - the entire budget. Adding the recorder's one-minute write interval and the poll on
    /// top left a correct product passing a few runs in a hundred. An instrument that fails a product which
    /// meets the criterion is worse than one that measures with the wrong clock, because its failures look
    /// like the product's.
    ///
    /// Not clamped at zero or otherwise tidied. If the figures say the qualifying sample came BEFORE the
    /// baseline, something is wrong with the recording and the caller is told, rather than shown a plausible
    /// zero.
    ///
    /// IT OVERSTATES BY AT MOST ONE SAMPLE INTERVAL, which is honest in the safe direction: the baseline is
    /// the first sample at or after the change, so the load may have begun up to five seconds before it.
    /// </summary>
    public static WarningVerdict JudgeWarning(
        IReadOnlyList<MeterObservation> window, DateTime changeUtc, Func<MeterObservation, bool> condition)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(condition);

        // THE RECORDING'S OWN TIMELINE DECIDES ORDER, AND - ONCE THE BASELINE IS KNOWN - MEMBERSHIP TOO.
        // Ordering by the stamp would hand a backward clock step the power to choose which two lines get
        // subtracted: a line stamped earlier sorts first even though the recorder wrote it later, so the
        // baseline could be a line that is not the earliest after the change. The measurement would still
        // be monotonic and the CHOICE of what to measure would not - the same defect one level up, and it
        // produced a false pass in review.
        var onTheTimeline = window.OrderBy(sample => sample.RecordingSeconds).ToList();

        // THE BASELINE IS THE EARLIEST LINE WHOSE RAW STAMP **OR** CORRECTED STAMP IS AT OR AFTER THE
        // CHANGE. Either one being right is enough, because the line cannot say which clock stamped the
        // change: the RAW stamp is the exact comparison when the step happened BEFORE the change, so the
        // change carries the stepped clock too, and the CORRECTED stamp is the exact comparison when the
        // step happened AFTER it, so the change carries the pre-step clock. Taking whichever qualifies is
        // therefore right whenever either is right.
        //
        // Both halves were found by review, each as a false pass. Without the corrected stamp, a BACKWARD
        // step larger than the gap to the next sample pushed that sample out of the selection and put its
        // mark below the scan at the same time, so a 75-second warning measured 65 (arrangement C). Without
        // the raw stamp, a FORWARD step just before the change excluded the one line that should have been
        // the baseline, and the next line became it: 75 seconds measured as 70, inside the limit, with no
        // caveat (probe D). Both are pinned as tests below.
        var baseline = onTheTimeline.FirstOrDefault(
            sample => TakenAfterTheChange(changeUtc, sample.TakenAtUtc)
                || TakenAfterTheChange(changeUtc, StampCorrectedByItsOwnStep(sample)));
        if (baseline is null) return WarningVerdict.NothingRecorded;

        // FROM THE BASELINE ON, THE STAMPS ARE NOT CONSULTED AT ALL. Every line at or after it on the
        // recording's timeline is a member, so a line the step left stamped early still counts, and the
        // qualifying sample is the TRUE first one rather than a later line whose stamp happened to climb
        // back past the change.
        var members = onTheTimeline
            .Where(sample => sample.RecordingSeconds >= baseline.RecordingSeconds)
            .ToList();

        // The condition is never reached when nothing was recorded after the change: a safety check must
        // not live inside it and quietly stop running.
        var met = members.FirstOrDefault(condition);
        if (met is null) return WarningVerdict.NotSeen(baseline);

        // Across a Director restart the recorder bridges the gap with the WALL clock, so a span containing
        // one is not a monotonic measurement at all. A run id is a fresh value per run, so comparing the
        // two ends is equivalent to looking for a run start inside the span.
        var restarted = !string.Equals(baseline.RunId, met.RunId, StringComparison.Ordinal);

        // SCANNED OVER THE MEMBERS, both ends inclusive. Because membership is taken from the timeline and
        // not from the stamps, this is the same set as "every line inside the span", so a line a backward
        // step left stamped before the change - which is the line carrying the mark - is scanned like any
        // other. That equivalence is the point of the membership rule above; the two scans used to differ,
        // and the mark could fall in the gap between them.
        var stepped = members.Any(sample =>
            sample.RecordingSeconds <= met.RecordingSeconds && sample.ClockStepSeconds is not null);

        var elapsed = TimeSpan.FromSeconds(met.RecordingSeconds - baseline.RecordingSeconds);
        return new WarningVerdict(
            Seen: true,
            Met: !restarted && WithinWarningDeadline(elapsed),
            Elapsed: elapsed,
            Sample: met,
            Baseline: baseline,
            ClockStepped: stepped,
            DirectorRestarted: restarted);
    }

    /// <summary>
    /// Whether a measured time met the warning limit. Negative is refused rather than clamped: once the
    /// ordering is taken from <c>recordingSeconds</c> this can only mean the recording itself ran
    /// backwards, which is a broken instrument - and zero would be a believable figure for it.
    /// </summary>
    public static bool WithinWarningDeadline(TimeSpan measured) =>
        measured >= TimeSpan.Zero && measured <= WarningDeadline;

    /// <summary>
    /// Whether a recorded sample belongs to the change at all: taken at or after it. This compares a stamp
    /// for ORDER against a single reference, which is selection, not measurement - the distinction the rest
    /// of this file turns on.
    ///
    /// <see cref="JudgeWarning"/> ASKS IT TWICE FOR THE BASELINE, with the raw stamp and with
    /// <see cref="StampCorrectedByItsOwnStep"/>, and takes the line if either qualifies. Neither stamp is
    /// sufficient alone, and each is exact in the case the other gets wrong: a step after the change needs
    /// the correction, a step before it needs the raw stamp. Asking only one of them UNDERSTATES - by
    /// starting from a later line while hiding the mark that would have said so - and an understatement is
    /// a false pass. It is asked only to pick the baseline; after that, membership comes from the timeline.
    /// </summary>
    public static bool TakenAfterTheChange(DateTime changeUtc, DateTime sampleTakenAtUtc) =>
        sampleTakenAtUtc >= changeUtc;

    /// <summary>
    /// A line's stamp read on the clock the CHANGE was stamped on: its own step mark undone. The recorder
    /// writes <c>clockStepSeconds</c> on the first line at which it saw the wall clock jump, so a backward
    /// step of eight seconds leaves minus eight on that line; subtracting the mark puts the line back where
    /// the pre-step clock would have stamped it. An unmarked line is its own stamp.
    ///
    /// WHAT IT DOES NOT COVER, SAID PLAINLY AND WITH THE DIRECTION OF THE ERROR. It corrects only the line
    /// CARRYING the mark; the later lines need no correction, because membership after the baseline is read
    /// off the recording's timeline.
    ///
    /// THE RESIDUAL IS A BACKWARD STEP SHORTLY BEFORE THE CHANGE. The change is then stamped on the pulled-
    /// back clock, so the UNMARKED lines taken before the step carry stamps at or after it and qualify by
    /// their raw stamps. The earliest of them becomes the baseline, and the measurement starts **up to the
    /// size of the step** too early - not a few seconds: a sixty-second step, with the change inside the
    /// following sixty seconds, starts it up to sixty seconds early. It OVERSTATES, which is the safe
    /// direction: it can turn a pass into a failure, never a failure into a pass.
    ///
    /// Such a window normally reports <c>ClockStepped</c>, because the marked line sits INSIDE the span -
    /// not because it is the baseline, which it is not. The scan misses it in one geometry only: the
    /// qualifying sample comes before the marked line on the timeline, which needs the condition to have
    /// already held before the change. That cannot happen on the processor wait or either clear wait; it can
    /// on the memory amber wait, where the meter may be amber while the fill is still finishing. The state
    /// reported there is true and the figure overstates, so it is recorded here rather than counted.
    ///
    /// A step under the recorder's roughly two-second tolerance carries no mark, and with no mark the raw
    /// stamps on both sides are read on one clock, so the only error is the same overstatement bounded by
    /// that tolerance. Two steps inside one sample interval are not handled.
    /// </summary>
    public static DateTime StampCorrectedByItsOwnStep(MeterObservation sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return sample.TakenAtUtc - TimeSpan.FromSeconds(sample.ClockStepSeconds ?? 0);
    }

    /// <summary>
    /// Reads the Gateway rows command's output: a JSON array of window start times. Each must carry its offset
    /// ("Z" or "+01:00"); a time with none is refused, because reading it as local time would shift every window
    /// by the machine's offset and report every row missing, or match by luck.
    /// </summary>
    public static IReadOnlyList<DateTime> ParseWindowStarts(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("The Gateway rows command must print a JSON array of window start times.");

        var starts = new List<DateTime>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var text = element.GetString() ?? throw new FormatException("A window start time is null.");
            if (!System.Text.RegularExpressions.Regex.IsMatch(text, @"(Z|[+-]\d{2}:\d{2})$"))
                throw new FormatException($"The window start \"{text}\" has no offset; write it in UTC with a Z.");
            starts.Add(DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).UtcDateTime);
        }
        return starts;
    }

    /// <summary>The sampler's own processor cost must stay under this share of one core (mission section 3).</summary>
    public const double SamplerCostLimitPercentOfOneCore = 0.5;

    /// <summary>The Gateway summary's window length (mission section 6).</summary>
    public static readonly TimeSpan SummaryWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether our figure agrees with the operating system's. A figure we could not read never agrees: a
    /// dash is not within five points of anything, and saying it was would pass a broken meter.
    /// </summary>
    public static AgreementResult Agreement(string resource, double? ours, OsFigure theirs)
    {
        ArgumentNullException.ThrowIfNull(theirs);
        if (ours is not { } value)
            return new AgreementResult(resource, null, theirs, null, Passed: false,
                $"{resource}: the meter could not read it, so there is nothing to compare with {theirs.Source}'s {Format(theirs.Percent)}%");

        var difference = Math.Abs(value - theirs.Percent);
        var passed = difference <= AgreementPoints;
        return new AgreementResult(resource, value, theirs, difference, passed,
            $"{resource}: meter {Format(value)}%, {theirs.Source} {Format(theirs.Percent)}%, {Format(difference)} points apart - " +
            (passed ? $"within {Format(AgreementPoints)}" : $"OUTSIDE {Format(AgreementPoints)}"));
    }

    /// <summary>
    /// The sampler's processor cost as a share of ONE core: processor time used divided by wall time, times
    /// 100. Ten minutes of wall time and 1.2 seconds of processor is 0.2 per cent of one core.
    /// </summary>
    public static CostResult SamplerCost(TimeSpan processorTime, TimeSpan wallTime)
    {
        if (wallTime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(wallTime), "A cost is measured over a positive length of time.");
        if (processorTime < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processorTime), "Processor time cannot be negative; the counter went backwards.");

        var percent = processorTime.TotalSeconds / wallTime.TotalSeconds * 100;
        var passed = percent < SamplerCostLimitPercentOfOneCore;
        return new CostResult(percent, processorTime, wallTime, passed,
            $"{Format(processorTime.TotalSeconds, 2)} s of processor over {Format(wallTime.TotalMinutes, 1)} minutes = " +
            $"{Format(percent, 3)}% of one core - {(passed ? "under" : "NOT under")} {Format(SamplerCostLimitPercentOfOneCore)}%");
    }

    /// <summary>
    /// Checks that every five-minute window from <paramref name="fromUtc"/> up to (not including)
    /// <paramref name="toUtc"/> has a stored row. The windows are aligned to whole five-minute boundaries,
    /// as the summary is. Returns the missing window starts; an empty list means no gap.
    ///
    /// An EMPTY set of stored rows is a failure, never a pass: no rows at all means the query found nothing,
    /// which is a broken instrument, not a clean run.
    /// </summary>
    public static GapResult Gaps(IReadOnlyCollection<DateTime> storedWindowStartsUtc, DateTime fromUtc, DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(storedWindowStartsUtc);
        if (toUtc <= fromUtc)
            throw new ArgumentOutOfRangeException(nameof(toUtc), "The checked span must end after it starts.");

        if (storedWindowStartsUtc.Count == 0)
            return new GapResult([], Passed: false, "the Gateway returned no rows at all - the query or the storage is broken, not clean");

        var stored = new HashSet<DateTime>(storedWindowStartsUtc.Select(AlignToWindow));
        var missing = new List<DateTime>();
        for (var window = AlignToWindow(fromUtc); window < toUtc; window += SummaryWindow)
        {
            if (window + SummaryWindow > toUtc) break; // a window still open is not owed yet
            if (!stored.Contains(window)) missing.Add(window);
        }

        return missing.Count == 0
            ? new GapResult(missing, Passed: true, $"every five-minute window from {fromUtc:HH:mm} to {toUtc:HH:mm} UTC is stored")
            : new GapResult(missing, Passed: false,
                $"{missing.Count} window(s) missing: {string.Join(", ", missing.Select(w => w.ToString("HH:mm", CultureInfo.InvariantCulture)))} UTC");
    }

    /// <summary>The start of the five-minute window a moment falls in.</summary>
    public static DateTime AlignToWindow(DateTime utc) =>
        new(utc.Ticks - utc.Ticks % SummaryWindow.Ticks, DateTimeKind.Utc);

    /// <summary>
    /// Whether the diagnosis names the expected cause. The cause must CONTAIN the expected word, so "Cpu, Disk"
    /// satisfies an expectation of "Cpu" - a machine can be slow for two reasons at once. An undiagnosable
    /// machine never matches: "not known" is not "CPU".
    /// </summary>
    public static bool DiagnosisNames(MeterObservation observation, string expectedCause)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCause);
        if (!observation.CouldDiagnose || !observation.IsSlow) return false;
        return observation.Cause
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(expectedCause, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Memory in use, in per cent, from the output of <c>free -b</c>: total minus AVAILABLE, the same figure
    /// the meter's MEM bar shows. "used" is deliberately not used - it leaves out reclaimable caches differently
    /// across versions of free.
    /// </summary>
    public static OsFigure ParseFreeMemoryPercent(string freeOutput)
    {
        ArgumentNullException.ThrowIfNull(freeOutput);
        var lines = freeOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines.FirstOrDefault(line => line.Contains("available", StringComparison.Ordinal))
            ?? throw new FormatException("free printed no header with an \"available\" column; this version of free is not supported.");
        var memory = lines.FirstOrDefault(line => line.StartsWith("Mem:", StringComparison.Ordinal))
            ?? throw new FormatException("free printed no \"Mem:\" line.");

        var columns = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var values = memory.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToArray();
        var totalIndex = Array.IndexOf(columns, "total");
        var availableIndex = Array.IndexOf(columns, "available");
        if (totalIndex < 0 || availableIndex < 0 || values.Length != columns.Length)
            throw new FormatException("free's columns do not line up with its header.");

        var total = double.Parse(values[totalIndex], CultureInfo.InvariantCulture);
        var available = double.Parse(values[availableIndex], CultureInfo.InvariantCulture);
        if (total <= 0) throw new FormatException("free reported a total of zero.");
        return new OsFigure((total - available) / total * 100, "free -b (total minus available)");
    }

    /// <summary>
    /// Processor busy, in per cent, from the LAST line of <c>vmstat 5 2</c>: 100 minus the idle column. The
    /// first line is the average since boot and is ignored, which is why the command takes two readings.
    /// </summary>
    public static OsFigure ParseVmstatBusyPercent(string vmstatOutput)
    {
        ArgumentNullException.ThrowIfNull(vmstatOutput);
        var lines = vmstatOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var header = lines.LastOrDefault(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("id"))
            ?? throw new FormatException("vmstat printed no header with an \"id\" column.");
        var data = lines.Where(line => line.Length > 0 && char.IsDigit(line[0])).ToArray();
        if (data.Length < 2)
            throw new FormatException("vmstat printed fewer than two readings; the first is the average since boot, so a second is needed.");

        var idleIndex = Array.IndexOf(header.Split(' ', StringSplitOptions.RemoveEmptyEntries), "id");
        var values = data[^1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (idleIndex >= values.Length) throw new FormatException("vmstat's last reading has fewer columns than its header.");
        return new OsFigure(100 - double.Parse(values[idleIndex], CultureInfo.InvariantCulture), "vmstat 5 2 (100 minus idle)");
    }

    /// <summary>
    /// Reads the output of <c>cc-devthrottle machine diagnose --json</c>: the newest sample the Director recorded,
    /// with the verdict and the diagnosis it recorded beside it. The harness reads these fields, and a missing one
    /// is an error naming it rather than a guess:
    /// <code>
    /// { "newest": { "cpu":    { "couldRead": true, "totalBusyPercent": 28.0 },
    ///               "memory": { "couldRead": true, "totalBytes": 17179869184, "availableBytes": 7666225480 },
    ///               "verdict":   { "resources": [ { "kind": "Cpu", "level": "Normal" }, { "kind": "Memory", "level": "Normal" } ] },
    ///               "diagnosis": { "couldDiagnose": true, "isSlow": false, "causes": [] } } }
    /// </code>
    /// MEM is memory in use - total minus available, over total - the same figure the toolbar bar shows.
    /// </summary>
    public static MeterObservation ParseDiagnoseJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var newest = Required(document.RootElement, "newest", "the root");
        if (newest.ValueKind == JsonValueKind.Null)
            throw new FormatException("The diagnose output has no newest sample: the Director has recorded nothing yet, so there is no meter to read.");
        return ParseLine(newest, json);
    }

    /// <summary>
    /// Reads every sample of the window from <c>cc-devthrottle machine diagnose --json --samples</c>, oldest first.
    /// The recorder writes a whole minute of lines in one append, so the newest line alone is the END of a batch:
    /// judging a time limit on it would report a meter that turned red at 65 seconds as red at 110. The harness
    /// therefore reads them all, and <see cref="JudgeWarning"/> judges on the earliest one that meets the condition.
    /// An empty or absent list is an error, never a clean window.
    /// </summary>
    public static IReadOnlyList<MeterObservation> ParseDiagnoseSamples(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var samples = Required(document.RootElement, "samples", "the root");
        if (samples.ValueKind != JsonValueKind.Array)
            throw new FormatException("The diagnose output carries no samples list; run the command with --json --samples.");
        var observations = samples.EnumerateArray().Select(line => ParseLine(line, line.GetRawText())).ToList();
        if (observations.Count == 0)
            throw new FormatException("The diagnose output's window holds no samples: the Director recorded nothing in it, so there is no meter to read.");
        return observations.OrderBy(observation => observation.TakenAtUtc).ToList();
    }


    /// <summary>
    /// How old the newest recorded sample may be for the idle comparison to mean anything: the recorder's one-minute
    /// write interval plus two five-second samples. An older one is a stopped or stuck Director, and comparing it with
    /// the operating system's figure on an idle machine would agree by luck.
    /// </summary>
    public static readonly TimeSpan FreshSampleAge = TimeSpan.FromSeconds(60 + 10);

    private static MeterObservation ParseLine(JsonElement newest, string json)
    {

        var levels = new Dictionary<string, MeterLevel>(StringComparer.Ordinal);
        var verdict = Required(newest, "verdict", "newest");
        // A sample recorded with no verdict leaves both levels Unknown - never Normal.
        var resources = verdict.ValueKind == JsonValueKind.Null
            ? Enumerable.Empty<JsonElement>()
            : Required(verdict, "resources", "newest.verdict").EnumerateArray();
        foreach (var resource in resources)
        {
            var kind = Required(resource, "kind", "a verdict resource").GetString() ?? "";
            var levelText = Required(resource, "level", $"the {kind} verdict").GetString();
            if (!Enum.TryParse<MeterLevel>(levelText, ignoreCase: false, out var level))
                throw new FormatException($"The {kind} verdict's level is \"{levelText}\", which is not Unknown, Normal, Tight or Critical.");
            levels[kind] = level;
        }

        var cpu = Required(newest, "cpu", "newest");
        var cpuCouldRead = Required(cpu, "couldRead", "newest.cpu").GetBoolean();
        var cpuPercent = Number(Required(cpu, "totalBusyPercent", "newest.cpu"), "newest.cpu.totalBusyPercent", cpuCouldRead);

        var memory = Required(newest, "memory", "newest");
        var memoryCouldRead = Required(memory, "couldRead", "newest.memory").GetBoolean();
        var total = Number(Required(memory, "totalBytes", "newest.memory"), "newest.memory.totalBytes", memoryCouldRead);
        var available = Number(Required(memory, "availableBytes", "newest.memory"), "newest.memory.availableBytes", memoryCouldRead);
        double? memoryPercent = null;
        if (memoryCouldRead)
        {
            if (total is not > 0 || available is null || available > total)
                throw new FormatException($"newest.memory says it could read but does not add up: {available} available of {total}.");
            memoryPercent = (total.Value - available.Value) / total.Value * 100;
        }

        var diagnosis = Required(newest, "diagnosis", "newest");
        var causes = Required(diagnosis, "causes", "newest.diagnosis").EnumerateArray()
            .Select(cause => cause.GetString() ?? throw new FormatException("A diagnosis cause is null."))
            .ToArray();

        return new MeterObservation(
            new MeterBar(cpuCouldRead, cpuPercent, LevelOf(levels, "Cpu")),
            new MeterBar(memoryCouldRead, memoryPercent, LevelOf(levels, "Memory")),
            Required(diagnosis, "couldDiagnose", "newest.diagnosis").GetBoolean(),
            Required(diagnosis, "isSlow", "newest.diagnosis").GetBoolean(),
            causes.Length == 0 ? "None" : string.Join(", ", causes),
            DateTimeOffset.Parse(Required(newest, "takenAtUtc", "newest").GetString()
                ?? throw new FormatException("newest.takenAtUtc is null."), CultureInfo.InvariantCulture).UtcDateTime,
            Required(newest, "recordingSeconds", "newest").GetDouble(),
            Required(newest, "run", "newest").GetString() ?? throw new FormatException("newest.run is null."),
            newest.TryGetProperty("clockStepSeconds", out var step) && step.ValueKind != JsonValueKind.Null
                ? step.GetDouble()
                : null,
            json);
    }

    // A figure on a reading that could not be taken is a made-up number, and a reading that could be taken with no
    // figure is a broken one; both are refused rather than passed on to a check.
    private static double? Number(JsonElement element, string path, bool couldRead)
    {
        var isNull = element.ValueKind == JsonValueKind.Null;
        if (couldRead && isNull) throw new FormatException($"{path} is null on a reading that says it could read.");
        if (!couldRead && !isNull) throw new FormatException($"{path} carries {element} on a reading that could not be taken - that is a made-up figure.");
        return isNull ? null : element.GetDouble();
    }

    private static MeterLevel LevelOf(Dictionary<string, MeterLevel> levels, string kind) =>
        levels.Count == 0
            ? MeterLevel.Unknown
            : levels.TryGetValue(kind, out var level)
                ? level
                : throw new FormatException($"The recorded verdict has no {kind} resource.");

    private static JsonElement Required(JsonElement parent, string name, string where) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value
            : throw new FormatException($"The diagnose output has no \"{name}\" in {where}; the harness needs it.");

    private static string Format(double value, int decimals = 1) =>
        value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}

/// <summary>The outcome of one agreement check.</summary>
public sealed record AgreementResult(string Resource, double? Ours, OsFigure Theirs, double? DifferencePoints, bool Passed, string Summary);

/// <summary>The outcome of the sampler cost check.</summary>
public sealed record CostResult(double PercentOfOneCore, TimeSpan ProcessorTime, TimeSpan WallTime, bool Passed, string Summary);

/// <summary>The outcome of the Gateway gap check.</summary>
public sealed record GapResult(IReadOnlyList<DateTime> MissingWindowStartsUtc, bool Passed, string Summary);
