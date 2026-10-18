using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// HOW EACH MAILBOX'S CHECKS WENT (Mail Desk mission, phase 3 contract, section 6.2: one mailbox failing does not stop
/// the others). The check reports each channel on its own - read, or a <c>failure</c> with the check's sentence - and
/// this store keeps what the problems list and the channel row need of it, in <c>mail_desk_channel_checks</c>.
///
/// WHAT IS WRITTEN (<see cref="Record"/>): every failed check of a mailbox, and a good check whenever the mailbox's
/// newest row is not already a good one. So the first good check after a failure is written - it clears the problem -
/// and so is the very first good check of a mailbox, which is how the channel row can say "signed in" from these rows
/// alone. A good check after a good check writes nothing.
///
/// The screening (phase 3, task F) calls <see cref="RecordAll"/> with what it parsed from one report; nothing here
/// reads a report itself.
/// </summary>
public sealed class MailDeskChannelCheckStore
{
    /// <summary>The longest a check's sentence or its detail is kept. Longer is cut: the row must still be kept.</summary>
    public const int MaxSentenceLength = 2000;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public MailDeskChannelCheckStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    // ---------- the write ----------

    /// <summary>
    /// Keep one mailbox's check, by the rule above. A failure carries the check's sentence; a good check carries none.
    /// The channel must be one of this account's channels (removed or not). Anything else is a caller defect and throws.
    /// </summary>
    public MailDeskChannelCheckWrite Record(TenantId tenant, MailDeskChannelCheckInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        Require(input);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return RecordOne(ctx, tenant, input);
        }
    }

    /// <summary>
    /// Keep every channel's check of one report, in the order given. What the screening calls once per report, with
    /// one input per channel the report carried.
    /// </summary>
    public IReadOnlyList<MailDeskChannelCheckWrite> RecordAll(TenantId tenant, IReadOnlyList<MailDeskChannelCheckInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            Require(input);
        }
        var duplicate = inputs.GroupBy(i => i.ChannelId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"One report checks each channel once; channel {duplicate.Key} is given {duplicate.Count()} times.", nameof(inputs));

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return inputs.Select(i => RecordOne(ctx, tenant, i)).ToList();
        }
    }

    private static MailDeskChannelCheckWrite RecordOne(GatewayDbContext ctx, TenantId tenant, MailDeskChannelCheckInput input)
    {
        if (!ctx.MailDeskChannels.AsNoTracking().Any(c => c.Id == input.ChannelId))
            throw new ArgumentException($"Mail Desk channel {input.ChannelId} is not a channel of this account.", nameof(input));

        var newest = ctx.MailDeskChannelChecks.AsNoTracking()
            .Where(c => c.ChannelId == input.ChannelId)
            .OrderByDescending(c => c.CheckedUtc).ThenByDescending(c => c.Id)
            .FirstOrDefault();
        if (input.Ok && newest is { Ok: true })
            return new MailDeskChannelCheckWrite(false, null);

        var row = new MailDeskChannelCheckEntity
        {
            TenantId = tenant.Value,
            ChannelId = input.ChannelId,
            TriggerRunId = input.TriggerRunId,
            CheckedUtc = DateTime.SpecifyKind(input.CheckedUtc, DateTimeKind.Utc),
            Machine = input.Machine.Trim(),
            Ok = input.Ok,
            Sentence = input.Ok ? null : Cut(input.Sentence!.Trim()),
            Detail = input.Ok || string.IsNullOrWhiteSpace(input.Detail) ? null : Cut(input.Detail.Trim()),
        };
        ctx.MailDeskChannelChecks.Add(row);
        ctx.SaveChanges();
        FileLog.Write($"[MailDeskChannelCheckStore] Record: {row.Id} on channel {row.ChannelId}: {(row.Ok ? (newest is null ? "first good check" : "good check after a failure") : "failed")} on {row.Machine}");
        return new MailDeskChannelCheckWrite(true, row);
    }

    // ---------- reads ----------

    /// <summary>Every kept check of the account, by channel, oldest first.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<MailDeskChannelCheckEntity>> ByChannel(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskChannelChecks.AsNoTracking().ToList()
                .GroupBy(c => c.ChannelId)
                .ToDictionary(g => g.Key,
                    g => (IReadOnlyList<MailDeskChannelCheckEntity>)g.OrderBy(c => c.CheckedUtc).ThenBy(c => c.Id).ToList());
        }
    }

    /// <summary>One kept check, or null when the account has no such row.</summary>
    public MailDeskChannelCheckEntity? Find(TenantId tenant, Guid id)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskChannelChecks.AsNoTracking().FirstOrDefault(c => c.Id == id);
        }
    }

    /// <summary>
    /// Mark the failed check <paramref name="id"/> resolved by hand. False when it is already resolved, so a second
    /// resolve changes nothing. Whether the row is the one that names its problem is the problems list's to say
    /// (<see cref="MailDeskProblems"/>); the caller asks it first.
    /// </summary>
    public bool Resolve(TenantId tenant, Guid id, string resolvedBy, string reason, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(resolvedBy))
            throw new ArgumentException("A resolve names who resolved it.", nameof(resolvedBy));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A resolve carries a reason.", nameof(reason));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.MailDeskChannelChecks.FirstOrDefault(c => c.Id == id)
                ?? throw new ArgumentException($"There is no Mail Desk mailbox check {id} in this account.", nameof(id));
            if (row.Ok)
                throw new InvalidOperationException($"Mail Desk mailbox check {id} read its mailbox; a good check is no problem to resolve.");
            if (row.ResolvedUtc is not null) return false;
            row.ResolvedUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
            row.ResolvedBy = resolvedBy.Trim();
            row.ResolvedReason = reason.Trim();
            ctx.SaveChanges();
        }
        FileLog.Write($"[MailDeskChannelCheckStore] Resolve: {id} by {resolvedBy}");
        return true;
    }

    private static void Require(MailDeskChannelCheckInput input)
    {
        if (input.ChannelId == Guid.Empty)
            throw new ArgumentException("A mailbox check names its channel.", nameof(input));
        if (string.IsNullOrWhiteSpace(input.Machine) || input.Machine.Trim().Length > MailDeskChannelStore.MaxMachineLength)
            throw new ArgumentException($"A mailbox check names the machine it ran on, at most {MailDeskChannelStore.MaxMachineLength} characters.", nameof(input));
        if (input.Ok && (input.Sentence is not null || input.Detail is not null))
            throw new ArgumentException("A good mailbox check carries no failure sentence or detail.", nameof(input));
        if (!input.Ok && string.IsNullOrWhiteSpace(input.Sentence))
            throw new ArgumentException("A failed mailbox check carries the check's own sentence, which names the fix.", nameof(input));
        if (input.CheckedUtc.Kind == DateTimeKind.Local)
            throw new ArgumentException("A mailbox check's time is UTC.", nameof(input));
    }

    private static string Cut(string text) => text.Length <= MaxSentenceLength ? text : text[..MaxSentenceLength];
}

/// <summary>
/// One channel's check from one report: the channel, the trigger run that carried the report, when and where it ran,
/// and - when it failed - the check's <c>failure.sentence</c> and <c>failure.detail</c> (phase 3 contract, section 6.1).
/// </summary>
public sealed record MailDeskChannelCheckInput(
    Guid ChannelId,
    Guid? TriggerRunId,
    DateTime CheckedUtc,
    string Machine,
    bool Ok,
    string? Sentence,
    string? Detail)
{
    /// <summary>A check that read the mailbox.</summary>
    public static MailDeskChannelCheckInput Read(Guid channelId, Guid? triggerRunId, DateTime checkedUtc, string machine)
        => new(channelId, triggerRunId, checkedUtc, machine, true, null, null);

    /// <summary>A check that could not read the mailbox, with the check's own sentence and detail.</summary>
    public static MailDeskChannelCheckInput Failed(Guid channelId, Guid? triggerRunId, DateTime checkedUtc, string machine,
        string sentence, string? detail)
        => new(channelId, triggerRunId, checkedUtc, machine, false, sentence, detail);
}

/// <summary>What <see cref="MailDeskChannelCheckStore.Record"/> did: whether it wrote a row, and the row.</summary>
public sealed record MailDeskChannelCheckWrite(bool Written, MailDeskChannelCheckEntity? Row);
