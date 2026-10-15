using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// THE STATE A FACTORY CHECK KEEPS (Factory Definition, phase 5b, design 1.4), over a real SQLite file: append-only,
/// the newest row is the state, an unchanged state writes nothing, a reset is an empty row marked as one, and each
/// account and each start reads only its own. The same rules run on PostgreSQL in
/// <c>CcDirector.Gateway.DatabaseTests</c>.
/// </summary>
public sealed class FactoryCheckStateStoreTests : IDisposable
{
    private static readonly TenantId A = new("acct-state-a");
    private static readonly TenantId B = new("acct-state-b");
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);
    private const string Owner = "trigger:11111111-1111-1111-1111-111111111111";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly FactoryCheckStateStore _store;

    public FactoryCheckStateStoreTests()
    {
        _store = new FactoryCheckStateStore(_harness.Open());
    }

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void StateFor_AStartWithNoRow_IsTheEmptyString()
    {
        Assert.Equal("", _store.StateFor(A, "warmforward", Owner));
        Assert.Null(_store.Newest(A, "warmforward", Owner));
    }

    [Fact]
    public void RecordIfChanged_AppendsOnlyAChange_AndTheNewestIsTheState()
    {
        Assert.NotNull(_store.RecordIfChanged(A, "warmforward", Owner, "one", "dir-1", "run-1", Now));
        Assert.Null(_store.RecordIfChanged(A, "warmforward", Owner, "one", "dir-1", "run-2", Now.AddMinutes(1)));
        var second = _store.RecordIfChanged(A, "warmforward", Owner, "two", "dir-2", "run-3", Now.AddMinutes(2));

        Assert.Equal(2, second!.Sequence);
        var newest = _store.Newest(A, "warmforward", Owner)!;
        Assert.Equal(("two", "dir-2", "run-3", false), (newest.Content, newest.WrittenBy, newest.RunRef, newest.Reset));
    }

    [Fact]
    public void RecordIfChanged_AnEmptyStateOnAFirstRun_WritesNothing()
    {
        Assert.Null(_store.RecordIfChanged(A, "warmforward", Owner, "", "dir-1", null, Now));
    }

    [Fact]
    public void RecordIfChanged_AState64KB_IsKeptWhole_AndOneByteMore_IsACallerError()
    {
        var at = new string('s', FactoryCheckStateStore.MaxContentBytes);

        _store.RecordIfChanged(A, "warmforward", Owner, at, "dir-1", null, Now);

        Assert.Equal(at, _store.StateFor(A, "warmforward", Owner));
        Assert.Throws<ArgumentException>(() => _store.RecordIfChanged(A, "warmforward", Owner, at + "s", "dir-1", null, Now));
    }

    [Fact]
    public void Reset_AppendsAnEmptyRowMarkedAsAReset_ByThePerson_AndKeepsTheHistory()
    {
        _store.RecordIfChanged(A, "warmforward", Owner, "stuck", "dir-1", "run-1", Now);

        var reset = _store.Reset(A, "warmforward", Owner, "soren", Now.AddMinutes(5));

        Assert.Equal((2, "", "person soren", true), (reset.Sequence, reset.Content, reset.WrittenBy, reset.Reset));
        Assert.Equal("", _store.StateFor(A, "warmforward", Owner));
        // The next success after a reset stores even the old content again: it differs from the empty newest row.
        Assert.NotNull(_store.RecordIfChanged(A, "warmforward", Owner, "stuck", "dir-1", "run-2", Now.AddMinutes(10)));
    }

    [Fact]
    public void TheState_IsPerAccount_PerFactory_AndPerStart()
    {
        _store.RecordIfChanged(A, "warmforward", Owner, "a's", "dir-1", null, Now);

        Assert.Equal("", _store.StateFor(B, "warmforward", Owner));
        Assert.Equal("", _store.StateFor(A, "other-factory", Owner));
        Assert.Equal("", _store.StateFor(A, "warmforward", "cron:cj_abc"));
    }

    [Theory]
    [InlineData("trigger:11111111-1111-1111-1111-111111111111", true)]
    [InlineData("cron:cj_abc123", true)]
    [InlineData("trigger:not-a-guid", false)]
    [InlineData("cron:", false)]
    [InlineData("session:abc", false)]
    [InlineData("", false)]
    public void IsOwnerKey_AcceptsOnlyATriggerIdOrAScheduleId(string key, bool expected)
        => Assert.Equal(expected, FactoryCheckStateStore.IsOwnerKey(key));
}
