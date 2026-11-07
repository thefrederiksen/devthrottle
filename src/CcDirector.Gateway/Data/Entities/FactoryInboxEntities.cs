namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE MESSAGE LEFT IN A FACTORY'S INBOX, in the <c>factory_inbox_messages</c> table (Factory Inbox mission, phase 1). The
/// owner leaves it from the phone or the Cockpit, typed or spoken; the factory's Secretary claims it, marks it received
/// and hands it to the Boss in a bundle; the Boss decides it (<see cref="FactoryInboxDecisionEntity"/>).
///
/// NOTHING IS DELETED. A message is logged the moment it is sent, so a missed one can be found again, and the decision
/// on it stays beside it. Every state word shown for it is folded on the Gateway (<c>Factory.Inbox.FactoryInboxWords</c>).
///
/// A CLAIM LAPSES WHEN ITS SESSION ENDS, as a dev report action claim does: <see cref="ClaimSessionId"/> holds the
/// Secretary that received the message until it hands it on in a bundle, and a claim held by a session the Gateway has
/// recorded as ended may be taken by the next one.
/// </summary>
public sealed class FactoryInboxMessageEntity : GatewayMintedKeyEntity
{
    /// <summary>The factory the message was left in, in the Gateway's one spelling of a factory id.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The message as it is kept and shown. For a spoken message: the speech model's output with only the
    /// owner's hand-listed corrections applied (<see cref="CorrectionsJson"/>), never a reword.</summary>
    public string Text { get; set; } = "";

    /// <summary>For a spoken message, the speech model's output byte for byte, kept beside <see cref="Text"/>. Null for a
    /// typed message.</summary>
    public string? RawText { get; set; }

    /// <summary>For a spoken message, the hand-listed corrections applied to <see cref="RawText"/> to make
    /// <see cref="Text"/>, as a JSON array of <c>{find, replace}</c>. Null for a typed message.</summary>
    public string? CorrectionsJson { get; set; }

    /// <summary>The owner's Good or Bad note: <c>none</c>, <c>good</c> or <c>bad</c>.</summary>
    public string Note { get; set; } = "";

    /// <summary>How it was put in: <c>typed</c> or <c>spoken</c>.</summary>
    public string Source { get; set; } = "";

    /// <summary>Where it was sent from: <c>phone</c>, <c>cockpit</c> or <c>session</c>.</summary>
    public string Surface { get; set; } = "";

    /// <summary>Who sent it: the device id of the owner's phone or browser.</summary>
    public string SentBy { get; set; } = "";

    /// <summary>When it was sent (UTC).</summary>
    public DateTime SentAtUtc { get; set; }

    /// <summary><c>sent</c>, <c>received</c>, <c>with-boss</c> or <c>decided</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>When a Secretary first marked it received (UTC), or null before then.</summary>
    public DateTime? ReceivedAtUtc { get; set; }

    /// <summary>The Secretary session that marked it received, or null before then.</summary>
    public string? ReceivedBySessionId { get; set; }

    /// <summary>The Secretary session holding it until it is handed on in a bundle, or null.</summary>
    public string? ClaimSessionId { get; set; }

    /// <summary>When that claim was taken (UTC), or null.</summary>
    public DateTime? ClaimedAtUtc { get; set; }

    /// <summary>The bundle it was handed to the Boss in, or null before then.</summary>
    public Guid? BundleId { get; set; }

    /// <summary>The message of the same factory this one repeats, when the Secretary merged it, or null.</summary>
    public Guid? MergedIntoId { get; set; }

    /// <summary>What the message is about, attached by the Secretary: a JSON array of <c>{kind, ref}</c> where the kind is
    /// <c>run</c>, <c>report</c> or <c>record</c>. Null when it attached none.</summary>
    public string? LinksJson { get; set; }

    /// <summary>When the owner dealt with the message outside the normal inbox flow (UTC), or null. When set, the message
    /// leaves the factory inbox stuck alert even if it was never received or decided.</summary>
    public DateTime? OwnerDealtWithAtUtc { get; set; }
}

/// <summary>
/// ONE BUNDLE A SECRETARY HANDS TO ITS BOSS, in the <c>factory_inbox_bundles</c> table. The messages it holds point at it
/// (<see cref="FactoryInboxMessageEntity.BundleId"/>). It is open until every message in it is decided.
/// </summary>
public sealed class FactoryInboxBundleEntity : GatewayMintedKeyEntity
{
    /// <summary>The factory the bundle belongs to.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The Secretary session that handed it over.</summary>
    public string HandedBySessionId { get; set; } = "";

    /// <summary>When it was handed over (UTC).</summary>
    public DateTime HandedAtUtc { get; set; }

    /// <summary>The Boss session that first read it, or null before one has.</summary>
    public string? ReadBySessionId { get; set; }

    /// <summary>When a Boss first read it (UTC), or null.</summary>
    public DateTime? ReadAtUtc { get; set; }

    /// <summary>When its last message was decided (UTC), or null while it is open.</summary>
    public DateTime? ClosedAtUtc { get; set; }
}

/// <summary>
/// THE BOSS'S DECISION ON ONE MESSAGE, in the <c>factory_inbox_decisions</c> table: delegated, to do, noted, declined,
/// cannot be done, or pushed back. One per message, held by a unique index, and never changed.
/// </summary>
public sealed class FactoryInboxDecisionEntity : GatewayMintedKeyEntity
{
    /// <summary>The message decided.</summary>
    public Guid MessageId { get; set; }

    /// <summary>The factory, the message's own.</summary>
    public string Factory { get; set; } = "";

    /// <summary><c>delegated</c>, <c>todo</c>, <c>noted</c>, <c>declined</c>, <c>cannot</c> or <c>pushback</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Why, in the Boss's words. Required for declined, cannot and pushback; null otherwise when not given.</summary>
    public string? Why { get; set; }

    /// <summary>For delegated, the session the Boss started to do it. Null for every other kind.</summary>
    public string? DelegatedSessionId { get; set; }

    /// <summary>For to do, the to-do item it added. Null for every other kind.</summary>
    public Guid? TodoId { get; set; }

    /// <summary>The Boss session that decided it.</summary>
    public string DecidedBySessionId { get; set; } = "";

    /// <summary>When it was decided (UTC).</summary>
    public DateTime DecidedAtUtc { get; set; }
}

/// <summary>
/// ONE ITEM ON A FACTORY'S TO-DO LIST, in the <c>factory_todos</c> table: something the Boss chose to do later. Added by a
/// <c>todo</c> decision on a factory inbox message.
/// </summary>
public sealed class FactoryTodoEntity : GatewayMintedKeyEntity
{
    /// <summary>The factory whose list it is on.</summary>
    public string Factory { get; set; } = "";

    /// <summary>What is to be done: the message's text.</summary>
    public string Text { get; set; } = "";

    /// <summary>The factory inbox message it came from, or null.</summary>
    public Guid? FromMessageId { get; set; }

    /// <summary>The Boss session that added it.</summary>
    public string AddedBySessionId { get; set; } = "";

    /// <summary>When it was added (UTC).</summary>
    public DateTime AddedAtUtc { get; set; }
}

/// <summary>
/// WHAT THE WAKE KNOWS OF ONE FACTORY, in the <c>factory_inbox_wakes</c> table (Factory Inbox mission, phase 2): one row
/// per factory, written only by <c>Factory.Inbox.FactoryInboxWake</c>.
///
/// THE ONE-START-PER-FACTORY RULE IS THIS ROW. A start of the factory's Secretary, or of its Boss, is claimed by one
/// conditional update on <see cref="Version"/> - the update that finds the version it read wins, every other writer
/// finds none - so two Gateway processes, or a Gateway restarted in the middle of a start, never start two. The row also
/// remembers the session each start returned (as a trigger remembers its last session), what the running Secretary and
/// Boss were last rung for, so nobody is rung twice for the same messages, and why the factory is waiting when nothing
/// could be started, in words the thread shows.
/// </summary>
public sealed class FactoryInboxWakeEntity : GatewayMintedKeyEntity
{
    /// <summary>The factory, in the Gateway's one spelling of a factory id. One row per factory of an account.</summary>
    public string Factory { get; set; } = "";

    /// <summary>Raised by every write. The claim of a start is a conditional update on the version that was read.</summary>
    public int Version { get; set; }

    /// <summary>The Secretary session the wake last started, or null before its start returned one.</summary>
    public string? SecretarySessionId { get; set; }

    /// <summary>When the wake claimed the Secretary's start (UTC). Set with no session id, a start is in flight, or its
    /// outcome is unknown; either holds the claim for the start grace. Null when no start is held.</summary>
    public DateTime? SecretaryStartedAtUtc { get; set; }

    /// <summary>The newest message (by its sent time) the running Secretary has been told of, by its opening or a ring.
    /// A ring is due only for a message sent after it.</summary>
    public DateTime? SecretaryToldUpToUtc { get; set; }

    /// <summary>The Boss session the wake last started, or null.</summary>
    public string? BossSessionId { get; set; }

    /// <summary>When the wake claimed the Boss's start (UTC), as <see cref="SecretaryStartedAtUtc"/>.</summary>
    public DateTime? BossStartedAtUtc { get; set; }

    /// <summary>The newest bundle (by its handed time) the running Boss has been told of.</summary>
    public DateTime? BossToldUpToUtc { get; set; }

    /// <summary>Why the factory's new messages wait for a Secretary, as a closed word (<c>FactoryInboxWords.Waiting</c>),
    /// or null when they do not wait.</summary>
    public string? SecretaryWaiting { get; set; }

    /// <summary>The detail the waiting sentence names: the computer with no Director, or the start's error.</summary>
    public string? SecretaryWaitingDetail { get; set; }

    /// <summary>Why the factory's bundles wait for a Boss, as a closed word, or null.</summary>
    public string? BossWaiting { get; set; }

    /// <summary>The detail the Boss's waiting sentence names.</summary>
    public string? BossWaitingDetail { get; set; }

    /// <summary>When the row was last written (UTC).</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
