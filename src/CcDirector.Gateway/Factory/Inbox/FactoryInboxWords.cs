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
        /// THE SEAM FOR YOUR INBOX (phase 3). Only these three decisions tell the owner: each will write exactly one
        /// Your inbox message, and delegated, to do and noted write none. Phase 1 writes nothing for them; the decide
        /// call returns the decision, and phase 3 adds its write where <see cref="FactoryInboxStore.Decide"/> names this.
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
