# Phase 3, second pull request - Switch now and the renewal date work against the real Gateway routes

13 October 2026, branch `license-switching/phase-3-page-writes`, rebased onto `origin/main` at `5bd850478` (main after `94dcfcc8b`, the commit
that merged the two write routes, pull request 3936). `README.md` beside this file is the record of the first pull
request (the page and its three reads); this file is the record of the second.

## What changed

- **Switch now** sends `POST /gateway/licenses/switch-now` as `application/json` with the account and the target, and
  shows the Gateway's answer: its one sentence for the whole outcome (`text`), then one row per machine sent a switch
  and one line per machine left out, each in the Gateway's own sentence.
- **Nothing is sent without a target.** Where the Gateway chose a target (`switchNowTargetAccountEmail`), that target
  is selected and is the one sent. Where it chose none, nothing is selected, the confirmation cannot be given, and no
  call leaves the browser until the person picks one of the `switchTargets` that can be chosen; that one is then sent.
- **A switch that had not finished is read from the switch log.** The answer's row for a machine is replaced by that
  machine's row of the log (found by `entryId`) once the log on screen holds it. The page never sends the same Switch
  now again to find out.
- **The renewal date of a subscription** can be set and cleared on each card ("Set renewal date"). It sends
  `PUT /gateway/licenses/accounts/renewal` as `application/json` with the date as `yyyy-MM-dd`, or an explicit
  `"renewsOn": null` to clear it. What the card then shows is the `renewsText` of the Gateway's answer.
- **A refusal is shown in the Gateway's sentence, word for word**: `403 owner_only` and `409` on Switch now inside the
  confirmation, which stays open; `403 owner_only` on the renewal date beside the date field, which stays open.
- **The two new last fields of an account**, `barredText` and `barredUntilUtc`, are shown on the card when the Gateway
  sends them: the sentence, and the time in the viewer's own time zone. The Gateway sends no tone for them, so they
  are drawn like their neighbours.
- **A stored failed renewal of a sign-in** was already drawn (first pull request); here it is shown from a real
  Gateway for the first time.
- The client waits 90 seconds for Switch now, because the Gateway itself waits up to 60 for the machines. The other
  reads keep their 10 seconds.
- `ConfirmDialog`, the Cockpit's one confirmation, gained `confirmDisabled` (default false), so a confirmation that
  needs a choice in its body can stay shut until the choice is made. No other caller passes it.

The page still decides nothing (`CLAUDE.md` rule 7). What it does itself in this change: keeps the confirmation shut
while no target is picked, keeps Save shut while the date field is empty, offers "Clear date" only when a date is
stored, and looks a log row up by its `entryId`.

Files: `packages/client-core/src/licenses/licensesClient.ts`, `apps/cockpit/src/licenses/` (the view, the card, the
parts, the stylesheet, the fixtures and the tests), `apps/cockpit/src/components/ConfirmDialog.tsx`. No C# of the
product is touched.

## The proof run - the real page against a real local Gateway

    python missions\license-switching\phase-3\qa_writes_run.py      (after npm ci at the repository root)

One command, all in the foreground, everything stopped at the end. Its whole output, with every request the browser
sent and every answer the Gateway gave, is `qa-writes/qa-writes-run-output.txt`. **45 checks, all held.**

**What the Gateway is.** `qa-gateway/` starts the product's own `GatewayHost`, built from this branch (main at
`5bd850478` plus this change, which touches no C# of the product), in hosted mode on a loopback port the operating
system picks, with its whole storage under a throwaway folder. It enrolls four accounts the way the Gateway's own
host tests do. Usage reports of two machines go in through the Gateway's own ingest. The page then runs in a real
browser against the Cockpit's development server, which forwards to that Gateway. **The browser intercepts nothing**:
both writes go through the real authentication, the real routes, the real decision engine and the real store.

**What it is not.** No Director was started, so no build slot was used and nothing of a running Director was touched.
No Claude sign-in of this machine was read, written or switched: the Gateway holds no sign-in key, and the reports are
made up by the QA program (emails at `example.com`, numbers and times). The "signed-in person" is a browser device key
the QA program enrolled, as the host tests do - nobody signed in through a login page.

| Write | Case | Screenshot | What the run showed |
|---|---|---|---|
| Renewal date | Set, by the owner's browser | `qa-writes/renewal-date-set.png` | `PUT` sent as `application/json` with `{"accountEmail":"account-a@example.com","renewsOn":"2026-11-02"}`; answered `200` with `"renewsText":"Renews 2 Nov"`; the card shows "Max - Renews 2 Nov"; the next read of the overview carries the date. |
| Renewal date | Cleared | `qa-writes/renewal-date-cleared.png` | The body was `{"accountEmail":"account-a@example.com","renewsOn":null}`; answered `200`, "Renewal date not set". |
| Renewal date | **Failure: 403 `owner_only`** | `qa-writes/renewal-date-refused-owner-only.png` | Sent with a Director's own credential (a workstation device key). The Gateway's sentence is on the card word for word: "only the owner on their own signed-in phone or browser may set the renewal date of a subscription; this request was made with a device (workstation) credential". The date is unchanged and the editor stays open. |
| Switch now | The confirmation | `qa-writes/switch-now-confirmation.png` | The Gateway's sentence, its own choice selected. Opening it sent nothing. |
| Switch now | Accepted | `qa-writes/switch-now-accepted.png`, `qa-writes/switch-now-accepted-log.png` | One `POST`, `application/json`, `{"accountEmail":"account-a@example.com","targetAccountEmail":"account-b@example.com"}`; answered `200`. The page shows the Gateway's sentence ("The switch of the-mac and the-windows-machine to account-b@example.com has not finished; the switch log shows how it ends.") and a row per machine, "Waiting for the machine". Both `entryId`s of the answer are rows of the switch log, and the log on the page lists them. The call was sent once. |
| Switch now | **Failure: 403 `owner_only`** | `qa-writes/switch-now-refused-owner-only.png` | With the workstation key. The confirmation stays open and shows the Gateway's sentence word for word; no answer panel is drawn. |
| Switch now | **Failure: 409** | `qa-writes/switch-now-refused-409.png` | The page had read that account B has room and offered it; the machines then reported B at 99%; the confirmed call was refused `409 target-full` and the confirmation shows "account-b@example.com has no room: it is at 99% of its 5-hour window." |
| Switch now | **No target chosen by the Gateway** | `qa-writes/switch-now-no-target-nothing-sent.png` | The Gateway answered `canSwitchNow: true`, `switchNowTargetAccountEmail: null`. Nothing is selected, the confirmation is shut, and pressing it sent nothing: no Switch now left the browser. |
| Switch now | A target picked by hand | `qa-writes/switch-now-target-picked-by-hand.png` | With account B picked, exactly one call was sent, naming B; the Gateway accepted it - a barred account can be chosen by hand. |
| An account | Barred | `qa-writes/barred-account.png` | The Gateway's `barredText` on the card of account B, with the time of `barredUntilUtc`; no other card says it. |
| A sign-in | A stored failed renewal | `qa-writes/failed-renewal-stored.png` | A machine reported that renewing its sign-in for account B failed. The Gateway stored it and answered "Renewing this sign-in failed 3 hours ago: no browser is set to approve this account." in tone `bad`; the card shows exactly that, on that machine's line only. |

The answers as the Gateway gave them are saved beside the screenshots: `qa-writes/answer-overview.json` (the failed
renewal), `qa-writes/answer-overview-barred.json` (the barred account and the account with no target chosen) and
`qa-writes/answer-log-after-switch-now.json`.

## Not proved

- **A switch that a machine carries out.** No Director is connected to the proof Gateway, so every accepted switch
  stayed "Waiting for the machine". A machine that switches, one that fails, and a machine left out (`skipped`) were
  never produced by the real Gateway here; the page's tests draw them from the shapes document's sentences.
- **The answer's row following the log to its end** (waiting, then switched) is in the page's tests only.
- **The 60-second wait.** No call took longer than a moment. That the client waits 90 seconds is held by a test of
  the number, not by a slow call.
- **Other refused callers.** Only a workstation device key was run in the browser. A session key, the shared machine
  token of a self-hosted Gateway and a request with no credential were not; the Gateway's own host tests cover them.
- **A person signed in through the login page, the hosted service, a real Director and a real Claude account.**
- **The renewal date's other refusals** (`400`, `404`): the page cannot produce them - the date field gives a real
  date or nothing, and the account is one the Gateway listed.
- **The renewal sentences for today, another year and a date that has passed.**
- **A phone-width window.** The mobile application has no Licenses page.
- **The parked suites and the database tests** were not run: this change touches no C# of the product.

## Tests

Run on 13 October 2026 on the Windows machine, one at a time, in the foreground:

- `apps/cockpit` (`npx vitest run`): 108 files, **1112 passed, 0 failed**. The page's own are 47: 21 in
  `LicensesView.test.tsx`, 12 in `LicensesViewRows.test.tsx`, 13 new ones in `LicensesViewWrites.test.tsx` and the one
  of `fixtures.test.ts`, which holds every fixture to its example in the shapes document. Two more are the shared
  confirmation's own (`components/ConfirmDialog.test.tsx`). These counts are from after the review of this pull
  request; `../review-3944-answer.md` answers each of its findings.
- `packages/client-core` (`npx vitest run`): 163 files, **2016 passed, 0 failed**. The client's own are 15, 6 of
  them new.
- `npm run typecheck` (all four workspaces): clean. `npx eslint` over the changed folders: clean.
- `.\scripts\test-local.ps1`: **GREEN**, 10 suites, 4,562 tests, 0 failed. It runs no web tests, so it says nothing
  about this change beyond "the product still builds and its default suites pass". It names the three parked suites
  as a coverage gap only because it cannot classify the files under `missions/`; `.\scripts\select-tests.ps1 -Explain`
  lists every product file of this change as "web workspace".

`LicensesViewWrites.test.tsx` replaces nothing of the page or of its client - only the browser's `fetch`. So it sees
what leaves the browser (route, method, content type, body) and what a person reads, worded by the same code that
words it in the Cockpit. Its answers are the Gateway's own examples: `fixtures.ts` is now written by
`copy_fixtures.py` from `cockpit-api.md` and is not edited by hand.

One test of the first pull request was changed, because it held the opposite of the rule: "selects nothing, sends a
null target on confirmation" is now "selects nothing and never calls Switch now with no target".

### Each test proved by breaking the code

`python missions\license-switching\phase-3\mutations.py`; its output is `mutations-output.txt`. It makes one wrong
change at a time, runs the Licenses tests, records which went red and puts the file back. **61 wrong changes, every
one caught**: the 31 of the first pull request, all still caught, the 26 below, and 4 from the review of this pull
request - its two that had left every test green, and one each for the two tests it asked for:

| Wrong change | Tests that went red |
|---|---|
| Switch now can be confirmed where the Gateway chose no target and none is picked | 2 |
| The shared dialog ignores that the confirmation cannot be given yet | 2 |
| Switch now is sent with no target where the Gateway chose none (both guards gone) | 2 |
| The saved renewal date is not shown from the Gateway's answer | 2 |
| The page words the saved renewal date itself | 2 |
| A saved renewal date is drawn on every account | 1 |
| Clear date sends an empty date in place of null | 1 |
| A renewal date can be saved before one is given | 1 |
| A refused renewal date is shown in the page's own words | 1 |
| A refused renewal date closes the editor as if it were stored | 1 |
| The barred sentence is not drawn | 1 |
| The time an account is barred until is not drawn | 1 |
| The page colours the barred sentence by itself | 1 |
| The Gateway's sentence for the outcome of Switch now is not drawn | 2 |
| The page puts a name of its own before a left-out machine's sentence | 2 |
| How a switch ended is never read from the switch log | 1 |
| A failed renewal is drawn on every sign-in of the card | 2 |
| The writes lose their content type (client tests) | 2 |
| The writes lose their content type (page tests) | 2 |
| Clearing the renewal date leaves `renewsOn` out of the body (client tests) | 1 |
| Clearing the renewal date leaves `renewsOn` out of the body (page tests) | 1 |
| The renewal date is sent by POST | 1 |
| The client accepts a stored answer that lost its sentence | 1 |
| The client gives up on Switch now before the Gateway's own wait ends | 1 |
| A refused renewal date loses the Gateway's sentence | 1 |
| A refused Switch now loses the Gateway's sentence | 2 |

Known and not hidden: the page has two guards against sending Switch now with no target (the shut confirmation, and
a check in the handler). Removing the handler's check alone leaves every test green, because the shut confirmation
still stops the call. Removing the shut confirmation alone, or both, is caught.

## The first pull request's run

`qa_run.py` (the three reads) used to confirm Switch now and record the `404` of a route that did not exist. The
route exists now, so that run opens the confirmation, takes its screenshot and cancels. Its folder `qa/` was not run
again and is as the first pull request left it; `qa/switch-now-no-route-yet.png` is history.

## Still owed

- The Add sign-in button, when the Gateway relay exists.
- A sentence per target in `switchTargets`, so the confirmation says which machines move when a target is picked by
  hand. Today the Gateway's sentence describes only its own choice, and the page shows it only while that choice is
  the one selected.
