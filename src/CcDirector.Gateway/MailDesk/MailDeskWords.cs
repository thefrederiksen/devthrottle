namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE MAIL DESK'S WORDS, HELD ONCE (Mail Desk mission, phase 1, contract section 2). Every filter, every store and
/// every route compares against these lists, so a word cannot mean one thing to the store and another to the command.
/// Each list is in the order a reader should see it - the order the counts by decision are listed in.
/// </summary>
public static class MailDeskWords
{
    /// <summary>The factory and the seat a channel's Mail Desk start belongs to unless said otherwise.</summary>
    public const string DefaultFactory = "mail-desk";

    /// <inheritdoc cref="DefaultFactory"/>
    public const string DefaultSeat = "mail-desk";

    /// <summary>The kinds of channel. Email only, for now; Teams and Slack are later missions (D3).</summary>
    public static class ChannelKinds
    {
        public const string Email = "email";
        public static readonly IReadOnlyList<string> All = new[] { Email };
    }

    /// <summary>The providers a channel accepts: Outlook and Gmail (D20).</summary>
    public static class Providers
    {
        public const string Outlook = "outlook";
        public const string Gmail = "gmail";
        public static readonly IReadOnlyList<string> All = new[] { Outlook, Gmail };

        /// <summary>Each provider's name as a reader sees it (phase 7). Every provider in <see cref="All"/> has one;
        /// the channel fold refuses a provider without one rather than show the raw word.</summary>
        public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Outlook] = "Outlook",
            [Gmail] = "Gmail",
        };

        /// <summary>The providers as the <c>--provider</c> placeholder of a command shows them: <c>outlook|gmail</c>.</summary>
        public static string Placeholder => string.Join("|", All);
    }

    /// <summary>
    /// The command that adds a mailbox, as every sentence that tells a reader to add one shows it - the page's empty
    /// state, the channel list's next step and the failed check's fix - so each names every provider the Gateway accepts.
    /// </summary>
    public static string ChannelAddCommand(string machine)
        => $"cc-devthrottle maildesk channel add <address> --provider {Providers.Placeholder} --machine {machine} --why \"<text>\"";

    /// <summary>A channel's mode: observe decides and records and changes nothing; live acts.</summary>
    public static class Modes
    {
        public const string Observe = "observe";
        public const string Live = "live";
        public static readonly IReadOnlyList<string> All = new[] { Observe, Live };
    }

    /// <summary>The layer that decided an email.</summary>
    public static class Layers
    {
        public const string Rule = "rule";
        public const string Allow = "allow";
        public const string Block = "block";
        /// <summary>The "person or spam?" model question.</summary>
        public const string SenderScreen = "sender-screen";
        /// <summary>The "which route?" model question.</summary>
        public const string RouteModel = "route-model";
        public static readonly IReadOnlyList<string> All = new[] { Rule, Allow, Block, SenderScreen, RouteModel };
    }

    /// <summary>What the Mail Desk decided for an email.</summary>
    public static class Decisions
    {
        public const string Route = "route";
        public const string Archive = "archive";
        public const string Junk = "junk";
        public const string KeepForOwner = "keep-for-owner";
        public const string MailDeskAgent = "mail-desk-agent";
        public const string Failed = "failed";
        public static readonly IReadOnlyList<string> All = new[] { Route, Archive, Junk, KeepForOwner, MailDeskAgent, Failed };
    }

    /// <summary>What was done with an email in live mode.</summary>
    public static class Outcomes
    {
        public const string Filed = "filed";
        public const string SeatStarted = "seat-started";
        public const string Failed = "failed";
        public static readonly IReadOnlyList<string> All = new[] { Filed, SeatStarted, Failed };
    }

    /// <summary>What an allow-list entry does with its sender's mail, besides a route.</summary>
    public static class AllowActions
    {
        public const string Archive = "archive";
        public const string KeepForOwner = "keep-for-owner";
        public static readonly IReadOnlyList<string> All = new[] { Archive, KeepForOwner };
    }

    /// <summary>What a recorded change was made to.</summary>
    public static class ChangeKinds
    {
        public const string Rule = "rule";
        public const string Allow = "allow";
        public const string Block = "block";
        public const string Route = "route";
        public const string Channel = "channel";
        public const string Mode = "mode";
        /// <summary>One of the account's Mail Desk settings: a threshold or the spend limit (phase 3).</summary>
        public const string Setting = "setting";
        public static readonly IReadOnlyList<string> All = new[] { Rule, Allow, Block, Route, Channel, Mode, Setting };
        /// <summary>The kinds that change the rules document, and so carry a version before and after.</summary>
        public static readonly IReadOnlyList<string> Rules = new[] { Rule, Allow, Block, Route };
    }

    /// <summary>What a recorded change did.</summary>
    public static class ChangeActions
    {
        public const string Add = "add";
        public const string Remove = "remove";
        public const string Set = "set";
        public static readonly IReadOnlyList<string> All = new[] { Add, Remove, Set };
    }

    /// <summary>Who made a change.</summary>
    public static class WhoKinds
    {
        public const string Person = "person";
        public const string Session = "session";
        public static readonly IReadOnlyList<string> All = new[] { Person, Session };
    }

    /// <summary>Where an email routed to a seat stands (phase 4).</summary>
    public static class RoutedStates
    {
        public const string Open = "open";
        public const string Done = "done";
        public const string Returned = "returned";
        public static readonly IReadOnlyList<string> All = new[] { Open, Done, Returned };
    }

    /// <summary>Whether <paramref name="word"/> is one of <paramref name="words"/>, exactly.</summary>
    public static bool IsOneOf(string? word, IReadOnlyList<string> words)
        => word is not null && words.Contains(word, StringComparer.Ordinal);

    /// <summary>The words as a reader sees them in a refusal: <c>'observe' or 'live'</c>.</summary>
    public static string InWords(IReadOnlyList<string> words)
    {
        var quoted = words.Select(w => $"'{w}'").ToList();
        return quoted.Count == 1 ? quoted[0] : string.Join(", ", quoted.Take(quoted.Count - 1)) + " or " + quoted[^1];
    }
}
