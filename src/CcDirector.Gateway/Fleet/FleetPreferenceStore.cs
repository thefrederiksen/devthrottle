using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Fleet;

/// <summary>
/// The owner's standing preferences for the Fleet Manager - "stop asking me about draft posts, just stage
/// them" - over the <c>fleet_preferences</c> table (the Fleet Manager mission, step 3).
///
/// THE TEXT IS THE OWNER'S WORDS, STORED EXACTLY AS GIVEN: not trimmed, not reworded. A blank preference is
/// refused rather than stored, because a preference nobody can read is one the Fleet Manager would act on
/// without knowing what it says.
///
/// LESSONS (issue #3559). The same table holds a second kind of row: a LESSON, the owner's correction of a mistake
/// the Fleet Manager made. It has the same shape - the owner's words, a date, who kept it - plus one optional line
/// saying what went wrong (<see cref="MaxMistakeLength"/>) and whether the owner kept or confirmed it. Lessons and
/// preferences are kept apart everywhere they are read: <see cref="List(TenantId)"/> still answers preferences only.
///
/// ONLY A CONFIRMED LESSON IS OBEYED. A lesson the owner keeps on their own device is confirmed when it is stored. A
/// lesson the Fleet Manager keeps with its session key waits for the owner's one-press confirmation, because any
/// session key can mark itself the Fleet Manager, and an unconfirmed lesson given to every later Fleet Manager as
/// "obey these" would be an instruction channel any session could write to.
///
/// CAPPED, NEVER CUT. A lesson is at most <see cref="MaxLessonLength"/> characters and an account holds at most
/// <see cref="MaxConfirmedLessons"/> confirmed ones, because the confirmed lessons are typed into a terminal at every
/// start. A lesson beyond either limit is REFUSED with instructions: it is never shortened, and it never pushes out
/// an older lesson.
///
/// Tenant-partitioned by construction, like <see cref="FleetOutcomeStore"/>.
/// </summary>
public sealed class FleetPreferenceStore
{
    /// <summary>The longest preference accepted.</summary>
    public const int MaxTextLength = 2000;

    /// <summary>A standing preference: how the owner wants things done.</summary>
    public const string KindPreference = "preference";

    /// <summary>A lesson: the owner's correction of a mistake, so no later Fleet Manager makes it again.</summary>
    public const string KindLesson = "lesson";

    public static readonly IReadOnlyList<string> Kinds = new[] { KindPreference, KindLesson };

    /// <summary>The longest lesson accepted. The confirmed lessons are typed into a terminal, so they are capped.</summary>
    public const int MaxLessonLength = 500;

    /// <summary>The longest one-line description of the mistake a lesson may carry.</summary>
    public const int MaxMistakeLength = 300;

    /// <summary>The most confirmed lessons one account holds.</summary>
    public const int MaxConfirmedLessons = 20;

    /// <summary>Where a refusal sends the reader to make room.</summary>
    public const string MakeRoomHint =
        "list them with `cc-devthrottle fleet preferences --kind lesson`, or on the Cockpit's Fleet Manager page, and "
        + "remove or edit one there first";

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FleetPreferenceStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Keep one preference and return it as stored.</summary>
    /// <param name="createdBy">The calling session id, or <see cref="FleetOutcomeStore.OwnerCaller"/>.</param>
    /// <exception cref="ArgumentException">The text is blank or too long.</exception>
    public FleetPreferenceDto Add(TenantId tenant, string? text, string createdBy, DateTime nowUtc)
    {
        FileLog.Write($"[FleetPreferenceStore] Add: tenant={tenant}, createdBy={createdBy}, length={text?.Length ?? 0}");
        try
        {
            CheckPreferenceText(text);
            if (string.IsNullOrWhiteSpace(createdBy))
                throw new ArgumentException($"createdBy is required: a session id or '{FleetOutcomeStore.OwnerCaller}'");

            var entity = new FleetPreferenceEntity
            {
                Text = text!,
                CreatedAtUtc = Utc(nowUtc),
                CreatedBy = createdBy,
                Kind = KindPreference,
            };
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                entity.TenantId = ctx.ActiveTenant!;
                ctx.FleetPreferences.Add(entity);
                ctx.SaveChanges();
            }

            FileLog.Write($"[FleetPreferenceStore] Add: stored id={entity.Id}");
            return ToDto(entity);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetPreferenceStore] Add FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Keep one lesson and return it as stored. A lesson kept by the owner is confirmed now; one kept by the Fleet
    /// Manager waits for the owner's confirmation. When <paramref name="lessonEvent"/> is given, the event it builds
    /// is saved IN THE SAME SAVE as the lesson, so a kept lesson is never without its event.
    /// </summary>
    /// <param name="createdBy">The calling session id, or <see cref="FleetOutcomeStore.OwnerCaller"/>.</param>
    /// <param name="confirmed">True when the owner kept it on their own device.</param>
    /// <exception cref="ArgumentException">The text is blank or too long, the mistake is too long, or the account
    /// already holds <see cref="MaxConfirmedLessons"/> confirmed lessons.</exception>
    public FleetPreferenceDto AddLesson(TenantId tenant, string? text, string? mistake, string createdBy, bool confirmed,
        DateTime nowUtc, Func<FleetPreferenceDto, FleetManagerEventEntity>? lessonEvent = null)
    {
        FileLog.Write($"[FleetPreferenceStore] AddLesson: tenant={tenant}, createdBy={createdBy}, confirmed={confirmed}, "
                      + $"length={text?.Length ?? 0}, mistakeLength={mistake?.Length ?? 0}");
        try
        {
            CheckLessonText(text);
            var line = CheckMistake(mistake);
            if (string.IsNullOrWhiteSpace(createdBy))
                throw new ArgumentException($"createdBy is required: a session id or '{FleetOutcomeStore.OwnerCaller}'");

            var now = Utc(nowUtc);
            var entity = new FleetPreferenceEntity
            {
                Text = text!,
                CreatedAtUtc = now,
                CreatedBy = createdBy,
                Kind = KindLesson,
                Mistake = line,
                ConfirmedByOwnerAtUtc = confirmed ? now : null,
            };
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                if (confirmed) CheckRoomForOneMoreConfirmed(ctx);
                entity.TenantId = ctx.ActiveTenant!;
                ctx.FleetPreferences.Add(entity);
                if (lessonEvent is not null)
                {
                    var evt = lessonEvent(ToDto(entity));
                    evt.TenantId = ctx.ActiveTenant!;
                    ctx.FleetManagerEvents.Add(evt);
                }
                ctx.SaveChanges();
            }

            FileLog.Write($"[FleetPreferenceStore] AddLesson: stored id={entity.Id}, confirmed={confirmed}, "
                          + $"event={lessonEvent is not null}");
            return ToDto(entity);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetPreferenceStore] AddLesson FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Every standing preference this account holds, oldest first - the order the owner gave them. Lessons
    /// are never among them (<see cref="List(TenantId, string)"/>).</summary>
    public IReadOnlyList<FleetPreferenceDto> List(TenantId tenant) => List(tenant, KindPreference);

    /// <summary>Every row of one kind this account holds, oldest first.</summary>
    /// <exception cref="ArgumentException">The kind is not <c>preference</c> or <c>lesson</c>.</exception>
    public IReadOnlyList<FleetPreferenceDto> List(TenantId tenant, string kind)
    {
        CheckKind(kind);
        FileLog.Write($"[FleetPreferenceStore] List: tenant={tenant}, kind={kind}");
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.FleetPreferences.AsNoTracking()
            .Where(p => p.Kind == kind)
            .OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id)
            .ToList();
        FileLog.Write($"[FleetPreferenceStore] List: kind={kind}, returned={rows.Count}");
        return rows.Select(ToDto).ToList();
    }

    /// <summary>The lessons the owner kept or confirmed, oldest first - the only ones given to a Fleet Manager as
    /// lessons it must obey.</summary>
    public IReadOnlyList<FleetPreferenceDto> ConfirmedLessons(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        var rows = ctx.FleetPreferences.AsNoTracking()
            .Where(p => p.Kind == KindLesson && p.ConfirmedByOwnerAtUtc != null)
            .OrderBy(p => p.CreatedAtUtc).ThenBy(p => p.Id)
            .ToList();
        FileLog.Write($"[FleetPreferenceStore] ConfirmedLessons: tenant={tenant}, returned={rows.Count}");
        return rows.Select(ToDto).ToList();
    }

    /// <summary>One row of this account by its id, or null.</summary>
    public FleetPreferenceDto? Find(TenantId tenant, Guid id)
    {
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.FleetPreferences.AsNoTracking().FirstOrDefault(p => p.Id == id);
        return row is null ? null : ToDto(row);
    }

    /// <summary>The owner confirms a lesson the Fleet Manager kept. Confirming one already confirmed changes
    /// nothing.</summary>
    /// <exception cref="ArgumentException">The row is a preference, or the account already holds
    /// <see cref="MaxConfirmedLessons"/> confirmed lessons.</exception>
    /// <returns>The lesson as stored, or null when this account holds no row with that id.</returns>
    public FleetPreferenceDto? Confirm(TenantId tenant, Guid id, DateTime nowUtc)
    {
        FileLog.Write($"[FleetPreferenceStore] Confirm: tenant={tenant}, id={id}");
        try
        {
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var row = ctx.FleetPreferences.FirstOrDefault(p => p.Id == id);
                if (row is null)
                {
                    FileLog.Write($"[FleetPreferenceStore] Confirm: id={id}, result=not found");
                    return null;
                }
                if (row.Kind != KindLesson)
                    throw new ArgumentException($"{id} is a standing preference, not a lesson; only a lesson is confirmed");
                if (row.ConfirmedByOwnerAtUtc is not null)
                {
                    FileLog.Write($"[FleetPreferenceStore] Confirm: id={id}, result=already confirmed");
                    return ToDto(row);
                }
                CheckRoomForOneMoreConfirmed(ctx);
                row.ConfirmedByOwnerAtUtc = Utc(nowUtc);
                ctx.SaveChanges();
                FileLog.Write($"[FleetPreferenceStore] Confirm: id={id}, result=confirmed");
                return ToDto(row);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetPreferenceStore] Confirm FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// The owner rewrites one row in their own words. A lesson keeps its confirmation, and its line about the mistake
    /// is replaced by <paramref name="mistake"/> (null clears it). A preference takes no mistake.
    /// </summary>
    /// <exception cref="ArgumentException">The text is blank or too long for its kind, or a preference was given a
    /// mistake.</exception>
    /// <returns>The row as stored, or null when this account holds no row with that id.</returns>
    public FleetPreferenceDto? Update(TenantId tenant, Guid id, string? text, string? mistake)
    {
        FileLog.Write($"[FleetPreferenceStore] Update: tenant={tenant}, id={id}, length={text?.Length ?? 0}");
        try
        {
            lock (_gate)
            {
                using var ctx = _db.CreateContext(tenant);
                var row = ctx.FleetPreferences.FirstOrDefault(p => p.Id == id);
                if (row is null)
                {
                    FileLog.Write($"[FleetPreferenceStore] Update: id={id}, result=not found");
                    return null;
                }
                if (row.Kind == KindLesson)
                {
                    CheckLessonText(text);
                    row.Mistake = CheckMistake(mistake);
                }
                else
                {
                    CheckPreferenceText(text);
                    if (mistake is not null)
                        throw new ArgumentException("a standing preference has no mistake; only a lesson says what went wrong");
                }
                row.Text = text!;
                ctx.SaveChanges();
                FileLog.Write($"[FleetPreferenceStore] Update: id={id}, kind={row.Kind}, result=updated");
                return ToDto(row);
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FleetPreferenceStore] Update FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Remove one row, preference or lesson. False when this account holds none with that id. Who may remove
    /// which row is the route's ruling (<see cref="MayForget"/>), made before this is called.</summary>
    public bool Delete(TenantId tenant, Guid id)
    {
        FileLog.Write($"[FleetPreferenceStore] Delete: tenant={tenant}, id={id}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.FleetPreferences.FirstOrDefault(p => p.Id == id);
            if (row is null)
            {
                FileLog.Write($"[FleetPreferenceStore] Delete: id={id}, result=not found");
                return false;
            }
            ctx.FleetPreferences.Remove(row);
            ctx.SaveChanges();
            FileLog.Write($"[FleetPreferenceStore] Delete: id={id}, kind={row.Kind}, result=removed");
            return true;
        }
    }

    /// <summary>
    /// Whether a Fleet Manager session may forget <paramref name="row"/>: any standing preference, and only an
    /// UNCONFIRMED lesson it kept itself. A confirmed lesson is removed only from the owner's own device. Null when
    /// it may; otherwise the sentence that says why not.
    /// </summary>
    public static string? MayForget(FleetPreferenceDto row, string fleetManagerSessionId)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Kind != KindLesson) return null;
        if (row.ConfirmedByOwnerAtUtc is not null)
            return $"lesson {row.Id} is confirmed by the owner, and only the owner removes a confirmed lesson, on the "
                   + "Cockpit's Fleet Manager page";
        if (!string.Equals(row.CreatedBy, fleetManagerSessionId, StringComparison.OrdinalIgnoreCase))
            return $"lesson {row.Id} was kept by {row.CreatedBy}, not by this session; a Fleet Manager forgets only an "
                   + "unconfirmed lesson it kept itself, and the owner removes the rest on the Cockpit's Fleet Manager page";
        return null;
    }

    /// <exception cref="ArgumentException">The kind is not one of <see cref="Kinds"/>.</exception>
    public static void CheckKind(string? kind)
    {
        if (kind is null || !Kinds.Contains(kind))
            throw new ArgumentException($"kind '{kind}' is not one of: {string.Join(", ", Kinds)}");
    }

    private static void CheckPreferenceText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("text is required: the owner's preference, in their own words");
        if (text.Length > MaxTextLength)
            throw new ArgumentException($"text is {text.Length} characters; the most accepted is {MaxTextLength}");
    }

    private static void CheckLessonText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("text is required: the owner's correction, in their own words");
        if (text.Length > MaxLessonLength)
            throw new ArgumentException($"the lesson is {text.Length} characters and the most a lesson may be is "
                + $"{MaxLessonLength}; nothing was kept and nothing is ever shortened. Say it in fewer words, or {MakeRoomHint}");
    }

    /// <summary>The line about the mistake as it is stored: null when none was given.</summary>
    private static string? CheckMistake(string? mistake)
    {
        if (string.IsNullOrEmpty(mistake)) return null;
        if (string.IsNullOrWhiteSpace(mistake))
            throw new ArgumentException("mistake is blank; leave it out, or say in one line what went wrong");
        if (mistake.Length > MaxMistakeLength)
            throw new ArgumentException($"mistake is {mistake.Length} characters; the most accepted is {MaxMistakeLength}, one line");
        return mistake;
    }

    private static void CheckRoomForOneMoreConfirmed(GatewayDbContext ctx)
    {
        var confirmed = ctx.FleetPreferences.Count(p => p.Kind == KindLesson && p.ConfirmedByOwnerAtUtc != null);
        if (confirmed >= MaxConfirmedLessons)
            throw new ArgumentException($"this account already holds {confirmed} confirmed lessons and the most it may hold "
                + $"is {MaxConfirmedLessons}; nothing was kept and no older lesson was pushed out. To make room, {MakeRoomHint}");
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.ToUniversalTime();

    private static FleetPreferenceDto ToDto(FleetPreferenceEntity e) => new()
    {
        Id = e.Id.ToString(),
        Text = e.Text,
        CreatedAtUtc = e.CreatedAtUtc,
        CreatedBy = e.CreatedBy,
        Kind = e.Kind,
        Mistake = e.Mistake,
        ConfirmedByOwnerAtUtc = e.ConfirmedByOwnerAtUtc,
    };
}
