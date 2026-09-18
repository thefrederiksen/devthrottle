using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The answer route's early exits each write their ledger line (the Wingman-on-every-turn mission, slice E round 2,
/// inspection finding 4). The route claims every exit after the account resolves is recorded; the invalid session id
/// and the missing settings store used to return with no line at all.
///
/// Driven through <c>GatewayEndpoints.AnswerTurnVerdictAsync</c>, the handler the route maps to one line, over the
/// real self-host boundary, with a records seam that keeps every line. No booted host: neither exit can be reached on
/// one, because a real host always wires the settings store.
///
/// PARKED SUITE. Gateway.UnitTests does not run in the default gate; these run under -Parked.
/// </summary>
public sealed class TurnVerdictAnswerRouteExitTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DirectorRegistry _registry;
    private readonly KeepingRecords _records = new();

    public TurnVerdictAnswerRouteExitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cc-answer-exits-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _registry = new DirectorRegistry(_tempDir);
    }

    public void Dispose()
    {
        _registry.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception) { /* best-effort temp cleanup */ }
    }

    private sealed class KeepingRecords : ITurnVerdictAnswerRecords
    {
        public readonly List<TurnVerdictRecord> Lines = new();
        public TurnVerdictLocated? FindVerdict(TenantId tenant, string verdictId) => null;
        public TurnVerdictDto? Latest(TenantId tenant, string sessionId) => null;
        public bool MarkAnswered(TenantId tenant, TurnVerdictStoredAnswer answer) => false;
        public void Record(TurnVerdictRecord record)
        {
            lock (Lines) Lines.Add(record);
        }
    }

    private static CcDirector.Gateway.Tenancy.HostedTenantBoundary SelfHostBoundary() =>
        new(new SingleTenantContext(), new CcDirector.Gateway.Pairing.DeviceRegistry());

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode!.Value;

    private TurnVerdictRecord OnlyLine()
    {
        lock (_records.Lines) return Assert.Single(_records.Lines);
    }

    [Fact]
    public async Task AnInvalidSessionId_IsRefused_AndWritesItsCauseWord()
    {
        var result = await GatewayEndpoints.AnswerTurnVerdictAsync(new DefaultHttpContext(), "not-a-session-id",
            SelfHostBoundary(), new TurnVerdictAnswerService(_records), tenantSettings: null, _registry,
            pushedSessions: null, streamStale: TimeSpan.FromSeconds(30), owners: null, sendCommand: null, wingmanTranslator: null);

        Assert.Equal(StatusCodes.Status400BadRequest, Status(result));
        var line = OnlyLine();
        Assert.Equal(ActivityEventTypes.TurnVerdictAnswerRefused, line.EventType);
        Assert.Equal(ActivityCauses.AnswerInvalidSessionId, line.Cause);
        Assert.Equal(TenantId.Local, line.Tenant);
        Assert.Equal("not-a-session-id", line.SessionId);
    }

    [Fact]
    public async Task AGatewayWithNoSettingsStore_IsRefusedAfterTheAccountResolves_AndWritesItsCauseWord()
    {
        var sid = Guid.NewGuid().ToString();

        var result = await GatewayEndpoints.AnswerTurnVerdictAsync(new DefaultHttpContext(), sid,
            SelfHostBoundary(), new TurnVerdictAnswerService(_records), tenantSettings: null, _registry,
            pushedSessions: null, streamStale: TimeSpan.FromSeconds(30), owners: null, sendCommand: null, wingmanTranslator: null);

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        var line = OnlyLine();
        Assert.Equal(ActivityEventTypes.TurnVerdictAnswerRefused, line.EventType);
        Assert.Equal(ActivityCauses.AnswerUnavailable, line.Cause);
        Assert.Equal(TenantId.Local, line.Tenant);
        Assert.Equal(sid, line.SessionId);
    }

    /// <summary>
    /// The inspection's round 2 finding 2: a Gateway with no answer service returned before the account resolved and
    /// wrote nothing. It now resolves the account and writes <c>answer-unavailable</c> through the ledger writer the
    /// route is given for exactly this exit.
    /// </summary>
    [Fact]
    public async Task AGatewayWithNoAnswerService_IsRefusedAfterTheAccountResolves_AndWritesItsCauseWord()
    {
        var sid = Guid.NewGuid().ToString();

        var result = await GatewayEndpoints.AnswerTurnVerdictAsync(new DefaultHttpContext(), sid,
            SelfHostBoundary(), turnVerdictAnswers: null, tenantSettings: null, _registry,
            pushedSessions: null, streamStale: TimeSpan.FromSeconds(30), owners: null, sendCommand: null, wingmanTranslator: null,
            turnVerdictLedger: _records.Record);

        Assert.Equal(StatusCodes.Status404NotFound, Status(result));
        var line = OnlyLine();
        Assert.Equal(ActivityEventTypes.TurnVerdictAnswerRefused, line.EventType);
        Assert.Equal(ActivityCauses.AnswerUnavailable, line.Cause);
        Assert.Equal(TenantId.Local, line.Tenant);
        Assert.Equal(sid, line.SessionId);
    }
}
