using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// THE CHANNELS EACH ACCOUNT'S MAIL DESK SCREENS (Mail Desk mission, phase 1, contract sections 1 and 2). A channel is
/// one mailbox, the machines that check it in order of preference, the start it belongs to, and its mode.
///
/// A NEW CHANNEL ALWAYS STARTS IN OBSERVE (design 5.8): it decides and records and changes nothing until a person
/// switches it to live. A REMOVED CHANNEL IS KEPT, marked removed, and its decisions stay; adding the address again
/// makes a new row. At most one active channel per (tenant, kind, address), held by the database, so two replicas
/// adding the same mailbox at once cannot both succeed.
///
/// EVERY CHANGE IS RECORDED in <c>mail_desk_list_changes</c> - kind <c>channel</c> or <c>mode</c>, no rules versions -
/// in the same transaction as the change itself.
/// </summary>
public sealed class MailDeskChannelStore
{
    /// <summary>The most machines one channel may name: the one that checks and its backups.</summary>
    public const int MaxMachines = 10;

    /// <summary>The longest a machine name may be.</summary>
    public const int MaxMachineLength = 128;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public MailDeskChannelStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    // ---------- reads ----------

    /// <summary>The account's channels in the order they were added: the active ones, or with
    /// <paramref name="includeRemoved"/> every one ever added.</summary>
    public IReadOnlyList<MailDeskChannelEntity> List(TenantId tenant, bool includeRemoved = false)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.MailDeskChannels.AsNoTracking();
            if (!includeRemoved) rows = rows.Where(c => c.RemovedUtc == null);
            return rows.OrderBy(c => c.CreatedUtc).ThenBy(c => c.Id).ToList();
        }
    }

    /// <summary>The active channel for <paramref name="address"/> (any spelling of it), or null.</summary>
    public MailDeskChannelEntity? FindActive(TenantId tenant, string address, string kind = MailDeskWords.ChannelKinds.Email)
    {
        if (!MailDeskAddresses.TryAddress(address, "address", out var folded, out _)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ActiveRow(ctx, kind, folded, tracked: false);
        }
    }

    /// <summary>The machine names a channel's row holds, in order of preference.</summary>
    public static IReadOnlyList<string> Machines(MailDeskChannelEntity channel)
        => JsonSerializer.Deserialize<List<string>>(channel.MachinesJson)
           ?? throw new InvalidOperationException($"Mail Desk channel {channel.Id} holds no machine list.");

    // ---------- writes ----------

    /// <summary>
    /// Add a channel for <paramref name="address"/>, checked from <paramref name="machines"/> in that order, in
    /// observe mode. Refused when the address, the provider or the machines are not usable, or when an active channel
    /// already has the address.
    /// </summary>
    public MailDeskChannelWrite Add(TenantId tenant, string? address, string? provider, IReadOnlyList<string>? machines,
        MailDeskWho who, string? why, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(who);
        var reason = MailDeskWho.RequireWhy(why);
        if (!MailDeskAddresses.TryAddress(address, "address", out var folded, out var addressRefusal))
            return MailDeskChannelWrite.Refused(MailDeskChannelOutcome.BadAddress, addressRefusal!);
        var foldedProvider = (provider ?? "").Trim().ToLowerInvariant();
        if (!MailDeskWords.IsOneOf(foldedProvider, MailDeskWords.Providers.All))
            return MailDeskChannelWrite.Refused(MailDeskChannelOutcome.BadProvider,
                $"provider '{provider}' is not one the Mail Desk reads; give {MailDeskWords.InWords(MailDeskWords.Providers.All)}");
        if (WhyNotMachines(machines, out var foldedMachines) is { } machineRefusal)
            return MailDeskChannelWrite.Refused(MailDeskChannelOutcome.BadMachines, machineRefusal);

        var kind = MailDeskWords.ChannelKinds.Email;
        var channel = new MailDeskChannelEntity
        {
            TenantId = tenant.Value,
            Kind = kind,
            Provider = foldedProvider,
            Address = folded,
            MachinesJson = JsonSerializer.Serialize(foldedMachines),
            Factory = MailDeskWords.DefaultFactory,
            Seat = MailDeskWords.DefaultSeat,
            Mode = MailDeskWords.Modes.Observe,
            CreatedUtc = nowUtc,
            CreatedBy = who.Id,
            ChangedUtc = nowUtc,
            ChangedBy = who.Id,
        };
        var detail = JsonSerializer.Serialize(new { provider = foldedProvider, machines = foldedMachines, mode = channel.Mode });
        var change = new MailDeskChange(MailDeskWords.ChangeKinds.Channel, MailDeskWords.ChangeActions.Add, folded, detail, who, reason);

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (ActiveRow(ctx, kind, folded, tracked: false) is not null)
                return AlreadyActive(folded);
            ctx.MailDeskChannels.Add(channel);
            ctx.MailDeskListChanges.Add(change.ToEntity(tenant.Value, nowUtc, null, null));
            try
            {
                ctx.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                ctx.ChangeTracker.Clear();
                // Another replica added the same address first: the unique index over active rows refused this one.
                if (ActiveRow(ctx, kind, folded, tracked: false) is not null) return AlreadyActive(folded);
                FileLog.Write($"[MailDeskChannelStore] Add FAILED for a channel and no other writer holds the address: {ex.Message}");
                throw;
            }
        }
        FileLog.Write($"[MailDeskChannelStore] Add: channel {channel.Id} ({foldedProvider}, {foldedMachines.Count} machine(s)) by {who.Kind} {who.Id}");
        return MailDeskChannelWrite.Done(MailDeskChannelOutcome.Added, channel);
    }

    /// <summary>Remove the active channel for <paramref name="address"/>. The row is kept, marked removed, and its
    /// decisions stay.</summary>
    public MailDeskChannelWrite Remove(TenantId tenant, string? address, MailDeskWho who, string? why, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(who);
        var reason = MailDeskWho.RequireWhy(why);
        if (!MailDeskAddresses.TryAddress(address, "address", out var folded, out var addressRefusal))
            return MailDeskChannelWrite.Refused(MailDeskChannelOutcome.BadAddress, addressRefusal!);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (ActiveRow(ctx, MailDeskWords.ChannelKinds.Email, folded, tracked: true) is not { } channel)
                return NoSuchChannel(folded);
            channel.RemovedUtc = nowUtc;
            channel.RemovedBy = who.Id;
            channel.RemovedWhy = reason;
            channel.ChangedUtc = nowUtc;
            channel.ChangedBy = who.Id;
            var detail = JsonSerializer.Serialize(new { provider = channel.Provider, mode = channel.Mode });
            var change = new MailDeskChange(MailDeskWords.ChangeKinds.Channel, MailDeskWords.ChangeActions.Remove, folded, detail, who, reason);
            ctx.MailDeskListChanges.Add(change.ToEntity(tenant.Value, nowUtc, null, null));
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskChannelStore] Remove: channel {channel.Id} by {who.Kind} {who.Id}");
            return MailDeskChannelWrite.Done(MailDeskChannelOutcome.Removed, channel);
        }
    }

    /// <summary>Set the mode of the active channel for <paramref name="address"/> to <paramref name="mode"/>
    /// (<c>observe</c> or <c>live</c>), recording the change from and to.</summary>
    public MailDeskChannelWrite SetMode(TenantId tenant, string? address, string? mode, MailDeskWho who, string? why, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(who);
        var reason = MailDeskWho.RequireWhy(why);
        if (!MailDeskAddresses.TryAddress(address, "address", out var folded, out var addressRefusal))
            return MailDeskChannelWrite.Refused(MailDeskChannelOutcome.BadAddress, addressRefusal!);
        var foldedMode = (mode ?? "").Trim().ToLowerInvariant();
        if (!MailDeskWords.IsOneOf(foldedMode, MailDeskWords.Modes.All))
            return MailDeskChannelWrite.Refused(MailDeskChannelOutcome.BadMode,
                $"mode '{mode}' is not a Mail Desk mode; give {MailDeskWords.InWords(MailDeskWords.Modes.All)}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (ActiveRow(ctx, MailDeskWords.ChannelKinds.Email, folded, tracked: true) is not { } channel)
                return NoSuchChannel(folded);
            var from = channel.Mode;
            channel.Mode = foldedMode;
            channel.ChangedUtc = nowUtc;
            channel.ChangedBy = who.Id;
            var detail = JsonSerializer.Serialize(new { from, to = foldedMode });
            var change = new MailDeskChange(MailDeskWords.ChangeKinds.Mode, MailDeskWords.ChangeActions.Set, folded, detail, who, reason);
            ctx.MailDeskListChanges.Add(change.ToEntity(tenant.Value, nowUtc, null, null));
            ctx.SaveChanges();
            FileLog.Write($"[MailDeskChannelStore] SetMode: channel {channel.Id} {from} -> {foldedMode} by {who.Kind} {who.Id}");
            return MailDeskChannelWrite.Done(MailDeskChannelOutcome.ModeSet, channel);
        }
    }

    // ---------- the rules ----------

    /// <summary>Why <paramref name="machines"/> cannot be a channel's machines, or null with them trimmed and in order.
    /// At least one; none blank; none twice (machine names compare without regard to case, as Windows names them).</summary>
    public static string? WhyNotMachines(IReadOnlyList<string>? machines, out List<string> folded)
    {
        folded = new List<string>();
        if (machines is null || machines.Count == 0)
            return "machines is empty: name at least one machine that checks this mailbox, and any backups after it";
        if (machines.Count > MaxMachines)
            return $"machines names {machines.Count}; a channel names at most {MaxMachines}";
        foreach (var (raw, i) in machines.Select((m, i) => (m, i)))
        {
            var name = (raw ?? "").Trim();
            if (name.Length == 0)
                return $"machines entry {i + 1} is blank; give the machine's name, or leave the entry out";
            if (name.Length > MaxMachineLength)
                return $"machines entry {i + 1} takes at most {MaxMachineLength} characters; this one is {name.Length}";
            if (folded.Contains(name, StringComparer.OrdinalIgnoreCase))
                return $"machines names '{name}' twice; name each machine once, in order of preference";
            folded.Add(name);
        }
        return null;
    }

    private static MailDeskChannelEntity? ActiveRow(GatewayDbContext ctx, string kind, string address, bool tracked)
    {
        var rows = tracked ? ctx.MailDeskChannels : ctx.MailDeskChannels.AsNoTracking();
        return rows.FirstOrDefault(c => c.Kind == kind && c.Address == address && c.RemovedUtc == null);
    }

    private static MailDeskChannelWrite AlreadyActive(string address)
    {
        FileLog.Write("[MailDeskChannelStore] Add: REFUSED - an active channel already has that address");
        return MailDeskChannelWrite.Refused(MailDeskChannelOutcome.AlreadyActive,
            $"'{address}' is already a Mail Desk channel; remove it first to add it again, or change its mode");
    }

    private static MailDeskChannelWrite NoSuchChannel(string address)
        => MailDeskChannelWrite.Refused(MailDeskChannelOutcome.NoSuchChannel,
            $"'{address}' is not an active Mail Desk channel; 'maildesk channel list' shows the ones there are");
}

/// <summary>What a channel write did.</summary>
public enum MailDeskChannelOutcome
{
    Added,
    Removed,
    ModeSet,
    BadAddress,
    BadProvider,
    BadMachines,
    BadMode,
    AlreadyActive,
    NoSuchChannel,
}

/// <summary>The result of a channel write: the channel as it now stands, or why nothing was written.</summary>
public sealed record MailDeskChannelWrite
{
    public MailDeskChannelOutcome Outcome { get; private init; }
    public MailDeskChannelEntity? Channel { get; private init; }
    public string? Refusal { get; private init; }
    public bool Succeeded => Channel is not null;

    internal static MailDeskChannelWrite Done(MailDeskChannelOutcome outcome, MailDeskChannelEntity channel)
        => new() { Outcome = outcome, Channel = channel };

    internal static MailDeskChannelWrite Refused(MailDeskChannelOutcome outcome, string refusal)
        => new() { Outcome = outcome, Refusal = refusal };
}
