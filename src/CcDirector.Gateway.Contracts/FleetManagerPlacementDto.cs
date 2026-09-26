namespace CcDirector.Gateway.Contracts;

/// <summary>
/// WHERE THE ACCOUNT'S FLEET MANAGER RUNS, folded once on the Gateway for the Settings tab (the Fleet Manager
/// mission, step 5). Answered by <c>GET /gateway/fleet-manager/placement</c>, and by every action route after it
/// acts, so the page always renders the Gateway's own reading of what just happened.
///
/// THE CLIENT RENDERS THIS AS SENT (CLAUDE.md rule 7). Every sentence, label, tone, which computer can be chosen
/// and which button is offered is decided on the Gateway. Times are UTC here; every sentence that shows a time
/// already carries it formatted in the account's display time zone.
/// </summary>
public sealed class FleetManagerPlacementDto
{
    /// <summary>The saved agent kind (as <see cref="NewSessionRequest.Agent"/> takes it), or the default when none is
    /// saved. Null only when the account has no computer at all, so there is nothing to default from.</summary>
    public string? Agent { get; set; }

    /// <summary>The display name of <see cref="Agent"/>, for example "Claude Code".</summary>
    public string AgentLabel { get; set; } = "";

    /// <summary>The saved computer, or the default when none is saved. Null when the account has no computer.</summary>
    public string? Machine { get; set; }

    /// <summary>True when nothing is saved yet and <see cref="Agent"/> and <see cref="Machine"/> are the default.
    /// A default is never written as a stored row until the owner saves or starts.</summary>
    public bool IsDefault { get; set; }

    /// <summary>The sentence that says where the default came from, or that there is no record to take one from.
    /// Null when a placement is saved.</summary>
    public string? DefaultNote { get; set; }

    /// <summary>The line under the agent choice.</summary>
    public string AgentNote { get; set; } = "";

    /// <summary>Every agent the Fleet Manager can run on, in the order they are offered.</summary>
    public List<FleetManagerAgentChoiceDto> Agents { get; set; } = new();

    /// <summary>The line under the computer label.</summary>
    public string MachineNote { get; set; } = "";

    /// <summary>Every computer on the account, in name order.</summary>
    public List<FleetManagerMachineChoiceDto> Machines { get; set; } = new();

    /// <summary>Whether the Fleet Manager is running now, and what can be done about it.</summary>
    public FleetManagerStatusDto Status { get; set; } = new();

    /// <summary>The save button. Its <see cref="FleetManagerActionDto.Verb"/> says whether saving also moves a
    /// running Fleet Manager ("move") or only records the choice ("save").</summary>
    public FleetManagerActionDto Save { get; set; } = new();

    /// <summary>When this answer was folded (UTC).</summary>
    public DateTime GeneratedAtUtc { get; set; }
}

/// <summary>One agent the Fleet Manager can run on.</summary>
public sealed class FleetManagerAgentChoiceDto
{
    /// <summary>The agent kind, sent verbatim as the placement's agent.</summary>
    public string Value { get; set; } = "";

    /// <summary>The name shown to the owner.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Whether the saved computer's running Director offers this agent. Null when that is not known
    /// (no Director running there, or its agent list could not be read).</summary>
    public bool? OfferedOnSavedMachine { get; set; }
}

/// <summary>One computer on the account, and whether the Fleet Manager can run there now.</summary>
public sealed class FleetManagerMachineChoiceDto
{
    /// <summary>A Director is running there.</summary>
    public const string StateRunning = "running";

    /// <summary>The computer is on and its launcher is connected, but no Director is running: the launcher will
    /// start one.</summary>
    public const string StateLauncherWillStart = "launcher-will-start";

    /// <summary>Nothing can be started there now.</summary>
    public const string StateNotReachable = "not-reachable";

    /// <summary>The computer's name.</summary>
    public string Machine { get; set; } = "";

    /// <summary>One of the State constants.</summary>
    public string State { get; set; } = "";

    /// <summary>The short state, for example "On - Director running".</summary>
    public string StateLabel { get; set; } = "";

    /// <summary>The rest of the line, shown after the state with a space between, or null. For example
    /// "- Claude Code installed", or the sentence that says why it cannot be chosen.</summary>
    public string? Detail { get; set; }

    /// <summary>ok | go | bad - the colour of the state.</summary>
    public string Tone { get; set; } = "";

    /// <summary>Whether the owner may choose this computer now. A computer that cannot be reached cannot be
    /// chosen, and the Gateway refuses a save that names one.</summary>
    public bool Selectable { get; set; }

    /// <summary>When the Gateway last heard from this computer (UTC), or null when it has no such record.</summary>
    public DateTime? LastSeenUtc { get; set; }
}

/// <summary>Whether the account's Fleet Manager is running, in the Gateway's words.</summary>
public sealed class FleetManagerStatusDto
{
    /// <summary>The marked Fleet Manager session is live.</summary>
    public const string StateRunning = "running";

    /// <summary>No live Fleet Manager, and the saved computer can take one.</summary>
    public const string StateNotRunning = "not-running";

    /// <summary>No live Fleet Manager, and the saved computer cannot be reached.</summary>
    public const string StateUnreachable = "unreachable";

    /// <summary>The account has no computer, so there is nowhere to run it.</summary>
    public const string StateNoComputer = "no-computer";

    /// <summary>One of the State constants.</summary>
    public string State { get; set; } = "";

    /// <summary>The whole sentence the bar shows.</summary>
    public string Sentence { get; set; } = "";

    /// <summary>The short state the Fleet Manager page shows under its title (step 6): "idle, watching 5
    /// sessions", "thinking, watching 5 sessions", or why it is not running.</summary>
    public string Line { get; set; } = "";

    /// <summary>True while the running Fleet Manager is in the middle of a turn: a message sent now queues
    /// behind that turn.</summary>
    public bool Thinking { get; set; }

    /// <summary>ok | idle | bad - the colour of the bar's dot.</summary>
    public string Tone { get; set; } = "";

    /// <summary>The session the account has marked as its Fleet Manager, or null when none is marked.</summary>
    public string? SessionId { get; set; }

    /// <summary>When the running Fleet Manager started (UTC), or null.</summary>
    public DateTime? SinceUtc { get; set; }

    /// <summary>How many live sessions the running Fleet Manager owns. 0 when it is not running.</summary>
    public int Watching { get; set; }

    /// <summary>Open the running Fleet Manager's session.</summary>
    public FleetManagerActionDto Open { get; set; } = new();

    /// <summary>Start it where the setting says.</summary>
    public FleetManagerActionDto Start { get; set; } = new();

    /// <summary>Start a new one in the saved place and close the old one after its current turn.</summary>
    public FleetManagerActionDto Restart { get; set; } = new();

    /// <summary>While a restart or a move is under way, the Gateway's sentence saying so: a new Fleet Manager has
    /// started and takes over once the one running now has finished its turn and closed - and, when the one running
    /// now is waiting for the owner, that it cannot close until the owner answers it. Null when no replacement is
    /// under way.</summary>
    public string? Replacement { get; set; }

    /// <summary>ok | idle | bad - the colour of <see cref="Replacement"/>: bad while the old Fleet Manager waits for
    /// the owner. Null when <see cref="Replacement"/> is null.</summary>
    public string? ReplacementTone { get; set; }

    /// <summary>The new Fleet Manager waiting to take over, or null.</summary>
    public string? SuccessorSessionId { get; set; }

    /// <summary>What the Fleet Manager page (step 6) may show and use in this state - decided here, rendered there.</summary>
    public FleetManagerPageControlsDto Page { get; set; } = new();
}

/// <summary>
/// The Fleet Manager page's controls for the current state (the steps 5 and 6 fixes). The page decides nothing about
/// what a state means: whether the composer and the quick prompts can be used, whether the not-running bar shows, and
/// every label and sentence come from here.
/// </summary>
public sealed class FleetManagerPageControlsDto
{
    /// <summary>"Claude Code on WORKSTATION-A", or null when nothing says where it runs.</summary>
    public string? Where { get; set; }

    /// <summary>The link to the Fleet Manager tab in Settings beside <see cref="Where"/>, for example "(change)".</summary>
    public string ChangeLabel { get; set; } = "";

    /// <summary>Whether the composer takes a message now.</summary>
    public bool ComposerUsable { get; set; }

    /// <summary>The composer's placeholder while it is usable.</summary>
    public string ComposerPlaceholder { get; set; } = "";

    /// <summary>What shows in the composer's place while it is not usable.</summary>
    public string? ComposerOffText { get; set; }

    /// <summary>The line under the composer.</summary>
    public string ComposerHint { get; set; } = "";

    /// <summary>Whether the quick prompt buttons may be pressed now.</summary>
    public bool QuickPromptsUsable { get; set; }

    /// <summary>The label a quick prompt button shows while its words are being sent.</summary>
    public string QuickPromptBusyLabel { get; set; } = "";

    /// <summary>Whether the "thinking" line shows under the conversation.</summary>
    public bool ThinkingShown { get; set; }

    /// <summary>Whether the bar that says the Fleet Manager is not running shows. Its sentence is
    /// <see cref="FleetManagerStatusDto.Sentence"/> and its start button <see cref="FleetManagerStatusDto.Start"/>.</summary>
    public bool NotRunningBarShown { get; set; }

    /// <summary>The bar's link to the Fleet Manager tab in Settings, for example "Move it in Settings".</summary>
    public string SettingsLabel { get; set; } = "";

    /// <summary>The header's "Start fresh" button: start a brand new Fleet Manager session in the saved place and close
    /// the old one after its current turn. It is the same action as <see cref="FleetManagerStatusDto.Restart"/> - offered
    /// exactly when a restart is, and sent to the same <c>POST /gateway/fleet-manager/restart</c> - with the page's own
    /// label and its one confirmation.</summary>
    public FleetManagerActionDto StartFresh { get; set; } = new();
}

/// <summary>One action the page may offer, with its label and, when it closes something, its confirmation.</summary>
public sealed class FleetManagerActionDto
{
    public bool Offered { get; set; }

    public string Label { get; set; } = "";

    /// <summary>The line shown beside the button, or null.</summary>
    public string? Note { get; set; }

    /// <summary>For the save button: "move" (save, then move the running Fleet Manager there) or "save" (record
    /// the choice only). Null for the other actions.</summary>
    public string? Verb { get; set; }

    /// <summary>The line shown while the action runs, for example "Starting...".</summary>
    public string BusyLabel { get; set; } = "";

    /// <summary>When set, the page asks before acting: the question.</summary>
    public string? ConfirmTitle { get; set; }

    /// <summary>When set, the page asks before acting: what will happen.</summary>
    public string? ConfirmMessage { get; set; }
}

/// <summary>Body of <c>PUT /gateway/fleet-manager/placement</c> and <c>POST /gateway/fleet-manager/move</c>. Both
/// fields are required; a body missing either is refused, never guessed.</summary>
public sealed class FleetManagerPlacementRequest
{
    public string? Agent { get; set; }

    public string? Machine { get; set; }
}
