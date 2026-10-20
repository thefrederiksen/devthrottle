namespace CcDirector.Gateway.Data.Entities;

/// <summary>
/// ONE CHANNEL THE MAIL DESK SCREENS, in the <c>mail_desk_channels</c> table (Mail Desk mission, phase 1, contract
/// section 2). For now a channel is one mailbox: its kind is <c>email</c> and its provider <c>outlook</c>.
///
/// A REMOVED CHANNEL IS KEPT. Removing marks the row (<see cref="RemovedUtc"/>, <see cref="RemovedBy"/>,
/// <see cref="RemovedWhy"/>) and its decisions stay, because the owner may still need to read what the Mail Desk did
/// with that mailbox's mail. Adding the same address again makes a NEW row, so at most one row per (tenant, kind,
/// address) is active - the database holds that with a unique index over the active rows only.
/// </summary>
public sealed class MailDeskChannelEntity : GatewayMintedKeyEntity
{
    /// <summary>The kind of channel: <c>email</c> (<see cref="MailDesk.MailDeskWords.ChannelKinds"/>).</summary>
    public string Kind { get; set; } = "";

    /// <summary>Who holds the mailbox: <c>outlook</c> (<see cref="MailDesk.MailDeskWords.Providers"/>).</summary>
    public string Provider { get; set; } = "";

    /// <summary>The mailbox address, lower case.</summary>
    public string Address { get; set; } = "";

    /// <summary>The machines that check this channel, as a JSON array of machine names in order of preference: the
    /// first is the one that checks, the others are its backups. At least one.</summary>
    public string MachinesJson { get; set; } = "[]";

    /// <summary>The factory that holds the account's Mail Desk start - <c>mail-desk</c> unless said otherwise.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The seat of that factory the Mail Desk start belongs to - <c>mail-desk</c> unless said otherwise.</summary>
    public string Seat { get; set; } = "";

    /// <summary><c>observe</c> (decides and records, changes nothing) or <c>live</c>. A new channel is always
    /// <c>observe</c>.</summary>
    public string Mode { get; set; } = "";

    /// <summary>When the channel was added (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Who added it: the person's identifier, or the session's id.</summary>
    public string CreatedBy { get; set; } = "";

    /// <summary>When the channel last changed (UTC) - its mode, or its removal. The creation time until then.</summary>
    public DateTime ChangedUtc { get; set; }

    /// <summary>Who changed it last: the person's identifier, or the session's id.</summary>
    public string ChangedBy { get; set; } = "";

    /// <summary>When the channel was removed (UTC), or null while it is active.</summary>
    public DateTime? RemovedUtc { get; set; }

    /// <summary>Who removed it, or null while it is active.</summary>
    public string? RemovedBy { get; set; }

    /// <summary>Why it was removed, as they said it, or null while it is active.</summary>
    public string? RemovedWhy { get; set; }
}

/// <summary>
/// ONE VERSION OF ONE ACCOUNT'S MAIL DESK RULES, in the <c>mail_desk_rules</c> table (contract sections 2 and 3). The
/// document is the fixed rules, the allow-list, the block list and the routes, as one JSON text.
///
/// APPEND-ONLY: a row is never updated or deleted, and every save is a new row numbered one above the account's
/// newest. No row at all means the empty document at version 0. The unique index on (tenant, version) is the
/// concurrency control: two Gateway replicas saving at the same moment both compute the same next version, and the
/// second insert cannot exist.
/// </summary>
public sealed class MailDeskRulesVersionEntity : GatewayMintedKeyEntity
{
    /// <summary>The version, counting from 1 within the account.</summary>
    public int Version { get; set; }

    /// <summary>The whole rules document at this version, as JSON. Written once and never edited.</summary>
    public string Document { get; set; } = "";

    /// <summary>When this version was saved (UTC).</summary>
    public DateTime SavedUtc { get; set; }

    /// <summary>Who saved it: <c>person</c> or <c>session</c>.</summary>
    public string SavedByKind { get; set; } = "";

    /// <summary>Who saved it: the person's identifier, or the session's id.</summary>
    public string SavedBy { get; set; } = "";
}

/// <summary>
/// ONE RECORDED CHANGE TO THE MAIL DESK, in the <c>mail_desk_list_changes</c> table (contract section 2; the owner's
/// decision D7: "freely, every change recorded and shown to you"). A change to the rules carries the version before
/// and after it; a change to a channel or its mode carries neither. Every row says who made the change and why.
/// Append-only.
/// </summary>
public sealed class MailDeskListChangeEntity : GatewayMintedKeyEntity
{
    /// <summary>When the change was made (UTC).</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>What was changed: <c>rule</c>, <c>allow</c>, <c>block</c>, <c>route</c>, <c>channel</c> or <c>mode</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary><c>add</c>, <c>remove</c> or <c>set</c>.</summary>
    public string Action { get; set; } = "";

    /// <summary>What it was done to: the pattern, the rule's name, <c>factory/seat</c>, or the channel's address.</summary>
    public string Subject { get; set; } = "";

    /// <summary>A JSON text of what was added or removed, or <c>{"from":"observe","to":"live"}</c> for a mode.</summary>
    public string Detail { get; set; } = "{}";

    /// <summary>The rules version this change was made on, or null for a channel or mode change.</summary>
    public int? VersionBefore { get; set; }

    /// <summary>The rules version this change wrote, or null for a channel or mode change.</summary>
    public int? VersionAfter { get; set; }

    /// <summary>Who made it: <c>person</c> or <c>session</c>.</summary>
    public string WhoKind { get; set; } = "";

    /// <summary>The person's identifier, or the session's id.</summary>
    public string WhoId { get; set; } = "";

    /// <summary>The session's name at the moment of the change, so the history says which session it was in words.
    /// Null for a person, and for a session that had no name.</summary>
    public string? WhoName { get; set; }

    /// <summary>Why, as they said it. Never blank.</summary>
    public string Why { get; set; } = "";
}

/// <summary>
/// ONE SCREENED EMAIL, in the <c>mail_desk_decisions</c> table (contract section 2, design 5.6): which layer decided,
/// what it decided and why, in which mode, and - in live mode - what was done.
///
/// NO BODY TEXT, EVER. There is deliberately no column for the email's body or its preview: the preview is used for
/// the model question and then thrown away. <c>MailDeskDecisionStoreTests</c> lists this type's properties so a body
/// column cannot be added without a test failing.
///
/// One row per (tenant, channel, message key), held by a unique index, so a check that reports the same message twice
/// records one decision.
/// </summary>
public sealed class MailDeskDecisionEntity : GatewayMintedKeyEntity
{
    /// <summary>The channel the email came in on (<see cref="MailDeskChannelEntity"/>'s id). A removed channel keeps
    /// its decisions.</summary>
    public Guid ChannelId { get; set; }

    /// <summary>The provider's own key for the message, unique within the channel.</summary>
    public string MessageKey { get; set; } = "";

    /// <summary>When the mailbox received the email (UTC).</summary>
    public DateTime ReceivedUtc { get; set; }

    /// <summary>The sender's address, lower case.</summary>
    public string SenderAddress { get; set; } = "";

    /// <summary>The email's subject line.</summary>
    public string Subject { get; set; } = "";

    /// <summary>The layer that decided (<see cref="MailDesk.MailDeskWords.Layers"/>).</summary>
    public string Layer { get; set; } = "";

    /// <summary>What it decided (<see cref="MailDesk.MailDeskWords.Decisions"/>).</summary>
    public string Decision { get; set; } = "";

    /// <summary>The factory the email was routed to, when the decision is a route.</summary>
    public string? RouteFactory { get; set; }

    /// <summary>The seat the email was routed to, when the decision is a route.</summary>
    public string? RouteSeat { get; set; }

    /// <summary>Why: the rule's name, the list entry, or the model's label.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The model's confidence, 0 to 1, when a model decided.</summary>
    public double? Confidence { get; set; }

    /// <summary>What the model call cost, in millionths of a US dollar, when there was one. Money is held as whole
    /// small units, never as a decimal (the model's bare-decimal ban in <c>GatewayDbContext</c>).</summary>
    public long? CostMicros { get; set; }

    /// <summary>The mode the decision was made in: <c>observe</c> or <c>live</c>.</summary>
    public string Mode { get; set; } = "";

    /// <summary>What was done, in live mode: <c>filed</c>, <c>already-gone</c>, <c>seat-started</c> or <c>failed</c>. Null in observe mode.</summary>
    public string? Outcome { get; set; }

    /// <summary>More about what was done, when there is more to say (the failure's sentence, the session started).</summary>
    public string? OutcomeDetail { get; set; }

    /// <summary>When the Mail Desk decided (UTC).</summary>
    public DateTime DecidedUtc { get; set; }

    /// <summary>The version of the account's rules document the email was decided by (phase 3, section 8); 0 is the
    /// empty document.</summary>
    public int RulesVersion { get; set; }

    /// <summary>How many times the email has been screened (phase 3, sections 1 and 8): 1 for the first, raised each
    /// time a <c>failed</c> decision is decided again. The third failed screening keeps it for the owner.</summary>
    public int AttemptCount { get; set; }
}

/// <summary>
/// ONE EMAIL ROUTED TO A SEAT, in the <c>mail_desk_routed</c> table (contract section 2, design 5.6). Live mode only:
/// the seat says it is <c>done</c>, or returns it as "not for me" with what it knows. Phase 1 builds the table; phase 4
/// writes and reads it.
/// </summary>
public sealed class MailDeskRoutedEntity : GatewayMintedKeyEntity
{
    /// <summary>The decision that routed the email (<see cref="MailDeskDecisionEntity"/>'s id).</summary>
    public Guid DecisionId { get; set; }

    /// <summary>The factory it was routed to.</summary>
    public string Factory { get; set; } = "";

    /// <summary>The seat it was routed to.</summary>
    public string Seat { get; set; } = "";

    /// <summary>The session that took it, once one did.</summary>
    public string? SessionId { get; set; }

    /// <summary><c>open</c>, <c>done</c> or <c>returned</c>.</summary>
    public string State { get; set; } = "";

    /// <summary>What the seat said when it returned the email.</summary>
    public string? ReturnNote { get; set; }

    /// <summary>How many times the email has been returned. A second return goes to the owner.</summary>
    public int ReturnedCount { get; set; }

    /// <summary>When the row was written (UTC).</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>When the row last changed (UTC).</summary>
    public DateTime ChangedUtc { get; set; }
}

/// <summary>
/// ONE CHECK OF ONE MAILBOX THAT IS WORTH KEEPING, in the <c>mail_desk_channel_checks</c> table (Mail Desk mission,
/// phase 3 contract, section 6.2: one mailbox failing does not stop the others). A row is written for every check of
/// the mailbox that failed, and for a good check whenever the mailbox's newest row is not already a good one - the
/// first good check after a failure, which clears the problem, and the very first good check of the mailbox, which is
/// how the channel row knows it was ever read. A good check after a good check writes nothing, so a mailbox that keeps
/// working costs no rows.
///
/// Failures in a row are ONE problem (<see cref="MailDesk.MailDeskProblems"/>), named by the first failed row of the
/// run; resolving it by hand marks that first row, and only that row carries the three resolve columns.
/// </summary>
public sealed class MailDeskChannelCheckEntity : GatewayMintedKeyEntity
{
    /// <summary>The channel checked (<see cref="MailDeskChannelEntity"/>'s id).</summary>
    public Guid ChannelId { get; set; }

    /// <summary>The trigger run whose report carried this check, when the report came through one.</summary>
    public Guid? TriggerRunId { get; set; }

    /// <summary>When the check ran (UTC).</summary>
    public DateTime CheckedUtc { get; set; }

    /// <summary>The machine the check ran on.</summary>
    public string Machine { get; set; } = "";

    /// <summary>True when the check read the mailbox; false when it could not.</summary>
    public bool Ok { get; set; }

    /// <summary>The check's own sentence for the failure, which names the fix. Null on a good check.</summary>
    public string? Sentence { get; set; }

    /// <summary>The check's second line about the failure, when it gave one. Null on a good check.</summary>
    public string? Detail { get; set; }

    /// <summary>When the problem this row starts was resolved by hand (UTC), or null.</summary>
    public DateTime? ResolvedUtc { get; set; }

    /// <summary>Who resolved it: "you" for a person, otherwise the session.</summary>
    public string? ResolvedBy { get; set; }

    /// <summary>The one-line reason it was resolved with.</summary>
    public string? ResolvedReason { get; set; }
}
