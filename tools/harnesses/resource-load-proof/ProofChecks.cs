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
public sealed record MeterObservation(MeterBar Cpu, MeterBar Memory, bool CouldDiagnose, bool IsSlow, string Cause, string Raw);

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

        var levels = new Dictionary<string, MeterLevel>(StringComparer.Ordinal);
        foreach (var resource in Required(Required(newest, "verdict", "newest"), "resources", "newest.verdict").EnumerateArray())
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
        levels.TryGetValue(kind, out var level)
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
