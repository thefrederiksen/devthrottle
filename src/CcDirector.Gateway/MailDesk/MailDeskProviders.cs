namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// WHAT IS SUPPORTED, BY PROVIDER - THE ONE LIST (mission "Zoho Mail Skill", sections 1 to 4). Each kind of mailbox, the
/// tool an agent uses for it, what that tool was PROVEN to do for email and calendar, whether the Mail Desk can read it,
/// and what the provider needs before a mailbox can be added. <c>GET /gateway/mail-desk/providers</c> serves it,
/// <c>cc-devthrottle maildesk providers</c> prints it, and the email skill sends an agent to that command; nothing else
/// holds a copy, so what we say and what we ship cannot disagree.
///
/// NOTHING IS LISTED AS WORKING ON TRUST. A feature is in <c>Works</c> only when the proof step showed it working, with
/// evidence (<c>_briefs/email-tools/proof/prover-a.md</c> and <c>prover-b.md</c>, 7 and 8 November 2026). A feature that
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
    public const string Zoho = "zoho";
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
            MailDesk: new MailDeskProviderMailDesk(MailDeskStates.NotYet,
                "Not yet. A Mail Desk reader was proven reading a Zoho inbox (over IMAP, read only), and the zoho provider is being built."),
            Needs: new[]
            {
                "IMAP turned on for the mailbox. It is off for a new Zoho user; switch it on in the Zoho Mail settings.",
                "The mailbox password in cc-secrets on every machine that may check it. It is never stored on the Gateway.",
            },
            Admin: "Not tested: Zoho's admin API (domains, users, aliases, groups, the IMAP switch, forwarding, vacation reply, signatures) needs an API sign-in the owner has not created yet.",
            Email: new MailDeskProviderArea(Array.Empty<string>(), new[]
            {
                NotTested("Everything in Zoho's own Mail API", "Zoho's API needs an API sign-in the owner has not created yet. Until then a Zoho mailbox works as an IMAP mailbox: see the imap provider."),
            }),
            Calendar: new MailDeskProviderArea(Array.Empty<string>(), new[]
            {
                NotTested("Everything in Zoho's own Calendar API", "Zoho's API needs an API sign-in the owner has not created yet. Until then a Zoho calendar works over CalDAV: see the imap provider."),
            }),
            ProvenOn: "Not proven yet: Zoho's own API was not tested."),

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
