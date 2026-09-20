namespace CcDirector.Reclaim.Removal;

/// <summary>
/// The ten refusals, in the order the gate runs them. The numbers are the mandate's own: anything the
/// tool ever removes has passed all ten, and each one is a numbered test that has been proven to fail
/// with the refusal taken out.
/// </summary>
public enum RefusalCheck
{
    /// <summary>Under a path the storage resolver names as credentials or vault.</summary>
    ProtectedPath = 1,

    /// <summary>Inside a git working tree. Worktrees belong to cc-worktrees.</summary>
    GitWorkingTree = 2,

    /// <summary>Under the user's own folders: Documents, Pictures, Videos, Desktop, OneDrive.</summary>
    UserFolder = 3,

    /// <summary>Reached through a link or junction.</summary>
    LinkOrJunction = 4,

    /// <summary>Younger than its rule's age gate, re-measured at the moment of the move.</summary>
    AgeGate = 5,

    /// <summary>An open file inside it.</summary>
    OpenFile = 6,

    /// <summary>The rule's controls are empty at the moment of the move.</summary>
    EmptyControls = 7,

    /// <summary>Changed between the recommendation and the removal.</summary>
    ChangedSinceRecommendation = 8,

    /// <summary>Removal without the explicit apply flag. A property of the run, checked once per run.</summary>
    NoApplyFlag = 9,

    /// <summary>Not canonical after resolution.</summary>
    NotCanonical = 10
}

/// <summary>What one check said about one item.</summary>
public enum CheckOutcome
{
    /// <summary>The check ran and did not fire.</summary>
    Passed,

    /// <summary>The check ran and fired; this is the refusal, with its reason.</summary>
    Refused,

    /// <summary>An earlier check fired, so this one was not run. Nobody can later read an unchecked refusal as a passed one.</summary>
    NotReached
}

/// <summary>One check's answer about one item, with the reason when it fired.</summary>
/// <param name="Check">Which of the ten.</param>
/// <param name="Outcome">What it said.</param>
/// <param name="Reason">
/// Why it refused, in one finished sentence, or null when it passed or was not reached.
/// </param>
public sealed record CheckResult(RefusalCheck Check, CheckOutcome Outcome, string? Reason);

/// <summary>
/// The refusal gate's whole answer about one item: the outcome of all ten checks, in the mandate's
/// numbered order, so an agent can see that a refusal was reached, refused, or never reached.
/// </summary>
public sealed record GateOutcome
{
    /// <summary>The item's path, as the recommendation spelled it.</summary>
    public required string Path { get; init; }

    /// <summary>
    /// True when every check that could fire at the item level - all but the ninth, which is a
    /// property of the run - ran and passed. A dry run reports eligible items as what it WOULD move;
    /// an apply moves them. The ninth check is reported separately so the two can never disagree
    /// about what is eligible.
    /// </summary>
    public required bool Eligible { get; init; }

    /// <summary>The run's apply state, carried on every outcome so one item can be read alone.</summary>
    public required bool Apply { get; init; }

    /// <summary>All ten checks, in the mandate's numbered order.</summary>
    public required IReadOnlyList<CheckResult> Checks { get; init; }

    /// <summary>
    /// The first check that fired at the item level, or null when none did. A dry run's ninth check
    /// firing is the run's own state, not an item's refusal, and is not recorded here.
    /// </summary>
    public required RefusalCheck? FiredCheck { get; init; }

    /// <summary>The fired check's reason, or null when none fired.</summary>
    public required string? Reason { get; init; }

    /// <summary>
    /// A refusal that is none of the ten checks: a broken configuration of the run itself, such as a
    /// holding root on a different volume from the item. Every check is reported not reached,
    /// because none of them could answer a question the run is not fit to ask. Null when the
    /// configuration is sound.
    /// </summary>
    public required string? ConfigurationReason { get; init; }

    /// <summary>The refusal's plain name for a reader, or null.</summary>
    public string RefusalName
    {
        get
        {
            if (ConfigurationReason is not null) return "the holding configuration";
            return FiredCheck is null ? string.Empty : WordsFor(FiredCheck.Value);
        }
    }

    /// <summary>The refusal as a line of text names it: the number, then what it declines.</summary>
    public static string WordsFor(RefusalCheck check) => check switch
    {
        RefusalCheck.ProtectedPath => "under a path the storage resolver protects",
        RefusalCheck.GitWorkingTree => "inside a git working tree",
        RefusalCheck.UserFolder => "under one of the user's own folders",
        RefusalCheck.LinkOrJunction => "reached through a link or junction",
        RefusalCheck.AgeGate => "younger than its rule's age gate",
        RefusalCheck.OpenFile => "an open file inside it",
        RefusalCheck.EmptyControls => "its rule's controls are empty",
        RefusalCheck.ChangedSinceRecommendation => "changed since it was recommended",
        RefusalCheck.NoApplyFlag => "removal without the explicit apply flag",
        RefusalCheck.NotCanonical => "not canonical after resolution",
        _ => throw new ArgumentOutOfRangeException(nameof(check), check, "There is no such refusal.")
    };
}
