namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// WHAT IS SUPPORTED, BY PROVIDER - THE ONE LIST (mission "Zoho Mail Skill", sections 1 to 4). Each kind of mailbox, the
/// tool an agent uses for it, what that tool was PROVEN to do for email and calendar, whether the Mail Desk can read it,
/// and what the provider needs before a mailbox can be added. <c>GET /gateway/mail-desk/providers</c> serves it,
/// <c>cc-devthrottle maildesk providers</c> prints it, and the email skill sends an agent to that command; nothing else
/// holds a copy, so what we say and what we ship cannot disagree.
///
/// NOTHING IS LISTED AS WORKING ON TRUST. A feature is in <c>Works</c> only when the proof step showed it working, with
/// evidence (<c>_briefs/email-tools/proof/prover-a.md</c> and <c>prover-b.md</c>, 7 and 8 November 2026), or, for Zoho's
/// own API, when a dt-zoho pull request ran it against the real organisation (pull requests 4781, 4784, 4788 and 4789,
/// 8 November 2026). A feature that
/// failed, or was not tested, is listed under <c>NotWorking</c> with the reason, never left out. Change this list only
/// with new proof.
///
/// WHETHER THE MAIL DESK READS A PROVIDER is not written here: it is <see cref="MailDeskWords.Providers.All"/>, the list
/// the Gateway accepts on <c>channel add</c>. So the day a provider is added there, this list says so, with no edit here.
/// </summary>
public static class MailDeskProviders
{
    /// <summary>The words for a feature that was not proven working.</summary>
    public static class States
    {
        public const string Fail = "fail";
        public const string NotTested = "not-tested";
    }

    /// <summary>The words for whether the Mail Desk can read a provider.</summary>
    public static class MailDeskStates
    {
        public const string Yes = "yes";
        public const string NotYet = "not-yet";
    }

    /// <summary>The provider ids, in the order a reader sees them. Each is the word <c>channel add --provider</c> takes.</summary>
    public const string Zoho = MailDeskWords.Providers.Zoho;
    public const string Imap = "imap";

    private const string ProvenZoho =
        "Proven by hand on 7 and 8 November 2026 over IMAP, SMTP and CalDAV against Zoho's servers, on soren@devthrottle.com, "
        + "before dt-email and dt-calendar were built: the tools follow that proof, but were not themselves run in it. "
        + "No other provider's servers were tested.";

    /// <summary>Every provider, fully described. The Mail Desk column is folded in from <see cref="MailDeskWords.Providers.All"/>.</summary>
    public static IReadOnlyList<MailDeskProvider> All => Raw.Select(WithMailDesk).ToList();

    /// <summary>One provider by its id, or null when there is no such provider.</summary>
    public static MailDeskProvider? Find(string id)
        => All.FirstOrDefault(p => string.Equals(p.Provider, id, StringComparison.OrdinalIgnoreCase));

    private static MailDeskProvider WithMailDesk(MailDeskProvider p)
    {
        var reads = MailDeskWords.Providers.All.Contains(p.Provider, StringComparer.Ordinal);
        return p with
        {
            MailDesk = reads
                ? new MailDeskProviderMailDesk(MailDeskStates.Yes,
                    $"Yes. Add a mailbox with: {MailDeskWords.ChannelAddCommand("<machine>", p.Provider)}")
                : new MailDeskProviderMailDesk(MailDeskStates.NotYet, p.MailDesk.Label),
        };
    }

    /// <summary>"4 providers: on the Mail Desk 2, not yet 2" - the whole list in one line.</summary>
    public static string SummaryOf(IReadOnlyList<MailDeskProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var on = providers.Count(p => p.MailDesk.State == MailDeskStates.Yes);
        return $"{providers.Count} providers: on the Mail Desk {on}, not yet {providers.Count - on}";
    }

    private static MailDeskProviderFeature Fail(string feature, string why) => new(feature, States.Fail, why);

    private static MailDeskProviderFeature NotTested(string feature, string why) => new(feature, States.NotTested, why);

    private static readonly MailDeskProvider[] Raw =
    {
        new(
            Provider: Zoho,
            Name: "Zoho",
            Tool: "dt-zoho",
            MailDesk: new MailDeskProviderMailDesk(MailDeskStates.NotYet, "Not yet."),
            Needs: new[]
            {
                "The Zoho key in cc-secrets on every machine that may check it: a Self Client of the mailbox's organisation, in the entries zoho-api-client-id, zoho-api-client-secret and zoho-api-refresh-token. One key reads every mailbox of that organisation it reaches. It is never stored on the Gateway.",
                "dt-zoho on that machine, with its data centre set (dt-zoho settings set --data-centre ca). Only Canada (ca) has been run against a real organisation.",
                "Not the mailbox's IMAP switch or an app password: the zoho provider reads through Zoho's own API. To read a Zoho mailbox over IMAP instead, use the imap provider.",
            },
            Admin: "Works through dt-zoho: read the organisation, its domains, users and groups; add and remove aliases; switch IMAP on and off; add and remove forwarding (the target must confirm first); set and remove the vacation reply (it answers everyone); add, change and delete signatures. Fails: changing the display name (Zoho answers OK and changes nothing). Groups were read only on an organisation that has none.",
            Email: new MailDeskProviderArea(new[]
            {
                "List folders and messages, read a message, search",
                "Download attachments (text attachments come back with CRLF line endings)",
                "Send: plain, HTML, To, CC and BCC",
                "Send attachments",
                "Reply and reply-all, keeping the thread",
                "Send from an alias",
                "Scheduled send",
                "Drafts: save, list, delete",
                "Read and unread, flag",
                "Move, archive, delete to Trash or for good",
                "Mark spam and not spam (Zoho moves the whole conversation, sent replies too)",
                "Create, rename, nest, empty and delete folders; show or hide a folder over IMAP",
                "Zoho labels: create, apply, remove, list by label",
                "Threads: mark or label every message in a thread",
            }, new[]
            {
                Fail("Forward", "Zoho's API answers 500 and sends nothing. Forward with dt-email over IMAP."),
                Fail("Filters", "Zoho's Mail API has no filter call; filters are made in Zoho Mail's web settings."),
                Fail("Unarchive with Zoho's own call", "It answers OK and leaves the message in Archive; dt-zoho moves the message out instead, which works."),
                Fail("Send or change a saved draft", "There is no call. Send with dt-zoho send and delete the draft; to change one, delete it and save it again."),
                Fail("List or cancel a scheduled message", "There is no call once a message is sent with --at."),
                NotTested("Mail Desk filing (live mode) on a real Zoho mailbox", "The Mail Desk read soren@devthrottle.com live in watch mode only, filing nothing. Archive and junk filing are proven against fakes of dt-zoho, not on a real mailbox."),
            }),
            Calendar: new MailDeskProviderArea(new[]
            {
                "List calendars and events",
                "Create, change and delete an event",
                "Repeating events: create, change or delete one occurrence, delete the series",
                "Reminders",
                "Invite people inside the organisation, and read their answers",
                "Several calendars, and sharing one for viewing",
                "Zoho Meeting on an event: a join link for guests, and a start link for the owner only",
            }, new[]
            {
                Fail("Invite people; invitation comes from the right address",
                    "Zoho drops an invitee outside the organisation without a word (dt-zoho exits 1 naming them), and the invitation comes From noreply@zohocalendar.ca."),
                Fail("Accept or decline an invitation", "Zoho's API answers 403 to the invitee. Answer it over CalDAV with dt-calendar respond."),
                NotTested("Free or busy times", "The key does not have the scope ZohoCalendar.freebusy.READ."),
                NotTested("Change a whole repeating series", "Not proven, and a change sent without the rule turned a test series into a single event, so dt-zoho refuses it: change one occurrence instead."),
                NotTested("Create or change an all-day event", "dt-zoho shows and deletes them; creating and changing one were not proven."),
            }),
            ProvenOn: "Proven 8 November 2026 through Zoho's own API in the Canada data centre, on soren@devthrottle.com: in the proof step, and in the real runs of the four dt-zoho pull requests. The Mail Desk zoho reader read that mailbox live, in watch mode, the same day. Other data centres follow Zoho's documented servers and were never run."),

        new(
            Provider: Imap,
            Name: "Any other provider (IMAP, SMTP and CalDAV: Purelymail, Fastmail, Migadu, any IMAP host)",
            Tool: "dt-email, dt-calendar",
            MailDesk: new MailDeskProviderMailDesk(MailDeskStates.NotYet, "Not yet."),
            Needs: new[]
            {
                "IMAP turned on for the mailbox, and an app password if the provider asks for one.",
                "The mailbox password in cc-secrets on every machine that may check it. It is never stored on the Gateway.",
                "A dt-email account for the mailbox on every machine that may check it (dt-email account add), naming that cc-secrets entry.",
                "Not Outlook: Microsoft 365 does not accept a password over IMAP and has no CalDAV. Use the outlook provider.",
            },
            Admin: "None: use the provider's own website.",
            Email: new MailDeskProviderArea(new[]
            {
                "List folders and messages, read a message, search",
                "Download attachments",
                "Send: plain, HTML, To, CC and BCC",
                "Send attachments",
                "Reply, reply-all and forward, keeping the thread",
                "Drafts",
                "Read and unread, flag",
                "Move, archive, delete",
                "Create folders",
            }, new[]
            {
                NotTested("Send from an alias", "No alias was set up to test with. Zoho refuses a From that is not one of the account's own addresses."),
            }),
            Calendar: new MailDeskProviderArea(new[]
            {
                "List calendars and events",
                "Create, change and delete an event",
                "Accept or decline an invitation (proven between two people in one Zoho organisation)",
            }, new[]
            {
                Fail("Invite people; invitation comes from the right address",
                    "Inside one Zoho organisation invitations arrive, but Zoho drops an attendee outside the organisation without a word, and the invitation comes From noreply@zohocalendar.ca."),
            }),
            ProvenOn: ProvenZoho),

        new(
            Provider: MailDeskWords.Providers.Gmail,
            Name: "Gmail",
            Tool: "cc-gmail",
            MailDesk: new MailDeskProviderMailDesk(MailDeskStates.NotYet, "Not yet."),
            Needs: new[]
            {
                "The Google sign-in, made with cc-gmail (cc-gmail auth), on every machine that may check it. It is never stored on the Gateway.",
            },
            Admin: "Sign-in setup: cc-gmail auth.",
            Email: new MailDeskProviderArea(new[]
            {
                "List folders and messages, read a message, search",
                "Send: plain, HTML, To, CC and BCC",
                "Send attachments",
                "Reply and reply-all, keeping the thread",
                "Drafts",
                "Mark read, mark unread, star",
                "Move, archive, delete to Trash",
                "Create labels",
                "Gmail labels: create, apply, search by label",
            }, new[]
            {
                Fail("Download attachments", "cc-gmail has no command for it, and read does not show attachments."),
                Fail("Forward", "cc-gmail has no forward command."),
                Fail("Send from an alias", "send, reply and draft have no From option."),
                Fail("Remove a star, mark read without opening", "No command removes a label from a message, and only read marks one read."),
                Fail("Delete permanently", "Refused by Google: the sign-in does not have the full mail scope. Trash empties itself after 30 days."),
                Fail("List messages by a label you made", "list -l fails for every label you made; search \"label:...\" works."),
            }),
            Calendar: new MailDeskProviderArea(new[]
            {
                "List calendars and events (on an account where the Google Calendar API is turned on)",
            }, new[]
            {
                Fail("Create, change and delete an event", "There is no command to change an event, and creating was not proven: the test account has the Google Calendar API turned off."),
                Fail("Invite people; invitation comes from the right address", "Not proven: the test account has the Google Calendar API turned off."),
                Fail("Accept or decline an invitation", "cc-gmail has no command for it."),
            }),
            ProvenOn: "Proven 7 November 2026 on soren@duksrevo.com, and read only on soren@centerconsulting.com."),

        new(
            Provider: MailDeskWords.Providers.Outlook,
            Name: "Outlook (Microsoft 365)",
            Tool: "cc-outlook",
            MailDesk: new MailDeskProviderMailDesk(MailDeskStates.NotYet, "Not yet."),
            Needs: new[]
            {
                "The Microsoft sign-in, made with cc-outlook (cc-outlook auth), on every machine that may check it. It is never stored on the Gateway.",
            },
            Admin: "Sign-in: cc-outlook auth.",
            Email: new MailDeskProviderArea(new[]
            {
                "List folders and messages, read a message, search",
                "Download attachments",
                "Send: plain, HTML, To, CC and BCC",
                "Send attachments",
                "Reply, reply-all and forward, keeping the thread",
                "Drafts",
                "Flag on and off",
                "Move, archive, delete",
                "Create folders",
                "Outlook categories",
            }, new[]
            {
                Fail("Send from an alias", "send, reply, forward and draft have no From option."),
                Fail("Mark unread", "read marks a message read; no command marks one unread."),
                Fail("List or search a folder you made", "list -f and search -f cannot open a folder made by create-folder, though move finds it."),
            }),
            Calendar: new MailDeskProviderArea(new[]
            {
                "List calendars and events",
                "Create and delete an event",
                "Invite people; the invitation comes from the right address",
            }, new[]
            {
                Fail("Change an event", "Changing the subject works, but changing the time fails, and changing the location crashes."),
                Fail("Accept or decline an invitation", "calendar respond crashes (devthrottle issue 4771)."),
            }),
            ProvenOn: "Proven 7 November 2026 on email@devthrottle.com's Microsoft 365 mailbox, and with reads, one send and the accept or decline test on soren.frederiksen@mindzie.com."),
    };
}

/// <summary>One kind of mailbox: its tool, what was proven for email and calendar, the Mail Desk, and what it needs.</summary>
public sealed record MailDeskProvider(
    string Provider,
    string Name,
    string Tool,
    MailDeskProviderMailDesk MailDesk,
    IReadOnlyList<string> Needs,
    string Admin,
    MailDeskProviderArea Email,
    MailDeskProviderArea Calendar,
    string ProvenOn);

/// <summary>Whether the Mail Desk reads this provider (<c>yes</c> or <c>not-yet</c>), in words.</summary>
public sealed record MailDeskProviderMailDesk(string State, string Label);

/// <summary>What was proven working, and what failed or was not tested, each with why, and the counts in words.</summary>
public sealed record MailDeskProviderArea(IReadOnlyList<string> Works, IReadOnlyList<MailDeskProviderFeature> NotWorking)
{
    /// <summary>How many features failed.</summary>
    public int Failed => NotWorking.Count(f => f.State == MailDeskProviders.States.Fail);

    /// <summary>How many features were not tested.</summary>
    public int NotTested => NotWorking.Count(f => f.State == MailDeskProviders.States.NotTested);

    /// <summary>"9 work, 1 fail, 1 not tested": works always, each other count when it is not zero. A feature in a
    /// state this list does not name is refused rather than left out of the count.</summary>
    public string Summary
    {
        get
        {
            var unknown = NotWorking.FirstOrDefault(f => f.State != MailDeskProviders.States.Fail && f.State != MailDeskProviders.States.NotTested);
            if (unknown is not null)
                throw new InvalidOperationException($"The feature '{unknown.Feature}' is in the state '{unknown.State}', which has no words.");
            var words = new List<string> { $"{Works.Count} work" };
            if (Failed > 0) words.Add($"{Failed} fail");
            if (NotTested > 0) words.Add($"{NotTested} not tested");
            return string.Join(", ", words);
        }
    }
}

/// <summary>A feature that was not proven working: <c>fail</c> or <c>not-tested</c>, and why.</summary>
public sealed record MailDeskProviderFeature(string Feature, string State, string Why);
