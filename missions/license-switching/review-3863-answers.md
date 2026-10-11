# Pull request 3863: answers to the review

The review is `review-3863-escape-only.md` beside this file. Both findings are accepted and fixed, each with tests
that fail on the code as it was reviewed (shown by putting the reviewed code back and running them: 10 test cases
red, all green with the fix).

## Finding 1, high: remote raw prompts bypass the "person at the terminal" exception - ACCEPTED, fixed

The exception trusted any typed origin, and a phone or browser prompt sent without Enter arrives as typed raw input,
so it passed the guard. Now only typed input on the **desktop surface** counts as a person at the terminal. The
desktop terminal control (`TerminalControl`, the physical keystrokes on this machine) is the one sender of that
origin, and a remote request cannot claim it: `InputOrigin.RemoteSurfaceFromDeviceType` resolves a Gateway device to
the phone, the Cockpit or unknown, never to the desktop. Typed input from the phone, the Cockpit and an unknown
surface is now refused while the dialog may be showing, like any automatic key.

Test: `Session_TypedInputFromAnotherSurfaceWhileTheDialogShows_IsRefused` (phone, Cockpit, unknown; sends Down and
Enter). Red on the reviewed code, green now. `Session_PersonTypesAtTheTerminalWhileTheDialogShows_ReachesIt` still
proves desktop keystrokes pass.

## Finding 2, high: a wrapped or changed option label makes recognition fail open - ACCEPTED, fixed

Two changes, following the rule that a screen which looks like the dialog but is not recognised with certainty
leans to blocking:

- **Recognition no longer depends on one intact row.** The certain check (`IsShowing`) reads the rows above the
  cancel hint joined together, so an option or a hint wrapped over two rows still matches.
- **The guard blocks on a wider check.** `MayBeShowing` is true when an open menu (its "Esc to cancel" or "Enter to
  confirm" hint) sits at the bottom of the screen with any word of a usage limit near it ("stop and wait", "limit to
  reset", "usage credits", "extra usage", "add funds", "/rate-limit-options", "spend limit", "session limit", "weekly
  limit", "usage limit"). A changed label, a changed order and a half-drawn menu all carry some of these. The input
  guard refuses automatic input on this wider check; the Director sends Escape only on the certain one, so an
  uncertain screen gets no keystroke at all, Escape included.

The cost is on the safe side: a menu shown next to a usage-limit notice is not answered automatically either. An
ordinary menu with no limit words (a permission prompt) is still answered, and a test holds that.

Tests: `IsShowing_FirstOptionWrappedOverTwoRows_IsStillTrue` (each of the six real screens with its option row split
in two), `MayBeShowing_DialogInAnUncapturedShape_IsTrueButItIsNotEscaped` and
`Session_DialogInAnUncapturedShape_AutomaticInputRefusedAndNoEscape` (a changed label, reordered options, a half-drawn
menu), `MayBeShowing_RealDialogScreen_IsTrue`, and the permission prompt negative now also asserts `MayBeShowing` is
false.

One honest limit of the wrapped-label proof: on the reviewed code it fails for the "rows intact" screen only. The
other five captures hold the dialog twice (the terminal redrew it), and the second, unwrapped copy still matched. The
fix is the same code for all six.

## Runs

- Core.UnitTests: 1,499 passed, 1 failed - `BackgroundWorkRegisterTests`, red on main already (`LinkRequestsCache.cs`
  has no row in `docs/BackgroundWork.md`), untouched by this change.
- Core.Tests, sessions, drivers and the terminal state detector: 355 passed.
