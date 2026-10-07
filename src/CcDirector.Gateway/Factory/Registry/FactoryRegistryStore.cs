using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Registry;

/// <summary>A factory that is not registered was named where a registered one is needed.</summary>
public sealed class FactoryNotRegisteredException : Exception
{
    public FactoryNotRegisteredException(string message) : base(message) { }
}

/// <summary>
/// THE FACTORY REGISTRY AND ITS GOAL NUMBERS (Factories screen mission, phase A).
///
/// The registry holds one row per factory: its title, folder, computer, CEO, goal and seats. It is written by
/// <c>cc-devthrottle factory register --manifest</c> and is what the owner's Factories screen lists, so a factory
/// that never wrote an activity row is still on the screen, and a session that merely wrote one is never mistaken
/// for a seat. Registering again replaces the whole row.
///
/// The goal numbers are what a factory's CEO posts on every run: the number its goal is measured by. Every post is
/// kept; the newest is the one shown. A number can only be posted for a registered factory and only by one of its
/// seats, because "posted by Nora Hale" on the owner's screen is a claim about a seat, and an unknown name there
/// would be a claim nobody can check.
///
/// Every refusal is a <see cref="FactoryViewValidationException"/> (or <see cref="FactoryNotRegisteredException"/>)
/// with a sentence the caller can act on, and nothing is stored when one is thrown.
/// </summary>
public sealed partial class FactoryRegistryStore
{
    public const int MaxFactories = 50;
    public const int MaxSeats = 40;
    public const int MaxSchedulesPerSeat = 10;
    public const int MaxTitleChars = 120;
    public const int MaxFolderChars = 1024;
    public const int MaxComputerChars = 128;
    public const int MaxRelativePathChars = 512;
    public const int MaxGoalChars = 16 * 1024;
    public const int MaxScheduleIdChars = 64;
    public const int MaxValueChars = 200;
    public const int MaxUnitChars = 120;
    public const int MaxLinkChars = 2048;

    /// <summary>The most posts one read of a factory's goal numbers returns.</summary>
    public const int MaxGoalNumbersPerRead = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // An absolute path on the factory's own computer, which may not be the Gateway's operating system: a Windows
    // drive path, a UNC path, or a path from the root. Path.IsPathFullyQualified would answer for the Gateway's
    // machine, and the hosted Gateway runs on Linux, where D:\ is not a full path.
    [GeneratedRegex(@"^([A-Za-z]:[\\/]|\\\\[^\\]|/)")]
    private static partial Regex AbsolutePath();

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FactoryRegistryStore(GatewayDatabase db) => _db = db ?? throw new ArgumentNullException(nameof(db));

    // ---------- the registry ----------

    /// <summary>
    /// Register a factory, replacing any registration it already has, seats included. Throws
    /// <see cref="FactoryViewValidationException"/> with the reason when the manifest is refused.
    /// </summary>
    public RegisteredFactoryDto Register(TenantId tenant, RegisterFactoryRequest? request, string registeredBy, DateTime nowUtc)
    {
        FileLog.Write($"[FactoryRegistryStore] Register: factory={request?.Factory}, seats={request?.Seats?.Count}, by={registeredBy}");
        var entity = Validate(request);
        entity.TenantId = tenant.Value;
        entity.RegisteredBy = Required(registeredBy, "registering caller", 256);
        entity.RegisteredAtUtc = nowUtc;

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var existing = ctx.FactoryRegistry.FirstOrDefault(f => f.Factory == entity.Factory);
            if (existing is null)
            {
                var count = ctx.FactoryRegistry.Count();
                if (count >= MaxFactories)
                    throw Refuse($"This account already registers {count} factories; an account registers at most {MaxFactories}.");
                ctx.FactoryRegistry.Add(entity);
            }
            else
            {
                // An archived factory stays archived when it is registered again: only the owner's Restore brings
                // it back, so a scheduled re-registration can never quietly return it to the list.
                entity.ArchivedAtUtc = existing.ArchivedAtUtc;
                entity.ArchivedBy = existing.ArchivedBy;
                entity.ArchivedSchedulesJson = existing.ArchivedSchedulesJson;
                ctx.Entry(existing).CurrentValues.SetValues(entity);
            }
            ctx.SaveChanges();
            FileLog.Write($"[FactoryRegistryStore] Register: {(existing is null ? "registered" : "replaced")} {entity.Factory}");
        }
        return ToDto(entity);
    }

    /// <summary>
    /// Mark a registered factory archived, recording who did it and which schedules the archive switches off. The
    /// row, its seats and its goal are kept. Throws <see cref="FactoryNotRegisteredException"/> when it is not
    /// registered and <see cref="FactoryViewValidationException"/> when it is already archived.
    /// </summary>
    public RegisteredFactoryDto Archive(TenantId tenant, string factory, string archivedBy, IReadOnlyList<string> schedulesSwitchedOff, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(schedulesSwitchedOff);
        FileLog.Write($"[FactoryRegistryStore] Archive: factory={factory}, by={archivedBy}, schedules={string.Join(",", schedulesSwitchedOff)}");
        var by = Required(archivedBy, "archiving caller", 256);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = Row(ctx, factory);
            if (row.ArchivedAtUtc is not null)
                throw Refuse($"{row.Title} is already archived.");
            row.ArchivedAtUtc = nowUtc;
            row.ArchivedBy = by;
            row.ArchivedSchedulesJson = JsonSerializer.Serialize(schedulesSwitchedOff, Json);
            ctx.SaveChanges();
            FileLog.Write($"[FactoryRegistryStore] Archive: archived {row.Factory}");
            return ToDto(row);
        }
    }

    /// <summary>
    /// Return an archived factory to the list. Throws <see cref="FactoryNotRegisteredException"/> when it is not
    /// registered and <see cref="FactoryViewValidationException"/> when it is not archived.
    /// </summary>
    public RegisteredFactoryDto Restore(TenantId tenant, string factory)
    {
        FileLog.Write($"[FactoryRegistryStore] Restore: factory={factory}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = Row(ctx, factory);
            if (row.ArchivedAtUtc is null)
                throw Refuse($"{row.Title} is not archived.");
            row.ArchivedAtUtc = null;
            row.ArchivedBy = null;
            row.ArchivedSchedulesJson = null;
            ctx.SaveChanges();
            FileLog.Write($"[FactoryRegistryStore] Restore: restored {row.Factory}");
            return ToDto(row);
        }
    }

    private static FactoryRegistryEntity Row(GatewayDbContext ctx, string factory)
    {
        var id = FactoryNames.TryFactory(factory, out var folded, out _) ? folded : null;
        return (id is null ? null : ctx.FactoryRegistry.FirstOrDefault(f => f.Factory == id))
            ?? throw new FactoryNotRegisteredException($"No factory '{factory}' is registered in this account.");
    }

    /// <summary>Every registered factory in the account, by title.</summary>
    public IReadOnlyList<RegisteredFactoryDto> List(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.FactoryRegistry.AsNoTracking().ToList()
                .Select(ToDto)
                .OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Factory, StringComparer.Ordinal)
                .ToList();
        }
    }

    /// <summary>One registered factory, or null when it is not registered. The id is folded to the one spelling;
    /// an id that cannot be one is simply not registered.</summary>
    public RegisteredFactoryDto? Find(TenantId tenant, string? factory)
    {
        if (!FactoryNames.TryFactory(factory, out var id, out _)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.FactoryRegistry.AsNoTracking().FirstOrDefault(f => f.Factory == id);
            return row is null ? null : ToDto(row);
        }
    }

    // ---------- goal numbers ----------

    /// <summary>
    /// Keep one goal number for a registered factory, posted by <paramref name="postedBy"/>, which must be one of
    /// its seats. Throws <see cref="FactoryNotRegisteredException"/> when the factory is not registered and
    /// <see cref="FactoryViewValidationException"/> when the post is refused.
    /// </summary>
    public GoalNumberDto PostGoalNumber(TenantId tenant, PostGoalNumberRequest? request, string? postedBy,
        string? postedBySession, DateTime nowUtc)
    {
        FileLog.Write($"[FactoryRegistryStore] PostGoalNumber: factory={request?.Factory}, by={postedBy}, session={postedBySession}");
        if (request is null) throw Refuse("A goal number body is required.");
        var factory = FactoryId(request.Factory);
        var value = Required(request.Value, "value", MaxValueChars);
        var unit = Required(request.Unit, "unit", MaxUnitChars);
        var asOf = Day(request.AsOf, "as-of date") ?? throw Refuse("The as-of date is missing: give it as YYYY-MM-DD.");
        var link = HttpLink(request.Link);
        if (!FactoryNames.TrySeat(postedBy, out var seat, out _))
            throw Refuse(string.IsNullOrWhiteSpace(postedBy)
                ? "Who posts this number is not known: name the seat (--by <seat id>)."
                : $"'{postedBy}' is not a seat id.");

        var registered = Find(tenant, factory)
            ?? throw new FactoryNotRegisteredException(
                $"The factory '{factory}' is not registered, so it has no goal number to post. Register it first with cc-devthrottle factory register --manifest <file>.");
        if (!registered.Seats.Any(s => s.Id == seat))
            throw Refuse($"'{seat}' is not a seat of {factory}. Its seats are: {string.Join(", ", registered.Seats.Select(s => s.Id))}.");

        var entity = new FactoryGoalNumberEntity
        {
            TenantId = tenant.Value,
            Factory = factory,
            Value = value,
            Unit = unit,
            AsOf = asOf,
            Link = link,
            PostedBy = seat,
            PostedBySession = string.IsNullOrWhiteSpace(postedBySession) ? null : postedBySession.Trim(),
            PostedAtUtc = nowUtc,
        };
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            ctx.FactoryGoalNumbers.Add(entity);
            ctx.SaveChanges();
        }
        FileLog.Write($"[FactoryRegistryStore] PostGoalNumber: kept {entity.Id} for {factory}: {value} {unit} as of {asOf}, by {seat}");
        return ToDto(entity);
    }

    /// <summary>A factory's goal numbers, newest first, at most <paramref name="limit"/> of them, with the total.</summary>
    public GoalNumbersDto GoalNumbers(TenantId tenant, string? factory, int limit)
    {
        var id = FactoryId(factory);
        if (limit < 1 || limit > MaxGoalNumbersPerRead)
            throw Refuse($"A read returns from 1 to {MaxGoalNumbersPerRead} goal numbers, not {limit}.");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var all = ctx.FactoryGoalNumbers.AsNoTracking().Where(g => g.Factory == id);
            var total = all.Count();
            var posts = all.OrderByDescending(g => g.PostedAtUtc).ThenByDescending(g => g.Id).Take(limit).ToList()
                .Select(ToDto).ToList();
            return new GoalNumbersDto { Factory = id, Latest = posts.FirstOrDefault(), Count = total, Posts = posts };
        }
    }

    // ---------- the rules ----------

    /// <summary>Every rule a manifest must meet. Returns the row to store (without tenant and registrar) or
    /// throws with the first rule it breaks.</summary>
    internal static FactoryRegistryEntity Validate(RegisterFactoryRequest? m)
    {
        if (m is null) throw Refuse("A factory manifest body is required.");
        var factory = FactoryId(m.Factory);
        var title = Required(m.Title, "title", MaxTitleChars);
        var folder = Required(m.Folder, "folder", MaxFolderChars);
        if (!AbsolutePath().IsMatch(folder))
            throw Refuse($"The folder '{folder}' must be an absolute path on the factory's computer, for example D:\\ReposFred\\cc-consult.");
        var computer = Required(m.Computer, "computer", MaxComputerChars);

        string? goalText = null;
        if (m.GoalText is not null)
        {
            if (m.GoalText.Trim().Length == 0)
                throw Refuse("The goal text is empty. Leave the goal out until the factory has one.");
            if (m.GoalText.Length > MaxGoalChars)
                throw Refuse($"The goal text is {m.GoalText.Length} characters; a goal takes at most {MaxGoalChars}.");
            goalText = m.GoalText;
        }
        var goalFile = m.GoalFile is null ? null : RelativePath(m.GoalFile, "goal file");
        var approved = Day(m.GoalApprovedOn, "goal approval date");
        if (goalText is null && (goalFile is not null || approved is not null))
            throw Refuse("The manifest names a goal file or an approval date but no goal text. A goal is registered with its text.");

        if (m.Seats is null || m.Seats.Count == 0) throw Refuse("A factory needs at least one seat.");
        if (m.Seats.Count > MaxSeats) throw Refuse($"A factory registers at most {MaxSeats} seats.");
        var seats = new List<RegisteredFactorySeatDto>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in m.Seats)
        {
            if (s is null) throw Refuse("A seat is empty.");
            if (!FactoryNames.TrySeat(s.Id, out var seatId, out var why))
                throw Refuse($"The seat '{s.Id}' is refused: {why}.");
            if (!ids.Add(seatId)) throw Refuse($"Two seats are called '{seatId}'.");
            var schedules = new List<string>();
            foreach (var raw in s.Schedules ?? new List<string>())
            {
                var schedule = Required(raw, $"schedule id of seat '{seatId}'", MaxScheduleIdChars);
                if (schedules.Contains(schedule, StringComparer.Ordinal))
                    throw Refuse($"Seat '{seatId}' names the schedule '{schedule}' twice.");
                schedules.Add(schedule);
            }
            if (schedules.Count > MaxSchedulesPerSeat)
                throw Refuse($"Seat '{seatId}' names {schedules.Count} schedules; a seat names at most {MaxSchedulesPerSeat}.");
            seats.Add(new RegisteredFactorySeatDto
            {
                Id = seatId,
                Name = Required(s.Name, $"name of seat '{seatId}'", MaxTitleChars),
                Role = Required(s.Role, $"role of seat '{seatId}'", MaxTitleChars),
                BriefFile = RelativePath(s.BriefFile, $"brief file of seat '{seatId}'"),
                Schedules = schedules,
                // A seat registered without a computer runs on the factory's: written down here, once.
                Computer = s.Computer is null ? computer : Required(s.Computer, $"computer of seat '{seatId}'", MaxComputerChars),
            });
        }

        string? ceo = null;
        if (m.CeoSeat is not null)
        {
            if (!FactoryNames.TrySeat(m.CeoSeat, out var ceoId, out _) || !ids.Contains(ceoId))
                throw Refuse($"The CEO seat '{m.CeoSeat}' is not one of the factory's seats ({string.Join(", ", ids)}).");
            ceo = ceoId;
        }

        return new FactoryRegistryEntity
        {
            Factory = factory,
            Title = title,
            Folder = folder,
            Computer = computer,
            CeoSeat = ceo,
            GoalText = goalText,
            GoalFile = goalFile,
            GoalApprovedOn = approved,
            SeatsJson = JsonSerializer.Serialize(seats, Json),
        };
    }

    private static string FactoryId(string? raw)
    {
        if (!FactoryNames.TryFactory(raw, out var id, out var why)) throw Refuse($"The factory id is refused: {why}.");
        return id;
    }

    private static string Required(string? text, string what, int max)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) throw Refuse($"The {what} is missing.");
        if (t.Length > max) throw Refuse($"The {what} is {t.Length} characters; it takes at most {max}.");
        return t;
    }

    /// <summary>A path relative to the factory's folder, staying inside it.</summary>
    private static string RelativePath(string? raw, string what)
    {
        var p = Required(raw, what, MaxRelativePathChars);
        if (AbsolutePath().IsMatch(p) || p.StartsWith('\\'))
            throw Refuse($"The {what} '{p}' must be relative to the factory's folder, not absolute.");
        if (p.Split('/', '\\').Any(part => part == ".."))
            throw Refuse($"The {what} '{p}' must stay inside the factory's folder; '..' is not allowed.");
        return p;
    }

    /// <summary>A day as YYYY-MM-DD, or null when none is given.</summary>
    private static string? Day(string? raw, string what)
    {
        if (raw is null) return null;
        var t = raw.Trim();
        if (!DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            throw Refuse($"The {what} '{raw}' is not a day written as YYYY-MM-DD.");
        return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string HttpLink(string? raw)
    {
        var t = Required(raw, "link to how it was measured", MaxLinkChars);
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw Refuse($"The link '{t}' must be a web address starting with http:// or https://, so the owner can open it.");
        return t;
    }

    private static RegisteredFactoryDto ToDto(FactoryRegistryEntity e) => new()
    {
        Factory = e.Factory,
        Title = e.Title,
        Folder = e.Folder,
        Computer = e.Computer,
        CeoSeat = e.CeoSeat,
        GoalText = e.GoalText,
        GoalFile = e.GoalFile,
        GoalApprovedOn = e.GoalApprovedOn,
        Seats = JsonSerializer.Deserialize<List<RegisteredFactorySeatDto>>(e.SeatsJson, Json)
                ?? throw new InvalidOperationException($"The seats of factory '{e.Factory}' are stored as something other than a list."),
        RegisteredBy = e.RegisteredBy,
        RegisteredAtUtc = e.RegisteredAtUtc,
        ArchivedAtUtc = e.ArchivedAtUtc,
        ArchivedBy = e.ArchivedBy,
        ArchivedSchedules = e.ArchivedSchedulesJson is null
            ? new List<string>()
            : JsonSerializer.Deserialize<List<string>>(e.ArchivedSchedulesJson, Json)
              ?? throw new InvalidOperationException($"The archived schedules of factory '{e.Factory}' are stored as something other than a list."),
    };

    private static GoalNumberDto ToDto(FactoryGoalNumberEntity e) => new()
    {
        Id = e.Id,
        Factory = e.Factory,
        Value = e.Value,
        Unit = e.Unit,
        AsOf = e.AsOf,
        Link = e.Link,
        PostedBy = e.PostedBy,
        PostedBySession = e.PostedBySession,
        PostedAtUtc = e.PostedAtUtc,
    };

    private static FactoryViewValidationException Refuse(string why) => new(why);
}
