# Phase 3 - the Licenses page of the Cockpit: what is built, and the QA note

13 October 2026, branch `license-switching/phase-3-licenses-page`, rebased onto `origin/main` at `0b83751ed` (the
commit that merged the Gateway routes the page reads, pull request 3925).

**This file is the record of the first pull request (3933), as it stood on the day it merged. The two things it says are
not yet real - Switch now and the editing of the renewal date - are done in the second pull request; `writes.md` beside
this file is its record, with the run against the real routes.**

**Two things on the page are built to a shape and have never met a real route: Switch now, and the editing of the
renewal date.** Their routes arrive in the follow-up pull request "Licenses API: Switch now, renewal date, failed
renewal". Against today's Gateway, Switch now is drawn, asks for confirmation, and is then answered "Not found" -
that is shown below, not hidden. The page has no control for editing the renewal date at all; it shows the date.

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
   sign-in for it: active or spare, when that sign-in expires, when it was made, and a failed renewal of the sign-in.
   Last on the card is Switch now, or the sentence saying why it is not offered.
2. **Schedule**: the next expected switch in one sentence, then one row per machine - its status, the account it is
   on, the account it moves to next, and the Gateway's sentence for when.
3. **Switch log**: when, what, machine, from, to, why, and the result with its sentence, its reason, its timing and
   whether the new sign-in answered a usage read. "Show older" reads the next page.
4. **Switch now** opens a confirmation that says what will move and where, lists every target (one that cannot be
   chosen is shown disabled with the reason), and sends nothing until it is confirmed. A refusal stays in the
   confirmation, in the Gateway's own sentence.

Left out on purpose: the Add sign-in button (the brief: it waits on the Gateway relay), and a control to set the
renewal date (its route, `PUT /gateway/licenses/accounts/renewal`, is in the follow-up).

## The page decides nothing

Rule 7 of `CLAUDE.md`. Every status, tone, sentence and offer is the Gateway's (`LicenseDisplayFold`) and is shown as
sent. The page never compares a percentage with 97 and never works out how far away a time is. What it does itself:
lays out, looks a row up by its account or its machine identifier, shows a time in the viewer's own time zone, and
picks a colour from a tone word and a bar style from a level word.

- **"Not read" is never drawn as zero, and an old number is never drawn as today's.** Only the levels `normal` and
  `at-limit` are a filled meter. `not-known`, `stale` and `reset-since` are a hatched bar with the Gateway's text.
- **A machine is called by the Gateway's `label`**, on the cards, in the schedule and in the log.
- **A read error has no colour of the page's own.** The Gateway sends no tone for it, so it is drawn in the dim text
  colour like its neighbours (the review's finding 2).
- **The two renewals are kept apart.** The subscription's renewal (`renewsText`) is beside the plan. A failed renewal
  of a sign-in (`renewalFailedText`) is on that machine's line, beside that sign-in's expiry.

## The QA run - against a real local Gateway with seeded data

    python missions\license-switching\phase-3\qa_run.py      (after npm ci at the repository root)

One command, all in the foreground, everything stopped at the end. Its output is `qa/qa-run-output.txt`.

**What the Gateway is.** `qa-gateway/` is a small program that starts the product's own `GatewayHost` in hosted mode
on a loopback port, with its whole storage under a throwaway folder, so nothing of this machine's real Gateway is
read or written. It enrolls two accounts the way the Gateway's own host tests do, each with a browser key. For the
first, the usage reports of two machines go in **through the Gateway's own ingest**, twenty minutes apart, and two
switches are written and settled through the Gateway's own store. The second account's machines never report.
The page's requests then go over HTTP through the real authentication and the real display fold; **the browser
intercepts nothing**. No sign-in key exists anywhere in it: a report carries emails, numbers and times, all made up.

What it is not: no Director is connected and no real Claude account is read. The reports are written by the QA
program in the shape a Director sends. It reaches three internal properties and one internal method of the host by
reflection, so that the product gains nothing for a QA program.

**The answers as the Gateway gave them** are saved beside the screenshots: `qa/answer-overview.json`,
`qa/answer-forecast.json`, `qa/answer-log.json`, `qa/answer-overview-empty.json`.

| Case | Screenshot | What the run showed |
|---|---|---|
| Accounts with data | `qa/accounts-with-data.png` | 4 account cards on 2 machines: in use, sign-in expires soon, at limit, and one never read. The schedule and the log beneath. |
| A failed switch in the log | `qa/log-with-failed-switch.png` | 2 log rows, 1 failed: "Failed", by hand, the Gateway's sentence and its reason ("The machine's spare for the target cannot be used; it needs a new sign-in."). The other row "Switched", threshold reached, with its timing and "The new sign-in answered a usage read." |
| Not read | `qa/not-read.png` | The account no machine could read: both bars hatched, "Not known", status "Stale", and the read error on the machine's line. 2 hatched bars on the page, no meter at zero. |
| Empty | `qa/empty.png` | The account whose machines never reported: the Gateway's sentence, no cards, no tables. |
| Switch now, the confirmation | `qa/switch-now-confirmation.png` | The Gateway's sentence for what moves, its own choice selected, two targets disabled each with its reason. Nothing was sent to reach this. |
| Switch now, confirmed, today | `qa/switch-now-no-route-yet.png` | **The Gateway answered 404.** The confirmation stays open and shows "Not found: POST /gateway/licenses/switch-now". This is the built-to-shape state; it is not a working Switch now. |

The requests the page made, from the run: `200 /gateway/licenses`, `200 /gateway/licenses/forecast`,
`200 /gateway/licenses/log`, `404 /gateway/licenses/switch-now`.

### What the run showed about the Gateway's sentences

Shown as given, and reported to the Delivery Lead rather than patched in the view:

1. For the account that was never read, two sentences on one card disagree. The account's status detail says "No
   usage reading has arrived for this account yet." and the machine's read error says "The machine could not reach
   the provider. The numbers shown are the last ones read." There are no numbers shown.
2. On the same card, paid extra usage is the bare words "Not known", and the age of the reading is "Never read". Each
   is clear beside its own label in a table; alone on a line, "Not known" does not say what is not known.

### Not covered by this run

- Switch now succeeding, being refused with a 409, or leaving machines out: the route does not exist yet.
- A renewal date that is set, and a failed sign-in renewal: the Gateway answers "Renewal date not set" and null for
  every account today. Both are drawn in the page's tests and in `layout-check/not-read-and-renewal.png` only.
- A switch in progress on a machine, and a machine that is not reporting.
- More than one page of the log ("Show older" is covered by the page's tests only).
- A real Director, a real account, the hosted service, and a phone-width window.

## Tests

Run from the repository root after `npm ci`:

    cd apps\cockpit ; npm test
    cd packages\client-core ; npm test

Results on 13 October 2026, on the Windows machine, after the rebase, with other sessions running:

- `apps/cockpit`: 104 files, **1087 passed, 0 failed**. Of these, 33 are the page's (21 in `LicensesView.test.tsx`
  and 12 in `LicensesViewRows.test.tsx`, added after the review), one is the new address in the route table
  (`routes.test.tsx`), and the menu test now expects the Licenses row.
- `packages/client-core`: 162 files, **2004 passed, 0 failed**. Of these, 9 are the client's (`licensesClient.test.ts`).
- `npm run typecheck` (all four workspaces): clean.
- `npx eslint apps/cockpit/src/licenses packages/client-core/src/licenses`: clean. `npx eslint .` reports three
  errors, all in files this work does not touch (`apps/cockpit/src/push/sw.test.ts`, `apps/mobile/src/pages/Recorder.tsx`,
  `apps/mobile/src/pages/VoiceMode.tsx`: a lint rule named in a comment is not installed).
- The Cockpit's scan of error display sites read 193 sites and found none unreported; the page's three failure
  banners are reported by its `usePolled` hook.

The fixtures (`apps/cockpit/src/licenses/fixtures.ts`) are the Gateway's own example answers, copied by a script from
`missions/license-switching/cockpit-api.md` as merged at `0b83751ed`, not written by hand. They were copied three
times, because the answers changed while this was being built; the second copy turned 2 of the page's tests red
until the page was brought in line.

`.\scripts\test-local.ps1` runs no web tests, so it says nothing about this change either way; it was not run. The
change touches no C# of the product. The QA program is in no solution and no test list.

### One of these tests failed now and then, and why

In the first full run of the Cockpit suite one test failed once. Running the page's tests 25 times showed which:
"still lists a row whose kind and state this build does not know" failed 1 time in 25. The cause was in the page, not
the test: the log table was drawn for one frame with no rows, because the rows were set by an effect after the answer
arrived. The page now takes the answer and makes the rows in one update (`foldNewestLogPage`), so that frame does not
exist. After the fix the page's tests ran 40 times with no failure.

### Each test proved by breaking the code

`python missions\license-switching\phase-3\mutations.py` makes one wrong change at a time, runs the tests, records
which went red, and puts the file back whether or not the run worked. It ends by running both suites on the restored
code. Its last output, from after the review, is `mutations-output.txt`. **31 changes, every one caught.** The
first eighteen are in the table below. The other thirteen came from the review of this pull request, which made
eight wrong changes that left all 21 tests green; `review-3933.md` has them and `review-3933-answers.md` says what
now catches each. The page's 33 tests were then run 20 times with no failure.

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

## The layout check - not from a Gateway

`layout-check/` holds six screenshots and the script that made them, taken before the routes were on main: the
Cockpit's development server with the Licenses routes answered from the example answers in `cockpit-api.md`, no
Gateway running. They are kept for the two cases the real Gateway cannot produce yet (`not-read-and-renewal.png`: a
renewal date that is set and a failed sign-in renewal; `switch-now-refused.png`: a refusal in the Gateway's
sentence). They are a check of the layout and nothing more.

## Still owed, in the follow-up

1. When `POST /gateway/licenses/switch-now` exists: make the client's reading of the accepted answer agree with the
   route (the shapes document describes that answer in words and gives no field names; the client reads
   `targetAccountEmail`, `switches[]` of `switchId`, `machineName`, `osUser`, `state`, `stateText`, `stateTone`,
   `summary`, and `skipped[]` of `machineName`, `why`), and run Switch now against a real Gateway: accepted, refused,
   and with a machine left out.
2. When `PUT /gateway/licenses/accounts/renewal` exists: a control to set the renewal date, if the owner wants it
   on the page.
3. The Add sign-in button, when the Gateway relay exists.
4. From the review: a sentence per target in `switchTargets`, so the confirmation says which machines move when a
   target is picked by hand (today it names the machines only for the Gateway's own choice); and the accepted
   answer's panel calling a machine left out by its `label`, keyed on `machineId`.
