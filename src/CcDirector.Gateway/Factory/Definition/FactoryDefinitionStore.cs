using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Definition;

/// <summary>
/// A FACTORY'S DEFINITION, HELD ON THE GATEWAY (Factory Definition mission, phase 1). A definition is everything that
/// says what a factory is - its factory file, one file per seat, its grants, rule sets, files, checks and its own
/// skills - held as one map of path to text. This store keeps every version of it, keyed by account, factory and
/// version.
///
/// BUILT EXACTLY LIKE A SEAT'S BRIEF (<see cref="Briefs.FactoryBriefStore"/>), with a content map instead of one text:
///
/// APPEND-ONLY, AND THE KEY IS THE CONCURRENCY CONTROL. A save never edits a row; it inserts the next version, and
/// the primary key (tenant, factory, version) makes a second writer's insert impossible rather than merely unlikely.
/// The hosted Gateway runs several replicas over one database, so a lock inside one process would let two of them
/// both mint version 4 and the second would quietly replace the first.
///
/// A WRITER SAYS WHICH VERSION IT READ. A save against a version that is no longer the newest is REFUSED, and the
/// refusal carries the newest version's summary so the caller can read it, merge and try again.
///
/// DRAFT, THEN PUBLISHED. A save makes a new DRAFT version. Publishing marks one version as published, and the
/// published definition is the highest version that is published. Publishing never goes backwards: restoring an
/// older version adds a copy of its content as the newest version, already published, recording which version it
/// copied.
///
/// ONLY A PERSON PUBLISHES OR RESTORES. A person, or a session of the factory, may save a draft. Publishing and
/// restoring decide what the factory is, so they are a person's acts, and that rule is enforced here rather than in
/// each route.
///
/// NO REGISTRATION IS REQUIRED. Unlike a brief, a definition may exist for a factory the registry does not hold:
/// importing a definition is how a factory comes to exist (phase 3). The factory id is only folded to its one
/// spelling.
///
/// THE STORE HOLDS NO PATH THE CHECK WOULD REFUSE. Every path is held to the places a definition holds and to the
/// path rules (<see cref="WhyNotAPath"/>), and every file and the whole version to their caps (all of them the
/// whole-definition check's own, in <see cref="DefinitionPaths"/>), whoever calls - so a
/// caller that forgot to run the whole-definition check still cannot store a definition no export could write. The
/// whole-definition check itself (schemas, references) is a separate pure function the routes run first.
///
/// WHAT IS BOUNDED IS THE WRITING AND THE READING, NEVER THE KEEPING, as for briefs: a SESSION may add a draft only
/// while fewer than <see cref="MaxWaitingDrafts"/> drafts wait above the published version, a person is not bound,
/// and the history is read one page at a time without any version's content.
///
/// WHAT READS IT (phase 4): <see cref="IsDefined"/>, which every route that would define a factory another way asks
/// before it writes (<see cref="DefinedByDefinition"/>).
/// </summary>
public sealed class FactoryDefinitionStore
{
    // THE CAPS AND THE PATH RULES ARE THE CHECK'S, read from DefinitionPaths: one constant each and one rule, so the
    // store can never hold what the whole-definition check would refuse. Only the change note is the store's own.

    /// <summary>The longest a change note may be, in characters.</summary>
    public const int MaxChangeNoteLength = 500;

    /// <summary>
    /// The most drafts that may wait above a factory's published version before a SESSION is refused another. With
    /// nothing published yet, every version is a waiting draft. The same number a seat's brief uses.
    /// </summary>
    public const int MaxWaitingDrafts = 20;

    /// <summary>How many versions one page of the history holds when the reader does not say.</summary>
    public const int DefaultHistoryPage = 50;

    /// <summary>The most versions one page of the history may hold.</summary>
    public const int MaxHistoryPage = 100;

    /// <summary>The sentence a session's publish is refused with. Held here so the routes and the tests say the
    /// same words.</summary>
    public const string OnlyAPersonPublishes =
        "Only a person publishes a factory's definition. A session of the factory may save a draft; " +
        "the owner reads it and publishes it.";

    /// <summary>The sentence a session's restore is refused with.</summary>
    public const string OnlyAPersonRestores =
        "Only a person publishes a factory's definition, and restoring an older version publishes a copy of it. " +
        "A session of the factory may save the content it wants as a new draft; the owner publishes it.";

    /// <summary>A strict UTF-8 encoding: a text that cannot be written as UTF-8 (a lone surrogate) throws instead of
    /// being quietly replaced, so a file can never be stored as anything but exactly what was sent.</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FactoryDefinitionStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    // ---------- reads ----------

    /// <summary>
    /// How a factory's definition stands: its newest version and its published version, without their content.
    /// Both are null for a factory that has no definition on the Gateway.
    /// </summary>
    public FactoryDefinitionStanding Standing(TenantId tenant, string factory)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return FactoryDefinitionStanding.None;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return StandingInside(ctx, factory);
        }
    }

    /// <summary>
    /// IS THIS FACTORY DEFINED (Factory Definition mission, phase 4): true when at least one version of its definition
    /// is PUBLISHED. A factory with only drafts, or with no definition at all, is not defined and keeps every path it
    /// had before (goal 14). This is the ONE answer every route that would define a factory another way asks
    /// (<see cref="DefinedByDefinition"/>), so it reads one indexed row and no content - cheap enough for every write.
    /// A blank or misspelt factory id is never defined: such a route names no factory a definition could hold.
    /// </summary>
    public bool IsDefined(TenantId tenant, string? factory)
    {
        if (string.IsNullOrWhiteSpace(factory) || !FactoryNames.TryFactory(factory, out var folded, out _)) return false;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return Versions(ctx, folded).Any(v => v.State == FactoryDefinitionStates.Published);
        }
    }

    /// <summary>
    /// EVERY FACTORY WITH A DEFINITION AND NOTHING PUBLISHED (Factory Definition, phase 6b): the factories whose versions
    /// are all drafts, by id, with the newest of them. A factory imported with New factory is one of these until its
    /// first publish registers it. Read without any content.
    /// </summary>
    public IReadOnlyList<FactoryDefinitionVersionSummary> Unpublished(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var published = ctx.FactoryDefinitions.AsNoTracking()
                .Where(v => v.State == FactoryDefinitionStates.Published)
                .Select(v => v.Factory).Distinct().ToList();
            var factories = ctx.FactoryDefinitions.AsNoTracking()
                .Select(v => v.Factory).Distinct().ToList()
                .Where(f => !published.Contains(f, StringComparer.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
            return factories.Select(f => Newest(ctx, f)!).ToList();
        }
    }

    /// <summary>
    /// ONE PAGE of one factory's history, newest first, WITHOUT ANY VERSION'S CONTENT. <paramref name="before"/> is
    /// the version the page starts below - null for the newest page - and the page says where the next one starts,
    /// or that there is none. <see cref="Find"/> reads one version with its content.
    /// </summary>
    public FactoryDefinitionHistoryPage History(TenantId tenant, string factory, int? before = null,
        int limit = DefaultHistoryPage)
    {
        if (limit is < 1 or > MaxHistoryPage)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"a page of the history holds 1 to {MaxHistoryPage} versions");
        if (before is < 1)
            throw new ArgumentOutOfRangeException(nameof(before), before, "versions count from 1");
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return FactoryDefinitionHistoryPage.Empty;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var all = Versions(ctx, factory);
            var total = all.Count();
            var below = before is { } b ? all.Where(v => v.Version < b) : all;
            // One more than the page, so the page knows whether an older one follows without counting again.
            var rows = Summaries(below.OrderByDescending(v => v.Version)).Take(limit + 1).ToList();
            var more = rows.Count > limit;
            var page = more ? rows.GetRange(0, limit) : rows;
            return new FactoryDefinitionHistoryPage(page, total, more ? page[^1].Version : null);
        }
    }

    /// <summary>One version of one factory's definition, with its content map, or null when there is no such
    /// version.</summary>
    public FactoryDefinitionVersion? Find(TenantId tenant, string factory, int version)
    {
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return FindInside(ctx, factory, version);
        }
    }

    /// <summary>
    /// THE STORE'S RULINGS ON A PERSON'S PUBLISH, WITHOUT WRITING: the refusal (no such version, already published,
    /// older than the published one), or null with the version to check. A publish that drives the factory asks this
    /// first, for the sentence, and never calls <see cref="Publish"/>: if the answer changes before its transaction,
    /// the transaction rules again on what it reads there (review of 4006, finding 1).
    /// </summary>
    public FactoryDefinitionWrite? RuleOnPublish(TenantId tenant, string factory, int version, out FactoryDefinitionVersion? wanted)
    {
        wanted = null;
        if (!FactoryNames.TryFactory(factory, out factory, out var factoryRefusal))
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.BadFactory, factoryRefusal!, null);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (WhyNotPublish(ctx, factory, version) is { } refused) return refused;
            wanted = FindInside(ctx, factory, version);
            return null;
        }
    }

    /// <summary>
    /// THE STORE'S RULINGS ON A PERSON'S RESTORE, WITHOUT WRITING: no such version, already the published one, and a
    /// stale <paramref name="expectedVersion"/> - or null with the version to check. As <see cref="RuleOnPublish"/>,
    /// for a restore that drives the factory.
    /// </summary>
    public FactoryDefinitionWrite? RuleOnRestore(TenantId tenant, string factory, int version, int? expectedVersion,
        out FactoryDefinitionVersion? wanted)
    {
        wanted = null;
        if (!FactoryNames.TryFactory(factory, out factory, out var factoryRefusal))
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.BadFactory, factoryRefusal!, null);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (WhyNotRestore(ctx, factory, version, out wanted) is { } refused)
            {
                wanted = null;
                return refused;
            }
            var head = Newest(ctx, factory);
            var expected = ExpectedForRestore(ctx, factory, expectedVersion);
            if (expected != (head?.Version ?? 0))
            {
                wanted = null;
                return StaleWrite(factory, expected, head);
            }
            return null;
        }
    }

    /// <summary>
    /// EVERY version of one factory's definition, oldest first, each with the text of ONE file in it - null when that
    /// version holds no such file. How a seat's brief history is read for a defined factory (phase 4, design section
    /// 9): which versions changed the seat's file is only known by reading the file in each of them. Every version's
    /// content is read once and only the one file is kept, so the answer is as large as that file's versions.
    /// </summary>
    public IReadOnlyList<FactoryDefinitionFileVersion> FileInEveryVersion(TenantId tenant, string factory, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!FactoryNames.TryFactory(factory, out factory, out _)) return Array.Empty<FactoryDefinitionFileVersion>();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var found = new List<FactoryDefinitionFileVersion>();
            foreach (var row in Versions(ctx, factory).OrderBy(v => v.Version).AsEnumerable())
                found.Add(new FactoryDefinitionFileVersion(Summary(row), ReadContent(row.Content).TryGetValue(path, out var text) ? text : null));
            FileLog.Write($"[FactoryDefinitionStore] {factory}: read {path} in {found.Count} versions");
            return found;
        }
    }

    // ---------- writes ----------

    /// <summary>
    /// Save a draft: add the next version with this content, not published. <paramref name="expectedVersion"/> is
    /// the factory's newest version as the writer last read it - 0 (or null) meaning "I believe this factory has no
    /// definition here yet". A mismatch is refused with the newest version's summary attached. A SESSION is also
    /// refused while <see cref="MaxWaitingDrafts"/> drafts already wait above the published version; a person is not.
    /// </summary>
    public FactoryDefinitionWrite SaveDraft(TenantId tenant, string factory, IReadOnlyDictionary<string, string>? files,
        int? expectedVersion, string authorKind, string? authorId, string? changeNote, DateTime nowUtc)
    {
        RequireAWriter(authorKind);
        if (!FactoryNames.TryFactory(factory, out factory, out var factoryRefusal))
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.BadFactory, factoryRefusal!, null);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return Append(ctx, tenant, factory, files, expectedVersion ?? 0, published: false, restoredFrom: null,
                authorKind, authorId, changeNote, nowUtc);
        }
    }

    /// <summary>
    /// Publish one version: from now on it is the factory's published definition. Only a person publishes. A
    /// version older than the published one is refused, because publishing never goes backwards -
    /// <see cref="Restore"/> is how an older definition is used again.
    /// </summary>
    public FactoryDefinitionWrite Publish(TenantId tenant, string factory, int version,
        string authorKind, string? authorId, DateTime nowUtc)
    {
        RequireAWriter(authorKind);
        if (authorKind != FactoryMemoryAuthorKinds.Person)
        {
            FileLog.Write($"[FactoryDefinitionStore] {factory}: REFUSED a publish of version {version} by {authorKind} {authorId ?? "(unknown)"}");
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.OnlyAPersonPublishes, OnlyAPersonPublishes, null);
        }
        if (!FactoryNames.TryFactory(factory, out factory, out var factoryRefusal))
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.BadFactory, factoryRefusal!, null);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (WhyNotPublish(ctx, factory, version) is { } refused) return refused;

            var changed = MarkPublished(ctx, factory, version, authorId, nowUtc);
            var now = SummaryOf(ctx, factory, version);
            if (changed == 0) return PublishedFirstBySomebodyElse(factory, version, now);
            FileLog.Write($"[FactoryDefinitionStore] {factory}: published version {version} by person {authorId ?? "(unknown)"}");
            return FactoryDefinitionWrite.Published(now!);
        }
    }

    /// <summary>
    /// THE STORE'S RULINGS ON A PUBLISH, read through <paramref name="ctx"/>: no such version, already published,
    /// older than the published one. Null when the version may be published. Shared by <see cref="Publish"/> and a
    /// definition's publish that drives the factory (phase 4), which asks inside its own transaction.
    /// </summary>
    internal static FactoryDefinitionWrite? WhyNotPublish(GatewayDbContext ctx, string factory, int version)
    {
        var standing = StandingInside(ctx, factory);
        var wanted = SummaryOf(ctx, factory, version);
        if (wanted is null)
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.NoSuchVersion,
                NoSuchVersion(factory, version, standing, "publish"), standing.Newest);

        if (wanted.State == FactoryDefinitionStates.Published)
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.AlreadyPublished,
                standing.Published?.Version == version
                    ? $"version {version} is already the published definition of {factory}"
                    : $"version {version} was published before, and version {standing.Published?.Version} has replaced it. " +
                      $"To use it again, restore version {version}: that publishes a copy of it as the newest version",
                standing.Published);

        if (standing.Published is { } published && published.Version > version)
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.OlderThanPublished,
                $"version {version} is older than the published version {published.Version}. Publishing does not go " +
                $"backwards: restore version {version}, which publishes a copy of it as the newest version",
                published);
        return null;
    }

    /// <summary>The refusal when <see cref="MarkPublished"/> marked nothing: another publish got there first.</summary>
    internal static FactoryDefinitionWrite PublishedFirstBySomebodyElse(string factory, int version, FactoryDefinitionVersionSummary? now)
    {
        FileLog.Write($"[FactoryDefinitionStore] {factory}: version {version} was published by somebody else first");
        return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.AlreadyPublished,
            $"version {version} of the definition of {factory} was published by somebody else a moment ago", now);
    }

    /// <summary>
    /// THE STORE'S RULINGS ON A RESTORE, read through <paramref name="ctx"/>: no such version, already the published
    /// one. Returns the refusal, or null with the version to copy. Shared by <see cref="Restore"/> and a definition's
    /// restore that drives the factory (phase 4).
    /// </summary>
    internal static FactoryDefinitionWrite? WhyNotRestore(GatewayDbContext ctx, string factory, int version,
        out FactoryDefinitionVersion? wanted)
    {
        var standing = StandingInside(ctx, factory);
        wanted = FindInside(ctx, factory, version);
        if (wanted is null)
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.NoSuchVersion,
                NoSuchVersion(factory, version, standing, "restore"), standing.Newest);
        if (standing.Published?.Version == version)
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.AlreadyPublished,
                $"version {version} is the published definition of {factory} already, so there is nothing to restore",
                standing.Published);
        return null;
    }

    /// <summary>The newest version a restore's copy must follow: the one the person was looking at, or - when they
    /// state none - whatever is newest now.</summary>
    internal static int ExpectedForRestore(GatewayDbContext ctx, string factory, int? expectedVersion)
        => expectedVersion is > 0 ? expectedVersion.Value : Newest(ctx, factory)!.Version;

    /// <summary>
    /// Restore an older version: add a copy of its content as the NEWEST version, already published, authored by the
    /// person who restored it and recording which version it copied. It is not a rewind - the history keeps every
    /// version in between. Only a person restores, because it publishes. <paramref name="expectedVersion"/> is the
    /// newest version the person was looking at, so a restore can lose a race with a draft saved since; a caller
    /// that states none is taken at its word and the copy goes after what is newest.
    /// </summary>
    public FactoryDefinitionWrite Restore(TenantId tenant, string factory, int version,
        string authorKind, string? authorId, string? changeNote, DateTime nowUtc, int? expectedVersion = null)
    {
        RequireAWriter(authorKind);
        if (authorKind != FactoryMemoryAuthorKinds.Person)
        {
            FileLog.Write($"[FactoryDefinitionStore] {factory}: REFUSED a restore of version {version} by {authorKind} {authorId ?? "(unknown)"}");
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.OnlyAPersonPublishes, OnlyAPersonRestores, null);
        }
        if (!FactoryNames.TryFactory(factory, out factory, out var factoryRefusal))
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.BadFactory, factoryRefusal!, null);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (WhyNotRestore(ctx, factory, version, out var wanted) is { } refused) return refused;

            return Append(ctx, tenant, factory, wanted!.Files, ExpectedForRestore(ctx, factory, expectedVersion),
                published: true, restoredFrom: version, authorKind, authorId, changeNote, nowUtc);
        }
    }

    // ---------- the rules ----------

    /// <summary>
    /// Why <paramref name="path"/> cannot be a path in a definition, or null when it can: the path rules and the places
    /// of the design's section 1, exactly as the whole-definition check holds them (<see cref="DefinitionPaths"/>).
    /// </summary>
    public static string? WhyNotAPath(string? path)
    {
        if (path is null) return "a definition holds no file without a path";
        return DefinitionPaths.ShapeProblem(path) ?? DefinitionPaths.Classify(path).Refusal;
    }

    /// <summary>The refusal of a path that maps to no text at all (a JSON null): the route says it in these words before
    /// the check runs, and the store says the same if a caller reaches it directly.</summary>
    public static string NoTextRefusal(string path) =>
        $"'{path}' has no text; every file in a definition is text, even if it is empty";

    /// <summary>A version is written by a person or by a session. Anything else is a caller this store was never
    /// meant to be reached by, which is a defect in the route rather than a refusal to word.</summary>
    private static void RequireAWriter(string authorKind)
    {
        if (authorKind is not (FactoryMemoryAuthorKinds.Person or FactoryMemoryAuthorKinds.Session))
            throw new ArgumentException($"'{authorKind}' is not a kind of writer: a definition is written by a person or a session.", nameof(authorKind));
    }

    /// <summary>
    /// Every file's path and size, held to the rules. Returns the refusal, or null with the files in their one order
    /// and their total size. Content is refused, never repaired: a path is not folded and a text is not re-encoded.
    /// </summary>
    private static FactoryDefinitionWrite? WhyNotContent(IReadOnlyDictionary<string, string>? files,
        FactoryDefinitionVersionSummary? head, out SortedDictionary<string, string> ordered, out int totalBytes)
    {
        ordered = new SortedDictionary<string, string>(StringComparer.Ordinal);
        totalBytes = 0;
        if (files is null || files.Count == 0)
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.Empty,
                "a definition needs files: at least factory.yaml and one seat", head);
        if (files.Count > DefinitionPaths.MaxFiles)
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.TooManyFiles,
                $"the definition holds {files.Count} files; a definition holds at most {DefinitionPaths.MaxFiles}", head);

        foreach (var (path, text) in files)
        {
            if (WhyNotAPath(path) is { } badPath)
                return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.BadPath, badPath, head);
            if (text is null)
                return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.NotText, NoTextRefusal(path), head);
            int bytes;
            try
            {
                bytes = StrictUtf8.GetByteCount(text);
            }
            catch (EncoderFallbackException)
            {
                return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.NotText,
                    $"'{path}' is not text that can be written as UTF-8 (it holds half of a character pair)", head);
            }
            if (bytes > DefinitionPaths.MaxFileBytes)
                return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.FileTooLarge,
                    $"'{path}' takes {bytes} bytes; a file in a definition takes at most {DefinitionPaths.MaxFileBytes}", head);
            totalBytes += bytes;
            if (totalBytes > DefinitionPaths.MaxVersionBytes)
                return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.VersionTooLarge,
                    $"the definition takes more than {DefinitionPaths.MaxVersionBytes} bytes; a version takes at most {DefinitionPaths.MaxVersionBytes} in all", head);
            ordered[path] = text;
        }
        var clashes = DefinitionPaths.Clashes(ordered.Keys);
        if (clashes.Count > 0)
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.BadPath, clashes[0].Refusal, head);
        return null;
    }

    private static FactoryDefinitionWrite Append(GatewayDbContext ctx, TenantId tenant, string factory,
        IReadOnlyDictionary<string, string>? files, int expectedVersion, bool published, int? restoredFrom,
        string authorKind, string? authorId, string? changeNote, DateTime nowUtc)
    {
        if (PrepareAppend(ctx, tenant, factory, files, expectedVersion, published, restoredFrom, authorKind, authorId,
                changeNote, nowUtc, out var written) is { } refused)
            return refused;

        // THE KEY DOES THE ARBITRATION. A concurrent writer that got here first owns version current+1, and this
        // insert then violates the primary key - which is reported as the same stale refusal a slow reader gets,
        // because that is what happened.
        ctx.FactoryDefinitions.Add(written!);
        try
        {
            ctx.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            ctx.ChangeTracker.Clear();
            return AfterAFailedAppend(ctx, factory, written!.Version, ex);
        }

        FileLog.Write($"[FactoryDefinitionStore] {factory}: wrote version {written!.Version} ({written.State}, {written.FileCount} files, {written.ContentBytes} bytes" +
                      $"{(restoredFrom is { } from ? $", restored from {from}" : "")}) by {authorKind} {authorId ?? "(unknown)"}");
        var summary = Summary(written);
        return published ? FactoryDefinitionWrite.Published(summary) : FactoryDefinitionWrite.Saved(summary);
    }

    /// <summary>
    /// WHY AN APPEND FAILED, read from a context that can still read: another writer took the version (the stale
    /// refusal), or the write failed for its own reason. ONLY A SECOND WRITER IS REPORTED AS ONE, as for briefs: a key
    /// violation is one cause of this exception and not the only one. Telling a caller that another writer won when
    /// none did tells it to merge and try again for ever. So the head decides.
    /// </summary>
    internal static FactoryDefinitionWrite AfterAFailedAppend(GatewayDbContext ctx, string factory, int attemptedVersion, Exception ex)
    {
        var headNow = Newest(ctx, factory);
        if (WasTakenByAnotherWriter(headNow, attemptedVersion))
        {
            FileLog.Write($"[FactoryDefinitionStore] {factory}: version {attemptedVersion} was taken by another writer");
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.Stale,
                $"another writer took version {attemptedVersion} of the definition of {factory} first; read it again, merge, and save once more",
                headNow);
        }

        FileLog.Write($"[FactoryDefinitionStore] {factory}: the write FAILED and no other writer holds version {attemptedVersion}: {ex.Message}");
        return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.WriteFailed,
            $"the definition of {factory} could not be stored: {Innermost(ex).Message}", headNow);
    }

    /// <summary>
    /// THE TWO-WRITER REFUSAL: the writer expected another newest version. The newest version rides the refusal so the
    /// caller can merge rather than read again and race again.
    /// </summary>
    internal static FactoryDefinitionWrite StaleWrite(string factory, int expectedVersion, FactoryDefinitionVersionSummary? head)
    {
        var current = head?.Version ?? 0;
        FileLog.Write($"[FactoryDefinitionStore] {factory}: REFUSED a stale write (expected {expectedVersion}, newest {current})");
        return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.Stale,
            current == 0
                ? $"{factory} has no definition on the Gateway yet, so save it expecting version 0"
                : $"the definition of {factory} is at version {current}, not {expectedVersion}; read it again, merge, and save once more",
            head);
    }

    /// <summary>
    /// Every rule an appended version is held to - the writer's version is the newest, a session's waiting drafts,
    /// the content, the change note - and the row it would be. Returns the refusal, or null with
    /// <paramref name="written"/> ready to add. Adds nothing to <paramref name="ctx"/>: the store's own append and a
    /// definition's restore (phase 4, inside its own transaction) both add and save it.
    /// </summary>
    internal static FactoryDefinitionWrite? PrepareAppend(GatewayDbContext ctx, TenantId tenant, string factory,
        IReadOnlyDictionary<string, string>? files, int expectedVersion, bool published, int? restoredFrom,
        string authorKind, string? authorId, string? changeNote, DateTime nowUtc, out FactoryDefinitionVersionEntity? written)
    {
        written = null;
        var head = Newest(ctx, factory);
        var current = head?.Version ?? 0;
        if (expectedVersion != current)
            return StaleWrite(factory, expectedVersion, head);

        if (!published && authorKind == FactoryMemoryAuthorKinds.Session
            && TooManyWaitingDrafts(ctx, factory, head, authorId) is { } tooMany)
            return tooMany;

        if (WhyNotContent(files, head, out var ordered, out var bytes) is { } badContent)
        {
            FileLog.Write($"[FactoryDefinitionStore] {factory}: REFUSED the content ({badContent.Outcome})");
            return badContent;
        }

        var note = string.IsNullOrEmpty(changeNote) ? null : changeNote;
        if (note is { Length: > MaxChangeNoteLength })
            return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.ChangeNoteTooLong,
                $"the change note is {note.Length} characters long; a change note is at most {MaxChangeNoteLength}", head);

        written = new FactoryDefinitionVersionEntity
        {
            TenantId = tenant.Value,
            Factory = factory,
            Version = current + 1,
            // THE SAME MAP IS ALWAYS THE SAME BYTES: the keys are written in ordinal order, so two equal maps store
            // equal text and a later reader can compare versions by their content alone.
            Content = JsonSerializer.Serialize(ordered),
            ContentBytes = bytes,
            FileCount = ordered.Count,
            State = published ? FactoryDefinitionStates.Published : FactoryDefinitionStates.Draft,
            AuthorKind = authorKind,
            AuthorId = authorId,
            ChangeNote = note,
            RestoredFrom = restoredFrom,
            WrittenAtUtc = nowUtc,
            PublishedAtUtc = published ? nowUtc : null,
            PublishedBy = published ? authorId : null,
        };

        return null;
    }

    /// <summary>
    /// THE BOUND ON A SESSION'S WRITING, as for briefs. The drafts that count are the ones ABOVE the published
    /// version, whoever wrote them; with nothing published, every version counts. Returns the refusal when
    /// <see cref="MaxWaitingDrafts"/> already wait, or null when there is room.
    /// </summary>
    private static FactoryDefinitionWrite? TooManyWaitingDrafts(GatewayDbContext ctx, string factory,
        FactoryDefinitionVersionSummary? head, string? authorId)
    {
        if (head is null) return null;
        var published = Versions(ctx, factory).Where(v => v.State == FactoryDefinitionStates.Published)
            .Select(v => (int?)v.Version).Max() ?? 0;
        var waiting = Versions(ctx, factory).Count(v => v.Version > published);
        if (waiting < MaxWaitingDrafts) return null;

        FileLog.Write($"[FactoryDefinitionStore] {factory}: REFUSED a draft by session {authorId ?? "(unknown)"} - {waiting} drafts already wait above version {published}");
        return FactoryDefinitionWrite.Refused(FactoryDefinitionOutcome.TooManyDrafts,
            $"the definition of {factory} has {waiting} drafts waiting that nobody has published (versions {published + 1} to {head.Version}), " +
            $"and a session may add a draft only while fewer than {MaxWaitingDrafts} are waiting. Nothing was saved, and no " +
            "version was removed. Ask the owner to read the drafts and publish one: publishing makes room for more.",
            head);
    }

    private static string NoSuchVersion(string factory, int version, FactoryDefinitionStanding standing, string act) =>
        standing.Newest is null
            ? $"{factory} has no definition on the Gateway yet, so there is nothing to {act}. Save a draft first."
            : $"version {version} of the definition of {factory} does not exist; its newest version is {standing.Newest.Version}";

    /// <summary>
    /// THE ONE CHANGE A STORED ROW EVER SEES, and it only ever happens once: the update matches the row only while it
    /// is still a draft, so two people publishing the same version on two replicas cannot both be recorded as the one
    /// who did - the second matches nothing and is told somebody else published first. The check on the version's
    /// state that <see cref="Publish"/> makes before this runs inside one process and reads from before the other
    /// replica's update, so THIS condition is the guard. Returns how many rows it marked: 1, or 0 when the row was
    /// already published.
    /// </summary>
    internal static int MarkPublished(GatewayDbContext ctx, string factory, int version, string? publishedBy, DateTime nowUtc)
        => ctx.FactoryDefinitions
            .Where(v => v.Factory == factory && v.Version == version && v.State == FactoryDefinitionStates.Draft)
            .ExecuteUpdate(set => set
                .SetProperty(v => v.State, FactoryDefinitionStates.Published)
                .SetProperty(v => v.PublishedAtUtc, (DateTime?)nowUtc)
                .SetProperty(v => v.PublishedBy, publishedBy));

    /// <summary>
    /// Did a second writer really take this version? The head decides: an insert can fail for reasons that have no
    /// writer behind them at all. True only when the version this write wanted now exists.
    /// </summary>
    internal static bool WasTakenByAnotherWriter(FactoryDefinitionVersionSummary? head, int attemptedVersion)
        => head is not null && head.Version >= attemptedVersion;

    /// <summary>The innermost reason, which is the one a database driver puts the real message on.</summary>
    private static Exception Innermost(Exception ex)
    {
        while (ex.InnerException is { } inner) ex = inner;
        return ex;
    }

    /// <summary>The content map as it was stored. A row this store wrote always reads back; one that does not is a
    /// damaged row, and saying so is the only honest answer.</summary>
    /// <summary>One version with its content, read through <paramref name="ctx"/>, or null when there is no such
    /// version. The factory id is already folded.</summary>
    internal static FactoryDefinitionVersion? FindInside(GatewayDbContext ctx, string factory, int version)
    {
        var row = Versions(ctx, factory).FirstOrDefault(v => v.Version == version);
        return row is null ? null : new FactoryDefinitionVersion(Summary(row), ReadContent(row.Content));
    }

    private static SortedDictionary<string, string> ReadContent(string content)
    {
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(content)
                  ?? throw new InvalidOperationException("a stored definition's content is not a map of path to text");
        return new SortedDictionary<string, string>(map, StringComparer.Ordinal);
    }

    private static IQueryable<FactoryDefinitionVersionEntity> Versions(GatewayDbContext ctx, string factory)
        => ctx.FactoryDefinitions.AsNoTracking().Where(v => v.Factory == factory);

    /// <summary>Versions as summaries, projected in the query so the content is never read.</summary>
    private static IQueryable<FactoryDefinitionVersionSummary> Summaries(IQueryable<FactoryDefinitionVersionEntity> rows)
        => rows.Select(v => new FactoryDefinitionVersionSummary(v.Factory, v.Version, v.State, v.AuthorKind, v.AuthorId,
            v.ChangeNote, v.RestoredFrom, v.WrittenAtUtc, v.PublishedAtUtc, v.PublishedBy, v.ContentBytes, v.FileCount));

    private static FactoryDefinitionVersionSummary Summary(FactoryDefinitionVersionEntity v)
        => new(v.Factory, v.Version, v.State, v.AuthorKind, v.AuthorId, v.ChangeNote, v.RestoredFrom, v.WrittenAtUtc,
            v.PublishedAtUtc, v.PublishedBy, v.ContentBytes, v.FileCount);

    internal static FactoryDefinitionVersionSummary? SummaryOf(GatewayDbContext ctx, string factory, int version)
        => Summaries(Versions(ctx, factory).Where(v => v.Version == version)).FirstOrDefault();

    /// <summary>The highest version of one factory's definition, or null when it has none.</summary>
    private static FactoryDefinitionVersionSummary? Newest(GatewayDbContext ctx, string factory)
        => Summaries(Versions(ctx, factory).OrderByDescending(v => v.Version)).FirstOrDefault();

    /// <summary>How a factory's definition stands, read through <paramref name="ctx"/> (inside a caller's transaction,
    /// when it has one). The factory id is already folded.</summary>
    internal static FactoryDefinitionStanding StandingInside(GatewayDbContext ctx, string factory)
        => new(Newest(ctx, factory),
            // THE PUBLISHED DEFINITION IS THE HIGHEST VERSION THAT IS PUBLISHED. Publishing only ever moves forward,
            // so this is one rule every reader shares instead of a pointer two replicas could disagree about.
            Summaries(Versions(ctx, factory).Where(v => v.State == FactoryDefinitionStates.Published)
                .OrderByDescending(v => v.Version)).FirstOrDefault());
}

/// <summary>How one factory's definition stands: its newest version and the one that is published, without content.
/// Both null when the factory has no definition on the Gateway.</summary>
public sealed record FactoryDefinitionStanding(FactoryDefinitionVersionSummary? Newest, FactoryDefinitionVersionSummary? Published)
{
    public static readonly FactoryDefinitionStanding None = new(null, null);
}

/// <summary>
/// One version of a definition, everything about it EXCEPT its content: who wrote it and when, its change note, the
/// version a restore copied it from, how many bytes its files take and how many files it holds.
/// </summary>
public sealed record FactoryDefinitionVersionSummary(string Factory, int Version, string State, string AuthorKind,
    string? AuthorId, string? ChangeNote, int? RestoredFrom, DateTime WrittenAtUtc, DateTime? PublishedAtUtc,
    string? PublishedBy, int ContentBytes, int FileCount);

/// <summary>One version of a definition with the text of one file in it, or null when that version holds no such file.</summary>
public sealed record FactoryDefinitionFileVersion(FactoryDefinitionVersionSummary Summary, string? Text);

/// <summary>One version of a definition with its content: path to text, in ordinal order of the paths.</summary>
public sealed record FactoryDefinitionVersion(FactoryDefinitionVersionSummary Summary, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// One page of a factory's history, newest first. <see cref="Total"/> is how many versions the factory has in all,
/// and <see cref="NextBefore"/> is the version to ask for the next page below, or null when this page reaches the
/// oldest version.
/// </summary>
public sealed record FactoryDefinitionHistoryPage(IReadOnlyList<FactoryDefinitionVersionSummary> Versions, int Total, int? NextBefore)
{
    public static readonly FactoryDefinitionHistoryPage Empty = new(Array.Empty<FactoryDefinitionVersionSummary>(), 0, null);
}

/// <summary>What a write to a factory's definition did, or why it was refused.</summary>
public enum FactoryDefinitionOutcome
{
    /// <summary>A new draft version was saved.</summary>
    Saved,
    /// <summary>A version was published, or an older one was restored as a published copy.</summary>
    Published,
    /// <summary>The writer's version is not the newest one - two writers, or a slow reader.</summary>
    Stale,
    /// <summary>The content holds no files.</summary>
    Empty,
    /// <summary>The content holds more than <see cref="DefinitionPaths.MaxFiles"/> files.</summary>
    TooManyFiles,
    /// <summary>A path breaks the path rules or is not a place a definition holds.</summary>
    BadPath,
    /// <summary>A file has no text, or text that cannot be written as UTF-8.</summary>
    NotText,
    /// <summary>One file is over <see cref="DefinitionPaths.MaxFileBytes"/>.</summary>
    FileTooLarge,
    /// <summary>The files together are over <see cref="DefinitionPaths.MaxVersionBytes"/>.</summary>
    VersionTooLarge,
    /// <summary>The change note is over <see cref="FactoryDefinitionStore.MaxChangeNoteLength"/> characters.</summary>
    ChangeNoteTooLong,
    /// <summary>A session asked to save a draft while <see cref="FactoryDefinitionStore.MaxWaitingDrafts"/> drafts
    /// already wait above the published version. A person's publish makes room.</summary>
    TooManyDrafts,
    /// <summary>The factory id is not one spelling of a factory id.</summary>
    BadFactory,
    NoSuchVersion,
    AlreadyPublished,
    /// <summary>The version is older than the published one; publishing never goes backwards.</summary>
    OlderThanPublished,
    /// <summary>A session asked to publish or to restore.</summary>
    OnlyAPersonPublishes,
    /// <summary>The write failed for its own reason, and no second writer holds the version it wanted. For a publish
    /// or a restore that drives the factory (phase 4): the transaction failed and changed nothing.</summary>
    WriteFailed,
    /// <summary>A publish or a restore for a factory the registry does not hold (phase 4: until phase 5 a defined
    /// factory runs in its registered folder).</summary>
    NotRegistered,
    /// <summary>A publish or a restore for an archived factory (phase 4).</summary>
    Archived,
    /// <summary>A publish or a restore whose plan would make a row its own rules refuse (phase 4).</summary>
    PlanRefused,
}

/// <summary>
/// The result of a write. A refusal carries the summary of the version the caller needs in order to act on it where
/// there is one - the newest version for a stale save, the published one for a publish that would go backwards.
/// </summary>
public sealed record FactoryDefinitionWrite(FactoryDefinitionOutcome Outcome, string? Refusal, FactoryDefinitionVersionSummary? Version)
{
    public bool Ok => Outcome is FactoryDefinitionOutcome.Saved or FactoryDefinitionOutcome.Published;

    public static FactoryDefinitionWrite Saved(FactoryDefinitionVersionSummary version) => new(FactoryDefinitionOutcome.Saved, null, version);

    public static FactoryDefinitionWrite Published(FactoryDefinitionVersionSummary version) => new(FactoryDefinitionOutcome.Published, null, version);

    public static FactoryDefinitionWrite Refused(FactoryDefinitionOutcome outcome, string refusal, FactoryDefinitionVersionSummary? current)
        => new(outcome, refusal, current);
}
