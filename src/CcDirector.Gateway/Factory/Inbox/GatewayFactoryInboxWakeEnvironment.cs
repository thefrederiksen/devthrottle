using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Talk;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Fleet;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>
/// The wake's production wiring (Factory Inbox mission, phase 2): each part of <see cref="IFactoryInboxWakeEnvironment"/>
/// handed in by the Gateway host, every one required, so a wiring line that forgot one fails to build. The ring is the
/// Fleet Manager's guarded prompt send (<see cref="GatewayFleetManagerEventEnvironment.SendPromptAsync"/>), not a new way
/// of typing into a session; the start is the machine spawner's one resolve-then-create path, as a trigger's is.
/// </summary>
internal sealed class GatewayFactoryInboxWakeEnvironment : IFactoryInboxWakeEnvironment
{
    private readonly Func<TenantId, string, RegisteredFactoryDto?> _factory;
    private readonly Func<TenantId, string, PublishedFactoryDefinition?> _published;
    private readonly Func<TenantId, IReadOnlyList<(string DirectorId, SessionDto Session)>> _roster;
    private readonly Func<TenantId, string, (string DirectorId, SessionDto Session)?> _lastKnown;
    private readonly Func<TenantId, string, bool> _hasEnded;
    private readonly Func<TenantId, string, string, FactoryGivenBrief?> _publishedBrief;
    private readonly Func<TenantId, string, bool, string?> _directorOn;
    private readonly Func<TenantId, string, NewSessionRequest, CancellationToken, Task<TriggerStartAttempt>> _start;
    private readonly Func<TenantId, string, string, string, CancellationToken, Task<FleetManagerPromptSend>> _ring;
    private readonly FactoryAgentsSwitch _factoryAgents;

    public GatewayFactoryInboxWakeEnvironment(
        Func<TenantId, string, RegisteredFactoryDto?> factory,
        Func<TenantId, string, PublishedFactoryDefinition?> published,
        Func<TenantId, IReadOnlyList<(string DirectorId, SessionDto Session)>> roster,
        Func<TenantId, string, (string DirectorId, SessionDto Session)?> lastKnown,
        Func<TenantId, string, bool> hasEnded,
        Func<TenantId, string, string, FactoryGivenBrief?> publishedBrief,
        Func<TenantId, string, bool, string?> directorOn,
        Func<TenantId, string, NewSessionRequest, CancellationToken, Task<TriggerStartAttempt>> start,
        Func<TenantId, string, string, string, CancellationToken, Task<FleetManagerPromptSend>> ring,
        FactoryAgentsSwitch factoryAgents)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _published = published ?? throw new ArgumentNullException(nameof(published));
        _roster = roster ?? throw new ArgumentNullException(nameof(roster));
        _lastKnown = lastKnown ?? throw new ArgumentNullException(nameof(lastKnown));
        _hasEnded = hasEnded ?? throw new ArgumentNullException(nameof(hasEnded));
        _publishedBrief = publishedBrief ?? throw new ArgumentNullException(nameof(publishedBrief));
        _directorOn = directorOn ?? throw new ArgumentNullException(nameof(directorOn));
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _factoryAgents = factoryAgents ?? throw new ArgumentNullException(nameof(factoryAgents));
    }

    public RegisteredFactoryDto? Factory(TenantId tenant, string factory) => _factory(tenant, factory);

    public PublishedFactoryDefinition? Published(TenantId tenant, string factory) => _published(tenant, factory);

    public IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant) => _roster(tenant);

    public (string DirectorId, SessionDto Session)? LastKnown(TenantId tenant, string sessionId) => _lastKnown(tenant, sessionId);

    public bool HasEnded(TenantId tenant, string sessionId) => _hasEnded(tenant, sessionId);

    public FactoryGivenBrief? PublishedBrief(TenantId tenant, string factory, string seat) => _publishedBrief(tenant, factory, seat);

    public string? DirectorOn(TenantId tenant, string machine, bool buildsKit) => _directorOn(tenant, machine, buildsKit);

    public Task<TriggerStartAttempt> StartAsync(TenantId tenant, string machine, NewSessionRequest request, CancellationToken ct)
        => _start(tenant, machine, request, ct);

    public Task<FleetManagerPromptSend> RingAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct)
        => _ring(tenant, directorId, sessionId, text, ct);

    /// <summary>Asked QUIETLY: the heartbeat asks every 15 seconds, and the answer is the same as the logged one.</summary>
    public bool FactoryAgentsOn(TenantId tenant) => _factoryAgents.AnswerQuietly(tenant).IsOn;

    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);

    public DateTime NowUtc() => DateTime.UtcNow;
}
