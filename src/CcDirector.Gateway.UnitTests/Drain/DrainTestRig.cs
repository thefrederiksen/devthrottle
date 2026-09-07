using System.Text.Json;
using CcDirector.ControlApi.Drain;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.UnitTests.Drain;

/// <summary>
/// A fake Director: a set of live session ids, and a record of everything the drain did to them.
///
/// It is deliberately not a stub that answers whatever the caller wants. Two of its behaviours are the
/// ones the drain is being tested against and are modelled honestly:
///
///  - A SESSION DOES NOT DISAPPEAR WHEN IT IS FLAGGED. Marking one done is a flag, and the Director's own
///    reaper removes it later. This one stays present for <see cref="PollsBeforeReap"/> further checks,
///    which is what makes "poll until it is genuinely absent" a testable rule rather than a comment.
///  - THERE IS NO KILL. The fake offers none, because the seam offers none.
/// </summary>
internal class FakeSessionControl : IDrainSessionControl
{
    private readonly Dictionary<string, int> _reapCountdown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The sessions that are still present.</summary>
    public HashSet<string> Live { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every message sent, in order: (sessionId, text).</summary>
    public List<(string SessionId, string Text)> Sent { get; } = new();

    /// <summary>Every rename, in order.</summary>
    public List<(string SessionId, string Name)> Renamed { get; } = new();

    /// <summary>Every seat flagged for deletion, IN THE ORDER IT WAS FLAGGED. This list is what the
    /// leaf-first assertions read.</summary>
    public List<string> Flagged { get; } = new();

    /// <summary>How many presence checks a flagged session survives before it goes. Models the Director's
    /// grace window and reaper timer without waiting for either.</summary>
    public int PollsBeforeReap { get; set; } = 2;

    /// <summary>Sessions that never go away however long they are flagged - a session stuck mid-turn.</summary>
    public HashSet<string> NeverReaped { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sessions whose message delivery fails.</summary>
    public HashSet<string> RefuseSendTo { get; } = new(StringComparer.OrdinalIgnoreCase);

    public virtual bool IsPresent(string sessionId)
    {
        if (!Live.Contains(sessionId)) return false;
        if (!_reapCountdown.TryGetValue(sessionId, out var left)) return true;
        if (NeverReaped.Contains(sessionId)) return true;
        if (left <= 0)
        {
            Live.Remove(sessionId);
            return false;
        }
        _reapCountdown[sessionId] = left - 1;
        return true;
    }

    public virtual Task<bool> SendAsync(string sessionId, string text)
    {
        if (RefuseSendTo.Contains(sessionId) || !Live.Contains(sessionId)) return Task.FromResult(false);
        Sent.Add((sessionId, text));
        return Task.FromResult(true);
    }

    public virtual bool Rename(string sessionId, string name)
    {
        if (!Live.Contains(sessionId)) return false;
        Renamed.Add((sessionId, name));
        return true;
    }

    public virtual bool MarkForDeletion(string sessionId, string reason)
    {
        if (!Live.Contains(sessionId)) return false;
        Flagged.Add(sessionId);
        _reapCountdown[sessionId] = PollsBeforeReap;
        return true;
    }
}

/// <summary>
/// A fake Gateway store.
///
/// SaveAsync returns a JSON ROUND-TRIPPED COPY, exactly as the real one does. That is not decoration: if
/// the drain ever adopted the returned document it would be holding a different object graph from the
/// seats it is still writing into, and every later change would go to a document nobody stores. The clone
/// is what makes that bug fail a test instead of shipping.
/// </summary>
internal sealed class FakeWorkspaceSink : IDrainWorkspaceSink
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The document the capture hands back.</summary>
    public WorkspaceDocument Captured { get; set; } = new();

    /// <summary>Every save, as it was at the moment of saving.</summary>
    public List<WorkspaceDocument> Saves { get; } = new();

    /// <summary>The capture request the drain built.</summary>
    public WorkspaceCaptureRequest? CaptureRequest { get; private set; }

    /// <summary>The last thing stored - which is what a reader would find afterwards.</summary>
    public WorkspaceDocument Last => Saves[^1];

    public Task<WorkspaceDocument> CaptureAsync(WorkspaceCaptureRequest request, CancellationToken ct)
    {
        CaptureRequest = request;
        Captured.Id = request.Id;
        Captured.Name = request.Name;
        Captured.DirectorId = request.DirectorId;
        Captured.Origin = WorkspaceOrigins.Captured;
        Captured.Outcome = WorkspaceOutcomes.Draining;
        Captured.Reason = request.Reason;
        return Task.FromResult(Captured);
    }

    public Task<WorkspaceDocument> SaveAsync(WorkspaceDocument doc, CancellationToken ct)
    {
        Saves.Add(Clone(doc));
        var echoed = Clone(doc);
        echoed.CreatedUtc = new DateTime(2026, 9, 6, 17, 25, 0, DateTimeKind.Utc);
        echoed.UpdatedUtc = new DateTime(2026, 9, 6, 18, 0, 0, DateTimeKind.Utc);
        return Task.FromResult(echoed);
    }

    private static WorkspaceDocument Clone(WorkspaceDocument doc)
        => JsonSerializer.Deserialize<WorkspaceDocument>(JsonSerializer.Serialize(doc, Json), Json)!;
}

/// <summary>A scratch directory that cleans itself up.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "drain-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* a test machine holding a file open must not fail the test */ }
    }
}

/// <summary>Builders shared by the drain tests.</summary>
internal static class DrainTestRig
{
    /// <summary>
    /// Enough handover prose to clear the "still being written" floor, which is 500 bytes by default. It
    /// is deliberately over the floor rather than at it: a test whose document sat on the boundary would
    /// start failing for a reason that has nothing to do with what it is asserting.
    /// </summary>
    public const string Body = """
        ## Where I am

        The work is described here at enough length to be a real document rather than a stub, because a
        handover shorter than the floor is treated by the drain as one that is still being written. That
        floor exists because a seat that writes its file in pieces would otherwise be read half-done and
        closed on a stub, and the stub would then be the only surviving account of what it was doing.

        ## What is proven, and what is only believed

        Proven: the shape of this document. Believed: nothing, because this is a test fixture.

        ## The exact next action

        Named here, so a reader can act without asking anybody anything.
        """;

    public static WorkspaceSeat Seat(string id, string name, string? reportsTo = null, int order = 0)
        => new()
        {
            SessionId = id,
            Name = name,
            Agent = "ClaudeCode",
            RepoPath = "D:/ReposFred/devthrottle",
            Role = reportsTo is null ? "Architect" : "Worker",
            ReportsTo = reportsTo,
            SortOrder = order,
            Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
        };

    public static WorkspaceDocument Document(params WorkspaceSeat[] seats)
        => new()
        {
            SchemaVersion = 1,
            Origin = WorkspaceOrigins.Captured,
            DirectorName = "TestDirector",
            Machine = "TEST_MACHINE",
            Seats = seats.ToList(),
        };

    /// <summary>Write a handover exactly where the drain will look for it.</summary>
    public static string WriteHandover(
        string dir, string sessionId, string name, string? block = null, string? body = null)
    {
        var path = DrainPaths.HandoverFor(dir, sessionId, name);
        File.WriteAllText(path, (body ?? Body) + "\n\n" + (block ?? ""));
        return path;
    }

    /// <summary>The block a seat declares.</summary>
    public static string Block(
        string state = "drained",
        bool? restore = false,
        string? why = "The work here is finished.",
        string? blockedReason = null,
        IEnumerable<(string Id, string Note)>? covered = null,
        IEnumerable<string>? questions = null)
    {
        var lines = new List<string> { "<!-- drain-report", $"state: {state}" };
        if (restore is not null) lines.Add($"restore: {(restore.Value ? "yes" : "no")}");
        if (why is not null) lines.Add($"why: {why}");
        if (blockedReason is not null) lines.Add($"blocked-reason: {blockedReason}");
        foreach (var (id, note) in covered ?? Array.Empty<(string, string)>())
            lines.Add($"covered: {id} | {note}");
        foreach (var q in questions ?? Array.Empty<string>())
            lines.Add($"question: {q}");
        lines.Add("-->");
        return string.Join("\n", lines);
    }
}
