namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>
/// EVERY WORD OF THE FACTORY INBOX, ONCE (Factory Inbox mission, phase 1; CLAUDE.md rule 7): the closed words stored in the
/// tables, and the label each one is shown with. A client prints the label it is handed and never turns a stored word
/// into one itself, so a new state or decision is one edit here and nowhere else.
/// </summary>
public static class FactoryInboxWords
{
    /// <summary>Where a message is on its way from the owner to a decision.</summary>
    public static class States
    {
        public const string Sent = "sent";
        public const string Received = "received";
        public const string WithBoss = "with-boss";
        public const string Decided = "decided";
    }

    /// <summary>The owner's Good or Bad note on a message.</summary>
    public static class Notes
    {
        public const string None = "none";
        public const string Good = "good";
        public const string Bad = "bad";
        public static readonly IReadOnlyList<string> All = [None, Good, Bad];
    }

    /// <summary>How a message was put in.</summary>
    public static class Sources
    {
        public const string Typed = "typed";
        public const string Spoken = "spoken";
        public static readonly IReadOnlyList<string> All = [Typed, Spoken];
    }

    /// <summary>Where a message was sent from.</summary>
    public static class Surfaces
    {
        public const string Phone = "phone";
        public const string Cockpit = "cockpit";
        public const string Session = "session";
    }

    /// <summary>What the Boss decided.</summary>
    public static class Decisions
    {
        public const string Delegated = "delegated";
        public const string Todo = "todo";
        public const string Noted = "noted";
        public const string Declined = "declined";
        public const string Cannot = "cannot";
        public const string Pushback = "pushback";
        public static readonly IReadOnlyList<string> All = [Delegated, Todo, Noted, Declined, Cannot, Pushback];

        /// <summary>The three decisions that must say why, in the Boss's words.</summary>
        public static bool NeedsWhy(string kind) => kind is Declined or Cannot or Pushback;

        /// <summary>
        /// THE SEAM FOR YOUR INBOX (phase 3). Only these three decisions tell the owner: each writes exactly one Your
        /// inbox message, in the same transaction as the decision (<see cref="FactoryInboxStore.Decide"/>), and delegated,
        /// to do and noted write none.
        /// </summary>
        public static bool TellsTheOwner(string kind) => kind is Declined or Cannot or Pushback;
    }

    /// <summary>The kinds of thing a Secretary may attach to a message.</summary>
    public static class LinkKinds
    {
        public const string Run = "run";
        public const string Report = "report";
        public const string Record = "record";
        public static readonly IReadOnlyList<string> All = [Run, Report, Record];
    }

    /// <summary>The steps of a message's trail, in order.</summary>
    public static class Steps
    {
        public const string Sent = "sent";
        public const string Received = "received";
        public const string WithBoss = "with-boss";
        public const string Decided = "decided";
        public const string Merged = "merged";
    }

    /// <summary>
    /// Why the wake could not start the seat a factory's messages wait for (Factory Inbox mission, phase 2). Stored on the
    /// factory's wake row and shown on the thread in <see cref="WaitingLabel"/>'s words, so a message that waits says why.
    /// </summary>
    public static class Waiting
    {
        /// <summary>No Director is running on the computer the seat runs on. It goes on when one connects there - never
        /// on another computer.</summary>
        public const string NoDirector = "no-director";

        /// <summary>The seat cannot be started as the factory stands: its published definition cannot be read or holds no
        /// Boss seat, or its Boss seat is not registered, names no computer, or the factory has no folder. The detail says
        /// which.</summary>
        public const string CannotStart = "cannot-start";

        /// <summary>The start was sent and failed; the detail is the reason the machine gave.</summary>
        public const string StartFailed = "start-failed";

        /// <summary>A Director runs on the seat's computer but is too old to build a seat's kit. It goes on when that
        /// Director is updated - never on another computer.</summary>
        public const string DirectorCannotBuildKit = "director-cannot-build-kit";

        public static readonly IReadOnlyList<string> All = [NoDirector, CannotStart, StartFailed, DirectorCannotBuildKit];
    }

    /// <summary>The seat a message waits for, as the waiting sentence names it.</summary>
    public enum WaitingFor { Secretary, Boss }

    /// <summary>
    /// The state label of a message that waits: what it waits for and why, in one sentence a client prints as it is.
    /// <paramref name="detail"/> is the computer with no Director, or the reason a start or a definition read failed.
    /// </summary>
    public static string WaitingLabel(WaitingFor who, string code, string? detail)
    {
        var seat = who == WaitingFor.Secretary ? "Secretary" : "Boss";
        var named = string.IsNullOrWhiteSpace(detail) ? "" : detail.Trim();
        return code switch
        {
            Waiting.NoDirector => named.Length == 0
                ? "Waiting - no Director is running on the Boss's computer; it goes on when one connects there"
                : $"Waiting - no Director is running on the Boss's computer, {named}; it goes on when one connects there",
            Waiting.DirectorCannotBuildKit => named.Length == 0
                ? "Waiting - the Director on the Boss's computer cannot build a seat kit; it goes on when that Director is updated"
                : $"Waiting - the Director on the Boss's computer, {named}, cannot build a seat kit; it goes on when that Director is updated",
            Waiting.CannotStart => $"Waiting - the {seat} cannot be started: {named}",
            Waiting.StartFailed => $"Waiting - the {seat} did not start: {named}; it is tried again",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "not a factory inbox waiting reason"),
        };
    }

    /// <summary>What a message's state is shown as. A decided message shows its decision.</summary>
    public static string StateLabel(string state, string? decision) => state switch
    {
        States.Sent => "Sent",
        States.Received => "Received",
        States.WithBoss => "With the Boss",
        States.Decided when decision is not null => DecisionLabel(decision),
        States.Decided => "Decided",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a factory inbox state"),
    };

    /// <summary>What a decision is shown as.</summary>
    public static string DecisionLabel(string kind) => kind switch
    {
        Decisions.Delegated => "Delegated",
        Decisions.Todo => "To do",
        Decisions.Noted => "Noted",
        Decisions.Declined => "Declined",
        Decisions.Cannot => "Cannot be done",
        Decisions.Pushback => "Pushed back",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a factory inbox decision"),
    };

    /// <summary>What a note is shown as: empty for none.</summary>
    public static string NoteLabel(string note) => note switch
    {
        Notes.None => "",
        Notes.Good => "Good",
        Notes.Bad => "Bad",
        _ => throw new ArgumentOutOfRangeException(nameof(note), note, "not a factory inbox note"),
    };

    /// <summary>Where a message came from, as a phrase.</summary>
    public static string SurfacePhrase(string surface) => surface switch
    {
        Surfaces.Phone => "from the phone",
        Surfaces.Cockpit => "from the Cockpit",
        Surfaces.Session => "from a session",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "not a factory inbox surface"),
    };
}
