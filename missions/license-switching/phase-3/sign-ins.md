# Phase 3, third pull request - the sign-ins of the Licenses page work against the real Gateway routes

13 October 2026, branch `license-switching/phase-3-page-sign-ins`, rebased onto `origin/main` at `0ae87f3e5` (main
after `d9ca4957a`, the commit that merged the Gateway's sign-in routes, pull request 3943). `README.md` beside this
file is the record of the first pull request (the page and its three reads), `writes.md` of the second (Switch now and
the renewal date); this file is the record of the third.

## What changed

The page now reads one more route and sends four more writes, all of `cockpit-api.md` section 7:

- **Who approves an account.** `GET /gateway/licenses/approvers` is read with the other three and shown under a new
  **Sign-ins** heading, between the schedule and the switch log: one row per approver with the Gateway's label for
  the machine, the browser profile, the status word in its tone, and the Gateway's sentence. With none set, the
  Gateway's own sentence is shown in place of the list. Each account card also shows the Gateway's `approverText` in
  its `approverTone`.
- **Set an approver** sends `PUT /gateway/licenses/approvers` as `application/json` with exactly the four fields the
  Gateway reads (`accountEmail`, `machineName`, `osUser`, `browserProfile`). **Change** on a row opens the same
  editor on what is stored, with its account fixed. **Remove** asks first, then sends
  `DELETE /gateway/licenses/approvers?accountEmail=...` with no body.
- **Add a spare** is on each machine's row of the Sign-ins table, where the Gateway offers it (`canAddSpare`), with
  the Gateway's sentence (`addSpareText`) shown whether it is offered or not. It asks for the account in a
  confirmation and sends `POST /gateway/licenses/sign-ins/add-spare` as `application/json` with the account and the
  machine's `machineId`.
- **Renew now** is on each sign-in's line of an account card, where the Gateway offers it (`canRenewNow`), with the
  Gateway's sentence (`renewNowText`) shown whether it is offered or not. It asks first, saying what it does in that
  sentence, and sends `POST /gateway/licenses/sign-ins/renew` with the same body.
- **A started sign-in** is shown in the Gateway's sentence (`text`) and its row of the log (`entry`). The route answers
  at once and the sign-in takes minutes: how it ended is read from that same row of the switch log, found by its
  `entryId`, with its reason when it failed. The page never starts the same sign-in again to find out.
- **A sign-in that has not ended** is shown on its machine's row in the Gateway's sentence (`signInsInProgress`).
- **A refusal is shown in the Gateway's sentence, word for word**: inside the confirmation, which stays open, for Add
  a spare, Renew now and Remove; inside the editor, which stays open with what was typed, for an approver.

Files: `packages/client-core/src/licenses/licensesClient.ts` (the types and the five calls),
`apps/cockpit/src/licenses/` (a new `LicenseSignIns.tsx`, the view, the card, the stylesheet, the fixtures and the
tests), the proof programs under this folder. No C# of the product is touched.

## The page still decides nothing

`CLAUDE.md` rule 7. Whether Add a spare or Renew now is offered, every sentence, every status word and every tone is
the Gateway's (`LicenseSignInFold`) and is shown as sent. Section 7 gave the page everything it needed to stay that
way; nothing was missing and nothing was decided in the view. What the page does itself in this change:

- keeps a confirmation shut while its account field is empty, and Save shut while one of the four approver fields is;
- sends what was typed less the space around it - whether it is an account, a machine or a browser profile is the
  Gateway's to say, and its refusal is shown (`400 invalid_account` and `400 invalid_browser_profile` in the run);
- looks a log row up by its `entryId`, and a machine by its `machineId`;
- after an approver is set or removed, shows the Gateway's sentence for it and reads the approvers and the overview
  again. It keeps no list of its own: a test holds that no row appears before the Gateway's next answer;
- offers the accounts and machines of the overview as suggestions in the approver editor, and as nothing more: any
  name may be typed, because the Gateway takes an approver on a machine that has not reported yet.

Choices of layout, not of meaning, that a reader may want to change: approvers are edited in one list under Sign-ins
and not on each card, because an approver can be set for an account no machine holds yet (the account of a spare
about to be added has no card); and the Sign-ins part is not drawn while the overview is empty (no machine has ever
reported), where the Gateway's one sentence stands in place of every list.

**No sign-in key is in the page, a request or a log.** The page names an account and a machine and nothing else. The
run holds this two ways: every body the browser sent is one of the two shapes above, field for field; and of the 120
different property names in the 60-odd answers of Licenses routes that reached the browser, none is the name of a
key, of an address a sign-in is approved at, or of an approval.

## The proof run - the real page against a real local Gateway

    python missions\license-switching\phase-3\qa_sign_ins_run.py      (after npm ci at the repository root)

One command, all in the foreground, everything stopped at the end. Its whole output, with every request the browser
sent and every answer the Gateway gave, is `qa-sign-ins/qa-sign-ins-run-output.txt`. **91 checks, all held.**

**What the Gateway is.** `qa-gateway/` starts the product's own `GatewayHost`, built from this branch (main at
`0ae87f3e5` plus this change, which touches no C# of the product), in hosted mode on a loopback port the operating
system picks, with its whole storage under a throwaway folder. A new account, "signins", has two machines on account
A, each with a spare for account B. The page runs in a real browser against the Cockpit's development server, which
forwards to that Gateway. **The browser intercepts nothing**: every read and every write goes through the real
authentication, the real routes, the real sign-in service and the real store.

**The Director is a stand-in, and this is what it stands in for.** The sign-in routes refuse a machine whose Director
is not connected, so "the-windows-machine" has a Director on the Gateway's real tunnel: the stand-in the Gateway's own
host tests use (`FakeTunnelDirector`, compiled into the proof program from their source). It makes a real connection
to the real hub, says Hello, sends usage reports and is sent commands. It runs no Claude command, opens no browser and
holds no sign-in. A sign-in command the Gateway sends it is held until the run says how it ends, and is then answered
the way a Director answers a sign-in that worked, or one that failed for want of an approver. So what is real is the
Gateway's whole side of a sign-in - the route, the row of the log, the command over the tunnel, the settling of the
row by the answer - and the page's whole side; what is not real is the sign-in itself. "the-mac" reports through the
ingest and has no Director connected.

**What it is not.** No Director of the product was started, so no build slot was used and nothing of a running
Director was touched. No Claude sign-in of this machine was read, written or renewed, no default login was used, and
no real account exists in it: the accounts are names at `example.com`. The "signed-in person" is a browser device key
the proof program enrolled, as the host tests do - nobody signed in through a login page.

| Write | Case | Screenshot | What the run showed |
|---|---|---|---|
| (the reads) | Before anything is done | `sign-ins-before.png`, `cards-renew-now-offered-and-not.png` | The Gateway's sentence for no approver in place of the list; on each card the Gateway's `approverText`; Add a spare and Renew now offered on the machine whose Director is connected and not on the other, each with the Gateway's sentence whole. Nothing was sent. |
| Set an approver | Stored | `approver-set.png`, `approver-set-on-the-card.png` | `PUT`, `application/json`, `{"accountEmail":"account-b@example.com","machineName":"the-mac","osUser":"owner","browserProfile":"work"}`; `200`. The page shows the Gateway's sentence, reads the approvers again and lists the row ("Not connected", warning); the card of account B shows the new `approverText`. |
| Set an approver | A machine that never reported | `approver-set-machine-never-reported.png` | `200`, `status: never-reported`, "Not reporting yet", and the Gateway's sentence saying no sign-in can be approved there yet. |
| Set an approver | A machine whose Director is connected | `approver-set-ready.png` | `200`, `status: ready`, "Ready" in the good tone, `canApproveNow: true`. |
| Set an approver | **Refused: 400** | `approver-refused-400.png` | A browser profile typed as "work/1". `400 invalid_browser_profile`; the editor shows "browserProfile is the name of a browser profile on the approving machine: letters, digits, spaces, dots, underscores and hyphens, at most 64 characters." word for word and stays open with what was typed. Nothing was stored. |
| Set an approver | Changed from its row | `approver-changed.png` | The editor opens on what is stored with its account fixed; the new browser profile is stored and listed. |
| Remove an approver | The confirmation, then removed | `approver-remove-confirmation.png`, `approver-removed.png` | The confirmation says what is removed in the Gateway's sentence and sends nothing. Then `DELETE /gateway/licenses/approvers?accountEmail=account-e%40example.com`, no body, no content type; `200`, "No machine is set to approve sign-ins for account-e@example.com any more." shown whole; the row is gone at the next read. |
| Add a spare | The confirmation | `add-spare-confirmation.png` | The Gateway's sentence for the machine; shut until an account is typed; opening it sent nothing. |
| Add a spare | Started | `add-spare-started.png`, `sign-in-in-progress.png` | `POST`, `application/json`, `{"accountEmail":"account-e@example.com","machineId":"..."}`; `202`. The page shows the Gateway's sentence and the row "Signing in"; the machine's row shows the Gateway's sentence for the sign-in that has not ended. |
| Add a spare | Ended: signed in | `add-spare-signed-in.png`, `add-spare-new-account-card.png` | The Director answered; the Gateway settled the row `added`; the page read "Signed in" and its sentence from that row of the log at its next read. No second call was sent to find out. When the machine's next report held the account, its card appeared. |
| Add a spare | **Refused: 409 `sign_in_running`** | `add-spare-refused-409-sign-in-running.png` | The same sign-in asked for again while it ran: "A sign-in for account-e@example.com on the-windows-machine is already running." in the confirmation, which stays open with what was typed. |
| Add a spare | **Refused: 409 `already_held`** | `add-spare-refused-409-already-held.png` | "the-windows-machine already holds a sign-in for account-a@example.com. Use Renew now to renew it." |
| Add a spare | **Refused: 400 `invalid_account`** | `add-spare-refused-400-invalid-account.png` | "not an account" was sent as typed and the Gateway judged it: "accountEmail is the email of a Claude account." |
| Renew now | The confirmation, then started | `renew-now-confirmation.png`, `renew-now-started.png` | The confirmation says what it does in the Gateway's `renewNowText`. `POST`, `application/json`, the account and the machine's identifier; `202`. While it runs the Gateway withdraws Renew now for that sign-in and the page follows. |
| Renew now | Ended: **failed** | `renew-now-failed.png` | The Director answered failed for want of an approver. The page read "Failed", the Gateway's sentence and its reason ("No browser profile is set to approve this account, on the machine or on another.") from that row of the log, in the bad tone. |
| Renew now | Ended: renewed | `renew-now-renewed.png`, `sign-ins-in-the-log.png` | Asked again; a row of its own; "Renewed". The switch log on the page lists the three sign-ins: one added, one failed, one renewed. |
| Renew now | **Refused: 409 `director_offline`** | `renew-now-refused-409-director-offline.png`, `sign-ins-not-offered-director-not-connected.png` | The page had offered it; the machine's Director then lost its connection; the confirmed call was refused "The Director on the-windows-machine is not connected, so a sign-in cannot be started there." At its next read the page offers no sign-in on that machine and shows that sentence in place of each button. |
| All four | **Refused: 403 `owner_only`** | `approver-set-refused-owner-only.png`, `approver-remove-refused-owner-only.png`, `add-spare-refused-owner-only.png`, `renew-now-refused-owner-only.png` | Sent with a Director's own credential (a workstation device key), which reads the page. Each write is refused in its own sentence - "only the owner on their own signed-in phone or browser may set which machine approves an account's sign-ins; ...", "... may remove the machine that approves an account's sign-ins; ...", "... may start a sign-in on a machine; ..." - shown word for word. Nothing was stored and nothing was started. |

The answers as the Gateway gave them are saved beside the screenshots: `qa-sign-ins/answer-overview.json`,
`answer-approvers-none-set.json`, `answer-approvers.json` and `answer-log-after-sign-ins.json`.

One thing in the screenshots that is the stand-in's and not the product's: the failed renewal gives the reason "no
approver" for account B while the card of account B says an approver is set. The stand-in was told to answer that
reason whatever is set; a real Director would have asked the approver.

## Not proved

- **A sign-in that a machine really carries out.** No Director of the product, no `claude auth login`, no browser
  approval and no real account. That a sign-in started from the page ends with a machine holding a working sign-in is
  phase 4's proof and phase 5's.
- **The approval relay** (section 7.7). The page never sees it, and the stand-in never asks for one.
- **A sign-in that gets no answer**: the state "Sent, no answer yet", a row settled from the machine's report, a row
  ended after a day without one, and an answer replayed. The page draws a log row from the Gateway's words whatever
  its state; none of these states was produced.
- **The refusals the run did not produce**: `404 unknown_machine` (the page sends a `machineId` the overview gave;
  the page's tests draw it from the document's example), `409 too_many_approvers`, `400 invalid_machine`,
  `400 bad_request_body`.
- **Other refused callers.** Only a workstation device key was run in the browser. A session key, the shared machine
  token of a self-hosted Gateway and a request with no credential were not; the Gateway's own host tests hold them.
- **A machine whose label is not its machine name** (two users of one computer) against the real Gateway. The page's
  tests hold the label on the Sign-ins table, the approvers list and the confirmation.
- **A person signed in through the login page, the hosted service, and a phone-width window.** The mobile application
  has no Licenses page, so there is nothing of this for the phone to stay in step with yet.
- **The parked suites and the database tests** were not run: this change touches no C# of the product.

## Tests

**The numbers below are those of the first push. The review round after it added three tests and three wrong changes
and removed one test; its numbers - 1144 in the Cockpit, 79 the page's own, 124 wrong changes, the gate green at
`9d4389ca6`, and the proof run made again - are in `../review-3954-answer.md`. The Cockpit's failing test that this
section could not name was found by the review: `src/team/teamPage.test.tsx`, on main, fixed in its own pull request.**

Run on 13 October 2026 on the Windows machine, one at a time, in the foreground, on the tree rebased onto main:

- `apps/cockpit` (`npx vitest run`): 109 files, **1142 passed, 0 failed**. The page's own are 77: 21 in
  `LicensesView.test.tsx`, 12 in `LicensesViewRows.test.tsx`, 13 in `LicensesViewWrites.test.tsx`, 29 new ones in
  `LicensesViewSignIns.test.tsx` and the 2 of `fixtures.test.ts`.
- `packages/client-core` (`npx vitest run`): 163 files, **2028 passed, 0 failed**. The client's own are 27, 12 of
  them new.
- `npm run typecheck` (every workspace): clean. `npx eslint` over the two changed folders: clean.
- `.\scripts\test-local.ps1`: **GREEN** at `9ed4ce7ed`, 10 suites, 4,463 tests, 0 failed. It runs no web tests, so it
  says nothing about this change beyond "the product still builds and its default suites pass". It names the three
  parked suites as a coverage gap only because it cannot classify the files under `missions/`. What was committed
  after that commit is this record, the last screenshots and one more case in the proof run's script: nothing the
  gate builds or tests.

**One run of the whole Cockpit suite reported 1 failed of 1142, and which test it was is not known.** It was the first
run after the rebase, taken while the machine was busy (77 seconds against the usual 30), and its output was cut to
its last lines, so the name was lost. It did not come back: the whole suite then ran 13 more times, one after the
other, each 1142 passed and 0 failed, and the page's own 77 tests ran 60 times with no failure. The rebase brought no
web file. So there is a test in the Cockpit suite that can fail on a busy machine, it has not been found, and nothing
here shows whether it is one of the 29 new ones.

The first run of the local gate was red on one test that is not about this change:
`GateTestsDoNotDependOnTheMachineTests` named four test files of another piece of work that read the clock. Main was
red on it at the commit this branch was cut from; pull request 3951 fixed it on main, and the run above is on the
tree rebased onto that.

`LicensesViewSignIns.test.tsx` replaces nothing of the page or of its client - only the browser's `fetch`. So it sees
what leaves the browser (route, method, content type, body) and what a person reads.

### The fixtures

`fixtures.ts` is written by `copy_fixtures.py` from the Gateway's own examples in `cockpit-api.md`; it holds 27 of
the document's 31 examples, and `fixtures.test.ts` holds each to the document. Every one is a copy and nothing else.

When this pull request was first pushed, one part was not: the document's full overview (section 2) had been
produced before the sign-in routes existed, so the script filled its nine sign-in fields in. After the review the
example was produced again by the Gateway's own route handler, with the fields, and the fill was deleted; the three
`403 owner_only` sentences, which had no example, were added to section 7 from the proof run. How, and what was
checked, is in `../review-3954-answer.md`, findings 7 and 8.

### Each test proved by breaking the code

`python missions\license-switching\phase-3\mutations.py`; its output is `mutations-output.txt`. It makes one wrong
change at a time, runs the Licenses tests, records which went red and puts the file back. **121 wrong changes, every
one caught**: the 61 of the first two pull requests, all still caught, and the 60 below.

| Wrong change | Tests that went red |
|---|---|
| Add a spare is offered on every machine | 1 |
| Renew now is offered on every sign-in | 1 |
| The Gateway's Add a spare sentence is not drawn on the machine's row | 1 |
| The Gateway's Renew now sentence is not drawn on the sign-in | 1 |
| Renew now sends without asking | 7 |
| Add a spare can be confirmed with no account typed | 2 |
| Add a spare is sent with no account (both guards gone) | 2 |
| Add a spare sends the account with the space around it | 1 |
| Add a spare is sent to the route of Renew now | 4 |
| Renew now names the machine by its name, not its identifier | 1 |
| The Gateway's sentence for a started sign-in is not drawn | 3 |
| The page puts the machine's label before the Gateway's row sentence of a sign-in | 2 |
| How a sign-in ended is never read from the log | 1 |
| The reason of a failed sign-in is not drawn | 1 |
| The approver sentence on a card loses the Gateway's tone | 1 |
| The approver sentence on a card is not drawn | 1 |
| The page words "no approver is set" itself | 2 |
| An approver's status word is shown in place of its label | 1 |
| An approving machine is called by its machine name | 1 |
| An approver's sentence is not drawn | 1 |
| The approvers are not read again after one is set | 1 |
| The approvers are not read again after one is removed | 1 |
| The page words the stored approver itself | 3 |
| The page words the removal itself | 1 |
| A refused approver is shown in the page's own words | 2 |
| A refused approver closes the editor as if it were stored | 2 |
| An approver can be saved with a field left empty | 1 |
| The approver's account is sent with the space around it | 1 |
| Changing an approver lets its account be changed | 1 |
| Remove sends without asking | 3 |
| The account typed for one machine is carried to the next | 1 |
| The sentence of the answer before stays beside a refused removal | 1 |
| A failed read of the approvers shows nothing at all | 1 |
| A sign-in that has not ended is not drawn on its machine's row | 1 |
| A machine is called by its machine name in the Sign-ins table | 1 |
| A machine is called by its machine name in the Add a spare confirmation | 1 |
| The Renew now confirmation says what it does in the page's own words | 1 |
| The removal confirmation says what is removed in the page's own words | 1 |
| The Add a spare confirmation drops the Gateway's sentence for the machine | 1 |
| A sign-in field of the overview fixture is edited by hand | 1 |
| A sign-in fixture is edited by hand and no longer the document's example | 1 |
| The approver is sent without the machine's user | 1 |
| The approver is sent without the machine's user - seen from the page's tests | 2 |
| The removal names its account without escaping it | 1 |
| The removal names its account without escaping it - seen from the page's tests | 1 |
| The removal is sent by POST | 1 |
| The removal is sent by POST - seen from the page's tests | 3 |
| Add a spare goes to the route of Renew now | 1 |
| Add a spare goes to the route of Renew now - seen from the page's tests | 4 |
| A sign-in is started without naming its machine | 2 |
| A sign-in is started without naming its machine - seen from the page's tests | 2 |
| A refused sign-in loses the Gateway's sentence | 1 |
| A refused sign-in loses the Gateway's sentence - seen from the page's tests | 7 |
| The client accepts a started sign-in that lost its sentence and its row | 1 |
| The client accepts a stored approver that lost its sentence | 1 |
| The client accepts a removal that lost its sentence | 1 |
| A refused approver loses the Gateway's sentence | 1 |
| A refused approver loses the Gateway's sentence - seen from the page's tests | 2 |
| A refused removal loses the Gateway's sentence | 1 |
| A refused removal loses the Gateway's sentence - seen from the page's tests | 2 |

The first run of it left two of these green: the account sent with the space around it, in both places. The cause was
in the page, not the tests: the two account fields were email fields, and a browser takes the space off an email field
before the page's code sees it, so the code that takes it off was never what did it. They are plain text fields with
the email keyboard now, the code is what does it, and both changes are caught.

## Still owed

- A sentence per target in `switchTargets` (carried from the second pull request).
- Who approves an account cannot be set while the overview is empty: the Sign-ins part is drawn with the machines,
  and an empty overview shows the Gateway's sentence in place of everything (`../review-3954-answer.md`, finding 2).
- The owner's email for a renewal that keeps failing is not part of the page; `cockpit-api.md` says it follows in its
  own pull request.
