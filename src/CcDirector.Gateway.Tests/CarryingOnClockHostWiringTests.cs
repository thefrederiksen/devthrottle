using CcDirector.Core.Tenancy;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// ISSUE #2992: THE CARRYING-ON CLOCK READS AN OWNED SESSION'S STORED CONVERSATION THROUGH THE HOST'S OWN WIRING.
///
/// The transcript-shape rules are proved in Gateway.UnitTests (<c>CarryingOnClockTranscriptShapesTests</c>) over an
/// environment the test builds. What only a booted HOSTED host proves is the one argument those tests supply for
/// themselves: the reader the host hands the verdict environment, and that it reads the store inside the account's
/// scope. Here the production sweep (<see cref="TurnVerdictWatchdogSweep"/>) runs over the host's own environment.
///
/// Two owners, each carrying on for its Worker, chosen so that a reader answering nothing fails BOTH ways:
///  - Worker A is inside a silent tool call. Read from its terminal alone it stopped 29 minutes ago, so its owner
///    would expire; only the stored conversation holds it.
///  - Worker B's turn ended in its conversation 25 minutes ago, but its terminal wrote a minute ago. Read from the
///    terminal alone its owner would NOT expire yet; only the conversation's own end time expires it now.
/// </summary>
public sealed class CarryingOnClockHostWiringTests : IAsyncLifetime
{
    private const string DirectorId = "director-2992";

    private GatewayHost _gateway = null!;
    private TenantId _tenant;
    private string? _priorHosted;

    private readonly string _runId = Guid.NewGuid().ToString("N")[..12];
    private readonly string _instancesDir =
        Path.Combine(Path.GetTempPath(), "cc-carrying-on-host-" + Guid.NewGuid().ToString("N"));

    private readonly string _ownerA = Guid.NewGuid().ToString();
    private readonly string _workerA = Guid.NewGuid().ToString();
    private readonly string _ownerB = Guid.NewGuid().ToString();
    private readonly string _workerB = Guid.NewGuid().ToString();

    public async Task InitializeAsync()
    {
        _priorHosted = Environment.GetEnvironmentVariable("CC_GATEWAY_HOSTED");
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", "1");

        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "carrying-on-host-token", authEnabled: true,
            instancesDirectory: _instancesDir,
            workListsPath: Path.Combine(_instancesDir, "worklists", "worklists.json"),
            streamMode: true);
        await _gateway.StartAsync();

        _tenant = HostedTestEnrollment.Enroll(
            _gateway, $"sub-carry-{_runId}", $"carry-{_runId}@example.com", $"dev-carry-{_runId}", "MCARRY").Tenant;
        Assert.True(_gateway.TenantBoundary.IsHosted, "The harness must be running the HOSTED tenant boundary.");
    }

    public async Task DisposeAsync()
    {
        await _gateway.StopAsync();
        Environment.SetEnvironmentVariable("CC_GATEWAY_HOSTED", _priorHosted);
        try { if (Directory.Exists(_instancesDir)) Directory.Delete(_instancesDir, true); } catch { /* best effort */ }
    }

    private static SessionDto Row(string sid, DateTime lastActivity, string? owner = null) => new()
    {
        SessionId = sid,
        Name = "session " + sid,
        Agent = "ClaudeCode",
        ActivityState = "WaitingForInput",
        Status = "Running",
        RepoPath = "repo",
        CreatedAt = lastActivity.AddHours(-1),
        LastActivityAt = lastActivity,
        IsControlled = owner is not null,
        ControllerSessionId = owner,
    };

    private static TurnVerdictDto CarryingOn(string id, DateTime judged) => new()
    {
        VerdictId = id,
        JudgedAtUtc = judged,
        TurnEndObservedAtUtc = judged.AddSeconds(-20),
        ScreenHash = "hash-" + id,
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v1",
        PackageKind = "agent-reply",
        Verdict = TurnVerdictVocabulary.ContinuesAlone,
        Confidence = "high",
        Evidence = "I will wait for the Worker's message.",
        Label = "Waiting for its Worker",
        Summary = "It is waiting for its Worker to report.",
        AnswerVia = "reply",
        Risk = "none",
        Spoken = "It is waiting for its Worker to report.",
    };

    private static PushedTurn Turn(int ordinal, string role, DateTime at, HistoryPartDto part) => new()
    {
        Ordinal = ordinal,
        Role = role,
        Timestamp = new DateTimeOffset(at),
        Parts = { part },
    };

    private static TurnPushBatch Push(string sid, string state, DateTime started, params PushedTurn[] turns) => new()
    {
        SessionId = sid,
        Generation = @"C:\transcripts\" + sid + ".jsonl",
        GenerationStartedUtc = started,
        Agent = "ClaudeCode",
        IsSupported = true,
        HistoryState = state,
        StartOrdinal = 0,
        TotalCount = turns.Length,
        Turns = turns.ToList(),
    };

    [Fact]
    public async Task TheSweep_HoldsTheOwnerOfARunningToolCall_AndExpiresTheOwnerOfAnEndedTurn_FromTheStoredConversation()
    {
        var now = DateTime.UtcNow;
        _gateway.TenantSettingsResolver.SetTurnVerdictJudgeEnabled(_tenant, true, now);
        _gateway.TenantSettingsResolver.SetTurnVerdictColourEnabled(_tenant, true, now);

        _gateway.Registry.RegisterFromStream(DirectorId, "MACHINE-2992", "soren", "1.0", pid: 4321,
            startedAt: now.AddHours(-2), tenant: _tenant);
        _gateway.PushedSessions.RegisterConnection(_tenant, DirectorId, "conn-2992");
        Assert.True(_gateway.PushedSessions.ApplySnapshot(_tenant, DirectorId, "conn-2992", 1, new[]
        {
            Row(_ownerA, now.AddMinutes(-31)),
            Row(_workerA, now.AddMinutes(-29), owner: _ownerA),
            Row(_ownerB, now.AddMinutes(-31)),
            Row(_workerB, now.AddMinutes(-1), owner: _ownerB),
        }));

        _gateway.TurnVerdicts.Store(_tenant, _ownerA, CarryingOn("carry-host-a-" + _runId, now.AddMinutes(-30)));
        _gateway.TurnVerdicts.Store(_tenant, _ownerB, CarryingOn("carry-host-b-" + _runId, now.AddMinutes(-30)));

        _gateway.SeedTurnPushForTest(_tenant, DirectorId, Push(_workerA, "Working", now.AddHours(-1),
            Turn(0, "User", now.AddMinutes(-30), new HistoryPartDto { Kind = "Text", Text = "run the gateway suite" }),
            Turn(1, "Assistant", now.AddMinutes(-29), new HistoryPartDto { Kind = "ToolUse", ToolName = "Bash", ToolId = "toolu_host_a", Text = "{}" })));
        _gateway.SeedTurnPushForTest(_tenant, DirectorId, Push(_workerB, "NeedsYou", now.AddHours(-1),
            Turn(0, "User", now.AddMinutes(-28), new HistoryPartDto { Kind = "Text", Text = "summarise the logs" }),
            Turn(1, "Assistant", now.AddMinutes(-25), new HistoryPartDto { Kind = "Text", Text = "The logs are clean." })));

        await _gateway.TurnVerdictWatchdogSweepForTest.SweepAsync();

        var a = _gateway.TurnVerdicts.Latest(_tenant, _ownerA)!;
        Assert.Equal(TurnVerdictVocabulary.ContinuesAlone, a.Verdict);
        Assert.NotEqual(TurnVerdictWatchdog.ExpiredLabel, a.Label);

        var b = _gateway.TurnVerdicts.Latest(_tenant, _ownerB)!;
        Assert.Equal(TurnVerdictVocabulary.NeededYou, b.Verdict);
        Assert.Equal(TurnVerdictWatchdog.ExpiredLabel, b.Label);
    }
}
