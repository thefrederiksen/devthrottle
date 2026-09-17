using System.Globalization;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Fleet;

/// <summary>What the Gateway knows about one computer on the account, gathered before the fold runs.</summary>
/// <param name="Machine">The computer's name.</param>
/// <param name="Launcher">Its launcher's registration row, or null when none is registered now.</param>
/// <param name="Reach">How far its launcher is from taking a command (<see cref="LauncherReachability"/>).</param>
/// <param name="LauncherCanStartDirector">False when the launcher declared that it cannot start a Director.</param>
/// <param name="Directors">Every Director the Gateway holds a registration for on this computer.</param>
/// <param name="FirstSeenUtc">When the account's earliest session the Gateway still holds on this computer started
/// (session history, kept 90 days), or null when it holds none.</param>
/// <param name="FirstAgent">The agent that earliest session ran, or null.</param>
/// <param name="HistoryLastSeenUtc">When a session on this computer was last seen, from session history - the one
/// "last seen" that survives a Gateway restart - or null.</param>
internal sealed record FleetManagerMachineFacts(
    string Machine,
    LauncherDto? Launcher,
    LauncherReach Reach,
    bool LauncherCanStartDirector,
    IReadOnlyList<DirectorDto> Directors,
    DateTime? FirstSeenUtc = null,
    string? FirstAgent = null,
    DateTime? HistoryLastSeenUtc = null);

/// <summary>Everything the placement fold reads, handed in, so every state is tested without a host.</summary>
/// <param name="SavedAgent">The saved agent, or null.</param>
/// <param name="SavedMachine">The saved computer, or null.</param>
/// <param name="MarkedSessionId">The session the account marked as its Fleet Manager, or null.</param>
/// <param name="Machines">Every computer on the account.</param>
/// <param name="Roster">The account's fresh session roster.</param>
/// <param name="AgentsOnPlacementMachine">The agents the placement computer's running Director offers, in its
/// order, or null when that is not known.</param>
/// <param name="TimeZone">The account's display time zone, for the times inside sentences.</param>
/// <param name="NowUtc">The clock.</param>
internal sealed record FleetManagerPlacementInputs(
    string? SavedAgent,
    string? SavedMachine,
    string? MarkedSessionId,
    IReadOnlyList<FleetManagerMachineFacts> Machines,
    IReadOnlyList<SessionDto> Roster,
    IReadOnlyList<AgentChoiceDto>? AgentsOnPlacementMachine,
    TimeZoneInfo TimeZone,
    DateTime NowUtc);

/// <summary>
/// The Fleet Manager setting, folded once on the Gateway (the Fleet Manager mission, step 5). Every sentence,
/// state and offered action the Settings tab shows is decided here (CLAUDE.md rule 7), and every input is handed
/// in, so each state is tested without a host.
///
/// A COMPUTER HAS THREE STATES, the three the design names: a Director is running there; it is on and its
/// launcher is connected, so the launcher will start a Director; or it cannot be reached now. A launcher that
/// is running but too old to take commands cannot start anything, so for this purpose it is not reachable, and
/// the line says so in its own words.
///
/// TIMES are UTC on the answer. Inside a sentence they are written in the account's display time zone - the
/// setting that exists for exactly this - as "07:02" for today, "yesterday 22:40", or "12 Sep 22:40".
/// </summary>
internal static class FleetManagerPlacementFold
{
    private const string ToneOk = "ok";
    private const string ToneGo = "go";
    private const string ToneBad = "bad";
    private const string ToneIdle = "idle";

    /// <summary>How long a start may wait for a Director the launcher is starting, as the page says it.</summary>
    public const int StartWaitSeconds = 90;

    public static FleetManagerPlacementDto Fold(FleetManagerPlacementInputs input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var now = input.NowUtc;

        var saved = FleetManagerAgents.Canonical(input.SavedAgent) is { } savedAgent
                    && !string.IsNullOrWhiteSpace(input.SavedMachine)
            ? (Agent: savedAgent, Machine: input.SavedMachine!.Trim())
            : ((string Agent, string Machine)?)null;

        var defaultMachine = DefaultMachine(input.Machines);
        var machine = saved?.Machine ?? defaultMachine?.Machine;
        var agent = saved?.Agent
            ?? (machine is null ? null : DefaultAgent(defaultMachine?.FirstAgent, input.AgentsOnPlacementMachine));

        var dto = new FleetManagerPlacementDto
        {
            Agent = agent,
            AgentLabel = agent is null ? "" : FleetManagerAgents.DisplayName(agent),
            Machine = machine,
            IsDefault = saved is null,
            GeneratedAtUtc = now,
            AgentNote = AgentNote(),
            MachineNote = "If no Director is running there, the launcher starts one.",
        };

        if (saved is null)
            dto.DefaultNote = DefaultNote(defaultMachine, agent, input.AgentsOnPlacementMachine, input.TimeZone, now);

        dto.Agents = FleetManagerAgents.All
            .Select(a => new FleetManagerAgentChoiceDto
            {
                Value = a.Value,
                DisplayName = a.DisplayName,
                OfferedOnSavedMachine = input.AgentsOnPlacementMachine is null
                    ? null
                    : input.AgentsOnPlacementMachine.Any(o => string.Equals(o.Type, a.Value, StringComparison.OrdinalIgnoreCase)),
            })
            .ToList();

        var facts = input.Machines.ToList();
        if (machine is not null && !facts.Any(f => SameMachine(f.Machine, machine)))
            facts.Add(new FleetManagerMachineFacts(machine, null, LauncherReach.NoLauncher, false, Array.Empty<DirectorDto>()));

        foreach (var f in facts.OrderBy(f => f.Machine, StringComparer.OrdinalIgnoreCase))
        {
            var choice = FoldMachine(f, input.TimeZone, now);
            if (machine is not null && SameMachine(f.Machine, machine) && agent is not null
                && choice.State == FleetManagerMachineChoiceDto.StateRunning && input.AgentsOnPlacementMachine is not null)
            {
                var label = FleetManagerAgents.DisplayName(agent);
                choice.Detail = input.AgentsOnPlacementMachine.Any(o => string.Equals(o.Type, agent, StringComparison.OrdinalIgnoreCase))
                    ? $"- {label} installed"
                    : $"- {label} is not offered by the Director there";
            }
            dto.Machines.Add(choice);
        }

        var placement = machine is null ? null : dto.Machines.First(m => SameMachine(m.Machine, machine));
        dto.Status = FoldStatus(input, agent, placement, now);
        dto.Save = FoldSave(dto, LiveMarked(input)?.MachineName);
        return dto;
    }

    /// <summary>A Director on this computer that is running now: not stopped, and heard from recently. The most
    /// recently heard from wins. Null when none is.</summary>
    public static DirectorDto? RunningDirector(FleetManagerMachineFacts facts, DateTime nowUtc)
        => facts.Directors
            .Where(d => d.StoppedAtUtc is null && d.LastSeen is { } seen && nowUtc - seen <= FleetMachinesFold.QuietAfter)
            .OrderByDescending(d => d.LastSeen)
            .FirstOrDefault();

    /// <summary>The computer the account set up first: the one with the earliest session the Gateway still holds.
    /// Null when no computer carries such a record - the Gateway does not invent a first computer.</summary>
    public static FleetManagerMachineFacts? DefaultMachine(IReadOnlyList<FleetManagerMachineFacts> machines)
        => machines
            .Where(m => m.FirstSeenUtc is not null)
            .OrderBy(m => m.FirstSeenUtc)
            .ThenBy(m => m.Machine, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// The default agent, in the order the Gateway can honestly learn it: the agent of the account's first session
    /// on that computer; else the first agent that computer's running Director offers; else Claude Code. Each is
    /// kept only when the Fleet Manager can run on it.
    /// </summary>
    public static string DefaultAgent(string? firstAgent, IReadOnlyList<AgentChoiceDto>? offered)
        => FleetManagerAgents.Canonical(firstAgent)
           ?? FirstOffered(offered)
           ?? FleetManagerAgents.Fallback;

    private static string? FirstOffered(IReadOnlyList<AgentChoiceDto>? offered)
        => offered?.Select(o => FleetManagerAgents.Canonical(o.Type)).FirstOrDefault(k => k is not null);

    /// <summary>Whether the Fleet Manager can be started on this computer now.</summary>
    public static bool IsReachable(FleetManagerMachineFacts facts, DateTime nowUtc)
        => FoldMachine(facts, TimeZoneInfo.Utc, nowUtc).Selectable;

    internal static bool SameMachine(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string AgentNote()
    {
        var names = FleetManagerAgents.All.Select(a => a.DisplayName).ToList();
        return $"{string.Join(", ", names.Take(names.Count - 1))} or {names[^1]} - any agent installed on the chosen "
               + "computer. It uses that agent's own sign-in and default model.";
    }

    private static string DefaultNote(FleetManagerMachineFacts? first, string? agent,
        IReadOnlyList<AgentChoiceDto>? offered, TimeZoneInfo tz, DateTime now)
    {
        if (first is null)
            return "Nothing is saved yet, and the Gateway has no record of which computer this account set up first, "
                   + "so there is no default. Choose an agent and a computer, then save.";
        var agentSource = FleetManagerAgents.Canonical(first.FirstAgent) is not null
            ? "the agent that first session ran"
            : FirstOffered(offered) is not null
                ? "the first agent its Director offers"
                : "Claude Code, because the Gateway cannot tell which agents that computer has";
        return $"Nothing is saved yet. This is the default: {FleetManagerAgents.DisplayName(agent)} on {first.Machine}, "
               + $"the computer of this account's earliest session the Gateway still holds (it keeps 90 days; that "
               + $"session started {FormatWhen(first.FirstSeenUtc!.Value, tz, now)}), and {agentSource}. It is not "
               + "stored until you save or start it.";
    }

    private static FleetManagerMachineChoiceDto FoldMachine(FleetManagerMachineFacts f, TimeZoneInfo tz, DateTime now)
    {
        var lastSeen = LastSeen(f);
        var choice = new FleetManagerMachineChoiceDto { Machine = f.Machine, LastSeenUtc = lastSeen };

        if (RunningDirector(f, now) is not null)
        {
            choice.State = FleetManagerMachineChoiceDto.StateRunning;
            choice.StateLabel = "On - Director running";
            choice.Tone = ToneOk;
            choice.Selectable = true;
            return choice;
        }

        choice.State = FleetManagerMachineChoiceDto.StateNotReachable;
        choice.Tone = ToneBad;
        choice.Selectable = false;
        switch (f.Reach)
        {
            case LauncherReach.Connected when f.LauncherCanStartDirector:
                choice.State = FleetManagerMachineChoiceDto.StateLauncherWillStart;
                choice.StateLabel = "On - no Director running. The launcher will start one.";
                choice.Tone = ToneGo;
                choice.Selectable = true;
                break;
            case LauncherReach.Connected:
                choice.StateLabel = "On - but it cannot run there.";
                choice.Detail = $"No Director is running, and its launcher (version {f.Launcher?.Version}) says it cannot "
                                + "start one. Start a Director on that computer, or update its launcher.";
                break;
            case LauncherReach.NotStreamCapable:
                choice.StateLabel = "Not reachable - its launcher is too old for commands.";
                choice.Detail = "The launcher is running but is older than remote commands, so it cannot start a Director "
                                + "there. Update it once on that computer; after that the Fleet Manager can run there.";
                break;
            default:
                choice.StateLabel = lastSeen is { } seen
                    ? $"Not reachable - last seen {FormatWhen(seen, tz, now)}."
                    : "Not reachable - the Gateway has not heard from it since the Gateway last started.";
                choice.Detail = "It cannot run there until it is back.";
                break;
        }
        return choice;
    }

    private static DateTime? LastSeen(FleetManagerMachineFacts f)
    {
        DateTime? latest = f.Launcher?.LastSeenAt;
        if (f.HistoryLastSeenUtc is { } history && (latest is null || history > latest))
            latest = history;
        foreach (var d in f.Directors)
            if (d.LastSeen is { } seen && (latest is null || seen > latest))
                latest = seen;
        return latest;
    }

    private static FleetManagerStatusDto FoldStatus(FleetManagerPlacementInputs input, string? agent,
        FleetManagerMachineChoiceDto? placement, DateTime now)
    {
        var tz = input.TimeZone;
        var status = new FleetManagerStatusDto
        {
            SessionId = input.MarkedSessionId,
            Open = new FleetManagerActionDto { Label = "Open it", BusyLabel = "Opening..." },
            Start = new FleetManagerActionDto { Label = "Start it", BusyLabel = StartingLabel },
            Restart = new FleetManagerActionDto { Label = "Restart it", BusyLabel = StartingLabel },
        };
        var where = agent is null || placement is null ? "" : $"{FleetManagerAgents.DisplayName(agent)} on {placement.Machine}";

        var live = LiveMarked(input);
        if (live is not null)
        {
            var watching = input.Roster.Count(s => string.Equals(s.ControllerSessionId, live.SessionId, StringComparison.OrdinalIgnoreCase)
                                                   && !IsGone(s));
            status.State = FleetManagerStatusDto.StateRunning;
            status.Tone = ToneOk;
            status.SinceUtc = live.CreatedAt;
            status.Watching = watching;
            status.Sentence = $"Running now: {FleetManagerAgents.DisplayName(live.Agent)} on {live.MachineName}, since "
                              + $"{FormatWhen(live.CreatedAt, tz, now)}. Watching {Plural(watching, "session")}.";
            status.Open.Offered = true;
            if (placement is { Selectable: true })
            {
                status.Restart.Offered = true;
                status.Restart.ConfirmTitle = "Restart the Fleet Manager?";
                status.Restart.ConfirmMessage =
                    $"A new Fleet Manager starts on {where} and picks up from its records. The one running now finishes "
                    + "its current turn and then closes. Nothing it was watching is lost.";
            }
            else if (placement is not null)
            {
                status.Restart.Note = $"It cannot be restarted on {placement.Machine} now: that computer cannot be reached. "
                                      + "Choose another computer below to move it.";
            }
            return status;
        }

        status.Tone = ToneIdle;
        if (placement is null && input.Machines.Count > 0)
        {
            status.State = FleetManagerStatusDto.StateNotRunning;
            status.Sentence = "The Fleet Manager is not running, and nothing says where it runs yet. Choose an agent and a "
                              + "computer below and save, then start it.";
            status.Start.Note = "Choose where it runs and save first.";
            return status;
        }

        if (placement is null)
        {
            status.State = FleetManagerStatusDto.StateNoComputer;
            status.Sentence = "The Fleet Manager is not running, and this account has no computer for it to run on. "
                              + "Install DevThrottle on a computer and sign in to this account.";
            return status;
        }

        if (!placement.Selectable)
        {
            status.State = FleetManagerStatusDto.StateUnreachable;
            status.Tone = ToneBad;
            var since = placement.LastSeenUtc is { } seen ? $", last seen {FormatWhen(seen, tz, now)}" : "";
            status.Sentence = $"The Fleet Manager is not running, and its computer, {placement.Machine}, cannot be reached{since}. "
                              + "It does not move by itself: choose another computer below and save to move it.";
            status.Start.Note = $"It cannot start until {placement.Machine} is back, or you choose another computer.";
            return status;
        }

        status.State = FleetManagerStatusDto.StateNotRunning;
        status.Sentence = $"The Fleet Manager is not running. Start it where the setting says: {where}."
                          + (placement.State == FleetManagerMachineChoiceDto.StateLauncherWillStart
                              ? " No Director is running there, so the launcher starts one first."
                              : "");
        status.Start.Offered = true;
        status.Start.Note = $"Starting can take up to {StartWaitSeconds} seconds when a Director has to be started first.";
        return status;
    }

    private const string StartingLabel = "Starting... this can take up to 90 seconds while a Director is started.";

    private static FleetManagerActionDto FoldSave(FleetManagerPlacementDto dto, string? runningOn)
    {
        var anySelectable = dto.Machines.Any(m => m.Selectable);
        if (dto.Status.State == FleetManagerStatusDto.StateRunning)
        {
            return new FleetManagerActionDto
            {
                Offered = anySelectable,
                Verb = "move",
                Label = "Save and move it",
                BusyLabel = StartingLabel,
                Note = "Saving starts a new Fleet Manager in the new place. It picks up from its records; the old one "
                       + "finishes its current turn and closes. Nothing it was watching is lost.",
                ConfirmTitle = "Move the Fleet Manager?",
                ConfirmMessage = "A new Fleet Manager starts on the agent and computer you chose and picks up from its "
                                 + $"records. The one running now{(string.IsNullOrWhiteSpace(runningOn) ? "" : $" on {runningOn}")} "
                                 + "finishes its current turn and then closes. Nothing it was watching is lost.",
            };
        }

        return new FleetManagerActionDto
        {
            Offered = anySelectable,
            Verb = "save",
            Label = "Save",
            BusyLabel = "Saving...",
            Note = anySelectable
                ? "Saving records where the Fleet Manager runs. Start it from the bar above."
                : "No computer on this account can take the Fleet Manager now, so there is nothing to save.",
        };
    }

    private static SessionDto? LiveMarked(FleetManagerPlacementInputs input)
    {
        // The one rule for which session is the Fleet Manager: the account's mark, on a session nobody owns.
        return input.Roster.FirstOrDefault(s => FleetManagerSessions.IsFleetManager(s, input.MarkedSessionId) && !IsGone(s));
    }

    private static bool IsGone(SessionDto s)
        => s.Crashed || string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    /// <summary>A UTC instant written in the account's time zone: "07:02" today, "yesterday 22:40", "12 Sep 22:40",
    /// and with the year when it is not this year.</summary>
    internal static string FormatWhen(DateTime utc, TimeZoneInfo tz, DateTime nowUtc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz).Date;
        var time = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (local.Date == today) return time;
        if (local.Date == today.AddDays(-1)) return $"yesterday {time}";
        return local.Year == today.Year
            ? $"{local.ToString("d MMM", CultureInfo.InvariantCulture)} {time}"
            : $"{local.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} {time}";
    }

    private static string Plural(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
