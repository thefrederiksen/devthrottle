using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CcDirector.Gateway.Tests.Messaging;

/// <summary>
/// Counts the commands the framework actually issues against one database file, from its own diagnostic events -
/// the same technique as the verdict store's reader counter, and for the same reason: the store opens its
/// contexts from the database's pooled factory, which has no seam for an interceptor. Matches on the harness's
/// per-test directory name, so another test's commands are never counted. A counter that hears nothing reads
/// zero, and every test that uses it also asserts a count above zero, so it cannot pass by not listening.
/// </summary>
internal sealed class DatabaseCommandCounter
    : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string _databaseDirectory;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly IDisposable _allListeners;
    private int _reads;
    private int _others;

    public DatabaseCommandCounter(string dbPath)
    {
        _databaseDirectory = Path.GetFileName(Path.GetDirectoryName(dbPath)!);
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    /// <summary>Commands executed as readers - every query, and on SQLite any save that reads back.</summary>
    public int Readers => Volatile.Read(ref _reads);

    /// <summary>Every other command (non-query, scalar).</summary>
    public int Others => Volatile.Read(ref _others);

    public int Total => Readers + Others;

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name != DbLoggerCategory.Name) return;
        lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this));
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> evt)
    {
        if (evt.Key != RelationalEventId.CommandExecuting.Name) return;
        if (evt.Value is not CommandEventData data) return;
        var source = data.Command.Connection?.DataSource ?? "";
        if (!source.Contains(_databaseDirectory, StringComparison.OrdinalIgnoreCase)) return;
        // The connection's own set-up statements (the journal mode) are not the store's work.
        if (data.Command.CommandText.StartsWith("PRAGMA", StringComparison.OrdinalIgnoreCase)) return;
        if (data.ExecuteMethod == DbCommandMethod.ExecuteReader) Interlocked.Increment(ref _reads);
        else Interlocked.Increment(ref _others);
    }

    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

    public void Dispose()
    {
        _allListeners.Dispose();
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }
}
