using System.ComponentModel;

namespace CcDirector.Avalonia;

/// <summary>
/// Keeps the "N need you" header current: watches every rail row's <see cref="SessionViewModel.NeedsYou"/>
/// and asks for ONE recount per dispatcher pass, however many rows moved in it.
///
/// It listens to the VIEW-MODEL's NeedsYou property, NOT to the raw Session.OnStatusColorChanged event. The
/// count is folded from hold + dictation + activity + overlays, and only ONE of those raises
/// OnStatusColorChanged - so hooking that event alone left the header stale until the 15 second timer
/// happened to fire. Snoozing a red session visibly left "1 need you" above a grey "Snoozed" row for up to
/// fifteen seconds. SessionViewModel raises NeedsYou from every handler that can move the verdict, so
/// subscribing to the property is what makes the count prompt.
///
/// WHY ONE RECOUNT. It used to post one recount per row whose NeedsYou was raised, so a Gateway fold sweep
/// across N sessions queued N recounts of N sessions each (terminal slowdown plan, step 4). MainWindow owns
/// one of these and does nothing else with the subscriptions, so the coordination a test drives is the one
/// the window runs.
/// </summary>
internal sealed class NeedsYouWatcher
{
    private readonly Dictionary<SessionViewModel, PropertyChangedEventHandler> _handlers = new();
    private readonly CoalescedUiAction _recount;

    /// <param name="recount">Recounts the header. Runs on the screen thread, once per pass.</param>
    public NeedsYouWatcher(Action recount)
    {
        ArgumentNullException.ThrowIfNull(recount);
        _recount = new CoalescedUiAction(recount);
    }

    /// <summary>Start watching a row. Watching a row twice is a no-op, so a row never asks twice.</summary>
    public void Watch(SessionViewModel vm)
    {
        if (_handlers.ContainsKey(vm)) return;
        PropertyChangedEventHandler h = (_, args) =>
        {
            if (args.PropertyName is not (null or nameof(SessionViewModel.NeedsYou))) return;
            _recount.Request();
        };
        _handlers[vm] = h;
        vm.PropertyChanged += h;
    }

    /// <summary>Stop watching a row that left the rail.</summary>
    public void Unwatch(SessionViewModel vm)
    {
        if (!_handlers.Remove(vm, out var h)) return;
        vm.PropertyChanged -= h;
    }

    /// <summary>Watch exactly these rows and no others - the rail's list was replaced wholesale.</summary>
    public void WatchOnly(IEnumerable<SessionViewModel> rows)
    {
        foreach (var kv in _handlers) kv.Key.PropertyChanged -= kv.Value;
        _handlers.Clear();
        foreach (var vm in rows) Watch(vm);
    }
}
