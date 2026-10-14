using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Registry;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Briefs;

/// <summary>
/// A SEAT'S BRIEF, HELD ON THE GATEWAY (Factory Seats mission, phase 2a). A brief is what a seat of a factory is
/// told to do. This store keeps every version of it, keyed by account, factory, seat and version.
///
/// WHAT THIS STORE IS FOR. Until it existed a seat's brief was a file in a repository on one computer. Renaming a
/// seat took a merged pull request, and the owner's screen could show the brief only through the editor of a
/// schedule - so a seat with no schedule could not be defined at all. The owner's decision of 13 October 2026 is
/// that the definitions of a factory live on the Gateway.
///
/// SHAPED LIKE FACTORY MEMORY, FOR THE SAME REASONS (<see cref="Memory.FactoryMemoryStore"/>):
///
/// APPEND-ONLY, AND THE KEY IS THE CONCURRENCY CONTROL. A save never edits a row; it inserts the next version,
/// and the primary key (tenant, factory, seat, version) makes a second writer's insert impossible rather than
/// merely unlikely. The hosted Gateway runs several replicas over one database, so a lock inside one process
/// would let two of them both mint version 4 and the second would quietly replace the first.
///
/// A WRITER SAYS WHICH VERSION IT READ. A save against a version that is no longer the newest is REFUSED, and the
/// refusal carries the newest version so the caller can merge and try again.
///
/// DRAFT, THEN PUBLISHED (the mission document's P2). A save makes a new DRAFT version. Publishing marks one
/// version as published, and the brief a talk or a run is given is the highest version that is published.
/// Publishing never goes backwards: bringing an older version back adds a copy of its text as the newest version,
/// already published, so the history keeps showing what happened.
///
/// ONLY A PERSON PUBLISHES (P3). A person, or a session of the factory, may save a draft. Publishing and bringing
/// back decide what every later run of the seat is told, so they are a person's acts, and that rule is enforced
/// here rather than in each route.
///
/// A BRIEF BELONGS TO A REGISTERED SEAT. A write for a factory that is not registered, or for a seat that is not
/// one of its seats, is refused: a brief nobody can open on the owner's screen is a brief nobody can correct.
///
/// THE CURRENT TEXT IS CAPPED, EVERY VERSION IS KEPT. The cap is on one version's text. Nothing is pruned,
/// because the mission's goal is that every version is kept and an older one can be brought back.
///
/// WHAT IS BOUNDED IS THE WRITING AND THE READING, NEVER THE KEEPING (review 1 of phase 2a). "Every version is
/// kept" rules out pruning; it does not rule out a limit on how many versions nobody has read. A session's key is
/// a credential no person is watching, and a session that saved a draft on every turn, or one stuck retrying,
/// would add rows without end. So a SESSION may add a draft only while fewer than <see cref="MaxWaitingDrafts"/>
/// drafts wait above the published version, and a person's publish is what makes room. A session cannot publish,
/// so sessions alone can never add more than that between two acts of a person. A PERSON IS NOT BOUND: the person
/// is the one who makes room, and a person who was bound could correct a pile of bad drafts only by first
/// publishing one of them. And the history is read ONE PAGE AT A TIME, WITHOUT ANY VERSION'S TEXT
/// (<see cref="History"/>); the text is read one version at a time (<see cref="Find"/>).
/// </summary>
public sealed class FactoryBriefStore
{
    /// <summary>The most one version's text may take, in UTF-8 bytes.</summary>
    public const int MaxBriefBytes = 64 * 1024;

    /// <summary>
    /// The most drafts that may wait above a seat's published version before a SESSION is refused another. With
    /// nothing published yet, every version is a waiting draft. Twenty is far more than a seat's sessions leave
    /// between two readings by its owner, and small enough that the worst a looping session can add to one seat is
    /// twenty versions of at most <see cref="MaxBriefBytes"/> each.
    /// </summary>
    public const int MaxWaitingDrafts = 20;

    /// <summary>How many versions one page of the history holds when the reader does not say.</summary>
    public const int DefaultHistoryPage = 50;

    /// <summary>The most versions one page of the history may hold.</summary>
    public const int MaxHistoryPage = 100;

    /// <summary>The sentence a session's publish is refused with. Held here so the routes and the tests say the
    /// same words.</summary>
    public const string OnlyAPersonPublishes =
        "Only a person publishes a seat's brief. A session of the factory may save a draft; " +
        "the owner reads it and publishes it.";

    /// <summary>The sentence a session's bring back is refused with.</summary>
    public const string OnlyAPersonBringsBack =
        "Only a person publishes a seat's brief, and bringing an older version back publishes a copy of it. " +
        "A session of the factory may save the text it wants as a new draft; the owner publishes it.";

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly FactoryRegistryStore _registry;

    public FactoryBriefStore(GatewayDatabase db, FactoryRegistryStore registry)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    // ---------- reads ----------

    /// <summary>
    /// How a seat's brief stands: its newest version, and its published version. Both are null for a seat that has
    /// no brief on the Gateway, which is the state every seat whose brief is still a file in a repository is in.
    /// </summary>
    public FactoryBriefStanding Standing(TenantId tenant, string factory, string seat)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return FactoryBriefStanding.None;
        if (!FactoryNames.TrySeat(seat, out seat, out _)) return FactoryBriefStanding.None;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return StandingInside(ctx, factory, seat);
        }
    }

    /// <summary>
    /// ONE PAGE of one seat's history, newest first, WITHOUT ANY VERSION'S TEXT: which version, its state, who
    /// wrote it, when, and how many bytes its text takes. <paramref name="before"/> is the version the page starts
    /// below - null for the newest page - and the page says where the next one starts, or that there is none.
    ///
    /// The text is never read here, not even to measure it: every version is kept for ever, so a history that
    /// carried the text would grow until the answer itself was the outage. <see cref="Find"/> reads one version.
    /// </summary>
    public FactoryBriefHistoryPage History(TenantId tenant, string factory, string seat, int? before = null,
        int limit = DefaultHistoryPage)
    {
        if (limit is < 1 or > MaxHistoryPage)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"a page of the history holds 1 to {MaxHistoryPage} versions");
        if (before is < 1)
            throw new ArgumentOutOfRangeException(nameof(before), before, "versions count from 1");
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return FactoryBriefHistoryPage.Empty;
        if (!FactoryNames.TrySeat(seat, out seat, out _)) return FactoryBriefHistoryPage.Empty;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var all = Versions(ctx, factory, seat);
            var total = all.Count();
            var below = before is { } b ? all.Where(v => v.Version < b) : all;
            // One more than the page, so the page knows whether an older one follows without counting again.
            var rows = below.OrderByDescending(v => v.Version)
                .Select(v => new FactoryBriefVersionSummary(v.Factory, v.Seat, v.Version, v.State, v.AuthorKind,
                    v.AuthorId, v.WrittenAtUtc, v.PublishedAtUtc, v.PublishedBy, v.TextBytes))
                .Take(limit + 1)
                .ToList();
            var more = rows.Count > limit;
            var page = more ? rows.GetRange(0, limit) : rows;
            return new FactoryBriefHistoryPage(page, total, more ? page[^1].Version : null);
        }
    }

    /// <summary>One version of one seat's brief, with its text, or null when the seat has no such version.</summary>
    public FactoryBriefVersionEntity? Find(TenantId tenant, string factory, string seat, int version)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return null;
        if (!FactoryNames.TrySeat(seat, out seat, out _)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return Version(ctx, factory, seat, version);
        }
    }

    /// <summary>
    /// Why <paramref name="seat"/> of <paramref name="factory"/> cannot have a brief, or null when it is a
    /// registered seat of a registered factory. A read uses it to say "that is not a seat" instead of the less true
    /// "that seat has no brief".
    /// </summary>
    public FactoryBriefWrite? WhyNotASeat(TenantId tenant, string factory, string seat)
        => Place(tenant, ref factory, ref seat);

    // ---------- writes ----------

    /// <summary>
    /// Save a draft: add the next version with this text, not published. <paramref name="expectedVersion"/> is the
    /// seat's newest version as the writer last read it - 0 (or null) meaning "I believe this seat has no brief
    /// here yet". A mismatch is refused with the newest version attached. A SESSION is also refused while
    /// <see cref="MaxWaitingDrafts"/> drafts already wait above the published version; a person is not.
    /// </summary>
    public FactoryBriefWrite SaveDraft(TenantId tenant, string factory, string seat, string? text,
        int? expectedVersion, string authorKind, string? authorId, DateTime nowUtc)
    {
        RequireAWriter(authorKind);
        if (Place(tenant, ref factory, ref seat) is { } refusal) return refusal;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return Append(ctx, tenant, factory, seat, text ?? "", expectedVersion ?? 0, published: false,
                authorKind, authorId, nowUtc);
        }
    }

    /// <summary>
    /// Publish one version: from now on it is the brief a talk or a run of the seat is given. Only a person
    /// publishes. A version older than the published one is refused, because publishing never goes backwards -
    /// <see cref="BringBack"/> is how an older text is used again.
    /// </summary>
    public FactoryBriefWrite Publish(TenantId tenant, string factory, string seat, int version,
        string authorKind, string? authorId, DateTime nowUtc)
    {
        RequireAWriter(authorKind);
        if (authorKind != FactoryMemoryAuthorKinds.Person)
        {
            FileLog.Write($"[FactoryBriefStore] {factory}/{seat}: REFUSED a publish of version {version} by {authorKind} {authorId ?? "(unknown)"}");
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.OnlyAPersonPublishes, OnlyAPersonPublishes, null);
        }
        if (Place(tenant, ref factory, ref seat) is { } refusal) return refusal;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var standing = StandingInside(ctx, factory, seat);
            var wanted = Version(ctx, factory, seat, version);
            if (wanted is null)
                return FactoryBriefWrite.Refused(FactoryBriefOutcome.NoSuchVersion,
                    NoSuchVersion(factory, seat, version, standing, "publish"), standing.Newest);

            if (wanted.State == FactoryBriefStates.Published)
                return FactoryBriefWrite.Refused(FactoryBriefOutcome.AlreadyPublished,
                    standing.Published?.Version == version
                        ? $"version {version} is already the published brief of '{seat}'"
                        : $"version {version} was published before, and version {standing.Published?.Version} has replaced it. " +
                          $"To use its text again, bring version {version} back: that publishes a copy of it as the newest version",
                    standing.Published);

            if (standing.Published is { } published && published.Version > version)
                return FactoryBriefWrite.Refused(FactoryBriefOutcome.OlderThanPublished,
                    $"version {version} is older than the published version {published.Version}. Publishing does not go " +
                    $"backwards: bring version {version} back, which publishes a copy of it as the newest version",
                    published);

            // THE ONE CHANGE A STORED ROW EVER SEES, and it only ever happens once: the update matches the row only
            // while it is still a draft, so two people publishing the same version cannot both be recorded as the
            // one who did.
            var changed = ctx.FactoryBriefs
                .Where(v => v.Factory == factory && v.Seat == seat && v.Version == version && v.State == FactoryBriefStates.Draft)
                .ExecuteUpdate(set => set
                    .SetProperty(v => v.State, FactoryBriefStates.Published)
                    .SetProperty(v => v.PublishedAtUtc, (DateTime?)nowUtc)
                    .SetProperty(v => v.PublishedBy, authorId));
            var now = Version(ctx, factory, seat, version);
            if (changed == 0)
            {
                FileLog.Write($"[FactoryBriefStore] {factory}/{seat}: version {version} was published by somebody else first");
                return FactoryBriefWrite.Refused(FactoryBriefOutcome.AlreadyPublished,
                    $"version {version} of the brief of '{seat}' was published by somebody else a moment ago", now);
            }
            FileLog.Write($"[FactoryBriefStore] {factory}/{seat}: published version {version} by person {authorId ?? "(unknown)"}");
            return FactoryBriefWrite.Published(now!);
        }
    }

    /// <summary>
    /// Bring an older version back: add a copy of its text as the NEWEST version, already published, authored by
    /// the person who brought it back. It is not a rewind - the history keeps every version in between. Only a
    /// person brings a version back, because it publishes.
    /// </summary>
    public FactoryBriefWrite BringBack(TenantId tenant, string factory, string seat, int version,
        string authorKind, string? authorId, DateTime nowUtc, int? expectedVersion = null)
    {
        RequireAWriter(authorKind);
        if (authorKind != FactoryMemoryAuthorKinds.Person)
        {
            FileLog.Write($"[FactoryBriefStore] {factory}/{seat}: REFUSED a bring back of version {version} by {authorKind} {authorId ?? "(unknown)"}");
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.OnlyAPersonPublishes, OnlyAPersonBringsBack, null);
        }
        if (Place(tenant, ref factory, ref seat) is { } refusal) return refusal;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var standing = StandingInside(ctx, factory, seat);
            var wanted = Version(ctx, factory, seat, version);
            if (wanted is null)
                return FactoryBriefWrite.Refused(FactoryBriefOutcome.NoSuchVersion,
                    NoSuchVersion(factory, seat, version, standing, "bring back"), standing.Newest);
            if (standing.Published?.Version == version)
                return FactoryBriefWrite.Refused(FactoryBriefOutcome.AlreadyPublished,
                    $"version {version} is the published brief of '{seat}' already, so there is nothing to bring back",
                    standing.Published);

            // THE VERSION THE PERSON WAS LOOKING AT, when they said. A bring back is a write like any other and must
            // be able to lose a race: a session can save a draft between the owner reading the history and pressing
            // the button. A caller that states nothing is taken at its word and the copy goes after what is newest.
            var expected = expectedVersion is > 0 ? expectedVersion.Value : standing.Newest!.Version;
            return Append(ctx, tenant, factory, seat, wanted.Text, expected, published: true,
                authorKind, authorId, nowUtc);
        }
    }

    // ---------- the rules ----------

    /// <summary>
    /// Fold the factory and the seat to their one spelling and check the seat is registered. Returns the refusal,
    /// or null when the seat is a registered seat of a registered factory.
    /// </summary>
    private FactoryBriefWrite? Place(TenantId tenant, ref string factory, ref string seat)
    {
        if (!FactoryNames.TryFactory(factory, out var foldedFactory, out var factoryRefusal))
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.BadFactory, factoryRefusal!, null);
        if (!FactoryNames.TrySeat(seat, out var foldedSeat, out var seatRefusal))
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.BadSeat, seatRefusal!, null);
        factory = foldedFactory;
        seat = foldedSeat;

        var registered = _registry.Find(tenant, foldedFactory);
        if (registered is null)
        {
            FileLog.Write($"[FactoryBriefStore] {foldedFactory}/{foldedSeat}: REFUSED - the factory is not registered");
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.FactoryNotRegistered,
                $"The factory '{foldedFactory}' is not registered in this account, so none of its seats has a brief on " +
                "the Gateway. Register it first with cc-devthrottle factory register --manifest <file>.", null);
        }
        if (!registered.Seats.Any(s => s.Id == foldedSeat))
        {
            FileLog.Write($"[FactoryBriefStore] {foldedFactory}/{foldedSeat}: REFUSED - not a seat of the factory");
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.SeatNotRegistered,
                $"'{foldedSeat}' is not a seat of {foldedFactory}. Its seats are: {string.Join(", ", registered.Seats.Select(s => s.Id))}.",
                null);
        }
        return null;
    }

    /// <summary>A version is written by a person or by a session. Anything else is a caller this store was never
    /// meant to be reached by, which is a defect in the route rather than a refusal to word.</summary>
    private static void RequireAWriter(string authorKind)
    {
        if (authorKind is not (FactoryMemoryAuthorKinds.Person or FactoryMemoryAuthorKinds.Session))
            throw new ArgumentException($"'{authorKind}' is not a kind of writer: a brief is written by a person or a session.", nameof(authorKind));
    }

    private FactoryBriefWrite Append(GatewayDbContext ctx, TenantId tenant, string factory, string seat, string text,
        int expectedVersion, bool published, string authorKind, string? authorId, DateTime nowUtc)
    {
        var head = Newest(ctx, factory, seat);
        var current = head?.Version ?? 0;
        if (expectedVersion != current)
        {
            // THE TWO-WRITER REFUSAL. The newest version rides the refusal so the caller can merge rather than read
            // again and race again.
            FileLog.Write($"[FactoryBriefStore] {factory}/{seat}: REFUSED a stale write (expected {expectedVersion}, newest {current})");
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.Stale,
                current == 0
                    ? $"'{seat}' of {factory} has no brief on the Gateway yet, so save it expecting version 0"
                    : $"the brief of '{seat}' is at version {current}, not {expectedVersion}; read it again, merge, and save once more",
                head);
        }

        if (!published && authorKind == FactoryMemoryAuthorKinds.Session
            && TooManyWaitingDrafts(ctx, factory, seat, head, authorId) is { } tooMany)
            return tooMany;

        if (string.IsNullOrWhiteSpace(text))
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.Empty,
                "a brief needs text: a seat told nothing has nothing to do", head);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(text);
        if (bytes > MaxBriefBytes)
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.TooLarge,
                $"the brief takes {bytes} bytes; a brief takes at most {MaxBriefBytes}", head);

        var written = new FactoryBriefVersionEntity
        {
            TenantId = tenant.Value,
            Factory = factory,
            Seat = seat,
            Version = current + 1,
            Text = text,
            TextBytes = bytes,
            State = published ? FactoryBriefStates.Published : FactoryBriefStates.Draft,
            AuthorKind = authorKind,
            AuthorId = authorId,
            WrittenAtUtc = nowUtc,
            PublishedAtUtc = published ? nowUtc : null,
            PublishedBy = published ? authorId : null,
        };

        // THE KEY DOES THE ARBITRATION. A concurrent writer that got here first owns version current+1, and this
        // insert then violates the primary key - which is reported as the same stale refusal a slow reader gets,
        // because that is what happened.
        ctx.FactoryBriefs.Add(written);
        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            ctx.ChangeTracker.Clear();
            var headNow = Newest(ctx, factory, seat);

            // ONLY A SECOND WRITER IS REPORTED AS ONE, as in factory memory: a key violation is one cause of this
            // exception and not the only one. Telling a caller that another writer won when none did tells it to
            // merge and try again for ever. So the head decides.
            if (WasTakenByAnotherWriter(headNow, written.Version))
            {
                FileLog.Write($"[FactoryBriefStore] {factory}/{seat}: version {written.Version} was taken by another writer");
                return FactoryBriefWrite.Refused(FactoryBriefOutcome.Stale,
                    $"another writer took version {written.Version} of the brief of '{seat}' first; read it again, merge, and save once more",
                    headNow);
            }

            FileLog.Write($"[FactoryBriefStore] {factory}/{seat}: the write FAILED and no other writer holds version {written.Version}: {ex.Message}");
            return FactoryBriefWrite.Refused(FactoryBriefOutcome.WriteFailed,
                $"the brief of '{seat}' could not be stored: {Innermost(ex).Message}", headNow);
        }

        FileLog.Write($"[FactoryBriefStore] {factory}/{seat}: wrote version {written.Version} ({written.State}, {bytes} bytes) by {authorKind} {authorId ?? "(unknown)"}");
        return published ? FactoryBriefWrite.Published(written) : FactoryBriefWrite.Saved(written);
    }

    /// <summary>
    /// THE BOUND ON A SESSION'S WRITING. The drafts that count are the ones ABOVE the published version - the ones
    /// nobody has acted on - whoever wrote them; with nothing published, every version counts. Returns the refusal
    /// when <see cref="MaxWaitingDrafts"/> already wait, or null when there is room.
    ///
    /// Two replicas cannot both slip past it: both would be writing the same next version, and the key lets only
    /// one of them exist. And a person's publish, which is the only thing that lowers the count, can only make a
    /// refusal that was about to happen unnecessary, never let an extra draft in.
    /// </summary>
    private static FactoryBriefWrite? TooManyWaitingDrafts(GatewayDbContext ctx, string factory, string seat,
        FactoryBriefVersionEntity? head, string? authorId)
    {
        if (head is null) return null;
        var published = Versions(ctx, factory, seat).Where(v => v.State == FactoryBriefStates.Published)
            .Select(v => (int?)v.Version).Max() ?? 0;
        var waiting = Versions(ctx, factory, seat).Count(v => v.Version > published);
        if (waiting < MaxWaitingDrafts) return null;

        FileLog.Write($"[FactoryBriefStore] {factory}/{seat}: REFUSED a draft by session {authorId ?? "(unknown)"} - {waiting} drafts already wait above version {published}");
        return FactoryBriefWrite.Refused(FactoryBriefOutcome.TooManyDrafts,
            $"the brief of '{seat}' has {waiting} drafts waiting that nobody has published (versions {published + 1} to {head.Version}), " +
            $"and a session may add a draft only while fewer than {MaxWaitingDrafts} are waiting. Nothing was saved, and no " +
            "version was removed. Ask the owner to read the drafts and publish one: publishing makes room for more.",
            head);
    }

    private static string NoSuchVersion(string factory, string seat, int version, FactoryBriefStanding standing, string act) =>
        standing.Newest is null
            ? $"'{seat}' of {factory} has no brief on the Gateway yet, so there is nothing to {act}. Save a draft first."
            : $"version {version} of the brief of '{seat}' does not exist; its newest version is {standing.Newest.Version}";

    /// <summary>
    /// Did a second writer really take this version? The head decides: an insert can fail for reasons that have no
    /// writer behind them at all. True only when the version this write wanted now exists.
    /// </summary>
    internal static bool WasTakenByAnotherWriter(FactoryBriefVersionEntity? head, int attemptedVersion)
        => head is not null && head.Version >= attemptedVersion;

    /// <summary>The innermost reason, which is the one a database driver puts the real message on.</summary>
    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return ex;
    }

    private static IQueryable<FactoryBriefVersionEntity> Versions(GatewayDbContext ctx, string factory, string seat)
        => ctx.FactoryBriefs.AsNoTracking().Where(v => v.Factory == factory && v.Seat == seat);

    private static FactoryBriefVersionEntity? Version(GatewayDbContext ctx, string factory, string seat, int version)
        => Versions(ctx, factory, seat).FirstOrDefault(v => v.Version == version);

    /// <summary>The highest version of one seat's brief, or null when it has none.</summary>
    private static FactoryBriefVersionEntity? Newest(GatewayDbContext ctx, string factory, string seat)
        => Versions(ctx, factory, seat).OrderByDescending(v => v.Version).FirstOrDefault();

    private static FactoryBriefStanding StandingInside(GatewayDbContext ctx, string factory, string seat)
        => new(Newest(ctx, factory, seat),
            // THE PUBLISHED BRIEF IS THE HIGHEST VERSION THAT IS PUBLISHED. Publishing only ever moves forward, so
            // this is one rule every reader shares instead of a pointer two replicas could disagree about.
            Versions(ctx, factory, seat).Where(v => v.State == FactoryBriefStates.Published)
                .OrderByDescending(v => v.Version).FirstOrDefault());
}

/// <summary>How one seat's brief stands: its newest version and the one that is published. Both null when the seat
/// has no brief on the Gateway.</summary>
public sealed record FactoryBriefStanding(FactoryBriefVersionEntity? Newest, FactoryBriefVersionEntity? Published)
{
    public static readonly FactoryBriefStanding None = new(null, null);
}

/// <summary>
/// One version in a page of the history: everything about it EXCEPT its text. <see cref="TextBytes"/> is how many
/// bytes that text takes.
/// </summary>
public sealed record FactoryBriefVersionSummary(string Factory, string Seat, int Version, string State,
    string AuthorKind, string? AuthorId, DateTime WrittenAtUtc, DateTime? PublishedAtUtc, string? PublishedBy,
    int TextBytes);

/// <summary>
/// One page of a seat's history, newest first. <see cref="Total"/> is how many versions the seat has in all, and
/// <see cref="NextBefore"/> is the version to ask for the next page below, or null when this page reaches the
/// oldest version.
/// </summary>
public sealed record FactoryBriefHistoryPage(IReadOnlyList<FactoryBriefVersionSummary> Versions, int Total, int? NextBefore)
{
    public static readonly FactoryBriefHistoryPage Empty = new(Array.Empty<FactoryBriefVersionSummary>(), 0, null);
}

/// <summary>What a write to a seat's brief did, or why it was refused.</summary>
public enum FactoryBriefOutcome
{
    /// <summary>A new draft version was saved.</summary>
    Saved,
    /// <summary>A version was published, or an older one was brought back as a published copy.</summary>
    Published,
    /// <summary>The writer's version is not the newest one - two writers, or a slow reader.</summary>
    Stale,
    /// <summary>The text is empty.</summary>
    Empty,
    TooLarge,
    /// <summary>A session asked to save a draft while <see cref="FactoryBriefStore.MaxWaitingDrafts"/> drafts
    /// already wait above the published version. A person's publish makes room.</summary>
    TooManyDrafts,
    /// <summary>The factory id is not one spelling of a factory id.</summary>
    BadFactory,
    /// <summary>The seat id is not one spelling of a seat id.</summary>
    BadSeat,
    FactoryNotRegistered,
    SeatNotRegistered,
    NoSuchVersion,
    AlreadyPublished,
    /// <summary>The version is older than the published one; publishing never goes backwards.</summary>
    OlderThanPublished,
    /// <summary>A session asked to publish or to bring a version back.</summary>
    OnlyAPersonPublishes,
    /// <summary>The write failed for its own reason, and no second writer holds the version it wanted.</summary>
    WriteFailed,
}

/// <summary>
/// The result of a write. A refusal carries the version the caller needs in order to act on it where there is one -
/// the newest version for a stale save, the published one for a publish that would go backwards.
/// </summary>
public sealed record FactoryBriefWrite(FactoryBriefOutcome Outcome, string? Refusal, FactoryBriefVersionEntity? Brief)
{
    public bool Ok => Outcome is FactoryBriefOutcome.Saved or FactoryBriefOutcome.Published;

    public static FactoryBriefWrite Saved(FactoryBriefVersionEntity brief) => new(FactoryBriefOutcome.Saved, null, brief);

    public static FactoryBriefWrite Published(FactoryBriefVersionEntity brief) => new(FactoryBriefOutcome.Published, null, brief);

    public static FactoryBriefWrite Refused(FactoryBriefOutcome outcome, string refusal, FactoryBriefVersionEntity? current)
        => new(outcome, refusal, current);
}
