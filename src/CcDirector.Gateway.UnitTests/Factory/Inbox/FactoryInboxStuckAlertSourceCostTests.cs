using System.Diagnostics;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory.Inbox;
using CcDirector.Gateway.Tests.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// WHAT ONE ALERTS POLL COSTS THE DATABASE (the deploy slowdown of 6 November 2026). The owner's Cockpit and phone poll
/// the alerts list every 5 seconds, and every source on it runs inside that poll. The stuck-inbox source added five
/// statements to every poll of every account, although almost every account has no inbox work at all, and the session
/// liveness it asks for cost one more statement per Secretary. So the cost is pinned here:
/// <list type="bullet">
/// <item>an account with no open inbox work: at most ONE statement, and neither the registry nor the liveness is asked;</item>
/// <item>an account with work: a FIXED number - the same for one stuck factory as for three, however many messages,
///   Secretaries or Directors there are.</item>
/// </list>
/// Counted by EF's own command event on the real store over a throwaway database, filtered to THIS test's database file,
/// so a test running beside it cannot add to the count. No clock is read: every time is chosen here.
/// </summary>
public sealed class FactoryInboxStuckAlertSourceCostTests : IDisposable
{
    /// <summary>The statements the source itself costs one poll when the account has work: the one overdue read. The
    /// registry and the liveness are the host's, asked once each (on the hosted Gateway two statements and one).</summary>
    private const int StatementsWithWork = 1;

    private static readonly TenantId A = new("acct-a");
    private static readonly DateTime T0 = new(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string[] FactoryIds = ["invoices", "payroll", "hiring"];

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly FactoryInboxStore _store;
    private readonly CommandCounter _commands;
    private int _registryCalls;
    private int _livenessCalls;
    private int _directorCalls;
    private readonly FactoryInboxStuckAlertSource _source;

    public FactoryInboxStuckAlertSourceCostTests()
    {
        _db = _h.Open();
        _store = new FactoryInboxStore(_db, FactoryInboxStoreTests.DictionaryOf);
        _commands = new CommandCounter(_h.DbPath);
        _source = new FactoryInboxStuckAlertSource(_db,
            _ => { _registryCalls++; return Factories(); },
            (_, sessions) => { _livenessCalls++; return sessions.ToHashSet(StringComparer.Ordinal); },
            (_, _) => { _directorCalls++; return true; });
    }

    public void Dispose()
    {
        _commands.Dispose();
        _h.Dispose();
    }

    private static List<RegisteredFactoryDto> Factories() => FactoryIds.Select(id => new RegisteredFactoryDto
    {
        Factory = id, Title = id, Computer = "FACTORY_BOX", BossSeat = "boss",
        Seats = [new RegisteredFactorySeatDto { Id = "boss", Name = "Boss", Computer = "SOREN_NORTH" }],
    }).ToList();

    private Guid Send(string factory, DateTime at)
        => _store.SendAsync(A, new FactoryInboxSendInput(factory, "Check the totals.", FactoryInboxWords.Notes.None,
            FactoryInboxWords.Sources.Typed, FactoryInboxWords.Surfaces.Phone, "owner-phone", at)).GetAwaiter().GetResult().Id;

    private static string SecretaryOf(int i) => $"1111111{i}-1111-4111-8111-111111111111";

    /// <summary>In every factory: messages nobody received, messages held by a Secretary that ended, and a bundle the Boss
    /// never decided - every reason the source knows, with a Secretary of its own per factory.</summary>
    private void MakeEveryFactoryStuck(int messagesPerReason)
    {
        for (var f = 0; f < FactoryIds.Length; f++)
        {
            var factory = FactoryIds[f];
            var held = Enumerable.Range(0, messagesPerReason).Select(_ => Send(factory, T0)).ToArray();
            var handed = Enumerable.Range(0, messagesPerReason).Select(_ => Send(factory, T0)).ToArray();
            _store.Receive(A, factory, SecretaryOf(f), [.. held, .. handed], [], [], [], T0.AddMinutes(1));
            _store.HandBundle(A, factory, SecretaryOf(f), handed, T0.AddMinutes(2));
            for (var i = 0; i < messagesPerReason; i++) Send(factory, T0.AddMinutes(3));
        }
    }

    [Fact]
    public void List_AccountWithNoInboxWork_CostsExactlyOneStatement_AndAsksNeitherTheRegistryNorTheLiveness()
    {
        var before = _commands.Count;

        Assert.Empty(_source.List(A, T0.AddDays(1)));

        // EXACTLY one, not "at most one": the one read always runs, so a counter that counted nothing would fail here.
        Assert.True(_commands.Count - before == 1, $"an idle account cost {_commands.Count - before} statements");
        Assert.Equal(0, _registryCalls);
        Assert.Equal(0, _livenessCalls);
        Assert.Equal(0, _directorCalls);
    }

    [Fact]
    public void List_ThreeStuckFactories_CostsTheFixedNumber_AndAsksTheRegistryAndTheLivenessOnce()
    {
        MakeEveryFactoryStuck(messagesPerReason: 1);
        var before = _commands.Count;

        var alerts = _source.List(A, T0.AddHours(3));

        Assert.Equal(FactoryIds.Length, alerts.Count);
        Assert.All(alerts, a => Assert.Equal(3, Assert.IsType<FactoryInboxStuckDetail>(a.Detail).Reasons.Count));
        Assert.Equal(StatementsWithWork, _commands.Count - before);
        Assert.Equal(1, _registryCalls);
        // ONE liveness question for every Secretary at once, never one per session.
        Assert.Equal(1, _livenessCalls);
    }

    [Fact]
    public void List_MoreMessagesInEveryStuckFactory_CostsTheSameFixedNumber()
    {
        MakeEveryFactoryStuck(messagesPerReason: 4);
        var before = _commands.Count;

        Assert.Equal(FactoryIds.Length, _source.List(A, T0.AddHours(3)).Count);

        Assert.Equal(StatementsWithWork, _commands.Count - before);
        Assert.Equal(1, _livenessCalls);
    }

    /// <summary>Counts every command EF executes against ONE database file, from EF's own command event - the event a
    /// command interceptor sees - so it observes the store exactly as it runs, with nothing added to the product.</summary>
    private sealed class CommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _databaseFile;
        private readonly IDisposable _all;
        private readonly List<IDisposable> _listeners = [];
        private int _count;

        public CommandCounter(string databaseFile)
        {
            _databaseFile = Path.GetFullPath(databaseFile);
            _all = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public int Count => Volatile.Read(ref _count);

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name != DbLoggerCategory.Name) return;
            lock (_listeners) _listeners.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (e.Key != RelationalEventId.CommandExecuting.Name || e.Value is not CommandEventData data) return;
            if (data.Context is not { } ctx) return;
            var source = ctx.Database.GetDbConnection().DataSource;
            if (string.IsNullOrEmpty(source) || !string.Equals(Path.GetFullPath(source), _databaseFile, StringComparison.OrdinalIgnoreCase))
                return;
            Interlocked.Increment(ref _count);
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            _all.Dispose();
            lock (_listeners) foreach (var l in _listeners) l.Dispose();
        }
    }
}
