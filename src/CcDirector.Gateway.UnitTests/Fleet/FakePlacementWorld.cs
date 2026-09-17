using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>A fake world for the Fleet Manager setting: computers, a roster, and a spawner and close that record
/// every call. The spawner asks the start's own Director check exactly as the real one does.</summary>
internal sealed class FakePlacementWorld : IFleetManagerPlacementEnvironment
{
    private readonly string _newId;
    private DateTime _now;

    public FakePlacementWorld(DateTime now, string newId)
    {
        _now = now;
        _newId = newId;
    }

    public List<FleetManagerMachineFacts> Machines { get; } = new();
    public List<(string DirectorId, SessionDto Session)> Roster { get; } = new();
    public List<AgentChoiceDto>? Agents { get; set; }
    public List<string> AgentListAskedOf { get; } = new();
    public HashSet<string> OldDirectors { get; } = new();
    public List<(string Machine, NewSessionRequest Request)> Spawns { get; } = new();
    public int CreatesSent { get; private set; }
    public string? SpawnError { get; set; }
    public SessionDto? NextSession { get; set; }
    public List<(string DirectorId, string SessionId, string Reason)> Closed { get; } = new();
    public Action? OnDelay { get; set; }
    public List<string> MarksRecorded { get; } = new();

    /// <summary>Each promotion that committed (its delivery booked), with how many closes had been sent before it.</summary>
    public List<(string SessionId, int ClosesBefore)> MarkMovedTo { get; } = new();

    /// <summary>When false, a close is refused by the fake Director.</summary>
    public bool CloseSucceeds { get; set; } = true;

    public void SetState(string sid, string state)
    {
        foreach (var r in Roster.Where(r => r.Session.SessionId == sid)) r.Session.ActivityState = state;
    }

    public void Advance(TimeSpan by) => _now += by;

    IReadOnlyList<FleetManagerMachineFacts> IFleetManagerPlacementEnvironment.Machines(TenantId tenant) => Machines.ToList();

    IReadOnlyList<(string DirectorId, SessionDto Session)> IFleetManagerPlacementEnvironment.Roster(TenantId tenant) => Roster.ToList();

    public Task<IReadOnlyList<AgentChoiceDto>?> AgentsOfferedAsync(TenantId tenant, string directorId, CancellationToken ct)
    {
        AgentListAskedOf.Add(directorId);
        return Task.FromResult<IReadOnlyList<AgentChoiceDto>?>(Agents);
    }

    public bool CreatesFleetManagerHome(TenantId tenant, string directorId) => !OldDirectors.Contains(directorId);

    public Task<(bool Ok, SessionDto? Session, string? Error, string? DirectorId)> SpawnAsync(TenantId tenant, string machine,
        NewSessionRequest request, Func<string, string?> refuseDirector, CancellationToken ct)
    {
        Spawns.Add((machine, request));
        var directorId = request.Director ?? "dir-launched";
        if (refuseDirector(directorId) is { } refusal)
            return Task.FromResult<(bool, SessionDto?, string?, string?)>((false, null, refusal, directorId));
        CreatesSent++;
        if (SpawnError is not null)
            return Task.FromResult<(bool, SessionDto?, string?, string?)>((false, null, SpawnError, directorId));
        var session = NextSession ?? new SessionDto { SessionId = _newId, ActivityState = "Starting", CreatedAt = _now };
        return Task.FromResult<(bool, SessionDto?, string?, string?)>((true, session, null, directorId));
    }

    /// <summary>Runs as a close is sent, before it is answered.</summary>
    public Func<Task>? OnClose { get; set; }

    public async Task<bool> CloseSessionAsync(TenantId tenant, string directorId, string sessionId, string reason, CancellationToken ct)
    {
        Closed.Add((directorId, sessionId, reason));
        if (OnClose is not null) await OnClose();
        return CloseSucceeds;
    }

    public void RecordMark(TenantId tenant, string sessionId, DateTime nowUtc) => MarksRecorded.Add(sessionId);

    /// <summary>The real one-transaction promotion the fake world writes through.</summary>
    public FleetManagerPromotionStore? Promotions { get; set; }

    /// <summary>A promotion that fails: thrown before anything is written, in place of the store.</summary>
    public Exception? PromotionFails { get; set; }

    public void PromoteSuccessor(TenantId tenant, string sessionId, DateTime nowUtc)
    {
        if (PromotionFails is not null) throw PromotionFails;
        if (Promotions is null) throw new InvalidOperationException("the test gave the fake world no promotion store");
        Promotions.Promote(tenant, sessionId, nowUtc);
        MarksRecorded.Add(sessionId);
        MarkMovedTo.Add((sessionId, Closed.Count));
    }

    public TimeZoneInfo TimeZone(TenantId tenant) => TimeZoneInfo.Utc;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        OnDelay?.Invoke();
        return Task.CompletedTask;
    }

    public DateTime NowUtc() => _now;
}
