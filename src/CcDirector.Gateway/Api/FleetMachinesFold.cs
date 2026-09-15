using CcDirector.Core.Update;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Machines view, folded once on the Gateway (fleet maintenance, devthrottle_internal#2026, epic #2027):
/// every machine this account owns, its launcher, the Directors on it, how each version compares to the newest
/// release, and which of update, restart and start can be done from here - with the reason when one cannot.
///
/// THE CLIENT RENDERS THIS AS SENT (CLAUDE.md rule 7). Every label, tone and offered action is decided here, so a
/// client cannot offer a button the launcher would refuse, and a new state is one edit in this file.
///
/// WHAT THE ACTIONS ACT ON. A launcher supervises ONE Director - the installed one. Development slot Directors on
/// the same machine are listed, but update, restart and start reach only the installed Director, and the session
/// count that holds an update back is the whole machine's, which is the careful side.
///
/// Pure: every input is handed in, so every state is tested without a host.
/// </summary>
internal static class FleetMachinesFold
{
    /// <summary>A Director the Gateway has not heard from for this long is not called running.</summary>
    public static readonly TimeSpan QuietAfter = TimeSpan.FromMinutes(2);

    private sealed class MachineParts
    {
        public required string Name { get; init; }
        public LauncherDto? Launcher { get; set; }
        public List<DirectorDto> Directors { get; } = new();
    }

    public static FleetMachinesDto Fold(
        IEnumerable<LauncherDto> launchers,
        Func<string, Streaming.LauncherStreamConnection?> connectionFor,
        IEnumerable<DirectorDto> directors,
        IReadOnlyDictionary<string, int> sessionsByDirector,
        NewestReleaseSnapshot newest,
        DateTime nowUtc)
    {
        var sessions = new Dictionary<string, int>(sessionsByDirector, StringComparer.OrdinalIgnoreCase);
        var byMachine = new SortedDictionary<string, MachineParts>(StringComparer.OrdinalIgnoreCase);

        foreach (var launcher in launchers)
            PartsFor(byMachine, launcher.MachineName).Launcher = launcher;
        foreach (var director in directors)
            PartsFor(byMachine, string.IsNullOrWhiteSpace(director.MachineName) ? "Unknown machine" : director.MachineName)
                .Directors.Add(director);

        var dto = new FleetMachinesDto { NewestRelease = FoldNewest(newest) };
        foreach (var parts in byMachine.Values)
            dto.Machines.Add(FoldMachine(parts, connectionFor(parts.Name), sessions, newest.Version, nowUtc));

        dto.Highlights = FoldHighlights(dto.Machines, newest.Version);
        return dto;
    }

    private static MachineParts PartsFor(SortedDictionary<string, MachineParts> byMachine, string name)
    {
        if (!byMachine.TryGetValue(name, out var parts))
        {
            parts = new MachineParts { Name = name };
            byMachine[name] = parts;
        }
        return parts;
    }

    private static NewestReleaseDto FoldNewest(NewestReleaseSnapshot newest) => new()
    {
        Version = newest.Version,
        CheckedAtUtc = newest.CheckedAtUtc,
        Label = newest.Version ?? (newest.Error is null ? "checking" : "unknown"),
        Detail = (newest.Version, newest.Error) switch
        {
            (null, null) => "The Gateway is reading the newest release now.",
            (null, { } error) => $"The newest release could not be read: {error}",
            ({ }, { } error) => $"The last check for a newer release failed ({error}), so this is the newest release found before it.",
            _ => null,
        },
    };

    private static FleetMachineDto FoldMachine(MachineParts parts, Streaming.LauncherStreamConnection? connection,
        Dictionary<string, int> sessions, string? newestVersion, DateTime nowUtc)
    {
        var capability = MachineRestartCapability.Judge(parts.Name, parts.Launcher, connection, nowUtc);
        var machine = new FleetMachineDto
        {
            Machine = parts.Name,
            LauncherVersion = parts.Launcher?.Version,
            LauncherLastSeenUtc = parts.Launcher?.LastSeenAt,
            Reach = capability.Reach,
            LauncherVersionState = parts.Launcher is null ? new FleetVersionDto() : CompareToNewest(parts.Launcher.Version, newestVersion),
        };
        (machine.ReachLabel, machine.ReachTone, machine.ReachDetail) = capability.Reach switch
        {
            LauncherReach.Connected => ("Launcher connected", FleetTone.Ok, (string?)null),
            LauncherReach.NotStreamCapable => ("Too old for commands", FleetTone.Bad,
                "The launcher is running but is older than remote commands, so nothing can be sent to it. Update it once "
                + "on the machine; after that it can be reached from here."),
            LauncherReach.NotConnected => ("Launcher offline", FleetTone.Idle,
                "The launcher has stopped talking to this Gateway. Start it on the machine to manage it from here."),
            _ => ("No launcher", FleetTone.Idle,
                "No launcher is registered for this machine, so it cannot be started, restarted or updated from here."),
        };

        foreach (var d in parts.Directors.OrderBy(NameOf, StringComparer.OrdinalIgnoreCase))
            machine.Directors.Add(FoldDirector(d, sessions.TryGetValue(d.DirectorId, out var n) ? n : 0, newestVersion, nowUtc));

        var running = machine.Directors.Where(d => d.StateLabel != StoppedLabel).ToList();
        var busy = running.Sum(d => d.Sessions);
        var connected = capability.Reach == LauncherReach.Connected;
        bool Declares(string token) => capability.DeclaredCommands.Any(c => string.Equals(c, token, StringComparison.OrdinalIgnoreCase));
        var declaredList = capability.Declaration == LauncherDeclarationState.Declared;

        machine.Update = !connected ? NotOffered("Update now", machine.ReachDetail)
            : newestVersion is null ? NotOffered("Update now", "The newest release is not known yet, so there is nothing to compare against.")
            : running.Count == 0 ? NotOffered("Update now", "No Director is running on this machine. It installs a downloaded update the next time it starts.")
            : !running.Any(d => d.VersionState.Behind) ? NotOffered("Up to date", null)
            : !Declares(LauncherCapabilities.DirectorUpdate) ? NotOffered("Update now",
                $"This launcher (version {machine.LauncherVersion}) is older than remote updates. It still installs a "
                + "downloaded update by itself once the Director is empty.")
            : busy > 0 ? NotOffered("Update when empty",
                $"{Plural(busy, "session")} running. The update installs by itself once the Director is empty, so no "
                + "session is interrupted.")
            : Offered("Update now", "Nothing is running, so the Director restarts straight into the new version. If the "
                + "new version does not start, the previous one is put back.");

        machine.Restart = !connected ? NotOffered("Restart", null)
            : running.Count == 0 ? NotOffered("Restart", null)
            : capability.GuardedRestart != CapabilityState.Available ? NotOffered("Restart", capability.GuardedRestartReason)
            : busy > 0 ? NotOffered("Restart", $"{Plural(busy, "session")} running. A restart from here only happens on an empty Director.")
            : Offered("Restart", "Restarts the Director. The launcher checks again that it is empty, and refuses if it is not.");

        machine.Start = !connected ? NotOffered("Start Director", null)
            : running.Count > 0 ? NotOffered("Start Director", null)
            : declaredList && !Declares(LauncherCapabilities.DirectorStart)
                ? NotOffered("Start Director", $"This launcher (version {machine.LauncherVersion}) declares that it cannot start a Director.")
            : Offered("Start Director", "Starts the installed Director on this machine.");

        machine.CanReportUpdateStatus = connected && Declares(LauncherCapabilities.DirectorUpdateStatus);

        return machine;
    }

    private const string StoppedLabel = "Stopped";

    private static string NameOf(DirectorDto d)
        => !string.IsNullOrWhiteSpace(d.DisplayName) ? d.DisplayName
            : !string.IsNullOrWhiteSpace(d.MachineName) ? d.MachineName
            : d.DirectorId;

    private static FleetDirectorDto FoldDirector(DirectorDto d, int sessions, string? newestVersion, DateTime nowUtc)
    {
        var quiet = d.LastSeen is not { } seen || nowUtc - seen > QuietAfter;
        var (label, tone) = d.StoppedAtUtc is not null ? (StoppedLabel, FleetTone.Idle)
            : quiet ? ("Not heard from", FleetTone.Warn)
            : ("Running", FleetTone.Ok);
        return new FleetDirectorDto
        {
            DirectorId = d.DirectorId,
            Name = NameOf(d),
            Version = string.IsNullOrWhiteSpace(d.Version) ? null : d.Version,
            Sessions = sessions,
            StateLabel = label,
            StateTone = tone,
            VersionState = CompareToNewest(d.Version, newestVersion),
        };
    }

    /// <summary>
    /// A running version against the newest release. Build metadata after a plus sign and a pre-release suffix
    /// are ignored for the comparison, the same way the updater itself compares versions.
    /// </summary>
    internal static FleetVersionDto CompareToNewest(string? running, string? newest)
    {
        if (newest is null || UpdateService.TryParseTag(newest) is not { } newestParsed)
            return new FleetVersionDto();
        if (string.IsNullOrWhiteSpace(running))
            return new FleetVersionDto { Label = "unreadable version", Tone = FleetTone.Idle };

        var plus = running.IndexOf('+');
        var core = plus >= 0 ? running[..plus] : running;
        if (UpdateService.TryParseTag(core) is not { } runningParsed)
            return new FleetVersionDto { Label = "unreadable version", Tone = FleetTone.Idle };

        var order = runningParsed.CompareTo(newestParsed);
        return order < 0 ? new FleetVersionDto { Label = "behind", Tone = FleetTone.Warn, Behind = true }
            : order == 0 ? new FleetVersionDto { Label = "current", Tone = FleetTone.Ok }
            : new FleetVersionDto { Label = "newer than release", Tone = FleetTone.Ok };
    }

    private static List<FleetHighlightDto> FoldHighlights(List<FleetMachineDto> machines, string? newestVersion)
    {
        var highlights = new List<FleetHighlightDto>();
        var directors = machines.SelectMany(m => m.Directors).ToList();
        var behind = directors.Count(d => d.VersionState.Behind);

        if (newestVersion is null)
            highlights.Add(new FleetHighlightDto { Text = "Newest release not known yet, so nothing can be called behind", Tone = FleetTone.Idle });
        else if (behind > 0)
            highlights.Add(new FleetHighlightDto { Text = $"{behind} of {Plural(directors.Count, "Director")} behind", Tone = FleetTone.Warn });
        else if (directors.Count > 0)
            highlights.Add(new FleetHighlightDto { Text = $"{(directors.Count == 1 ? "The Director is" : $"All {directors.Count} Directors are")} current", Tone = FleetTone.Ok });

        var tooOld = machines.Count(m => m.Reach == LauncherReach.NotStreamCapable);
        if (tooOld > 0)
            highlights.Add(new FleetHighlightDto { Text = $"{Plural(tooOld, "launcher")} too old for commands", Tone = FleetTone.Bad });

        var offline = machines.Count(m => m.Reach == LauncherReach.NotConnected);
        if (offline > 0)
            highlights.Add(new FleetHighlightDto { Text = $"{Plural(offline, "launcher")} offline", Tone = FleetTone.Idle });

        var ready = machines.Count(m => m.Update.Offered);
        if (ready > 0)
            highlights.Add(new FleetHighlightDto { Text = $"{Plural(ready, "machine")} can be updated now", Tone = FleetTone.Ok });

        return highlights;
    }

    private static FleetActionDto Offered(string label, string reason) => new() { Offered = true, Label = label, Reason = reason };

    private static FleetActionDto NotOffered(string label, string? reason) => new() { Offered = false, Label = label, Reason = reason };

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
