using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Memory;

/// <summary>
/// A FACTORY'S MEMORY: many small named notes, one idea each, every change kept as a version (Factory Memory
/// mission, phase 2). One memory per factory, shared by all its agents, keyed by account and factory id.
///
/// WHAT THIS STORE IS FOR. A factory runs the same job again and again and learns something each run - this
/// trade has no email, that registry needs another step, those messages all failed authentication. Today that
/// learning survives only if somebody edits a file in one repository on one machine, so most of it is lost when
/// the session ends and the factory makes the same mistake next run.
///
/// APPEND-ONLY, AND THE KEY IS THE CONCURRENCY CONTROL. A write never edits a row; it inserts the next version,
/// and the primary key (tenant, factory, name, version) makes a second writer's insert impossible rather than
/// merely unlikely. That is what the hosted Gateway needs: it runs several replicas over one database, so a lock
/// inside one process would let two of them both mint version 4 and the second would quietly supersede the
/// first - losing exactly the lesson this mission exists to keep (review finding 7).
///
/// A WRITER SAYS WHICH VERSION IT READ. A write against a version that is no longer current is REFUSED, and the
/// refusal carries the current note so the caller can merge and try again. Silently winning would be the same
/// lost lesson with a different cause.
///
/// SIZES ARE CAPPED ON THE CURRENT TEXT, VERSIONS ARE PRUNED (review finding 6). A cap counting every version
/// would be reached by a daily note rewrite and could never be freed, because a delete adds a version too. So
/// the current text is capped, and history is kept to the last <see cref="KeepVersions"/> versions or
/// <see cref="KeepDays"/> days, whichever keeps MORE.
///
/// The factory is never taken from a caller: every method takes it from the Gateway's own reading of who is
/// asking, which is what phase 1 built.
/// </summary>
public sealed class FactoryMemoryStore
{
    /// <summary>The most a single note's current text may take, in UTF-8 bytes.</summary>
    public const int MaxNoteBytes = 16 * 1024;

    /// <summary>The most notes one factory may keep at once, counting only those not deleted.</summary>
    public const int MaxNotes = 100;

    /// <summary>The most all of one factory's current notes may take together, in UTF-8 bytes.</summary>
    public const int MaxFactoryBytes = 512 * 1024;

    /// <summary>How many versions of a note are always kept, however old.</summary>
    public const int KeepVersions = 50;

    /// <summary>How many days of versions are always kept, however many.</summary>
    public const int KeepDays = 90;

    /// <summary>The longest a note's name may be.</summary>
    public const int MaxNameLength = 200;

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FactoryMemoryStore(GatewayDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    // ---------- reads ----------

    /// <summary>
    /// The factory's notes as they stand: the highest version of each name, DELETED NAMES LEFT OUT. A deleted
    /// note is still there and still restorable, but a listing that showed it would make "what does this factory
    /// know" unanswerable at a glance.
    /// </summary>
    public IReadOnlyList<FactoryMemoryNoteEntity> List(TenantId tenant, string factory)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return Array.Empty<FactoryMemoryNoteEntity>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return Current(ctx, factory).Where(n => !n.Deleted)
                .OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// One note as it stands - its highest version - or null when the name has never been written. A DELETED
    /// note is returned rather than hidden, with <see cref="FactoryMemoryNoteEntity.Deleted"/> set, so the caller
    /// can say "deleted in version N by X, restorable in the Cockpit" instead of the less true "no such note".
    /// </summary>
    public FactoryMemoryNoteEntity? Get(TenantId tenant, string factory, string name)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return null;
        if (!FactoryNames.TryNoteName(name, out name, out _)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return Head(ctx, factory, name);
        }
    }

    /// <summary>Every kept version of one note, newest first.</summary>
    public IReadOnlyList<FactoryMemoryNoteEntity> History(TenantId tenant, string factory, string name)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return Array.Empty<FactoryMemoryNoteEntity>();
        if (!FactoryNames.TryNoteName(name, out name, out _)) return Array.Empty<FactoryMemoryNoteEntity>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.FactoryMemoryNotes.AsNoTracking()
                .Where(n => n.Factory == factory && n.Name == name)
                .OrderByDescending(n => n.Version)
                .ToList();
        }
    }

    // ---------- writes ----------

    /// <summary>
    /// Write a note: add the next version with this text. <paramref name="expectedVersion"/> is the version the
    /// writer last read - 0 (or null) meaning "I believe this note does not exist yet". A mismatch is refused
    /// with the current note attached.
    /// </summary>
    public FactoryMemoryWrite Set(TenantId tenant, string factory, string name, string text,
        int? expectedVersion, string authorKind, string? authorId, DateTime nowUtc)
        => Append(tenant, factory, name, text, deleted: false, expectedVersion, authorKind, authorId, nowUtc);

    /// <summary>
    /// Delete a note: add a version that marks it deleted. The note's history is untouched and a person can
    /// restore it, which is the condition the owner attached to letting factory sessions delete at all.
    /// </summary>
    public FactoryMemoryWrite Delete(TenantId tenant, string factory, string name,
        int? expectedVersion, string authorKind, string? authorId, DateTime nowUtc)
        => Append(tenant, factory, name, text: null, deleted: true, expectedVersion, authorKind, authorId, nowUtc);

    /// <summary>
    /// Put an old version's text back as a NEW version, authored by whoever restored it. A restore is not a
    /// rewind: the history keeps showing what happened, including the delete or the mistake being undone.
    /// </summary>
    public FactoryMemoryWrite Restore(TenantId tenant, string factory, string name, int version,
        string authorKind, string? authorId, DateTime nowUtc, int? expectedVersion = null)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var wanted = ctx.FactoryMemoryNotes.AsNoTracking()
                .FirstOrDefault(n => n.Factory == factory && n.Name == name && n.Version == version);
            if (wanted is null)
                return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.NoSuchVersion,
                    $"version {version} of '{name}' is not kept", null);
            if (wanted.Deleted)
                return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.NoSuchVersion,
                    $"version {version} of '{name}' is the delete itself, so there is no text in it to put back; " +
                    "restore the version before it", null);
            var head = Head(ctx, factory, name);
            // THE VERSION THE PERSON WAS LOOKING AT, when they said (finding 6). A restore is a write like any
            // other and must be able to lose a race: an agent can add a version between the owner reading the
            // history and pressing restore, and burying that silently is the same lost lesson with a kinder face.
            // A caller that states nothing is taken at its word, so an older client keeps working.
            var expected = expectedVersion is > 0 ? expectedVersion.Value : head?.Version ?? 0;
            return AppendInside(ctx, tenant, factory, name, wanted.Text, deleted: false,
                expectedVersion: expected, authorKind, authorId, nowUtc);
        }
    }

    private FactoryMemoryWrite Append(TenantId tenant, string factory, string name, string? text, bool deleted,
        int? expectedVersion, string authorKind, string? authorId, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return AppendInside(ctx, tenant, factory, name, text, deleted, expectedVersion ?? 0, authorKind, authorId, nowUtc);
        }
    }

    private FactoryMemoryWrite AppendInside(GatewayDbContext ctx, TenantId tenant, string factory, string name,
        string? text, bool deleted, int expectedVersion, string authorKind, string? authorId, DateTime nowUtc)
    {
        // ONE SPELLING, FOLDED AND CHECKED HERE (phase 2 review, findings 1 and 2). Both of these are refused
        // rather than accepted-and-coped-with-later, because the Director turns a note into a FILE before an
        // agent starts and a name it cannot write is a hard stop for every later session of the factory.
        if (!FactoryNames.TryFactory(factory, out var foldedFactory, out var factoryRefusal))
            return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.BadFactory, factoryRefusal!, null);
        factory = foldedFactory;
        if (!FactoryNames.TryNoteName(name, out var foldedName, out var nameRefusal))
            return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.BadName, nameRefusal!, null);
        name = foldedName;

        var head = Head(ctx, factory, name);
        var current = head?.Version ?? 0;
        if (expectedVersion != current)
        {
            // THE TWO-WRITER REFUSAL. The current note rides the refusal so the caller can merge rather than
            // read again and race again.
            FileLog.Write($"[FactoryMemoryStore] {factory}/{name}: REFUSED a stale write (expected {expectedVersion}, current {current})");
            return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.Stale,
                current == 0
                    ? $"'{name}' does not exist yet, so write it expecting version 0"
                    : $"'{name}' is at version {current}, not {expectedVersion}; read it again, merge, and write once more",
                head);
        }

        if (!deleted)
        {
            var bytes = System.Text.Encoding.UTF8.GetByteCount(text ?? "");
            if (bytes > MaxNoteBytes)
                return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.NoteTooLarge,
                    $"the note takes {bytes} bytes; one note takes at most {MaxNoteBytes}", head);

            // Names are folded above, so an exact comparison is now the right one: before folding, 'Domains'
            // beside 'domains' counted against neither the note limit nor the factory's bytes.
            var others = Current(ctx, factory).Where(n => !n.Deleted && !string.Equals(n.Name, name, StringComparison.Ordinal)).ToList();
            var isNewName = head is null || head.Deleted;
            if (isNewName && others.Count + 1 > MaxNotes)
                return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.TooManyNotes,
                    $"this factory already keeps {others.Count} notes; a factory keeps at most {MaxNotes}. " +
                    "Delete one it no longer needs, or write this into a note it already has", head);

            var total = others.Sum(n => (long)System.Text.Encoding.UTF8.GetByteCount(n.Text ?? "")) + bytes;
            if (total > MaxFactoryBytes)
                return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.FactoryTooLarge,
                    $"with this note the factory's memory would take {total} bytes; it takes at most {MaxFactoryBytes} together", head);
        }
        else if (head is null)
        {
            return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.NoSuchNote,
                $"there is no note called '{name}' to delete", null);
        }
        else if (head.Deleted)
        {
            return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.AlreadyDeleted,
                $"'{name}' was already deleted in version {head.Version}", head);
        }

        var written = new FactoryMemoryNoteEntity
        {
            TenantId = tenant.Value,
            Factory = factory,
            Name = name,
            Version = current + 1,
            Text = deleted ? null : text ?? "",
            Deleted = deleted,
            AuthorKind = authorKind,
            AuthorId = authorId,
            WrittenAtUtc = nowUtc,
        };

        // ONE TRANSACTION, and the key does the arbitration. A concurrent writer that got here first owns
        // version current+1, and this insert then violates the primary key - which is reported as the same stale
        // refusal a slow reader gets, because that is what happened.
        using var tx = ctx.Database.BeginTransaction();
        ctx.FactoryMemoryNotes.Add(written);
        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            tx.Rollback();
            ctx.ChangeTracker.Clear();
            var headNow = Head(ctx, factory, name);

            // ONLY A SECOND WRITER IS REPORTED AS ONE (phase 2 review, finding 3). A key violation is one cause
            // of this exception and not the only one: on PostgreSQL a text holding a zero character, or a value
            // longer than its column, raises the same thing. Telling such a caller that another writer won is a
            // refusal naming a cause that did not happen - and it tells the caller to merge and try again, which
            // it will do for as long as it is willing, never storing the lesson. So the head decides: if the
            // version this write wanted now exists, a writer really did take it; if it does not, the write failed
            // for its own reason and the caller is told that instead.
            if (WasTakenByAnotherWriter(headNow, written.Version))
            {
                FileLog.Write($"[FactoryMemoryStore] {factory}/{name}: version {written.Version} was taken by another writer");
                return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.Stale,
                    $"another writer took version {written.Version} of '{name}' first; read it again, merge, and write once more",
                    headNow);
            }

            FileLog.Write($"[FactoryMemoryStore] {factory}/{name}: the write FAILED and no other writer holds version {written.Version}: {ex.Message}");
            return FactoryMemoryWrite.Refused(FactoryMemoryOutcome.WriteFailed,
                $"'{name}' could not be stored: {Innermost(ex).Message}", headNow);
        }

        Prune(ctx, factory, name, nowUtc);
        ctx.SaveChanges();
        tx.Commit();
        FileLog.Write($"[FactoryMemoryStore] {factory}/{name}: wrote version {written.Version} ({(deleted ? "deleted" : $"{System.Text.Encoding.UTF8.GetByteCount(written.Text ?? "")} bytes")}) by {authorKind} {authorId ?? "(unknown)"}");
        return FactoryMemoryWrite.Written(written);
    }

    /// <summary>
    /// Drop versions kept by neither rule: the last <see cref="KeepVersions"/> are kept however old, and
    /// anything written inside <see cref="KeepDays"/> days is kept however many there are - whichever keeps
    /// more. The head is never dropped, whatever the arithmetic says.
    /// </summary>
    private static void Prune(GatewayDbContext ctx, string factory, string name, DateTime nowUtc)
    {
        var all = ctx.FactoryMemoryNotes
            .Where(n => n.Factory == factory && n.Name == name)
            .OrderByDescending(n => n.Version)
            .ToList();
        if (all.Count <= KeepVersions) return;

        var cutoff = nowUtc.AddDays(-KeepDays);
        var keepByCount = all.Take(KeepVersions).Select(n => n.Version).ToHashSet();
        var head = all[0].Version;
        foreach (var row in all)
        {
            if (row.Version == head) continue;
            if (keepByCount.Contains(row.Version)) continue;
            if (row.WrittenAtUtc >= cutoff) continue;
            ctx.FactoryMemoryNotes.Remove(row);
        }
    }

    /// <summary>
    /// Did a second writer really take this version? The head decides, and nothing else can: an insert can fail
    /// for reasons that have no writer behind them at all. True only when the version this write wanted now
    /// exists - which is exactly what a key violation by a competing insert leaves behind.
    /// </summary>
    internal static bool WasTakenByAnotherWriter(FactoryMemoryNoteEntity? head, int attemptedVersion)
        => head is not null && head.Version >= attemptedVersion;

    /// <summary>The innermost reason, which is the one a database driver puts the real message on.</summary>
    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return ex;
    }

    /// <summary>The highest version of one name, or null when the name has never been written.</summary>
    private static FactoryMemoryNoteEntity? Head(GatewayDbContext ctx, string factory, string name)
        => ctx.FactoryMemoryNotes.AsNoTracking()
            .Where(n => n.Factory == factory && n.Name == name)
            .OrderByDescending(n => n.Version)
            .FirstOrDefault();

    /// <summary>The highest version of every name in one factory.</summary>
    private static List<FactoryMemoryNoteEntity> Current(GatewayDbContext ctx, string factory)
        => ctx.FactoryMemoryNotes.AsNoTracking()
            .Where(n => n.Factory == factory)
            .GroupBy(n => n.Name)
            .Select(g => g.OrderByDescending(n => n.Version).First())
            .ToList();
}

/// <summary>Why a write to a factory's memory was refused, or that it was not.</summary>
public enum FactoryMemoryOutcome
{
    Written,
    /// <summary>The writer's version is not the current one - two writers, or a slow reader.</summary>
    Stale,
    NoteTooLarge,
    TooManyNotes,
    FactoryTooLarge,
    NoSuchNote,
    AlreadyDeleted,
    NoSuchVersion,
    BadName,
    /// <summary>The factory id is not one spelling of a factory id.</summary>
    BadFactory,
    /// <summary>The write failed for its own reason, and no second writer holds the version it wanted.</summary>
    WriteFailed,
}

/// <summary>
/// The result of a write. A refusal carries the CURRENT note where there is one, because every refusal this
/// store makes is one the caller can act on - merge and write again, or delete something first - and it cannot
/// do that from a sentence alone.
/// </summary>
public sealed record FactoryMemoryWrite(FactoryMemoryOutcome Outcome, string? Refusal, FactoryMemoryNoteEntity? Note)
{
    public bool Ok => Outcome == FactoryMemoryOutcome.Written;

    public static FactoryMemoryWrite Written(FactoryMemoryNoteEntity note) => new(FactoryMemoryOutcome.Written, null, note);

    public static FactoryMemoryWrite Refused(FactoryMemoryOutcome outcome, string refusal, FactoryMemoryNoteEntity? current)
        => new(outcome, refusal, current);
}
