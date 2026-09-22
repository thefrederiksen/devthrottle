using Avalonia.Threading;

namespace CcDirector.Avalonia;

/// <summary>
/// Runs an action on the screen thread ONCE per dispatcher pass, however many times it was requested in
/// that pass.
///
/// WHY. The "N need you" header recounts every session. It used to post one recount per session whose
/// NeedsYou property was raised, so a Gateway fold sweep that touched forty sessions queued forty recounts,
/// each walking all forty sessions - forty squared reads on the screen thread for one visible number. A
/// pending flag collapses the burst to one recount that runs after every change in the burst has landed,
/// so the number it shows is the same one the last of the forty recounts would have shown.
/// </summary>
internal sealed class CoalescedUiAction
{
    private readonly Action _action;
    private bool _pending;

    public CoalescedUiAction(Action action)
    {
        _action = action;
    }

    /// <summary>Ask for the action to run. The first request in a pass posts it; the rest are free.
    /// Call on the screen thread.</summary>
    public void Request()
    {
        if (_pending) return;
        _pending = true;
        Dispatcher.UIThread.Post(Run);
    }

    private void Run()
    {
        // Cleared BEFORE the action, so a request made by the action itself (or by anything it raises)
        // posts a fresh run rather than being swallowed.
        _pending = false;
        _action();
    }
}
