namespace CcDirector.Gateway.Inbox;

/// <summary>
/// EVERY WORD OF YOUR INBOX, ONCE (Factory Inbox mission, phase 3; CLAUDE.md rule 7): the closed words stored in the
/// tables, and the label each one is shown with. A client prints the label it is handed and never turns a stored word
/// into one itself, so a new state or a new kind of sender is one edit here and nowhere else.
/// </summary>
public static class YourInboxWords
{
    /// <summary>Where a message is in the owner's reading. Nothing is deleted: archived is as far as a message goes.</summary>
    public static class States
    {
        public const string Unread = "unread";
        public const string Read = "read";
        public const string Archived = "archived";
        public static readonly IReadOnlyList<string> All = [Unread, Read, Archived];
    }

    /// <summary>Who a message is from.</summary>
    public static class Senders
    {
        /// <summary>Any session, with <c>cc-devthrottle inbox send</c>.</summary>
        public const string Session = "session";

        /// <summary>A factory: its Boss telling the owner it declined, cannot, or pushes back, and its morning summary.</summary>
        public const string Factory = "factory";
    }

    /// <summary>Where a reply went.</summary>
    public static class ReplyTargets
    {
        /// <summary>Into the inbox of the session that sent the message, which reads it when it is free.</summary>
        public const string Session = "session";

        /// <summary>Into the Factory inbox of the factory that sent the message.</summary>
        public const string Factory = "factory";
    }

    /// <summary>The two lists the owner reads: what is in the inbox, and what was archived.</summary>
    public static class Views
    {
        public const string Inbox = "inbox";
        public const string Archived = "archived";
        public static readonly IReadOnlyList<string> All = [Inbox, Archived];
    }

    /// <summary>The things the owner can do to a message, each sent back as the state it sets.</summary>
    public static class Actions
    {
        public const string MarkRead = "read";
        public const string MarkUnread = "unread";
        public const string Archive = "archived";
    }

    /// <summary>The kinds of link a message carries back to where it came from.</summary>
    public static class LinkKinds
    {
        public const string Session = "session";
        public const string Report = "report";
        public const string Factory = "factory";
    }

    /// <summary>What a state is shown as.</summary>
    public static string StateLabel(string state) => state switch
    {
        States.Unread => "Unread",
        States.Read => "Read",
        States.Archived => "Archived",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a Your inbox state"),
    };

    /// <summary>The button that sets a state, as the owner reads it.</summary>
    public static string ActionLabel(string action) => action switch
    {
        Actions.MarkRead => "Mark read",
        Actions.MarkUnread => "Mark unread",
        Actions.Archive => "Archive",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "not a Your inbox action"),
    };

    /// <summary>
    /// The actions offered on a message in a state. An unread message can be read or archived; a read one marked unread
    /// again or archived; an archived one put back in the inbox as read (or unread). The client offers exactly these.
    /// </summary>
    public static IReadOnlyList<string> ActionsFor(string state) => state switch
    {
        States.Unread => [Actions.MarkRead, Actions.Archive],
        States.Read => [Actions.MarkUnread, Actions.Archive],
        States.Archived => [Actions.MarkRead, Actions.MarkUnread],
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a Your inbox state"),
    };

    /// <summary>The label of the archived message's way back to the inbox: "Mark read" reads oddly there.</summary>
    public static string ActionLabelFor(string state, string action)
        => state == States.Archived && action == Actions.MarkRead ? "Move to inbox" : ActionLabel(action);

    /// <summary>What the unread count says on the Inbox entry: empty when there is nothing unread.</summary>
    public static string CountLabel(int unread) => unread switch
    {
        0 => "",
        1 => "1 unread",
        _ => $"{unread} unread",
    };
}
