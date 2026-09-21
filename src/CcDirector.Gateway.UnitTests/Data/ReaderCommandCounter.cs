using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Counts the reader commands the framework actually issues against one database file, so "one query" is
/// measured rather than asserted about code somebody read.
///
/// It listens to the framework's own diagnostic events rather than adding a command interceptor, because
/// the public store opens its contexts from the database's pooled factory and there is no seam to add an
/// interceptor there - and adding one to production code only so a test can count would be a second path.
/// A counter that sees nothing reads zero, which fails the assertion of one: this cannot pass by not
/// listening.
/// </summary>
internal sealed class ReaderCommandCounter
    : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    private readonly string _databaseDirectory;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly IDisposable _allListeners;
    private int _reads;

    public ReaderCommandCounter(string dbPath)
    {
        // The harness directory name is a fresh identifier per test, so matching on it cannot count
        // another test's commands.
        _databaseDirectory = Path.GetFileName(Path.GetDirectoryName(dbPath)!);
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public int Reads => Volatile.Read(ref _reads);

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name != DbLoggerCategory.Name) return;
        lock (_subscriptions) _subscriptions.Add(listener.Subscribe(this));
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> evt)
    {
        if (evt.Key != RelationalEventId.CommandExecuting.Name) return;
        if (evt.Value is not CommandEventData data || data.ExecuteMethod != DbCommandMethod.ExecuteReader) return;
        var source = data.Command.Connection?.DataSource ?? "";
        if (!source.Contains(_databaseDirectory, StringComparison.OrdinalIgnoreCase)) return;
        Interlocked.Increment(ref _reads);
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
