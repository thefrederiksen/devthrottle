using System.Runtime.CompilerServices;
using Avalonia.Controls;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// The one way a button in this application runs asynchronous work (issues #1105 and #1107).
///
/// A CLICK IS A PROMISE. It is answered within about a tenth of a second - the control enters a working
/// state BEFORE any await, not after the work returns - and it cannot be redeemed twice. Those two
/// properties were missing all over the Director, and the shapes they produced were all the same bug:
///
///   - The Cockpit button ran an eight-second probe with no state change, so it looked broken and invited
///     more clicks; five clicks produced five modal dialogs stacked on the taskbar (#1105).
///   - The workflow recorder disabled Record AFTER the network call and reported failure only to the log,
///     so a failed start left the interface saying IDLE and the button apparently dead (#1107, item 1).
///   - Compact set a LABEL but never disabled anything, so three clicks bought three compactions - a label
///     is not a guard, and this one read as if re-entrancy had been considered (#1107, item 3).
///
/// This is modelled on what the repository already got right - <c>ConnectionsView.OpenConnection</c> checks
/// a busy flag, sets it BEFORE the network call, and always clears it - rather than inventing a new
/// pattern. The three rules it enforces are:
///
///   1. FEEDBACK BEFORE THE AWAIT. The control is disabled and relabelled synchronously, so the very next
///      frame shows it.
///   2. A REAL GUARD. A second click while the work is in flight returns immediately. Tracked by an
///      explicit in-flight set rather than by reading IsEnabled, so a control disabled for some unrelated
///      reason is never confused with one that is working.
///   3. FAILURE REACHES THE SCREEN. An unhandled exception is shown to the user, not written only to
///      FileLog. A button that fails invisibly is indistinguishable from a button that does nothing, which
///      is the defect behind three of the nine sites in #1107.
///
/// The finally always restores the control, including when the work throws, so a failure can never leave a
/// button disabled forever - which would turn a transient error into a dead control until restart.
/// </summary>
public static class BusyAction
{
    // The controls whose work is in flight. An explicit set, because IsEnabled means "the user may click
    // this", which is a different question from "this is already running" - a control can be disabled for
    // any number of reasons that have nothing to do with a click being in progress. Entries are removed in
    // a finally, so this cannot grow.
    private static readonly HashSet<Control> InFlight = new();

    /// <summary>
    /// Run <paramref name="work"/> with <paramref name="button"/> in a working state, guarded against
    /// re-entry, with any failure surfaced to the screen.
    /// </summary>
    /// <param name="button">The control that was clicked. Disabled for the duration and always restored.</param>
    /// <param name="work">The asynchronous work. Started only after the button is already showing busy.</param>
    /// <param name="busyLabel">
    /// What the button says while working (e.g. "Opening..."). Null leaves the label alone and only
    /// disables - correct for icon buttons, where a text swap would deform the control.
    /// </param>
    /// <param name="onFailure">
    /// How a failure is shown. Null uses <paramref name="owner"/> to show a modal dialog. A caller with its
    /// own inline status line passes that instead - the requirement is that the failure is VISIBLE, not
    /// that it is a dialog.
    /// </param>
    /// <param name="owner">The window to parent the default failure dialog to.</param>
    /// <param name="failureTitle">Title for the default failure dialog.</param>
    /// <param name="origin">Caller name, for the log. Supplied by the compiler.</param>
    /// <returns>True when the work ran to completion; false when it was blocked as re-entrant or it threw.</returns>
    public static async Task<bool> RunAsync(
        Control button,
        Func<Task> work,
        string? busyLabel = null,
        Action<string>? onFailure = null,
        Window? owner = null,
        string? failureTitle = null,
        [CallerMemberName] string? origin = null)
    {
        ArgumentNullException.ThrowIfNull(button);
        ArgumentNullException.ThrowIfNull(work);

        // THE GUARD, and it is deliberately the first thing here. Everything below this line - including the
        // busy state - happens exactly once per completed run.
        lock (InFlight)
        {
            if (!InFlight.Add(button))
            {
                FileLog.Write($"[BusyAction] {origin}: ignored, this action is already running");
                return false;
            }
        }

        // FEEDBACK BEFORE THE AWAIT. No await has happened yet, so this is on the same dispatcher turn as
        // the click and is painted before the work starts.
        //
        // Both original values are captured, not assumed. Restoring to "enabled" unconditionally would
        // ENABLE a control that some other rule had deliberately disabled, and restoring only a non-null
        // label would leave a busy label stuck on a control that started with no content at all. Neither
        // arises at today's call sites; both are the kind of thing that arrives later with the site that
        // trips it, by which point this helper is trusted everywhere and nobody re-reads it.
        var originalEnabled = button.IsEnabled;
        var labelled = busyLabel is not null ? button as ContentControl : null;
        var originalContent = labelled?.Content;

        button.IsEnabled = false;
        if (labelled is not null) labelled.Content = busyLabel;

        try
        {
            await work();
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BusyAction] {origin} FAILED: {ex}");

            // The failure must land somewhere the user can see. Reporting it only to FileLog is what made
            // the recorder's Record button look like it did not exist.
            var message = ex.Message;
            if (onFailure is not null)
            {
                onFailure(message);
            }
            else if (owner is not null)
            {
                await new MessageDialog(failureTitle ?? "Something went wrong", message).ShowDialog<bool?>(owner);
            }
            else
            {
                // No surface was given and no owner to parent a dialog to. That is a wiring mistake at the
                // call site rather than a user-facing condition, and it is logged as loudly as it gets
                // BECAUSE the alternative is the silent failure this helper exists to abolish.
                FileLog.Write($"[BusyAction] {origin} FAILED: NO FAILURE SURFACE was supplied, so the user was told "
                    + $"nothing. Pass onFailure or owner. The failure was: {message}");
            }

            return false;
        }
        finally
        {
            // Always restore, including on the exception path: a failure that left the control disabled
            // would turn one bad network moment into a button that stays dead until the app restarts.
            if (labelled is not null) labelled.Content = originalContent;
            button.IsEnabled = originalEnabled;
            lock (InFlight) InFlight.Remove(button);
        }
    }

    /// <summary>Test seam: whether this control currently has work in flight.</summary>
    internal static bool IsRunning(Control button)
    {
        lock (InFlight) return InFlight.Contains(button);
    }
}
