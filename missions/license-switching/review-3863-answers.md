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

# Fix round: answers

The fix-round review is `review-3863-fix-round.md` beside this file.

## Medium: unrelated usage-limit text makes an ordinary menu drop automatic input - ACCEPTED, fixed

Two parts, following the Tech Lead's reading.

- **The limit words must be in the same menu as the hint.** `MayBeShowing` now reads, for each hint row in the lower
  half, only that menu: from its question ("What do you want to do?", "Do you want to proceed?" - the nearest row with
  a question mark, at most six rows up) down to the hint. Output above the question is not part of the menu. When no
  question is in reach, every row in reach counts, which leans to blocking. A permission prompt under "The usage limit
  is described in the README" - directly above the prompt or above its question - is answered again.
- **A blocked remote prompt is no longer a silent success.** The prompt verb checks the dialog before it types and
  answers not-delivered with a sentence the phone and the Cockpit can show ("The session is stopped at Claude Code's
  usage-limit dialog, which only a person at that machine's terminal may answer; nothing was typed."). A recorded
  delivery is written not-delivered with the same reason. For a dialog drawn while a send waits, the verb watches the
  guard during the send (`UsageLimitInputGuard.WatchRefusals`) and answers the same refusal when any write was
  refused. Every refused write is still logged by the guard. Writers on timer threads still get a logged drop, never
  an exception, since an exception escaping a timer ends the process.

The trade: a menu whose own rows carry no limit word is no longer blocked by limit text elsewhere on the screen. So
the first round's "half drawn" shape (the menu's question and hint with no option drawn yet) no longer blocks. It is
replaced by "option cut short" (the first option drawn only as far as "Stop and wait for li"), which is a partial
drawing that does carry the menu's own words, and still blocks without an Escape.

Tests, each red with the fix taken out (8 of 8) and green with it:

- `MayBeShowing_PermissionPromptUnderUnrelatedLimitText_IsFalse` and
  `Session_PermissionPromptUnderUnrelatedLimitText_AutomaticAnswerReachesIt` (the line above the prompt, and above its
  question).
- `SendPromptAsync_PhoneAnswerWhileTheDialogShows_RefusedWithAReasonAndNothingTyped` (with and without Enter; without
  the fix the Enter case answered "delivering" after the 20-second budget, the raw case answered accepted),
  `SendPromptAsync_RecordedDeliveryWhileTheDialogShows_RecordSaysNotDeliveredWithTheReason`, and
  `SendPromptAsync_PhoneAnswerToAPermissionPromptUnderUnrelatedLimitText_ReachesIt`, through the real prompt core.
- `WatchRefusals_GuardRefusesInsideTheScope_IsRefusedAndNotAfterIt` and
  `WatchRefusals_RefusalInsideAnAwaitedSend_IsSeenByTheCaller`.

## Runs, fix round

- Core.UnitTests: 1,505 passed, 1 failed - the same `BackgroundWorkRegisterTests` row missing on main.
- Gateway.UnitTests, every prompt and delivery test (filter `Prompt|Delivery|UsageLimitDialogPrompt`): 501 passed.
