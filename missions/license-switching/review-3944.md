# Review of pull request 3944 - the two writes of the Licenses page

13 October 2026. Branch `license-switching/phase-3-page-writes` at `5a86c7d5d`, which sits exactly on the head of
`origin/main` (`5bd850478`); the working tree was clean. The review read the mission document (section 5, its
traps), `cockpit-api.md` section 6, `phase-3/writes.md`, and the whole diff against `origin/main`. Nothing in this
worktree was edited; every mutation was made in a separate detached worktree under the scratch folder and that
worktree was removed afterwards.

## Verdict

**Not blocking.** The change does what the record says it does, the proof covers what it claims, nothing private is
in the diff, and every suite is green. Two assertions use "contains" where the rule is "word for word", and two
out-of-tree mutations proved that they let a page add words of its own; that is a weakness of the tests, not of the
page, and it is recommended to fix before or soon after merging.

## What was checked and held

**The two writes go to the right routes with the right body.**

- Switch now sends `POST /gateway/licenses/switch-now` with `Content-Type: application/json` and the body
  `{"accountEmail", "targetAccountEmail"}` (`licensesClient.ts`, `switchLicenseNow`). The client test and the page
  test both read the content type and the body off the browser's `fetch`; the proof run recorded three real calls
  with exactly that shape.
- The renewal date sends `PUT /gateway/licenses/accounts/renewal` as `application/json`, with the date as
  `yyyy-MM-dd` or an explicit `"renewsOn": null` (`setLicenseRenewalDate`). The clear test pins the raw body text,
  so a body that leaves the field out cannot pass. The proof run recorded both the set and the clear.
- Both writes share one header builder (`jsonWriteHeaders`), so neither can lose the content type on its own.

**Nothing is sent when `switchNowTargetAccountEmail` is null.** Two guards: the confirmation button is disabled
through the new `confirmDisabled` of `ConfirmDialog`, and the handler `confirmSwitchNow` returns before calling the
client when `target` is null. The target starts as the Gateway's choice and changes only when the person picks a
radio. The page test and the proof run (a forced click on the disabled button, then no request seen leaving the
browser) prove the button guard; the record says plainly that the handler guard alone is not pinned by a test.
That is accepted: the button guard is the one a person meets, and removing both is caught.

**A `403 owner_only` and a `409` are shown in the Gateway's sentence, word for word.** The client throws
`GatewayError.from`, which reads the `error` string out of the body; the shown sentence comes from
`gatewayErrorMessage`, which returns the server's reason unchanged for a 4xx (a 4xx is not retryable, so no
"Try again" is appended). The dialog shows it in `.ui-confirm-error`; the renewal editor beside the date field.
The page tests assert exact equality for both statuses on both writes, and the proof run showed the exact sentence
in each of the three refused cases (the renewal date and Switch now with a workstation key, and Switch now
refused `409 target-full` after the target filled up).

**`barredUntilUtc`, `barredText` and a stored failed renewal are shown as given.** The card draws `barredText`
only when it is not null, with no tone class of its own, and the time through `LocalTime`, which renders the
Gateway's instant in the viewer's time zone and keeps the original value in the `datetime` attribute (the tests
and the run check that attribute). The failed renewal line was already drawn in the first pull request and is
shown here from a real Gateway for the first time; its tone is the Gateway's `renewalFailedTone`. No sentence,
colour or verdict is worked out on the page (rule 7). What the page does decide is only the state of its own
controls on the presence of data: the confirmation stays shut without a target, Save stays shut with an empty
field, Clear date is offered only when a date is stored. The record says so.

**The settings and shared-component rules.** No settings page is touched. The Licenses page is a Cockpit page by
the mission's design; the mobile application has no Licenses page and references nothing of it (checked by
search). The client lives in `packages/client-core`, where both shells could reach it. The `ConfirmDialog` change
is additive with a default of false and no other caller passes it.

**The tests would fail if the behaviour were wrong - mostly.** The Developer's `mutations-output.txt` lists 57
wrong changes, each with the tests it turned red, and a baseline of 0 red after restoring. My own out-of-tree
mutations, in a detached scratch worktree with the root `node_modules` joined in:

| Wrong change | Result |
|---|---|
| Control: the barred sentence is not drawn (the Developer's W11) | 1 red of 46 - so the scratch files were the ones under test |
| The page puts a word of its own before the barred sentence (`Barred: {account.barredText}`) | **46 green - not caught** |
| The page puts the machine's label before the Gateway's row sentence in the Switch now answer (`{machine.label}: {row.summary}`) | **46 green - not caught** |

See finding 1.

**The suites and the counts in the record.** Run here, one at a time, in the foreground:

| Run | Result |
|---|---|
| `apps/cockpit`, the three Licenses test files | 46 passed |
| `packages/client-core`, the licenses client tests | 15 passed |
| `apps/cockpit`, the whole suite | 106 files, 1109 passed, 0 failed (matches the record) |
| `packages/client-core`, the whole suite | 163 files, 2016 passed, 0 failed (matches the record) |
| `npm run typecheck` (all four workspaces) | clean |
| `npx eslint` over the two licenses folders and `ConfirmDialog.tsx` | clean |

**The fixtures really are the Gateway's own examples.** `copy_fixtures.py` was run from the scratch folder with
its output redirected there; the file it wrote is byte-for-byte identical to the committed `fixtures.ts`.

**The proof in `writes.md` covers what it claims.** The saved output holds 45 `[OK]` lines and no `[FAIL]`; every
row of the record's table is in that output with the request and the answer as the Gateway gave them; the saved
answer bodies carry the barred fields and the failed renewal as described. The "Not proved" list is honest, and
the one point worth adding (the no-target check proves the disabled button, not the handler) is already stated
under "Known and not hidden".

**Nothing private is in the diff.** Every added line was swept for the owner's names, mail domains, machine names,
home paths, addresses and repository paths; every email address on an added line is at `example.com` or
`qa.example.com`; the machine names are `the-mac`, `the-windows-machine`, `the-linux-machine` and `machine-of-one`
with users `owner` and `second`. All eleven screenshots were opened and show only those made-up accounts and
machines, the Cockpit's own shell with its generic "Account 1" label, and no window title, address bar or
bookmark. The run output names only a loopback port.

## Findings

### 1. Two "word for word" assertions only check containment - not blocking, fix recommended

`LicensesViewWrites.test.tsx` asserts the barred sentence with `toContain(BARRED_FIELDS.barredText)` and the
machine row of the Switch now answer with `toContain(stateText)` and `toContain(summary)`; the proof run's checks
for the same two things (`barredText in line.inner_text()`, `machine["summary"] in rows.nth(index).inner_text()`)
are containment too. A page that put a prefix of its own before either sentence would pass all of them, and the
two mutations above show it does. The skipped-machine row and the outcome sentence, by contrast, are pinned with
exact equality, which is why the Developer's W14 and W15 are caught. Recommended: assert the whole text of the
barred line equals the sentence followed by the time part, and the whole text of a machine row equals the pill's
text, a space and the summary. Then the page cannot add a word anywhere in the answer panel or on the barred line.

### 2. Nothing guards that `fixtures.ts` still matches the shapes document - not blocking

The record says the fixtures are written by `copy_fixtures.py` and not edited by hand, and today that is true (the
regenerated file is identical). Nothing runs the script or compares the two, so the next edit of `cockpit-api.md`
leaves the fixtures stale without any test noticing, and the sentence "a test that draws these draws what the
Gateway sends" quietly stops being true. A small check (a test that re-extracts the examples and compares, or a
step in the gate) would keep the claim honest.

### 3. The 403 fixture for Switch now carries the renewal-date sentence - not blocking, cosmetic

`OWNER_ONLY` is the document's example for the renewal date ("may set the renewal date of a subscription") and the
page test for a refused Switch now serves that same sentence. The test still proves word for word, and the proof
run shows the real route answers "may switch the machines of a subscription to another one". The fixture's
comment "Either write" is therefore slightly off; a second example in the document, or a note, would settle it.

### 4. The open confirmation is a snapshot - not blocking, for the record

`confirming` is the account as it was when the dialog opened. A 30-second poll that changes `switchTargets` or
`switchNowTargetAccountEmail` while the dialog is open is not reflected in it. The Gateway then refuses the stale
choice with a `409` and the page shows that sentence, which the proof run demonstrated with `target-full`, so the
outcome is correct; it is only worth knowing when reading the page.

### 5. The answer panel reads only the newest page of the log - not blocking, edge case

`SwitchNowAnswer` looks the machine's row up in `log.data.entries`, which is the newest page (plus any older rows
the person walked to). A switch that has dropped off the newest page by the time it ends keeps the answer's own
"Waiting for the machine" row. With one page of rows and a switch made moments ago that is far-fetched, and
dismissing the panel clears it; noted so it is not mistaken for a defect later.

### 6. `confirmDisabled` has no test of its own - not blocking

The new `ConfirmDialog` property is covered only through the Licenses page (the Developer's W2 goes red through
the page tests). A one-line case in the dialog's own tests would keep it covered when the Licenses page changes.

## Not reviewed here

The Gateway's side of the two routes (pull request 3936) and the first Licenses page pull request (3933) were
reviewed in their own sessions; this review takes their records as read. The mobile application was not run,
because it has no Licenses page.
