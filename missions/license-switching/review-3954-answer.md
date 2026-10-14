# The review of pull request 3954, answered

13 October 2026. Every finding of `review-3954.md`, in its order. Five are fixed here, one is fixed in its own pull
request, and five stand, each with its reason. The code of this answer is commit `9d4389ca6`.

## 1. A Team page test fails under load, on main - FIXED IN ITS OWN PULL REQUEST

Not in this branch, as the review says: `src/team/teamPage.test.tsx` is not touched by it. It is fixed from a fresh
branch off main, in a pull request of its own, after this one is pushed.

## 2. The Sign-ins part is not drawn while the overview is empty - STANDS

It is more than a few lines, and it is a choice about the page that is not mine to make in a review round. Today an
empty overview shows the Gateway's one sentence "in place of every list" - that is what the first pull request of this
phase built and what its tests hold - and the Sign-ins part takes its machines from the overview. Drawing who approves
an account on an empty page means splitting that part in two, deciding what the empty page shows around the
Gateway's sentence, and proving it against the real Gateway. What it costs to leave: an owner cannot name an approver
before the first Director has reported, and at that moment no sign-in can be started or approved anyway. It is in the
record under "Still owed", so that it is a decision somebody makes and not something forgotten.

## 3. No test pinned that the empty approvers list is the Gateway's word - FIXED

A new test, "takes whether the list is empty from the Gateway's own word for it, not from counting the rows", gives
the page two answers whose word and rows disagree: `isEmpty` true with two approvers (the sentence is drawn, no row),
and `isEmpty` false with none (the table is drawn, no sentence). The test says in a comment that the Gateway never
sends such an answer and that they are made to disagree so that which of the two the page goes by can be seen. The
review's wrong change is now `S42` in `phase-3/mutations.py` and turns that test red.

## 4. Dismiss on a started sign-in's answer was not tested - FIXED

A new test, "takes the answer to a started sign-in off the screen when it is dismissed". The review's wrong change is
`S43` and turns it red.

## 5. The Add a spare account field stayed open while the call was out - FIXED

The field is shut from the moment Add a spare is confirmed until the Gateway answers, and opens again with what was
typed when the Gateway refuses (`LicensesView.tsx`, `AddSpareChoice` in `LicenseSignIns.tsx`). A new test holds the
Gateway's answer back and checks all three moments. Leaving the field open is wrong change `S44` and turns it red.

## 6. A started sign-in's outcome is looked for in the newest page of the log only - STANDS

The newest page is what the page reads every 30 seconds, and the Gateway's default page is 50 rows. For the answer to
miss its row, more than a page of switches and sign-ins must be written for one owner's machines in the minutes a
sign-in takes. Looking further means reading older pages in a loop for as long as the answer is on screen, which is
more requests for a case that needs dozens of machines. The same is true of the Switch now answer of the second pull
request, and the two should change together if they change. The row is not lost: it is in the log, and Show older
reaches it.

## 7. Section 2's and section 5's examples lacked the sign-in fields, and the fixture copier filled them in - FIXED

**Section 2's example is produced again, by the Gateway's own route handler, and the fill is deleted.**

- `phase-3/overview-example/OverviewExample.cs` builds the picture the document has always shown (two machines, three
  accounts, the clock at 2026-10-11 03:12:00 UTC, no approver set, no Director connected) by applying usage reports
  to the real store on a throwaway SQLite file, and writes out what `LicenseEndpoints.Overview` answers.
  `produce_overview_example.py` beside it copies that file into the Gateway's unit test project for the length of
  the run - the handler is internal to the Gateway, and that project is one the Gateway lets see it - runs the one
  method, takes the copy away again, and writes the answer over the example in the document.
- **The new answer differs from the old example in nothing but the new fields.** The script compares the two and
  refuses to write when anything else differs. It printed: 0 differences, 25 fields added - `approver`,
  `approverText` and `approverTone` on each of 3 accounts, `canAddSpare`, `addSpareText` and `signInsInProgress` on
  each of 2 machines, `canRenewNow` and `renewNowText` on each of 5 sign-ins. So the picture is the same picture.
- **One thing in the answer is changed, and it is said in the script.** The store gives a machine a new random
  identifier on every run, and the forecast and log examples of the document name the two machines by the
  identifiers of the run they came from. The two new identifiers are replaced by those two, by machine name. Every
  other character is the handler's.
- `copy_fixtures.py` no longer has `complete_overview`, and `fixtures.test.ts` no longer has the test that held the
  fill: `OVERVIEW` is held to the document like every other fixture. The values the fill had put there are the
  values the Gateway produced - `fixtures.ts` changed in its comments and its three new constants only.

**Section 5's example did not need producing again.** The nine fields are on an account, a machine and a sign-in, and
the answer for an account whose machines never reported has none of the three: its lists are empty. Its five
top-level names are unchanged, and the Gateway's own test holds them
(`Overview_AnswersTheExactShapeTheLicensesPageIsBuiltFrom`).

## 8. The three `owner_only` sentences had no example in the document - FIXED

They are in section 7, under "Who may call", each as the real Gateway answered a Director's own key over HTTP in the
proof run of the page (`phase-3/qa-sign-ins/qa-sign-ins-run-output.txt`, part 5). `copy_fixtures.py` copies them as
`APPROVER_SET_OWNER_ONLY`, `APPROVER_REMOVE_OWNER_ONLY` and `SIGN_IN_OWNER_ONLY`, `fixtures.test.ts` holds them to the
document, and the page's tests no longer spell them by hand. The document now has 31 examples and the fixtures hold 27
of them.

## 9. Save and Add a spare are shut on an empty field - STANDS

As the review says, it is not a decision about what a state means. The page does not send a request it knows holds
nothing; the same was accepted for Switch now without a target in the second pull request. Everything a person can
type is sent as typed and the Gateway rules on it, which the proof run shows with "not an account".

## 10. The proof program compiles two files of the Gateway's test project - STANDS

It is deliberate, and the reason is in the project file: one stand-in Director, the one the Gateway's own host tests
use, and not a second one written here that could drift from it. A rename in the test project breaks the build of a
proof program, loudly, and never the product.

## 11. Two wrong changes stayed green until the account fields became text fields - NOTHING TO DO

Confirmed by the review as the right fix.

## What was run after these changes

One at a time, in the foreground, on the Windows machine, at `9d4389ca6`.

| What | Result |
|---|---|
| `apps/cockpit`, `npx vitest run` | 109 files, **1144 passed, 0 failed**. The page's own are 79: the 3 new tests, and 1 fewer in `fixtures.test.ts` (the test of the fill went with the fill) |
| `packages/client-core`, `npx vitest run` | 163 files, 2028 tests. **Ten runs: nine were 2028 passed, 0 failed; the first was 2027 passed, 1 failed** (below) |
| Typecheck of both, the linter over the page | clean |
| `phase-3/mutations.py` | **124 wrong changes, every one caught** (the 121 of before and `S42`, `S43`, `S44`); the tree restored, 79 and 27 green after |
| `phase-3/qa_sign_ins_run.py`, the real page against the local Gateway, on the changed code | **all 91 checks held**; the screenshots and the recorded answers are those of this run |
| `.\scripts\test-local.ps1` | **GREEN** at `9d4389ca6`, tree clean, 10 suites, 4,463 tests. It runs no web tests. The parked suites were not run: no file of the product's C# is changed |

**One run of `packages/client-core` reported 1 failed of 2028, and which test it was is not known.** It was the first
run, and its output was cut to its last lines, so the name was lost - the same mistake that lost the Cockpit's
failing test before the review found it. Nothing under `packages/client-core` is changed by this round. The suite
then ran nine more times, one after the other, with every failure printed by name: each was 2028 passed, 0 failed.
So there is a test in that suite that can fail, it is on main, and it has not been found. Every run of a suite made
from here on prints its failures by name.

## Not proved, still

Unchanged from the record (`phase-3/sign-ins.md`): a sign-in that a machine really carries out, the approval relay, a
sign-in that gets no answer, `404 unknown_machine` and `409 too_many_approvers` against the real Gateway, callers
other than a workstation key, the hosted service, a phone.
