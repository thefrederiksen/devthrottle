# Answers to the review of pull request 3933 - the Licenses page of the Cockpit

13 October 2026, by the Developer who built the page. Every finding of `review-3933.md` is answered here.

## Finding 1 - eight wrong changes leave every test green: ACCEPTED, fixed

The reviewer was right on all eight, and for the reason given: the example answers the first test file draws have
the same forecast sentence on every machine, a label equal to its machine name everywhere, and nothing that failed
to read, so a page that mixed rows up or dropped a line passed.

A second test file, `apps/cockpit/src/licenses/LicensesViewRows.test.tsx` (12 tests), changes the example answers so
that every row differs from its neighbour, and reads each row for its own. The page's tests are now 33.

| The reviewer's change | What now catches it |
|---|---|
| R1 every pill in the good colour | The status pill of each account and the result pill of each log row are checked by the class the stylesheet colours (`lic-tone-*`), for good, warning, bad and neutral, and a tone word this build does not know is checked to be neutral. |
| R2 every schedule row shows the first machine's forecast | The two machines are given different next accounts and sentences; each row is read for its own and checked not to hold the other's. |
| R3 every card shows the first account's forecast sentence | Each card's expected sentence is checked against its own account's; the test also checks that the three sentences differ. |
| R4 a read error is not drawn | An account and a machine's line are given a read error; both sentences are checked on the card, and checked absent where nothing failed. |
| R5 a machine called by its machine name | One machine's label is made "the-windows-machine (owner)"; the label is checked on the card, in the schedule and in the machine column of every log row. |
| R6 a failed read of the log shows nothing | The first read of the log fails; the banner with the sentence is checked, with the rest of the page still on screen. |
| R7 the page never shows Loading | Loading is checked before anything has answered, and under the Switch log while only the log has not answered. |
| R8 how old the numbers are is not drawn | Each card's line is checked against its own account's sentence. |

`mutations.py` now carries the eight as thirteen separate changes (R4, R5 and R7 are split by the place they hit),
plus one for finding 2. It was run again on the final code: **31 wrong changes, every one turns at least one test
red**; the output is `phase-3/mutations-output.txt`. The 33 tests were then run 20 times with no failure.

Results after the fix: `apps/cockpit` 104 files, 1087 passed, 0 failed; `packages/client-core` unchanged at 2004
passed; the type check of all four workspaces clean; lint clean on the two licenses folders.

## Finding 2 - the page decides that a read error is red: ACCEPTED, fixed

The page no longer gives a read error a tone. The account's read error and the machine's are drawn in the dim text
colour like their neighbours (`LicenseAccountCard.tsx`, `licenses.css`). A test holds it: neither element may carry
a `lic-tone-` class, and the wrong change that puts the red back (R9 in `mutations.py`) turns it red. A failed
renewal of a sign-in keeps its colour, because the Gateway sends a tone for that one (`renewalFailedTone`).

The QA run against the local Gateway was repeated after this change, so `qa/accounts-with-data.png` and
`qa/not-read.png` show the read error on account-d in the dim colour.

If the owner wants a read error to stand out, the fold should send a tone for it; that is the Gateway's to add.

## Finding 3 - Switch now for a target picked by hand names no machine: ACCEPTED, for the follow-up

Agreed, and agreed that the page cannot fix it alone: the Gateway sends one sentence, for its own choice. The
follow-up should send a sentence per target in `switchTargets`, and the confirmation then shows the sentence of
whichever target is selected. Nothing is changed in the page for it now, because inventing that sentence in the view
is exactly what rule 7 forbids. It is added to "Still owed, in the follow-up" in `phase-3/README.md`.

Its two neighbours:

- **No target chosen automatically.** ACCEPTED in part. A test now draws the state (`canSwitchNow` true,
  `switchNowTargetAccountEmail` null): no radio button is selected, the Gateway's sentence is shown, Confirm sends a
  null target, and the Gateway's refusal is shown in the confirmation. DECLINED for now: disabling Confirm until a
  target is picked. With a null target the route chooses for itself or refuses with its own sentence, which is the
  Gateway deciding; whether the page should also hold the button back is a question for when the route exists.
- **The accepted answer calls a machine left out by its machine name.** ACCEPTED, for the follow-up. That shape is
  my guess and the route does not exist; when it does, the panel shows `label` and keys on `machineId`. It is listed
  in "Still owed".

## The smaller things

- **A blank "Next account" while the forecast has not arrived.** DECLINED. The gap is one request long and the
  failure has its banner; a second loading mark inside a table cell is more than it is worth.
- **One primary button per surface.** DECLINED, with the reason: each card is its own surface and has at most one.
  If the Reviewer of the follow-up reads the guide the other way, the change is one word (`secondary`).
- **A gap in the log after more than a page of new rows in one poll.** DECLINED, as the reviewer suggests.
