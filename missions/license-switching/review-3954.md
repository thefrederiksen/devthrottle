# Review of pull request 3954 - the sign-ins of the Licenses page

13 October 2026. Branch `license-switching/phase-3-page-sign-ins` at `71548ff2b`, read against `origin/main` at
`a247c66c0` (merge base `0ae87f3e5`). The diff is 54 files: the page, the client, their tests and fixtures, the proof
programs under `missions/license-switching/phase-3/`, 28 screenshots, four recorded answers, and the record
`sign-ins.md`. No C# of the product is touched; the only C# in the diff is the proof program `qa-gateway/`.

**Verdict: nothing blocking. Eleven findings, none of them blocking; one of them is the lost failing test, found and
explained, and it is not in this change.**

Every wrong change was tried in a scratch copy of the branch under the temporary folder (a detached worktree with the
installed packages linked in), never in this worktree. This worktree's tracked files were not edited; the only file
added is this one.

## What was checked, and what held

**The page reads who approves an account and sends the four writes to the routes of section 7, as `application/json`.**
Held, three ways. In the client (`licensesClient.ts`): `GET /gateway/licenses/approvers`; `PUT /gateway/licenses/approvers`
with a body of exactly `accountEmail`, `machineName`, `osUser`, `browserProfile`; `DELETE /gateway/licenses/approvers?accountEmail=...`
with no body and the account escaped in the query; `POST /gateway/licenses/sign-ins/add-spare` and
`POST /gateway/licenses/sign-ins/renew` with a body of exactly `accountEmail` and `machineId`. The writes with a body
share one set of headers that carries `Content-Type: application/json`. In the tests: the client's own 27 and the
page's 29 new ones compare the route, the method, the content type and the whole body string. In the proof run
(`qa-sign-ins/qa-sign-ins-run-output.txt`): every body the browser sent is printed and is one of those two shapes.

**Every refusal shows the Gateway's sentence word for word.** Held. A refusal throws `GatewayError.from`, whose
sentence is the body's `error` field (`stringFromErrorBody` reads `error` first); `gatewayErrorMessage` returns that
sentence unchanged for a status that is not retryable, and a 400, 403, 404 or 409 is not. The page's tests compare
the shown text with `toBe(refusal.error)` for 400 `invalid_browser_profile`, 403 `owner_only` (all four writes),
404 `unknown_machine`, 409 `sign_in_running`, 409 `director_offline` and 409 `already_held`. The proof run shows the
real Gateway's sentences for 400 `invalid_browser_profile`, 400 `invalid_account`, 409 `sign_in_running`,
409 `already_held`, 409 `director_offline` and the three 403 `owner_only` sentences, each one shown whole. The three
owner-only sentences are spelled by hand in the tests, because the document has no example of them; the run shows
the real ones, and they are the same.

**The client decides nothing about what a state means (rule 7).** Held. Every conditional in the three view files was
read. The ones that branch on a state are all on a verdict the Gateway sent: `machine.canAddSpare` (the button),
`holding.canRenewNow` (the button), `approvers.isEmpty` (the sentence in place of the list), `row.reasonText !== null`
(draw the reason when there is one). Every label, status word, tone and sentence is a Gateway field shown as sent:
`addSpareText`, `renewNowText`, `approverText` with `approverTone`, `statusText` with `statusTone`, `text`,
`emptyText`, `signInsInProgress[].text`, `entry.summary`, `entry.reasonText`. The page's own decisions are not
about a state's meaning: Save is shut while one of the four fields is empty, Add a spare is shut while the account
field is empty, the account of an approver being changed is fixed, and the Sign-ins part is not drawn while the
overview is empty. They are listed under the findings below because two of them narrow what the Gateway would
allow.

**No sign-in key in the page, a request or a log.** Held. The client sends only the typed fields and the machine's
identifier; nothing in the diff writes to the console or to browser storage. The error reports the page sends to the
Gateway carry the refusal sentence, the status and the code, which name an account and a machine and nothing else.
The four recorded answers and every answer printed in the run output hold 111 distinct property names between them;
the only ones that match a key-like word are `code` (a refusal's code) and `reasonCode`. The proof program and the
script name no credentials file, no `CLAUDE_CONFIG_DIR`, no `claude auth` and no keychain.

**The tests assert whole text exactly and would fail if the behaviour were wrong.** Held. The page's tests use
`toBe` on the whole text of each sentence, row and body, and `toEqual` on the list of writes. Fourteen wrong changes
of my own, none of them in the Developer's list of 60, were made one at a time in the scratch copy; twelve were
caught and two survived (findings 3 and 4). The twelve caught: the started sign-in's row found by its account instead
of its `entryId`; every approver status pill drawn in the good tone; a word of the page's own before the approver
sentence on the card; Add a spare naming the machine by its name instead of its identifier; the Renew now
confirmation showing the machine's Add a spare sentence instead of the sign-in's; the browser profile sent with the
space around it; a refusal shown with its code in front of the Gateway's sentence (a change in `api/client.ts`,
caught by 22 tests across the page and the client); a sign-in in progress worded by the page; a sign-in started with
one field more than the Gateway reads (caught by the whole-body comparison in both the client and the page); the
page not reading again after Renew now starts; the Add a spare account carried from one machine to the next; and a
refused approver shown in the page's own words. The Developer's own record of 121 wrong changes ends
"0 left every test green" (`mutations-output.txt`, line 335).

**The proof in `sign-ins.md` covers what it claims.** Held. Every row of its table has its lines in the run output
and its screenshot in the folder; the run's last line is "QA RUN DONE: all 91 checks held". The claims that matter
were checked against the output: the confirmations send nothing until confirmed; the sign-in was started once and
the outcome was read from the log row; the Gateway withdrew Renew now while the renewal ran and offered it again
after; the page sent "not an account" as typed and the Gateway refused it; the Director's lost connection made the
real route refuse `director_offline` after the page had offered the button. The record's "Not proved" list is
honest: no real sign-in, no approval relay, no `unknown` row, four refusals never produced, one caller only. The test
counts in the record match a run of the Licenses tests in the scratch copy: 77 on the page, 27 in the client. What
was committed after the gate commit `9ed4ce7ed` is 33 files, all under `missions/` (the record, the script, the
screenshots): nothing the gate builds or tests.

**Nothing private in the diff or the screenshots.** Held as far as looked. A search of the whole text diff for the
owner's name, machine name, mail domains, user folder and repository path found nothing; every account is at
`example.com` and the machines are "the-mac", "the-windows-machine" and "the-laptop". Five of the 28 screenshots were
opened (`sign-ins-before`, `approver-refused-400`, `renew-now-failed`, `add-spare-refused-owner-only`,
`sign-ins-in-the-log`): the sidebar shows "Account 1", and no real name, machine or address is on them. The other 23
were not opened; they are frames of the same page in the same run, and the run output that produced them names the
same things.

**No character outside plain ASCII** in any added line of the code, the tests or the markdown.

## The whole Cockpit suite, seven runs, one at a time

Each run was `vitest run` in `apps/cockpit` with its whole output captured to a file in the temporary folder, outside
the worktree (`scratchpad/runs/cockpit-run-1.txt` to `cockpit-run-7.txt`). Nothing else of mine ran while a suite
run was going.

| Run | Result | Wall time |
|---|---|---|
| 1 | 1142 passed, 0 failed | 28 seconds |
| 2 | 1142 passed, 0 failed | 25 seconds |
| 3 | 1142 passed, 0 failed | 29 seconds |
| 4 | 1142 passed, 0 failed | 36 seconds |
| 5 | 1142 passed, 0 failed | 49 seconds |
| 6 | **1141 passed, 1 failed** | 35 seconds |
| 7 | 1142 passed, 0 failed | 36 seconds |

**The failing test is `src/team/teamPage.test.tsx`, "the Team page, per role > Owner: every role but their own is a
dropdown, Remove on everyone but themselves, Invite someone shown", at line 153.** It is not a Licenses test and the
Team page is not touched by this branch. Run alone, the file passed five times out of five.

Why it fails: the test awaits the heading "Members" (line 151) and then reads the page synchronously
(`getByText("3 paid seats, 2 Collaborators (no charge), 1 invitation waiting")`, line 153). But the Team page draws
that heading while it is still loading: the failure's own document dump shows `<h1>Members</h1>` beside
"Loading the team...". So the awaited heading proves nothing about the data having arrived, and when the mocked
answer's render has not been flushed by the time the `findByRole` promise settles, the synchronous read finds the
loading state and throws. That window is narrow, which is why it takes a busy machine to hit it. The same pattern
is in the two tests after it (lines 220 to 221, "Manager", and 235 to 236, "Developer"): the heading is awaited and
the data is read synchronously. The fix is one line in each: await the first piece of data
(`await screen.findByText("3 paid seats, ...")`) instead of, or after, the heading. It belongs on main, not in this
pull request.

## Findings

1. **Not blocking. The Team page test above fails under load, on main, and this change inherits it.** Finding and
   fix as described. It should be fixed on main the same day under the owner's ruling that a test works or is
   removed; the fix does not touch the Licenses page.

2. **Not blocking. The Sign-ins part is not drawn while the overview is empty, so an approver cannot be set before
   any machine has reported.** `LicensesView.tsx` draws the whole Sign-ins part inside the branch that is skipped
   when `data.isEmpty`. The Gateway allows an approver on a machine that has not reported (section 7.2), and
   `GET /gateway/licenses/approvers` answers on an empty account. The record discloses this as a layout choice. It
   costs nothing until a fresh account wants to name an approver before its first Director reports, which is also
   the moment no sign-in could be started anyway. Worth a line in the record or a later change; not a defect of
   this one.

3. **Not blocking. A test gap: whether the approvers list is empty is taken from the Gateway's `isEmpty`, but no test
   pins that.** Changing `approvers.isEmpty` to `approvers.approvers.length === 0` left all 77 page tests green,
   because every fixture's `isEmpty` agrees with its list. The code is right; the test cannot tell. A fixture whose
   `isEmpty` and list disagree would pin it, but the Gateway never sends such an answer, so this is a weak-signal
   note only.

4. **Not blocking. A test gap: Dismiss on a started sign-in's answer is not tested.** Making the Dismiss button do
   nothing left all 77 page tests green. One click and one `queryByTestId("lic-sign-in-answer")` being null would
   close it.

5. **Not blocking. The Add a spare account field stays editable while the request is in flight.** `AddSpareChoice`
   takes no busy flag, so a person can keep typing after pressing Add a spare while "Starting..." shows. What is
   sent was read when the button was pressed, so nothing wrong goes over the wire; the field just does not lock the
   way the shared dialog's buttons do.

6. **Not blocking. A started sign-in's outcome is looked for in the newest page of the log only.** `SignInAnswer`
   finds the row by `entryId` in `log.data.entries`, which is the newest page. If more rows than one page holds
   land after the sign-in starts, the answer keeps showing "Signing in" from the start answer until it is dismissed.
   With one owner and a few machines that is remote; noted so the behaviour is known.

7. **Not blocking. Section 2's and section 5's examples in `cockpit-api.md` lack the nine sign-in fields, and the
   fixture copier fills them in.** The record says so and says the examples should be produced again; the fill is
   held by its own test and the proof run showed the real Gateway wording the filled sentence the same way. The
   right end state is the one the record names: produce the examples again and delete the fill. Owed, not owed here.

8. **Not blocking. The three `owner_only` sentences have no example in the document.** The tests spell them as the
   route does and the run shows them equal. Adding them to section 7 would let the fixture copier hold them like
   every other refusal.

9. **Not blocking. The page keeps Save and Add a spare shut on an empty field, so the Gateway's own refusal of an
   empty field is never seen.** This is the page deciding that an empty field is not worth sending, the same as
   Switch now without a target in the second pull request. Not a decision about a state's meaning; recorded here
   because the brief asked for every page-side conditional to be named.

10. **Not blocking. The proof program compiles two files of the Gateway's test project into itself**
    (`FakeTunnelDirector.cs`, `DeadPortReservation.cs` by path in `QaGateway.csproj`). A rename in the test project
    breaks the proof program's build. It is a proof program under `missions/`, not the product, and the coupling is
    said in a comment; acceptable.

11. **Not blocking. The first run of the Developer's mutation script left two wrong changes green until the account
    fields were changed from email fields to text fields.** This is in the record and is the right fix: the browser
    was trimming the field, not the code. Noted as confirmed, not as a defect.

## What this review did not do

- It did not run `scripts/test-local.ps1`; the record's green run at `9ed4ce7ed` was checked only by confirming that
  nothing the gate builds or tests was committed after it.
- It did not run the proof run `qa_sign_ins_run.py` again; its output and screenshots were read and checked against
  the record's claims.
- It opened five of the 28 screenshots.
- It did not open a browser against the page.
