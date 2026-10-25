using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Alerts;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Alerts;

/// <summary>
/// ONE LIST PER ACCOUNT, COMPOSED AT READ TIME (the Alerts mission). Hand-built sources stand in for the features - the
/// features' own folds are pinned by their source tests - and the marks are the real <see cref="AlertMarkStore"/> on a
/// throwaway SQLite file. What these hold down: the red count counts only needs-answer alerts; needs-answer comes first,
/// newest first; seen is remembered per version, so a change makes an alert unseen again; a needs-answer alert can never
/// be cleared; clear-all clears exactly the rest and says how many; and one account's marks never touch another's.
/// </summary>
public sealed class AlertsServiceTests : IDisposable
{
    private readonly GatewayDbTestHarness _harness = new();
    private readonly AlertMarkStore _marks;
    private readonly FakeSource _restarts = new(AlertKinds.RestartRequest);
    private readonly FakeSource _secrets = new(AlertKinds.SecretTransfer);
    private readonly AlertsService _service;
    private DateTime _now = new(2026, 10, 25, 21, 0, 0, DateTimeKind.Utc);

    private static readonly TenantId Tenant = new("acct-a");
    private static readonly TenantId Other = new("acct-b");

    public AlertsServiceTests()
    {
        _marks = new AlertMarkStore(_harness.Open());
        _service = new AlertsService(new IAlertSource[] { _restarts, _secrets }, _marks, () => _now);
    }

    public void Dispose() => _harness.Dispose();

    private AlertDto Alert(string kind, string sourceId, bool needsAnswer, int minutesAgo)
        => new()
        {
            Id = AlertKinds.IdOf(kind, sourceId),
            Kind = kind,
            NeedsAnswer = needsAnswer,
            Title = sourceId,
            Summary = sourceId,
            Status = needsAnswer ? "Waiting" : "Declined",
            CreatedAtUtc = _now.AddMinutes(-minutesAgo),
            UpdatedAtUtc = _now.AddMinutes(-minutesAgo),
        };

    [Fact]
    public void List_CountsOnlyNeedsAnswer_AndOrdersNeedsAnswerFirstThenNewest()
    {
        _restarts.Add(Tenant, Alert(AlertKinds.RestartRequest, "old-closed", false, 30));
        _restarts.Add(Tenant, Alert(AlertKinds.RestartRequest, "pending", true, 20));
        _secrets.Add(Tenant, Alert(AlertKinds.SecretTransfer, "new-closed", false, 1));
        _secrets.Add(Tenant, Alert(AlertKinds.SecretTransfer, "waiting", true, 5));

        var list = _service.List(Tenant);

        Assert.Equal(2, list.NeedsAnswerCount);
        Assert.Equal(new[] { "waiting", "pending", "new-closed", "old-closed" }, list.Alerts.Select(a => a.Title));
    }

    [Fact]
    public void List_WithNothing_IsAnEmptyListAndACountOfNought()
    {
        var list = _service.List(Tenant);

        Assert.NotNull(list.Alerts);
        Assert.Empty(list.Alerts);
        Assert.Equal(0, list.NeedsAnswerCount);
    }

    [Fact]
    public void MarkSeen_IsRemembered_UntilTheAlertChanges()
    {
        var alert = Alert(AlertKinds.RestartRequest, "r1", false, 10);
        _restarts.Add(Tenant, alert);
        Assert.False(Assert.Single(_service.List(Tenant).Alerts).Seen);

        Assert.Equal(1, _service.MarkSeen(Tenant, new[] { alert.Id }));
        Assert.True(Assert.Single(_service.List(Tenant).Alerts).Seen);

        // The Director reports progress: the record changed, so the owner has not seen THIS version.
        alert.UpdatedAtUtc = _now.AddMinutes(-1);
        Assert.False(Assert.Single(_service.List(Tenant).Alerts).Seen);
    }

    [Fact]
    public void MarkSeen_SkipsIdsThatNameNoCurrentAlert()
    {
        _restarts.Add(Tenant, Alert(AlertKinds.RestartRequest, "r1", false, 10));

        Assert.Equal(1, _service.MarkSeen(Tenant, new[] { "restart-request:r1", "restart-request:gone" }));
    }

    [Fact]
    public void Clear_RefusesANeedsAnswerAlert_AndLeavesItListed()
    {
        _restarts.Add(Tenant, Alert(AlertKinds.RestartRequest, "pending", true, 1));

        Assert.Equal(AlertClearOutcome.NeedsAnswer, _service.Clear(Tenant, "restart-request:pending"));
        Assert.Single(_service.List(Tenant).Alerts);
    }

    [Fact]
    public void Clear_RemovesAClosedAlertFromTheList_AndItStaysCleared()
    {
        _restarts.Add(Tenant, Alert(AlertKinds.RestartRequest, "closed", false, 1));

        Assert.Equal(AlertClearOutcome.Cleared, _service.Clear(Tenant, "restart-request:closed"));

        Assert.Empty(_service.List(Tenant).Alerts);
        Assert.Empty(new AlertsService(new IAlertSource[] { _restarts, _secrets }, new AlertMarkStore(_harness.Open()), () => _now)
            .List(Tenant).Alerts);
    }

    [Fact]
    public void Clear_AnUnknownId_IsNotFound()
    {
        Assert.Equal(AlertClearOutcome.NotFound, _service.Clear(Tenant, "restart-request:nope"));
    }

    [Fact]
    public void AClearedAlertThatNeedsAnAnswerAgain_IsListedAgain()
    {
        var alert = Alert(AlertKinds.RestartRequest, "r1", false, 1);
        _restarts.Add(Tenant, alert);
        _service.Clear(Tenant, alert.Id);

        alert.NeedsAnswer = true;

        Assert.True(Assert.Single(_service.List(Tenant).Alerts).NeedsAnswer);
    }

    [Fact]
    public void ClearAll_ClearsEveryAlertThatNeedsNoAnswer_AndSaysHowMany()
    {
        _restarts.Add(Tenant, Alert(AlertKinds.RestartRequest, "a", false, 1));
        _restarts.Add(Tenant, Alert(AlertKinds.RestartRequest, "b", true, 2));
        _secrets.Add(Tenant, Alert(AlertKinds.SecretTransfer, "c", false, 3));
        _secrets.Add(Tenant, Alert(AlertKinds.SecretTransfer, "d", false, 4));
        _service.Clear(Tenant, "secret-transfer:d");

        Assert.Equal(2, _service.ClearAll(Tenant));

        var left = Assert.Single(_service.List(Tenant).Alerts);
        Assert.Equal("b", left.Title);
        Assert.Equal(0, _service.ClearAll(Tenant));
    }

    [Fact]
    public void Marks_AreKeptPerAccount()
    {
        _restarts.Add(Tenant, Alert(AlertKinds.RestartRequest, "same-id", false, 1));
        _restarts.Add(Other, Alert(AlertKinds.RestartRequest, "same-id", false, 1));

        _service.Clear(Tenant, "restart-request:same-id");
        _service.MarkSeen(Tenant, new[] { "restart-request:same-id" });

        var other = Assert.Single(_service.List(Other).Alerts);
        Assert.False(other.Seen);
        Assert.Empty(_service.List(Tenant).Alerts);
    }

    [Fact]
    public void List_SweepsTheMarkOfAnAlertNoSourceListsAnyMore_OnceItIsOld()
    {
        var alert = Alert(AlertKinds.RestartRequest, "r1", false, 1);
        _restarts.Add(Tenant, alert);
        _service.Clear(Tenant, alert.Id);
        _restarts.Remove(Tenant, alert.Id);

        _service.List(Tenant);
        Assert.Single(_marks.All(Tenant));

        _now += AlertWindow.Earlier + TimeSpan.FromMinutes(1);
        _service.List(Tenant);
        Assert.Empty(_marks.All(Tenant));
    }

    [Fact]
    public void List_KeepsTheMarkOfAnAlertStillListed_HoweverOld()
    {
        var alert = Alert(AlertKinds.RestartRequest, "r1", true, 1);
        _restarts.Add(Tenant, alert);
        _service.MarkSeen(Tenant, new[] { alert.Id });

        _now += TimeSpan.FromDays(3);
        _service.List(Tenant);

        Assert.Single(_marks.All(Tenant));
    }

    [Theory]
    [InlineData("restart-request:3f9c", true)]
    [InlineData("secret-transfer:0123456789abcdef0123456789abcdef", true)]
    [InlineData("message-request:abc", true)]
    [InlineData("restart:3f9c", false)]
    [InlineData("restart-request:", false)]
    [InlineData(":abc", false)]
    [InlineData("restart-request", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWellFormedId_AcceptsOnlyAKnownKindAColonAndASourceId(string? id, bool wellFormed)
    {
        Assert.Equal(wellFormed, AlertsService.IsWellFormedId(id));
    }

    [Fact]
    public void TwoSourcesOfOneKind_AreRefusedAtConstruction()
    {
        Assert.Throws<ArgumentException>(() => new AlertsService(
            new IAlertSource[] { new FakeSource(AlertKinds.RestartRequest), new FakeSource(AlertKinds.RestartRequest) },
            _marks, () => _now));
    }

    private sealed class FakeSource : IAlertSource
    {
        private readonly Dictionary<TenantId, List<AlertDto>> _alerts = new();

        public FakeSource(string kind) => Kind = kind;

        public string Kind { get; }

        public void Add(TenantId tenant, AlertDto alert)
        {
            if (!_alerts.TryGetValue(tenant, out var list)) _alerts[tenant] = list = new List<AlertDto>();
            list.Add(alert);
        }

        public void Remove(TenantId tenant, string id) => _alerts[tenant].RemoveAll(a => a.Id == id);

        // A fresh copy per read, as a real source folds afresh from its store every time.
        public IReadOnlyList<AlertDto> List(TenantId tenant, DateTime nowUtc)
            => _alerts.TryGetValue(tenant, out var list)
                ? list.Select(a => new AlertDto
                {
                    Id = a.Id, Kind = a.Kind, NeedsAnswer = a.NeedsAnswer, Title = a.Title, Summary = a.Summary,
                    Status = a.Status, CreatedAtUtc = a.CreatedAtUtc, UpdatedAtUtc = a.UpdatedAtUtc,
                }).ToList()
                : new List<AlertDto>();
    }
}
