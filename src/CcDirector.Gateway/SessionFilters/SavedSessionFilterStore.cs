using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.SessionFilters;

/// <summary>One colour a saved filter may wear: the key that is stored and the colour every client paints, verbatim.</summary>
public sealed record SavedFilterColour(string Key, string Label, string Hex);

/// <summary>A saved session filter as the routes return it. The colour's hex is stamped here, so a client never maps a
/// colour key to a colour itself.</summary>
public sealed record SavedSessionFilter(
    string Id,
    string Name,
    string Colour,
    string ColourHex,
    IReadOnlyList<string> Repos,
    IReadOnlyList<string> Machines,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>What a person asked to save: a name, a colour key and the repos and machines to show.</summary>
public sealed record SavedSessionFilterInput(string? Name, string? Colour, IReadOnlyList<string>? Repos, IReadOnlyList<string>? Machines);

/// <summary>How a write ended. Each refusal carries the sentence the person reads.</summary>
public enum SavedSessionFilterOutcome
{
    /// <summary>The write was made.</summary>
    Saved,
    /// <summary>No filter with that identifier on this account.</summary>
    NotFound,
    /// <summary>Another filter on this account already has that name.</summary>
    NameTaken,
    /// <summary>The input was refused; the error says why.</summary>
    Invalid,
    /// <summary>The account already holds the most filters it may.</summary>
    TooMany,
}

/// <summary>The result of a write: the outcome, the filter as stored when it was saved, and the refusal sentence when it was not.</summary>
public sealed record SavedSessionFilterResult(SavedSessionFilterOutcome Outcome, SavedSessionFilter? Filter, string? Error);

/// <summary>
/// The saved session filters on each account (issue #4009), in <c>saved_session_filters</c>. A filter is a name, a
/// colour and the repos and machines it shows; what a filter MATCHES is decided by the clients' one shared rule
/// (<c>packages/client-core/src/sessions/filter.ts</c>), never here - this store only keeps the selection.
///
/// Every call takes the account's <see cref="TenantId"/> explicitly, as <see cref="Settings.TenantSettingsStore"/> does,
/// and reads and writes through <see cref="GatewayDatabase.CreateContext(TenantId)"/>, so one account can never see or
/// change another's filters. A name is unique within an account ignoring case, because the address bar names a filter
/// by its name.
/// </summary>
public sealed class SavedSessionFilterStore
{
    /// <summary>The longest name a filter may have.</summary>
    public const int MaxNameLength = 40;

    /// <summary>The most filters one account may hold.</summary>
    public const int MaxFiltersPerAccount = 50;

    /// <summary>The most values one facet (repos or machines) of a filter may hold.</summary>
    public const int MaxValuesPerFacet = 100;

    /// <summary>The longest single repo or machine name a filter may hold.</summary>
    public const int MaxValueLength = 200;

    /// <summary>The colours a filter may wear, in the order the picker offers them. The first is the default.</summary>
    public static readonly IReadOnlyList<SavedFilterColour> Colours = new[]
    {
        new SavedFilterColour("blue", "Blue", "#3B82F6"),
        new SavedFilterColour("green", "Green", "#22C55E"),
        new SavedFilterColour("amber", "Amber", "#F59E0B"),
        new SavedFilterColour("red", "Red", "#EF4444"),
        new SavedFilterColour("purple", "Purple", "#A855F7"),
        new SavedFilterColour("teal", "Teal", "#14B8A6"),
        new SavedFilterColour("pink", "Pink", "#EC4899"),
        new SavedFilterColour("grey", "Grey", "#9CA3AF"),
    };

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <param name="db">The Gateway database this store reads and writes through. Required.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public SavedSessionFilterStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Every saved filter on this account, sorted by name ignoring case.</summary>
    public IReadOnlyList<SavedSessionFilter> List(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var rows = ctx.SavedSessionFilters.AsNoTracking().ToList();
            var list = rows.Select(ToFilter)
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => f.Id, StringComparer.Ordinal)
                .ToList();
            FileLog.Write($"[SavedSessionFilterStore] List: tenant={tenant.ToLogString()} count={list.Count}");
            return list;
        }
    }

    /// <summary>Save a new filter on this account. Refuses an invalid input, a name the account already uses, and a
    /// filter beyond <see cref="MaxFiltersPerAccount"/>.</summary>
    public SavedSessionFilterResult Create(TenantId tenant, SavedSessionFilterInput input, DateTime nowUtc)
    {
        FileLog.Write($"[SavedSessionFilterStore] Create: tenant={tenant.ToLogString()}");
        var (clean, error) = Validate(input);
        if (clean is null) return Refused(SavedSessionFilterOutcome.Invalid, error!);

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            if (ctx.SavedSessionFilters.Count() >= MaxFiltersPerAccount)
                return Refused(SavedSessionFilterOutcome.TooMany,
                    $"This account already has {MaxFiltersPerAccount} saved filters. Delete one before saving another.");
            if (ctx.SavedSessionFilters.Any(e => e.NameKey == clean.NameKey))
                return Refused(SavedSessionFilterOutcome.NameTaken, $"A filter named \"{clean.Name}\" already exists.");

            var at = nowUtc.ToUniversalTime();
            var row = new SavedSessionFilterEntity
            {
                TenantId = tenant.Value,
                Id = Guid.NewGuid().ToString("N"),
                CreatedAtUtc = at,
            };
            Apply(row, clean, at);
            ctx.SavedSessionFilters.Add(row);
            if (!TrySave(ctx, clean.Name, out var raced)) return raced!;
            FileLog.Write($"[SavedSessionFilterStore] Create: tenant={tenant.ToLogString()} id={row.Id} repos={clean.Repos.Count} machines={clean.Machines.Count}");
            return new SavedSessionFilterResult(SavedSessionFilterOutcome.Saved, ToFilter(row), null);
        }
    }

    /// <summary>Replace one filter's name, colour, repos and machines. Refuses an unknown identifier, an invalid input, and
    /// a name another filter on the account already uses.</summary>
    public SavedSessionFilterResult Update(TenantId tenant, string id, SavedSessionFilterInput input, DateTime nowUtc)
    {
        FileLog.Write($"[SavedSessionFilterStore] Update: tenant={tenant.ToLogString()} id={id}");
        var (clean, error) = Validate(input);
        if (clean is null) return Refused(SavedSessionFilterOutcome.Invalid, error!);

        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.SavedSessionFilters.FirstOrDefault(e => e.Id == id);
            if (row is null) return Refused(SavedSessionFilterOutcome.NotFound, "That saved filter no longer exists.");
            if (ctx.SavedSessionFilters.Any(e => e.NameKey == clean.NameKey && e.Id != id))
                return Refused(SavedSessionFilterOutcome.NameTaken, $"A filter named \"{clean.Name}\" already exists.");

            Apply(row, clean, nowUtc.ToUniversalTime());
            if (!TrySave(ctx, clean.Name, out var raced)) return raced!;
            FileLog.Write($"[SavedSessionFilterStore] Update: tenant={tenant.ToLogString()} id={id} saved");
            return new SavedSessionFilterResult(SavedSessionFilterOutcome.Saved, ToFilter(row), null);
        }
    }

    /// <summary>Delete one filter. Answers false when this account has no filter with that identifier.</summary>
    public bool Delete(TenantId tenant, string id)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var row = ctx.SavedSessionFilters.FirstOrDefault(e => e.Id == id);
            if (row is null)
            {
                FileLog.Write($"[SavedSessionFilterStore] Delete: tenant={tenant.ToLogString()} id={id} not found");
                return false;
            }
            ctx.SavedSessionFilters.Remove(row);
            ctx.SaveChanges();
            FileLog.Write($"[SavedSessionFilterStore] Delete: tenant={tenant.ToLogString()} id={id} deleted");
            return true;
        }
    }

    /// <summary>The colour for a stored key. A stored key outside the palette is a defect in this store, so it throws.</summary>
    internal static SavedFilterColour ColourFor(string key)
        => Colours.FirstOrDefault(c => c.Key == key)
           ?? throw new InvalidOperationException($"saved filter colour '{key}' is not in the palette");

    private sealed record CleanInput(string Name, string NameKey, string Colour, List<string> Repos, List<string> Machines);

    // Checks and normalises a person's input: the name trimmed and required, the colour from the palette, and each
    // facet trimmed, de-duplicated (keeping first order) and bounded. A filter that selects nothing at all is refused,
    // because saving "every session" under a name would be a filter that filters nothing.
    private static (CleanInput? Clean, string? Error) Validate(SavedSessionFilterInput? input)
    {
        if (input is null) return (null, "The request has no filter in it.");
        var name = (input.Name ?? "").Trim();
        if (name.Length == 0) return (null, "A saved filter needs a name.");
        if (name.Length > MaxNameLength) return (null, $"A filter's name can be at most {MaxNameLength} characters.");
        if (name.Any(char.IsControl)) return (null, "A filter's name cannot contain control characters.");

        var colour = (input.Colour ?? "").Trim().ToLowerInvariant();
        if (Colours.All(c => c.Key != colour))
            return (null, $"\"{input.Colour}\" is not a filter colour. Pick one of: {string.Join(", ", Colours.Select(c => c.Key))}.");

        var (repos, repoError) = CleanFacet(input.Repos, "repo");
        if (repos is null) return (null, repoError);
        var (machines, machineError) = CleanFacet(input.Machines, "machine");
        if (machines is null) return (null, machineError);
        if (repos.Count == 0 && machines.Count == 0)
            return (null, "A saved filter must pick at least one repo or machine.");

        return (new CleanInput(name, name.ToLowerInvariant(), colour, repos, machines), null);
    }

    private static (List<string>? Values, string? Error) CleanFacet(IReadOnlyList<string>? values, string what)
    {
        var kept = new List<string>();
        foreach (var raw in values ?? Array.Empty<string>())
        {
            var v = (raw ?? "").Trim();
            if (v.Length == 0) continue;
            if (v.Length > MaxValueLength) return (null, $"A {what} name can be at most {MaxValueLength} characters.");
            if (!kept.Contains(v, StringComparer.Ordinal)) kept.Add(v);
        }
        if (kept.Count > MaxValuesPerFacet) return (null, $"A filter can pick at most {MaxValuesPerFacet} of each: too many {what}s.");
        return (kept, null);
    }

    private static void Apply(SavedSessionFilterEntity row, CleanInput clean, DateTime atUtc)
    {
        row.Name = clean.Name;
        row.NameKey = clean.NameKey;
        row.Colour = clean.Colour;
        row.ReposJson = JsonSerializer.Serialize(clean.Repos);
        row.MachinesJson = JsonSerializer.Serialize(clean.Machines);
        row.UpdatedAtUtc = atUtc;
    }

    // The unique name index is the last word: two writers saving the same name at once both pass the check above, and
    // the database lets exactly one in. The loser is told the name is taken, which is true by then.
    private static bool TrySave(GatewayDbContext ctx, string name, out SavedSessionFilterResult? refused)
    {
        try
        {
            ctx.SaveChanges();
            refused = null;
            return true;
        }
        catch (DbUpdateException ex)
        {
            FileLog.Write($"[SavedSessionFilterStore] TrySave: refused by the database, treated as a taken name: {ex.InnerException?.Message ?? ex.Message}");
            refused = Refused(SavedSessionFilterOutcome.NameTaken, $"A filter named \"{name}\" already exists.");
            return false;
        }
    }

    private static SavedSessionFilterResult Refused(SavedSessionFilterOutcome outcome, string error)
    {
        FileLog.Write($"[SavedSessionFilterStore] refused: outcome={outcome} error={error}");
        return new SavedSessionFilterResult(outcome, null, error);
    }

    private static SavedSessionFilter ToFilter(SavedSessionFilterEntity e)
    {
        var colour = ColourFor(e.Colour);
        return new SavedSessionFilter(
            e.Id,
            e.Name,
            colour.Key,
            colour.Hex,
            JsonSerializer.Deserialize<List<string>>(e.ReposJson) ?? throw new InvalidOperationException($"saved filter {e.Id} has no repos array"),
            JsonSerializer.Deserialize<List<string>>(e.MachinesJson) ?? throw new InvalidOperationException($"saved filter {e.Id} has no machines array"),
            DateTime.SpecifyKind(e.CreatedAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(e.UpdatedAtUtc, DateTimeKind.Utc));
    }
}
