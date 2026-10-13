# Phase 3 - the Licenses page of the Cockpit: what is built, what is proven, and what is still owed

State on 13 October 2026, on branch `license-switching/phase-3-licenses-page`, cut from `origin/main` at `982091146`.
**This is not yet the phase's finished proof.** The Gateway routes the page reads are in pull request 3925, which had
not merged when this was written, and Switch now's route comes in a follow-up after it. What can be proven without a
Gateway is proven below. What needs a real Gateway is listed under "Still owed" and is not claimed.

## What is in it

- `packages/client-core/src/licenses/licensesClient.ts` - the types of the three reads and of Switch now, and the four
  calls: `GET /gateway/licenses`, `GET /gateway/licenses/forecast`, `GET /gateway/licenses/log`,
  `POST /gateway/licenses/switch-now`.
- `apps/cockpit/src/licenses/` - the page (`LicensesView.tsx`), one account as a card (`LicenseAccountCard.tsx`), the
  small drawing pieces (`licenseParts.tsx`), the stylesheet, and the tests.
- The route `/licenses` in `apps/cockpit/src/routes.tsx`, and a **Licenses** row in the Set up section of the menu,
  after Directors, with its own icon (a key).

The page, top to bottom, in the order of the mockup the owner saw:

1. **Accounts**, one card each: the account, its status, its plan and the subscription's renewal date, the 5-hour
   window and the week as two bars with their reset sentences, what the Gateway says about the account, when it is
   expected to reach its limit, paid extra usage, how old the numbers are, and then one line per machine that holds a
   sign-in for it: active or spare, when that sign-in expires, when it was made, and a failed renewal. Last on the card
   is Switch now, or the sentence saying why it is not offered.
2. **Schedule**: the next expected switch in one sentence, then one row per machine - its status, the account it is
   on, the account it moves to next, and the Gateway's sentence for when.
3. **Switch log**: when, what, machine, from, to, why, and the result with its sentence, its reason and its timing.
   "Show older" reads the next page.
4. **Switch now** opens a confirmation that says what will move and where, lists every target (one that cannot be
   chosen is shown disabled with the reason), and sends nothing until it is confirmed. A refusal stays in the
   confirmation, in the Gateway's own sentence.

## The page decides nothing

Rule 7 of `CLAUDE.md`. Every status, tone, sentence and offer is the Gateway's (`LicenseDisplayFold`) and is shown as
sent. The page never compares a percentage with 97 and never works out how far away a time is. What it does itself:
lays out, looks a row up by its account or its machine, shows a time in the viewer's own time zone, and picks a
colour from a tone word and a bar style from a level word.

Three things the page needs were not in the answers. They were reported to the Delivery Lead rather than worked out
in the view, and the names below are the ones the phase 2 Tech Lead settled:

| Needed | Field | State |
|---|---|---|
| The subscription's renewal date | `renewsOn`, `renewsText` on an account | Read fields in pull request 3925. The page shows `renewsText`. |
| A sign-in whose last renewal failed | `renewalFailedAtUtc`, `renewalFailedText`, `renewalFailedTone` on a holding | Null in 3925, filled by the follow-up. The page shows the sentence in its tone when it is not null. |
| Switch now | `POST /gateway/licenses/switch-now`, the shape in `cockpit-api.md` section 6 | In the follow-up. The page and the client are built to that shape. Section 6 describes the accepted answer in words and does not give its field names; the client reads `targetAccountEmail`, `switches[]` (`switchId`, `machineName`, `osUser`, `state`, `stateText`, `stateTone`, `summary`) and `skipped[]` (`machineName`, `why`), and these must be made to agree with the route when it exists. |

**"Not read" is never drawn as zero, and an old number is never drawn as today's.** A bar is drawn from the
Gateway's level word alone. Only `normal` and `at-limit` are a filled meter. A window with no number (`not-known`),
one whose reading is too old to trust (`stale`) and one that has reset since the reading (`reset-since`) are a
hatched bar with the Gateway's text beside it.

**A machine is called by the Gateway's label.** Two users of one computer are two machines; the page shows `label`
and never builds a name from the machine name and the user. One place has no label to show: a row of the switch log
carries `machineName` and `osUser` only, so the log shows the machine name.

Left out on purpose: the Add sign-in button (the brief: it waits on the Gateway relay), and setting the renewal date
from the page (its route, `PUT /gateway/licenses/accounts/renewal`, arrives in the follow-up; the brief asks for the
date to be shown).

## Tests

Run from the repository root after `npm ci`:

    cd apps\cockpit ; npm test
    cd packages\client-core ; npm test

Results on 13 October 2026, on the Windows machine, with other sessions running:

- `apps/cockpit`: 103 files, **1075 passed, 0 failed**. Of these, 21 are the page's (`LicensesView.test.tsx`), one is
  the new address in the route table (`routes.test.tsx`), and the menu test now expects the Licenses row.
- `packages/client-core`: 162 files, **2004 passed, 0 failed**. Of these, 9 are the client's (`licensesClient.test.ts`).
- `npm run typecheck` (all four workspaces): clean.
- `npx eslint apps/cockpit/src/licenses packages/client-core/src/licenses`: clean. `npx eslint .` reports three
  errors, all in files this work does not touch (`apps/cockpit/src/push/sw.test.ts`, `apps/mobile/src/pages/Recorder.tsx`,
  `apps/mobile/src/pages/VoiceMode.tsx`: a lint rule named in a comment is not installed).
- The Cockpit's scan of error display sites (`errorDisplaySites.test.ts`) read 193 sites and found none unreported;
  the page's three failure banners are reported by its `usePolled` hook.

The fixtures (`apps/cockpit/src/licenses/fixtures.ts`) are the Gateway's own example answers, copied by a script from
`missions/license-switching/cockpit-api.md` on the Gateway branch, not written by hand. They were copied twice: the
answers changed while this was being built (a machine became four fields with a label, a window gained the `stale`
level, a log row gained `verifiedText`, an account gained its renewal date and its first sign-in to expire), and the
second copy, from commit `63aa81f61`, turned 2 of the page's tests red until the page was brought in line. They are
copied once more when pull request 3925 merges.

### One of these tests failed now and then, and why

In the first full run of the Cockpit suite one test failed once. Running the page's tests 25 times showed which:
"still lists a row whose kind and state this build does not know" failed 1 time in 25. The cause was in the page, not
the test: the log table was drawn for one frame with no rows, because the rows were set by an effect after the answer
arrived. The page now takes the answer and makes the rows in one update (`foldNewestLogPage`), so that frame does not
exist. After the fix the page's tests ran 40 times with no failure, and the whole Cockpit suite ran green.

### Each test proved by breaking the code

`python missions\license-switching\phase-3\mutations.py` makes one wrong change at a time, runs the tests, records
which went red, and puts the file back whether or not the run worked. It ends by running both suites on the restored
code. Its last output is committed beside it as `mutations-output.txt`. Eighteen changes, every one caught:

| Wrong change | Tests that went red |
|---|---|
| The page works a window's colour out of its number | 1 |
| A window that was not read is drawn as a meter | 1 |
| The status word is shown in place of its label | 2 |
| A sign-in's expiry loses the Gateway's tone | 1 |
| Switch now is offered on every account | 1 |
| Switch now sends without asking | 4 |
| Switch now drops the picked target | 2 |
| The empty answer is drawn as tables | 1 |
| A log row loses its reason | 1 |
| Older rows are read without the cursor | 1 |
| A failed renewal is drawn without the Gateway's tone | 1 |
| The log merge repeats a row already on screen | 2 |
| A new newest page throws away the older rows already read | 1 |
| The renewal date is not drawn | 1 |
| A failed overview read is drawn as an empty page | 1 |
| The client accepts an answer that lost its list | 1 |
| The client drops the log cursor | 1 |
| The client sends Switch now without its target | 2 |

## The layout check - screenshots, but not from a Gateway

`layout-check/` holds six screenshots and the script that made them (`layout_check.py`). **They are a check of the
layout only.** The script starts the Cockpit's own development server and answers the Licenses routes with the
Gateway's example answers from `cockpit-api.md`; no Gateway is running, and every other request is refused. They show
that the page lays out and reads properly; they do not show the page working against a Gateway.

| File | What it shows |
|---|---|
| `accounts-with-data.png` | Three accounts (in use, sign-in expires soon, at limit), the schedule, the log. |
| `log-with-failed-switch.png` | The log with a switch that failed, its reason beneath it. |
| `switch-now-confirmation.png` | The confirmation: the Gateway's sentence, the target chosen, one target disabled with its reason. |
| `switch-now-refused.png` | The confirmation after a refusal, in the Gateway's sentence. |
| `not-read-and-renewal.png` | A window that was not read (hatched), a stale account (muted), a renewal date, a failed renewal. |
| `empty.png` | An account whose machines have never reported. |

Two defects were found in the first set and fixed before these were taken: the Switch now button broke over two
lines, and an account or a machine name in the log broke in the middle.

## Still owed before this phase is done

1. **The QA note the brief asks for**: screenshots of the empty state, accounts with data, a log with a failed switch
   and the Switch now confirmation, **against a local Gateway with seeded data**. It needs the routes of pull request
   3925 on main; Switch now's part needs the follow-up route too. The Cockpit's development server already forwards
   `/gateway` to a Gateway named in `COCKPIT_PROXY_TARGET`.
2. Rebase onto main once 3925 has merged, copy the fixtures again from the merged `cockpit-api.md`, and run both
   suites and the mutations again.
3. The pull request, opened only after that, and its review by a seat the Delivery Lead opens.
4. Switch now has never been sent to a real Gateway from this page. Its request and its refusal are tested at the
   wire against the planned shape, which is not the same thing.

`.\scripts\test-local.ps1` runs no web tests, so it says nothing about this change either way; it was not run.
