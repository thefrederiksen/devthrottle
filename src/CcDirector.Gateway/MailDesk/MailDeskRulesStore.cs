using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// EACH ACCOUNT'S MAIL DESK RULES, VERSIONED, AND THE RECORD OF EVERY CHANGE (Mail Desk mission, phase 1, contract
/// sections 2 and 3; the owner's decision D7: "freely, every change recorded and shown to you").
///
/// A SAVE NEVER OVERWRITES. Every save is a new version, numbered one above the account's newest, written in ONE
/// transaction with the change row that says who made it and why - so there is never a version nobody accounts for,
/// and never a change row for a version that was not kept. No version at all is the empty document at version 0, and
/// every old version still reads back.
///
/// TWO SAVES AT ONCE NEVER MAKE THE SAME VERSION. A save states the version it was made on; when that is no longer the
/// newest, it is refused as <see cref="MailDeskRulesOutcome.Stale"/> and nothing is written. Within one process the
/// lock decides; across the hosted Gateway's replicas the unique index on (tenant, version) decides, and the loser is
/// told the same thing. The caller reads the newest document again, makes its change on that, and saves once more.
/// This store never retries on a caller's behalf, because only the caller knows how to make its change again.
/// </summary>
public sealed class MailDeskRulesStore
{
    /// <summary>How many changes the history holds when the reader does not say.</summary>
    public const int DefaultHistory = 50;

    /// <summary>The most changes one history read may hold.</summary>
    public const int MaxHistory = 500;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public MailDeskRulesStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    // ---------- reads ----------

    /// <summary>The account's newest rules: the empty document at version 0 when it has never saved any.</summary>
    public MailDeskRulesVersion Newest(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.MailDeskRules.AsNoTracking().OrderByDescending(r => r.Version).FirstOrDefault();
            return row is null ? MailDeskRulesVersion.Zero : MailDeskRulesVersion.From(row);
        }
    }

    /// <summary>The account's rules at <paramref name="version"/>: version 0 is the empty document. Null when the account
    /// has no such version.</summary>
    public MailDeskRulesVersion? Read(TenantId tenant, int version)
    {
        if (version == 0) return MailDeskRulesVersion.Zero;
        if (version < 0) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.MailDeskRules.AsNoTracking().FirstOrDefault(r => r.Version == version);
            return row is null ? null : MailDeskRulesVersion.From(row);
        }
    }

    /// <summary>
    /// The recorded changes, newest first: every kind, or only <paramref name="kind"/> (one of
    /// <see cref="MailDeskWords.ChangeKinds"/>). Says how many there are in all, so a reader that got a full page knows
    /// there is more. Channel and mode changes are recorded here too, by <see cref="MailDeskChannelStore"/>.
    /// </summary>
    public MailDeskChangeHistory History(TenantId tenant, int n = DefaultHistory, string? kind = null)
    {
        if (n is < 1 or > MaxHistory)
            throw new ArgumentOutOfRangeException(nameof(n), n, $"a history read holds 1 to {MaxHistory} changes");
        if (kind is not null && !MailDeskWords.IsOneOf(kind, MailDeskWords.ChangeKinds.All))
            throw new ArgumentException($"'{kind}' is not a kind of Mail Desk change; it is one of {MailDeskWords.InWords(MailDeskWords.ChangeKinds.All)}.", nameof(kind));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.MailDeskListChanges.AsNoTracking();
            if (kind is not null) rows = rows.Where(c => c.Kind == kind);
            var total = rows.Count();
            // AtUtc orders; two changes in one instant are told apart by version, which only grows.
            var page = rows.OrderByDescending(c => c.AtUtc).ThenByDescending(c => c.VersionAfter).Take(n).ToList();
            return new MailDeskChangeHistory(page, total);
        }
    }

    /// <summary>
    /// Every recorded change made at or after <paramref name="fromUtc"/> and before <paramref name="toUtc"/>, oldest
    /// first - all of them, never a page, because the 08:00 email lists every change of its day (phase 3 contract,
    /// section 9).
    /// </summary>
    public IReadOnlyList<MailDeskListChangeEntity> ChangesBetween(TenantId tenant, DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc < fromUtc)
            throw new ArgumentException($"The span ends ({toUtc:o}) before it starts ({fromUtc:o}).", nameof(toUtc));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.MailDeskListChanges.AsNoTracking()
                .Where(c => c.AtUtc >= fromUtc && c.AtUtc < toUtc)
                .OrderBy(c => c.AtUtc).ThenBy(c => c.VersionAfter)
                .ToList();
        }
    }

    // ---------- the one write ----------

    /// <summary>
    /// Save <paramref name="document"/> as the next version, made on <paramref name="basedOnVersion"/> (the version the
    /// caller read and changed), with <paramref name="change"/> recorded beside it in the same transaction. The
    /// document is checked first (<see cref="MailDeskRulesValidator"/>) and stored in its one spelling.
    /// </summary>
    public MailDeskRulesWrite Save(TenantId tenant, int basedOnVersion, MailDeskRulesDocument document, MailDeskChange change, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!change.IsRulesChange)
            throw new ArgumentException($"A '{change.Kind}' change is not a change to the rules; the channel store records it.", nameof(change));
        if (basedOnVersion < 0)
            throw new ArgumentOutOfRangeException(nameof(basedOnVersion), basedOnVersion, "a rules version is 0 or more");

        var validation = MailDeskRulesValidator.Validate(document);
        if (!validation.IsValid)
        {
            FileLog.Write($"[MailDeskRulesStore] Save: REFUSED a {change.Kind} {change.Action} by {change.Who.Kind} {change.Who.Id}: {validation.Problems.Count} problem(s)");
            return MailDeskRulesWrite.Invalid(validation.Refusal, validation.Problems);
        }

        var version = basedOnVersion + 1;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var newest = NewestVersion(ctx);
            if (newest != basedOnVersion)
            {
                FileLog.Write($"[MailDeskRulesStore] Save: STALE - made on version {basedOnVersion}, newest is {newest}");
                return MailDeskRulesWrite.Stale(newest, StaleRefusal(basedOnVersion, newest));
            }

            var row = new MailDeskRulesVersionEntity
            {
                TenantId = tenant.Value,
                Version = version,
                Document = validation.Document!.ToJson(),
                SavedUtc = nowUtc,
                SavedByKind = change.Who.Kind,
                SavedBy = change.Who.Id,
            };
            var changeRow = change.ToEntity(tenant.Value, nowUtc, basedOnVersion, version);
            // ONE SaveChanges, so ONE transaction: the version and its change row are kept together or not at all.
            ctx.MailDeskRules.Add(row);
            ctx.MailDeskListChanges.Add(changeRow);
            try
            {
                ctx.SaveChanges();
            }
            catch (DbUpdateException ex)
            {
                ctx.ChangeTracker.Clear();
                // ONLY A SECOND WRITER IS REPORTED AS ONE: a key violation is one cause of this exception and not the
                // only one, so the newest version decides. Telling a caller another writer won when none did would
                // send it round for ever.
                var now = NewestVersion(ctx);
                if (now >= version)
                {
                    FileLog.Write($"[MailDeskRulesStore] Save: version {version} was taken by another writer");
                    return MailDeskRulesWrite.Stale(now, StaleRefusal(basedOnVersion, now));
                }
                FileLog.Write($"[MailDeskRulesStore] Save FAILED and no other writer holds version {version}: {ex.Message}");
                throw;
            }

            FileLog.Write($"[MailDeskRulesStore] Save: version {version} ({change.Kind} {change.Action}) by {change.Who.Kind} {change.Who.Id}");
            return MailDeskRulesWrite.Saved(MailDeskRulesVersion.From(row), changeRow);
        }
    }

    private static int NewestVersion(GatewayDbContext ctx)
        => ctx.MailDeskRules.AsNoTracking().Select(r => (int?)r.Version).Max() ?? 0;

    private static string StaleRefusal(int basedOn, int newest)
        => $"the rules changed while this change was being made: it was made on version {basedOn} and the newest is now " +
           $"version {newest}. Read the rules again and make the change on version {newest}";
}

/// <summary>One version of an account's rules. Version 0 is the empty document, saved by nobody.</summary>
public sealed record MailDeskRulesVersion(int Version, DateTime? SavedUtc, string? SavedByKind, string? SavedBy, MailDeskRulesDocument Document)
{
    public static readonly MailDeskRulesVersion Zero = new(0, null, null, null, MailDeskRulesDocument.Empty);

    internal static MailDeskRulesVersion From(MailDeskRulesVersionEntity row)
        => new(row.Version, row.SavedUtc, row.SavedByKind, row.SavedBy, MailDeskRulesDocument.Parse(row.Document));
}

/// <summary>What a save did.</summary>
public enum MailDeskRulesOutcome
{
    /// <summary>The new version and its change row were written.</summary>
    Saved,
    /// <summary>The document broke a rule of section 3; nothing was written.</summary>
    Invalid,
    /// <summary>The save was made on a version that is no longer the newest; nothing was written.</summary>
    Stale,
}

/// <summary>The result of <see cref="MailDeskRulesStore.Save"/>.</summary>
public sealed record MailDeskRulesWrite
{
    public MailDeskRulesOutcome Outcome { get; private init; }

    /// <summary>The version written, when saved.</summary>
    public MailDeskRulesVersion? Version { get; private init; }

    /// <summary>The change row written with it, when saved.</summary>
    public MailDeskListChangeEntity? Change { get; private init; }

    /// <summary>The newest version now, when the save was stale.</summary>
    public int? NewestVersion { get; private init; }

    /// <summary>Why nothing was written, as one sentence for the caller.</summary>
    public string? Refusal { get; private init; }

    /// <summary>Every problem the validator found, when the document was invalid.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = Array.Empty<string>();

    internal static MailDeskRulesWrite Saved(MailDeskRulesVersion version, MailDeskListChangeEntity change)
        => new() { Outcome = MailDeskRulesOutcome.Saved, Version = version, Change = change };

    internal static MailDeskRulesWrite Invalid(string refusal, IReadOnlyList<string> problems)
        => new() { Outcome = MailDeskRulesOutcome.Invalid, Refusal = refusal, Problems = problems };

    internal static MailDeskRulesWrite Stale(int newest, string refusal)
        => new() { Outcome = MailDeskRulesOutcome.Stale, NewestVersion = newest, Refusal = refusal };
}

/// <summary>One page of the change history, newest first, and how many changes there are in all.</summary>
public sealed record MailDeskChangeHistory(IReadOnlyList<MailDeskListChangeEntity> Changes, int Total);
