using System.Globalization;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// One warning banner, finished: the view binds to it and decides nothing (CLAUDE.md rule 7). Mission
/// section 5e has the wording.
/// </summary>
/// <param name="Kind">The resource the banner is about.</param>
/// <param name="Level">Tight draws amber, Critical draws red. A banner is never Normal or Unknown.</param>
/// <param name="Headline">"CPU is running hot on devthrottle-mac-mini."</param>
/// <param name="Body">The verdict's evidence and the biggest user, in one or two sentences.</param>
public sealed record ResourceBannerView(ResourceKind Kind, ResourceLevel Level, string Headline, string Body);

/// <summary>
/// Turns a crossing into the words of its banner. A pure function, so every sentence the owner can read is
/// pinned by a unit test.
/// </summary>
public static class ResourceBannerFold
{
    /// <summary>The words for one crossing that shows a banner.</summary>
    /// <param name="crossing">A crossing whose <see cref="ResourceCrossing.ShowsBanner"/> is true.</param>
    /// <param name="sample">The sample that produced it, so the biggest user is named from the same moment.</param>
    /// <param name="machineName">The machine's name.</param>
    /// <param name="sessions">
    /// This Director's sessions and their process trees. NULL WHEN NOT KNOWN, which is not the same as empty: an
    /// empty list is a Director running no sessions, so the busiest process is certainly not one of ours; null
    /// means nobody has worked out which processes belong to which session, and the banner then makes no claim
    /// about ownership either way.
    /// </param>
    public static ResourceBannerView Build(
        ResourceCrossing crossing,
        ResourceSample? sample,
        string machineName,
        IReadOnlyList<ResourceSessionInput>? sessions)
    {
        ArgumentNullException.ThrowIfNull(crossing);
        ArgumentException.ThrowIfNullOrWhiteSpace(machineName);
        if (!crossing.ShowsBanner)
            throw new ArgumentException($"A {crossing.Change} crossing shows no banner.", nameof(crossing));

        var headline = Headline(crossing.Kind, crossing.To, machineName);
        var body = Sentence(crossing.Evidence) + " " + Busiest(crossing.Kind, sample, sessions);
        return new ResourceBannerView(crossing.Kind, crossing.To, headline, body);
    }

    private static string Headline(ResourceKind kind, ResourceLevel level, string machine)
    {
        var critical = level == ResourceLevel.Critical;
        return kind switch
        {
            ResourceKind.Cpu => critical ? $"CPU is maxed out on {machine}." : $"CPU is running hot on {machine}.",
            ResourceKind.Memory => critical ? $"Memory is nearly gone on {machine}." : $"Memory is running short on {machine}.",
            ResourceKind.Swap => critical ? $"Swap is nearly full on {machine}." : $"Swap is filling up on {machine}.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No banner wording for this resource."),
        };
    }

    // The evidence is written by ResourcePressure as a plain-English fragment; the banner prints it as a sentence.
    private static string Sentence(string evidence)
    {
        var text = evidence.Trim();
        if (text.Length == 0) return string.Empty;
        text = char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
        return text.EndsWith('.') ? text : text + ".";
    }

    private static string Busiest(ResourceKind kind, ResourceSample? sample, IReadOnlyList<ResourceSessionInput>? sessions)
    {
        if (sample is null || !sample.Processes.CouldRead)
            return "The busiest process could not be named: " +
                   (sample is null ? "there is no reading yet." : $"{sample.Processes.UnreadableReason}.");

        var byProcessor = kind == ResourceKind.Cpu;
        ProcessReading? top = null;
        foreach (var process in sample.Processes.Processes)
        {
            if (byProcessor)
            {
                if (process.CpuPercent is not { } cpu) continue;
                if (top is null || cpu > top.CpuPercent!.Value) top = process;
            }
            else
            {
                if (process.MemoryBytes is not { } bytes) continue;
                if (top is null || bytes > top.MemoryBytes!.Value) top = process;
            }
        }

        // Every figure unknown - the first sample, or a platform that refused them all. Said plainly, never a zero.
        if (top is null)
            return byProcessor
                ? "The busiest process is not known yet: no process has a processor figure in this reading."
                : "The biggest process is not known: no process has a memory figure in this reading.";

        var figure = byProcessor
            ? ResourceViewText.PercentOf100(top.CpuPercent)
            : ResourceViewText.Bytes(top.MemoryBytes);
        string where;
        if (sessions is null)
        {
            where = string.Empty;
        }
        else
        {
            var owner = sessions.FirstOrDefault(s => s.Usage.Pids.Contains(top.Pid));
            where = owner is null ? " (not one of this Director's sessions)" : $" in session {owner.Label}";
        }
        return $"{(byProcessor ? "Busiest" : "Biggest")}: {top.Name}{where}, {figure}.";
    }
}

/// <summary>
/// Which banners are up, one per resource at most. The one place the banner's lifetime is decided, so the
/// view only draws the list it is handed.
///
/// THE RULES, from mission section 4 ("How loud the warning is") and section 5e:
/// <list type="bullet">
/// <item>A crossing into amber or into red puts that resource's banner up, replacing any banner it had -
/// so a resource never has two, and amber escalating to red is one banner that turns red.</item>
/// <item>Easing from red to amber is still a warning but is not news: no new banner, and a dismissed one
/// stays dismissed.</item>
/// <item>Coming back to normal takes the banner down, dismissed or not. A warning must clear when the load
/// goes away.</item>
/// <item>Dismiss hides a banner until that resource next crosses. It does not end the warning: the meter
/// keeps its colour.</item>
/// </list>
///
/// Not thread-safe: the view model calls it on the user-interface thread only.
/// </summary>
public sealed class ResourceBannerBoard
{
    private static readonly ResourceKind[] Order = [ResourceKind.Cpu, ResourceKind.Memory, ResourceKind.Swap];
    private readonly Dictionary<ResourceKind, ResourceBannerView> _up = new();

    /// <summary>The banners currently up, processor first, then memory, then swap.</summary>
    public IReadOnlyList<ResourceBannerView> Visible =>
        Order.Where(_up.ContainsKey).Select(kind => _up[kind]).ToList();

    /// <summary>Applies one crossing. Returns true when the visible list changed.</summary>
    public bool Apply(ResourceCrossing crossing, ResourceBannerView? banner)
    {
        ArgumentNullException.ThrowIfNull(crossing);

        if (crossing.ShowsBanner)
        {
            if (banner is null)
                throw new ArgumentNullException(nameof(banner), "A crossing that shows a banner must be handed its words.");
            _up[crossing.Kind] = banner;
            FileLog.Write($"[ResourceBannerBoard] Apply: up - {crossing.Kind} {crossing.To}");
            return true;
        }

        if (crossing.EndsWarning)
        {
            var removed = _up.Remove(crossing.Kind);
            FileLog.Write($"[ResourceBannerBoard] Apply: cleared - {crossing.Kind}, wasVisible={removed}");
            return removed;
        }

        return false;
    }

    /// <summary>Hides one resource's banner until it next crosses. Returns true when one was up.</summary>
    public bool Dismiss(ResourceKind kind)
    {
        var removed = _up.Remove(kind);
        FileLog.Write($"[ResourceBannerBoard] Dismiss: {kind}, wasVisible={removed}");
        return removed;
    }
}
