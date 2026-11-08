using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Activity;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Inbox;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Wingman;

namespace CcDirector.Gateway.Supervision;

/// <summary>
/// The production wiring of <see cref="ISupervisorEnvironment"/> (issue #915): the supervisor's reads, its
/// one write, its recovery log and its escalation, each pointed at machinery that already exists.
///
/// Nothing here is new plumbing. The screen read and the menu check are the same tunnel read and the same
/// pure classifier the voice cluster uses (<see cref="WaitingScreenReader"/>); the activity-state read is the
/// pushed roster snapshot, so liveness is never established by dialing a session; the send is the ordinary
/// prompt verb; the recovery log is the durable activity ledger, already tenant-scoped and already served
/// over an endpoint; the escalation is a message in the owner's Your inbox, from "Session supervisor", with a push
/// to the phones - the same channel the network-diagnostics alerts use (<see cref="SystemAlertInbox"/>). It was an
/// email until the owner's ruling "Move them all to Your inbox, with a push to the phone"; this class holds no mail
/// sender of any kind any more.
///
/// TENANT SCOPE. The engine runs its ladder on a background task that outlives the turn-end callback, so
/// every operation that touches per-tenant storage enters that tenant's scope explicitly rather than
/// inheriting an ambient one. A missing scope on hosted would be a cross-partition write, not merely a wrong
/// answer.
/// </summary>
internal sealed class GatewaySupervisorEnvironment : ISupervisorEnvironment
{
    private readonly TenantSettingsResolver _settings;
    private readonly Func<TenantId, string, SessionVerbClient?> _route;
    private readonly Func<TenantId, string, string?> _activityState;
    private readonly Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> _brainProvider;
    private readonly ActivityEventStore? _ledger;
    private readonly Func<TenantId, IDisposable>? _enterTenantScope;
    private readonly SystemAlertInbox? _ownerInbox;
    private readonly Func<TenantId, string, (int? Number, string? Name)>? _sessionLabel;
    private readonly Func<DateTime> _nowUtc;

    private readonly object _messageGate = new();
    private DateOnly _messageDay;
    private int _messagesToday;

    /// <summary>
    /// How many escalation messages one day may carry. An escalation is already rare - it ends the episode and
    /// a new one needs a fresh fault - but a cap means a pathological night cannot turn into an inbox full
    /// of identical messages. Matches the network-diagnostics alert cap in spirit.
    /// </summary>
    public const int DailyEscalationMessageCap = 10;

    /// <param name="settings">The per-tenant settings resolver the supervisor's knobs are read from.</param>
    /// <param name="route">Resolves a tunnel caller for (tenant, director id); null means that Director is
    /// not connected, which every read below treats as "cannot tell" rather than as a fault.</param>
    /// <param name="activityState">Reads a session's current activity state from the pushed roster snapshot,
    /// or null when the session is no longer there.</param>
    /// <param name="brainProvider">The model provider for step 3. The FAST role is used deliberately: this is
    /// a one-word classification, and it is the model the tenant already chose for latency-sensitive work.</param>
    /// <param name="ledger">The durable activity ledger the recovery log is written to. Optional: null means
    /// the process log carries the record alone (older tests), and a ledger fault never stops a recovery.</param>
    /// <param name="enterTenantScope">Enters a tenant's storage scope for the duration of a write. Optional
    /// (self-host has one partition and the scope is inert).</param>
    /// <param name="ownerInbox">Writes the escalation into the owner's Your inbox and pushes it to the phones.
    /// Optional: without it (older tests) the escalation lands in the recovery log and the process log alone.</param>
    /// <param name="sessionLabel">The session's number and name as the roster last showed them, so the message
    /// names the session the way the owner knows it; either may be null.</param>
    /// <param name="nowUtc">Clock seam for the daily message cap.</param>
    public GatewaySupervisorEnvironment(
        TenantSettingsResolver settings,
        Func<TenantId, string, SessionVerbClient?> route,
        Func<TenantId, string, string?> activityState,
        Func<TenantId, WingmanModelRole, string, CancellationToken, Task<IAgentBrain>> brainProvider,
        ActivityEventStore? ledger = null,
        Func<TenantId, IDisposable>? enterTenantScope = null,
        SystemAlertInbox? ownerInbox = null,
        Func<TenantId, string, (int? Number, string? Name)>? sessionLabel = null,
        Func<DateTime>? nowUtc = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _route = route ?? throw new ArgumentNullException(nameof(route));
        _activityState = activityState ?? throw new ArgumentNullException(nameof(activityState));
        _brainProvider = brainProvider ?? throw new ArgumentNullException(nameof(brainProvider));
        _ledger = ledger;
        _enterTenantScope = enterTenantScope;
        _ownerInbox = ownerInbox;
        _sessionLabel = sessionLabel;
        _nowUtc = nowUtc ?? (() => DateTime.UtcNow);
    }

    /// <inheritdoc />
    public SupervisorSettings Settings(TenantId tenant) => _settings.SessionSupervisor(tenant);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> ReadScreenRowsAsync(
        TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        var route = _route(tenant, directorId);
        if (route is null) return null;
        try
        {
            var grid = await route.GetScreenGridAsync(sessionId, ct).ConfigureAwait(false);
            if (grid is null || !grid.HasGrid) return null;
            return grid.Rows;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewaySupervisorEnvironment] screen read FAILED sid={sessionId}: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public string? ReadActivityState(TenantId tenant, string sessionId) => _activityState(tenant, sessionId);

    /// <inheritdoc />
    public async Task<bool> IsMenuOnScreenAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        var route = _route(tenant, directorId);
        if (route is null) return false;    // unreachable is not a menu; the send below will fail honestly
        return await WaitingScreenReader.IsMenuAsync(route, sessionId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SendContinueAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        var route = _route(tenant, directorId);
        if (route is null)
        {
            FileLog.Write($"[GatewaySupervisorEnvironment] continue NOT sent sid={sessionId}: director {directorId} is not connected");
            return false;
        }
        // Labelled as the Gateway's own text, so the Director never stamps it as the owner's turn (GatewayAuthoredPrompt).
        var request = GatewayAuthoredPrompt.For(SessionSupervisor.ContinueText);
        var (ok, _, error) = await route.PostPromptAsync(sessionId, request, ct).ConfigureAwait(false);
        if (!ok)
            FileLog.Write($"[GatewaySupervisorEnvironment] continue send FAILED sid={sessionId}: {error}");
        return ok;
    }

    /// <inheritdoc />
    public async Task<string?> AskModelVerdictAsync(TenantId tenant, IReadOnlyList<string> rows, CancellationToken ct)
    {
        try
        {
            using var brain = await _brainProvider(tenant, WingmanModelRole.Fast, Core.HostedAi.AiFeature.Supervision, ct).ConfigureAwait(false);
            var result = await brain.AskAsync(SupervisorVerdict.BuildPrompt(rows), ct).ConfigureAwait(false);
            return result?.Text;
        }
        catch (Exception ex)
        {
            // A model that cannot be asked leaves the fault unclassified, which escalates. It never
            // degrades into an assumption that the session is safe to type into.
            FileLog.Write($"[GatewaySupervisorEnvironment] model fallback FAILED: {ex.Message}");
            return null;
        }
    }

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    /// <inheritdoc />
    public void Record(SupervisorRecord record)
    {
        if (record is null) return;
        FileLog.Write($"[SessionSupervisor] {record.EventType} cause={record.Cause} sid={record.SessionId} " +
                      $"director={record.DirectorId} tenant={record.Tenant.ToLogString()}: {record.Detail}");
        AppendToLedger(record);
    }

    /// <inheritdoc />
    public async Task EscalateAsync(SupervisorRecord record, CancellationToken ct)
    {
        if (record is null) return;
        FileLog.Write($"[SessionSupervisor] ESCALATED cause={record.Cause} sid={record.SessionId} " +
                      $"director={record.DirectorId} tenant={record.Tenant.ToLogString()}: {record.Detail}");
        AppendToLedger(record);

        if (_ownerInbox is null) return;
        if (!TakeMessageBudget())
        {
            FileLog.Write($"[SessionSupervisor] escalation message SKIPPED sid={record.SessionId}: daily cap of {DailyEscalationMessageCap} reached");
            return;
        }

        try
        {
            var (number, name) = _sessionLabel?.Invoke(record.Tenant, record.SessionId) ?? (null, null);
            var (subject, body) = EscalationWords(record, number, name);
            var written = await _ownerInbox.TellAsync(record.Tenant, YourInboxWords.SessionSupervisorSeat, subject, body, ct,
                record.SessionId, number, name).ConfigureAwait(false);
            FileLog.Write($"[SessionSupervisor] escalation told in Your inbox sid={record.SessionId}: message={written.Id:D}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[SessionSupervisor] escalation message FAILED sid={record.SessionId}: {ex.Message}");
        }
    }

    /// <summary>
    /// The escalation's Your inbox subject and body, decided once here. The session is named by its number and name
    /// (<see cref="DevReportSessionLabel.Session"/>), never by its id; the message carries the way back to it.
    /// </summary>
    public static (string Subject, string Body) EscalationWords(SupervisorRecord record, int? sessionNumber, string? sessionName)
    {
        ArgumentNullException.ThrowIfNull(record);
        var session = DevReportSessionLabel.Session(sessionNumber, sessionName);
        var subject = $"Session {session} needs you ({record.Cause})";
        var body =
            "A session stopped and DevThrottle could not resume it on its own.\n\n" +
            $"Session: {session}\n" +
            $"Reason:  {record.Cause}\n" +
            $"Detail:  {record.Detail}\n\n" +
            "Open the session to see what it is waiting on.";
        return (subject, body);
    }

    /// <summary>One record into the durable ledger, inside the owning tenant's scope. The ledger OBSERVES the
    /// supervisor: an append fault is logged loudly and never turns a recovery into a failure.</summary>
    private void AppendToLedger(SupervisorRecord record)
    {
        if (_ledger is null) return;
        try
        {
            using var scope = _enterTenantScope?.Invoke(record.Tenant);
            _ledger.AppendBatch(new[]
            {
                new ActivityEventRecord
                {
                    EventId = Guid.NewGuid(),
                    DirectorSequence = 0,
                    OccurredUtc = _nowUtc(),
                    DirectorId = string.IsNullOrWhiteSpace(record.DirectorId) ? "gateway" : record.DirectorId,
                    SessionId = record.SessionId,
                    EventType = record.EventType,
                    Cause = record.Cause,
                    Detail = record.Detail,
                },
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SessionSupervisor] recovery-log append FAILED for {record.EventType} sid={record.SessionId}: {ex.Message}");
        }
    }

    /// <summary>Take one unit of today's escalation message budget, rolling the day over as needed.</summary>
    private bool TakeMessageBudget()
    {
        var today = DateOnly.FromDateTime(_nowUtc());
        lock (_messageGate)
        {
            if (_messageDay != today)
            {
                _messageDay = today;
                _messagesToday = 0;
            }
            if (_messagesToday >= DailyEscalationMessageCap) return false;
            _messagesToday++;
            return true;
        }
    }
}
