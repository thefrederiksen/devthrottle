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

        /// <summary>The Gateway itself, with no session and no factory behind it: the 08:00 morning report's message about
        /// open problems whose schedule no factory's morning summary carries, the notice that a schedule in no factory
        /// failed, and the system alerts that were emails until the owner moved them here (<see cref="NetworkSeat"/>,
        /// <see cref="LicensesSeat"/>, <see cref="SessionSupervisorSeat"/>). Nobody reads a reply to it.</summary>
        public const string Gateway = "gateway";
    }

    /// <summary>The seat a Gateway message is written from: the 08:00 morning report.</summary>
    public const string MorningReportSeat = "morning report";

    /// <summary>The seat a schedule failure notice is written from (issue #4436, moved off email by the Factory Inbox
    /// mission): a factory's seat when the schedule belongs to a factory, the Gateway's when it belongs to none.</summary>
    public const string ScheduleFailureSeat = "schedule failure";

    /// <summary>The seat a disk space notice is written from (Disk Space on the Meter, 7 November 2026): a fixed drive on
    /// one of the owner's machines went under 10 GB free, or came back above it. Always from the Gateway.</summary>
    public const string DiskSpaceSeat = "disk space";

    /// <summary>The seat of the alert that the home network is slow, and that it recovered (the Network Diagnostics
    /// alerts; an email until the owner ruled "Move them all to Your inbox, with a push to the phone").</summary>
    public const string NetworkSeat = "network";

    /// <summary>The seat of the License Switching alerts to the owner: a license alarm that opened, and a sign-in whose
    /// renewal keeps failing (an email until the same ruling).</summary>
    public const string LicensesSeat = "licenses";

    /// <summary>The seat of the session supervisor's escalation: a session stopped and could not be resumed on its own
    /// (an email until the same ruling).</summary>
    public const string SessionSupervisorSeat = "session supervisor";

    /// <summary>Who a Gateway message is from, as the owner reads it.</summary>
    public static string GatewayFromLabel(string? seat) => seat switch
    {
        MorningReportSeat => "Morning report - schedules in no factory",
        ScheduleFailureSeat => "Schedule failure - schedule in no factory",
        DiskSpaceSeat => "Disk space - a drive on one of your machines",
        NetworkSeat => "Network",
        LicensesSeat => "Licenses",
        SessionSupervisorSeat => "Session supervisor",
        _ => throw new ArgumentOutOfRangeException(nameof(seat), seat, "not a seat a Gateway message is written from"),
    };

    /// <summary>What the reply route answers when a reply is posted to a Gateway message written from
    /// <paramref name="seat"/>.</summary>
    public static string NoReplyToGateway(string? seat) => seat switch
    {
        ScheduleFailureSeat =>
            "This message is the Gateway's notice that a schedule in no factory failed, not a message from a session or a factory, "
            + "so nobody reads a reply to it. Nothing was sent. Fix the schedule on the Schedule page, or put it into a factory.",
        DiskSpaceSeat =>
            "This message is the Gateway's notice about a drive's free space, not a message from a session or a factory, "
            + "so nobody reads a reply to it. Nothing was sent. Free some space on that drive; the machine's page shows every drive.",
        NetworkSeat =>
            "This message is the Gateway's alert about your home network, not a message from a session or a factory, "
            + "so nobody reads a reply to it. Nothing was sent.",
        LicensesSeat =>
            "This message is the Gateway's license alert, not a message from a session or a factory, "
            + "so nobody reads a reply to it. Nothing was sent. The Licenses page shows what is wrong and what to do.",
        SessionSupervisorSeat =>
            "This message is the session supervisor's notice that a session could not be resumed, not a message from the session, "
            + "so nobody reads a reply to it. Nothing was sent. Open the session and answer it there.",
        _ =>
            "This message is from the Gateway's morning report, not from a session or a factory, so nobody reads a reply to it. "
            + "Nothing was sent. Put the schedule into a factory, or fix it on the Schedule page.",
    };

    /// <summary>Where a reply went.</summary>
    public static class ReplyTargets
    {
        /// <summary>Into the inbox of the session that sent the message, which reads it when it is free.</summary>
        public const string Session = "session";

        /// <summary>Into the Factory inbox of the factory that sent the message.</summary>
        public const string Factory = "factory";
    }

    /// <summary>
    /// The lists the owner reads, which are the filters on the Your inbox screen, in the order they are shown: what is
    /// unread, everything in the inbox (unread and read), and what was archived.
    /// </summary>
    public static class Views
    {
        public const string Unread = "unread";
        public const string Inbox = "inbox";
        public const string Archived = "archived";
        public static readonly IReadOnlyList<string> All = [Unread, Inbox, Archived];
    }

    /// <summary>What a view is shown as, on its filter button.</summary>
    public static string ViewLabel(string view) => view switch
    {
        Views.Unread => "Unread",
        Views.Inbox => "All",
        Views.Archived => "Archived",
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, "not a Your inbox view"),
    };

    /// <summary>
    /// What a view says when it holds nothing. The inbox's sentence is the owner's: Your inbox replaces the email that
    /// factories and sessions sent him.
    /// </summary>
    public static string EmptyLine(string view) => view switch
    {
        Views.Unread => "Nothing unread.",
        Views.Inbox => "Nothing for you. Factories and sessions write here instead of emailing you.",
        Views.Archived => "Nothing archived. An archived message stays here; nothing is ever deleted.",
        _ => throw new ArgumentOutOfRangeException(nameof(view), view, "not a Your inbox view"),
    };

    /// <summary>
    /// The colour a message's state badge is shown in, as one closed word the screens map to a colour (the same words as
    /// the Factory inbox: ok, amber, red, grey). Unread stands out; read and archived do not.
    /// </summary>
    public static string Tone(string state) => state switch
    {
        States.Unread => "red",
        States.Read => "grey",
        States.Archived => "grey",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a Your inbox state"),
    };

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
