# The review of pull request 3944, answered

13 October 2026. Every finding of `review-3944.md`, in its order. Five are fixed; two stand, with the reason.

## 1. Two "word for word" assertions only checked containment - FIXED

- The barred line: the test now holds the WHOLE text of the line to the Gateway's sentence followed by its time in
  brackets, and nothing else.
- A machine's row of the Switch now answer: the test now holds the WHOLE text of the row to the Gateway's label, a
  space and the Gateway's sentence - both where the row is the answer's own and where it is the row of the switch log.
- The proof run's two checks of the same things are whole-text too (`qa_writes_run.py`); the run was made again and
  all 45 checks held.

The review's two wrong changes, which left all 46 tests green, are now in `phase-3/mutations.py` and both fail
(`phase-3/mutations-output.txt`):

| Wrong change | Before | Now |
|---|---|---|
| V1 - the page puts a word of its own before the barred sentence (`Barred: {account.barredText}`) | 46 green | 1 red: "shows the Gateway's barred sentence and its time on that account, and nothing on an account that is not barred" |
| V2 - the page puts the machine's label before the Gateway's row sentence (`{machine.label}: {row.summary}`) | 46 green | 2 red: "sends one application/json POST naming the account and the target, and shows the Gateway's answer" and "reads how a switch ended from its row of the switch log, and never sends the same Switch now again" |

The whole run: 61 wrong changes, every one caught; the tree restored, 47 and 15 green after.

## 2. Nothing guarded that `fixtures.ts` still matches the shapes document - FIXED

`apps/cockpit/src/licenses/fixtures.test.ts` reads the examples out of `cockpit-api.md` again on every run of the
Cockpit's tests and holds each fixture to its own example. It fails when the document gains or loses an example,
when a fixture differs from its example, and when a fixture exists that is not one of the document's. Wrong change
V4 (one sentence of a fixture edited by hand) turns it red. The fixtures' header no longer names a commit, which
could go stale; it names the test.

## 3. The 403 fixture for Switch now carried the renewal date's sentence - FIXED

`cockpit-api.md` section 6 now also gives the answer the Gateway gave to a refused Switch now, taken as it came from
the proof run (`phase-3/qa-writes/qa-writes-run-output.txt`): "... may switch the machines of a subscription to
another one ...". The fixtures have one constant for each write, `RENEWAL_OWNER_ONLY` and `SWITCH_NOW_OWNER_ONLY`,
and each test serves its own.

## 4. The open confirmation is a snapshot - STANDS

Keeping the open confirmation in step with each 30-second read would change what a person is looking at while they
decide: a target they have selected could become one that cannot be chosen under their cursor, and the page would
then have to choose what to do with their selection - a judgement, which rule 7 keeps off the page. As it is, the
person confirms what they read, and the Gateway judges it against what is true now and refuses with its own
sentence. The proof run shows exactly that case (`409 target-full`).

## 5. The answer panel reads only the rows of the log that are on screen - STANDS

For the row to fall off the newest page, fifty newer rows have to be written between the Switch now and the next
read. Until the row is on screen the panel shows what the Gateway answered, which was true when it was said, and the
Gateway's own sentence above it says "the switch log shows how it ends". Fetching more of the log to find one row
would be a second way of reading the log on this page for a case nobody has seen.

## 6. `confirmDisabled` had no test of its own - FIXED

`apps/cockpit/src/components/ConfirmDialog.test.tsx`, two tests: with the property the confirm button is shut,
pressing it runs nothing, and Cancel still works; without it the dialog confirms as before. Wrong change V3 (the
dialog ignores the property) turns the first red.

## Run after these changes

Each alone, in the foreground; the counts are in `phase-3/writes.md`.
