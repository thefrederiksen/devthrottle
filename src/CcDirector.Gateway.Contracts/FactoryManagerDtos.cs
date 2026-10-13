namespace CcDirector.Gateway.Contracts;

/// <summary>
/// <c>GET /gateway/factory-manager</c> and <c>PUT /gateway/factory-manager</c> (Factory Control, step 5): which session
/// is the account's Factory Manager, and where it runs.
/// </summary>
public sealed class FactoryManagerBindingDto
{
    /// <summary>The bound session's full id, or null when the account has no Factory Manager.</summary>
    public string? SessionId { get; set; }

    /// <summary>The computer the Factory Manager runs on, or null when none was saved yet.</summary>
    public string? Computer { get; set; }

    /// <summary>The folder it runs in, on <see cref="Computer"/>, or null when none was saved yet.</summary>
    public string? Folder { get; set; }
}

/// <summary>The body of <c>POST /gateway/factory-agents/factory-manager/talk</c>: where to start it, when none is saved.</summary>
public sealed class FactoryManagerTalkRequest
{
    /// <summary>The computer to run it on. Saved with <see cref="Folder"/> when both are given.</summary>
    public string? Computer { get; set; }

    /// <summary>The folder to run it in, on <see cref="Computer"/>.</summary>
    public string? Folder { get; set; }
}

/// <summary>
/// <c>GET /gateway/factory-agents/factory-manager</c>: the Factories screen's Factory Manager card, folded on the
/// Gateway. The Cockpit renders it as given.
/// </summary>
public sealed class FactoryManagerCardDto
{
    /// <summary>"Factory Manager".</summary>
    public string Title { get; set; } = "";

    /// <summary>What the seat is, in one sentence.</summary>
    public string Purpose { get; set; } = "";

    /// <summary>"Running on SOREN_NORTH", "Not running - its last session has ended", "Not started yet".</summary>
    public string StateText { get; set; } = "";

    /// <summary>"ok" when running, "grey" otherwise.</summary>
    public string StateTone { get; set; } = "";

    /// <summary>The running Factory Manager's session, to open; null when it is not running.</summary>
    public string? SessionHref { get; set; }

    /// <summary>"Open the Factory Manager" when it runs, "Talk to the Factory Manager" otherwise.</summary>
    public string ActionLabel { get; set; } = "";

    /// <summary>"Starting the Factory Manager...".</summary>
    public string BusyLabel { get; set; } = "";

    /// <summary>True when no computer and folder are saved, so the card asks for them before the first start.</summary>
    public bool NeedsPlacement { get; set; }

    /// <summary>The computers it may run on: the account's running Directors' computers. Only when placement is needed.</summary>
    public List<string> Computers { get; set; } = new();

    /// <summary>A sentence saying what the computer and the folder are for. Only when placement is needed.</summary>
    public string? PlacementText { get; set; }

    /// <summary>The computer picker's label, "Computer". Only when placement is needed; null otherwise.</summary>
    public string? ComputerLabel { get; set; }

    /// <summary>The folder field's label, "Folder". Only when placement is needed; null otherwise.</summary>
    public string? FolderLabel { get; set; }

    /// <summary>"Runs on (the saved computer) in (the saved folder)" when placement is saved; null otherwise.</summary>
    public string? PlacementSavedText { get; set; }

    /// <summary>The name check, as on the command line.</summary>
    public FactoryNameCheckDto NameCheck { get; set; } = new();
}

/// <summary>
/// <c>GET /gateway/factory/name-check</c>: every seat of the account's factories that brings back "CEO" or a name
/// that is not a role word (the owner's ruling: "A factory has a boss, not a CEO ... there are no names").
/// </summary>
public sealed class FactoryNameCheckDto
{
    /// <summary>How many findings; 0 when every factory passes.</summary>
    public int Count { get; set; }

    /// <summary>How many factories were checked.</summary>
    public int FactoriesChecked { get; set; }

    /// <summary>The check's heading: "Name check: 2 seats flagged", or "Name check: nothing flagged".</summary>
    public string HeadingText { get; set; } = "";

    /// <summary>The rule, in words.</summary>
    public string RuleText { get; set; } = "";

    /// <summary>"Every factory passes." when there is nothing to report; null otherwise.</summary>
    public string? PassText { get; set; }

    public List<FactoryNameFindingDto> Findings { get; set; } = new();
}

/// <summary>One seat the name check flags.</summary>
public sealed class FactoryNameFindingDto
{
    public string Factory { get; set; } = "";
    public string FactoryTitle { get; set; } = "";
    public string SeatId { get; set; } = "";

    /// <summary>"ceo" or "not-a-role-word".</summary>
    public string Kind { get; set; } = "";

    /// <summary>The finding in one sentence, for example "WarmForward: the seat 'nora-hale' is named 'Nora Hale', which is
    /// not a role word of this factory (its role is Boss)."</summary>
    public string Text { get; set; } = "";
}
