using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Workspaces;

/// <summary>A workspace write lost a race - the id is already taken by a create. Maps to HTTP 409.</summary>
public sealed class WorkspaceConflictException : Exception
{
    /// <summary>Create the exception with the message the caller is shown verbatim.</summary>
    public WorkspaceConflictException(string message) : base(message) { }
}

/// <summary>
/// The Gateway-owned store of workspaces (issue #2722, Phase 3 of #2719): a named set of seats, authored
/// by hand or captured from a running Director, kept where the machine it describes cannot take it down.
///
/// This REPLACES the Director-local workspace files. Those lived under the Director's own configuration
/// directory, which means they were readable only while that machine was up and editable only at that
/// keyboard - and the one moment a workspace is worth having is the moment the machine has just been
/// restarted out from under its fleet.
///
/// Criticality: FAIL-LOUD, not quarantine. A workspace is the record somebody acts on when the sessions
/// it describes no longer exist; a write that silently did not happen means a fleet nobody can bring
/// back. So a failed write throws with the reason named rather than returning false and continuing.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over a
/// fresh pooled context.
/// </summary>
public sealed class WorkspaceStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>
    /// How the document is written to and read from the JSON column. camelCase so the stored document
    /// reads the same as the hand-written index it was taken from, and so anyone reading the column
    /// directly sees the field names the skill and the API use.
    /// </summary>
    public static readonly JsonSerializerOptions DocumentJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public WorkspaceStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Create or replace a workspace. The document is validated first, so nothing unactionable is stored.
    /// <see cref="WorkspaceDocument.CreatedUtc"/> is preserved from the existing row on a replace, and
    /// <see cref="WorkspaceDocument.UpdatedUtc"/> is always stamped here - a caller cannot backdate either.
    /// </summary>
    /// <param name="doc">The workspace to store.</param>
    /// <param name="nowUtc">The write time. Injected so tests are deterministic.</param>
    /// <returns>The stored document, with the store's own timestamps on it.</returns>
    /// <exception cref="WorkspaceValidationException">The document breaks a content rule.</exception>
    public WorkspaceDocument Save(WorkspaceDocument doc, DateTime nowUtc)
    {
        WorkspaceValidation.Validate(doc);

        var at = nowUtc.ToUniversalTime();
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var existing = ctx.Workspaces.FirstOrDefault(e => e.Id == doc.Id);

            doc.CreatedUtc = existing?.CreatedUtc ?? at;
            doc.UpdatedUtc = at;

            var json = JsonSerializer.Serialize(doc, DocumentJsonOptions);

            if (existing is null)
            {
                ctx.Workspaces.Add(new WorkspaceEntity
                {
                    Id = doc.Id,
                    TenantId = ctx.ActiveTenant!,
                    Name = doc.Name,
                    Origin = doc.Origin,
                    Machine = doc.Machine,
                    DirectorId = doc.DirectorId,
                    DirectorName = doc.DirectorName,
                    Outcome = doc.Outcome,
                    Description = doc.Description,
                    SeatCount = doc.Seats.Count,
                    CreatedUtc = doc.CreatedUtc,
                    UpdatedUtc = doc.UpdatedUtc,
                    DocumentJson = json,
                });
            }
            else
            {
                existing.Name = doc.Name;
                existing.Origin = doc.Origin;
                existing.Machine = doc.Machine;
                existing.DirectorId = doc.DirectorId;
                existing.DirectorName = doc.DirectorName;
                existing.Outcome = doc.Outcome;
                existing.Description = doc.Description;
                existing.SeatCount = doc.Seats.Count;
                existing.UpdatedUtc = doc.UpdatedUtc;
                existing.DocumentJson = json;
            }

            ctx.SaveChanges();
            FileLog.Write(
                $"[WorkspaceStore] Save: id={doc.Id}, origin={doc.Origin}, seats={doc.Seats.Count}, " +
                $"outcome={doc.Outcome ?? "none"}, created={existing is null}");
            return doc;
        }
    }

    /// <summary>
    /// Create a workspace that must not already exist. Used by capture, where silently replacing an
    /// existing record would destroy a drain somebody is halfway through.
    /// </summary>
    /// <param name="doc">The workspace to store.</param>
    /// <param name="nowUtc">The write time.</param>
    /// <returns>The stored document.</returns>
    /// <exception cref="WorkspaceConflictException">A workspace with that id already exists.</exception>
    public WorkspaceDocument Create(WorkspaceDocument doc, DateTime nowUtc)
    {
        WorkspaceValidation.Validate(doc);

        lock (_gate)
        {
            using (var ctx = _db.CreateContext())
            {
                if (ctx.Workspaces.AsNoTracking().Any(e => e.Id == doc.Id))
                    throw new WorkspaceConflictException(
                        $"A workspace with id \"{doc.Id}\" already exists. Read it, or choose another id - " +
                        "replacing it would destroy whatever it already records.");
            }
        }

        return Save(doc, nowUtc);
    }

    /// <summary>One workspace by id, or null when there is none.</summary>
    /// <param name="id">The workspace slug.</param>
    public WorkspaceDocument? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var slug = id.Trim();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var row = ctx.Workspaces.AsNoTracking().FirstOrDefault(e => e.Id == slug);
            return row is null ? null : Deserialize(row);
        }
    }

    /// <summary>
    /// Every workspace, most recently written first. Summaries only - the list is a chooser, and a fleet
    /// of documents each carrying twenty seats is not something to send to render a list.
    /// </summary>
    public IReadOnlyList<WorkspaceSummaryDto> List()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var rows = ctx.Workspaces.AsNoTracking().ToList();

            // Read from the head columns ONLY - the list never parses a stored document. That is what keeps
            // one damaged row from taking the whole list down with it: a workspace that cannot be read is
            // still listed, and the failure surfaces where it belongs, on the read of THAT workspace.
            return rows
                .OrderByDescending(e => e.UpdatedUtc)
                .ThenBy(e => e.Id, StringComparer.Ordinal)
                .Select(e => new WorkspaceSummaryDto
                {
                    Id = e.Id,
                    Name = e.Name,
                    Description = e.Description,
                    Origin = e.Origin,
                    Machine = e.Machine,
                    DirectorName = e.DirectorName,
                    Outcome = e.Outcome,
                    SeatCount = e.SeatCount,
                    CreatedUtc = e.CreatedUtc,
                    UpdatedUtc = e.UpdatedUtc,
                })
                .ToList();
        }
    }

    /// <summary>Delete a workspace. Returns false when there was none to delete.</summary>
    /// <param name="id">The workspace slug.</param>
    public bool Delete(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var slug = id.Trim();

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var row = ctx.Workspaces.FirstOrDefault(e => e.Id == slug);
            if (row is null)
            {
                FileLog.Write($"[WorkspaceStore] Delete: id={slug} not found");
                return false;
            }

            ctx.Workspaces.Remove(row);
            ctx.SaveChanges();
            FileLog.Write($"[WorkspaceStore] Delete: id={slug} deleted");
            return true;
        }
    }

    /// <summary>
    /// Read the stored document. A workspace that cannot be read fails LOUDLY and names where the bytes
    /// are, because the alternative - handing back a half-built document - is a fleet restored from a
    /// record that was quietly missing seats.
    /// </summary>
    private static WorkspaceDocument Deserialize(WorkspaceEntity row)
    {
        WorkspaceDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<WorkspaceDocument>(row.DocumentJson, DocumentJsonOptions);
        }
        catch (JsonException ex)
        {
            FileLog.Write($"[WorkspaceStore] Get FAILED for id={row.Id}: {ex.Message}");
            throw new InvalidOperationException(
                $"Workspace \"{row.Id}\" could not be read: {ex.Message}. The row is intact - read the " +
                "document_json column of the workspaces table directly to recover what it holds.", ex);
        }

        if (doc is null)
            throw new InvalidOperationException(
                $"Workspace \"{row.Id}\" could not be read: its stored document deserialized to null. " +
                "The row is intact - read the document_json column of the workspaces table directly.");

        return doc;
    }
}
