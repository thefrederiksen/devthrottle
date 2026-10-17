# Track C, developer C3 - approver health on the Licenses page (pull request 2 of 2)

17 October 2026, branch `license-c3-approver-health`, cut from `origin/main` at `f742b9f2f` (after C1's 4127 and the
client approver fields of 4150).

## What the page shows now

Each row of "Who approves a sign-in" shows, all of it the Gateway's words as given:

- **Browser profile**: the profile's display name (`browserProfileName`) once a check has seen one, and always the
  identifier the record holds (`browserProfile`) under it, dim. No name is made up when none was sent.
- **Status**: the Gateway's status in its tone (`statusText`, `statusTone`), then:
  - **who made the record** - `sourceText`: "Set by you." or "Registered by machine-a's Director.";
  - **why a sign-in cannot reach the machine**, only when it cannot - `reachText`: "No Director on machine-c has
    reported to this account, so no sign-in can be approved there yet." or "The Director on machine-a is not
    connected, so no sign-in can be approved there now.";
  - **the profile's health** - `healthText`, for example "The approver for carol@example.com (browser profile "work" on
    machine-a) needs a fresh claude.ai sign-in on the approval page.";
  - **the one thing to do**, only when there is one - `whatToDo`, in bold: "Open that browser on machine-a and sign in
    to claude.ai once."
- Change and Remove on every row, a registered one included. The Remove confirmation still shows the Gateway's whole
  `text` for that approver.

The page branches on nothing to decide what a state means. It draws a field when the Gateway sent one (`reachText`,
`whatToDo`, `browserProfileName` are null when there is nothing to say) and never reads `canApproveNow`: as the Tech Lead
said, the relay still asks a connected approver whatever its health, so the page promises nothing from it. A test
gives a row `canApproveNow: true` beside the Gateway's "Cannot approve" and checks that the row says only "Cannot
approve".

## One Gateway change, and why (repository rule 7)

The brief asks for the source as words ("Set by you" / "Registered by machine-a's Director"), shown verbatim, but the
Gateway sent only the word `owner` or `registered`. And the row's existing `text` is two different things joined:
for an unreachable machine it is who approves plus why it cannot be reached; for a reachable one that cannot approve
it is `healthText` plus `whatToDo`. Drawing `text` beside `healthText` and `whatToDo` would say the same sentence twice,
and picking parts of it apart would be the page deciding. So `LicenseSignInFold.Approver` now also sends:

| Field | What it is |
|---|---|
| `sourceText` | "Set by you." or "Registered by {label}'s Director." |
| `reachText` | The unreachable-machine sentence, or null when the machine's Director is connected. It is the same string the fold already put at the end of `text`, now built once and used in both. |

`text` is unchanged, so the account cards (`approverText`) and the Remove confirmation read as before.

`cockpit-api.md` documents both fields in section 7.1. **The section 7 examples were not re-produced by running the
route**: I found no producer for them. They were given the two fields by a short
script that applies the fold's own rule to each example's `source`, `label` and `status` - two lines added per
approver, five approvers, nothing else touched - and `copy_fixtures.py` then rewrote the Cockpit fixtures from the
document. The Gateway tests below pin the exact wording, so a fold that drifts from the examples fails there.

## The tests, in plain words

### Gateway - `LicenseSignInEndpointsTests`

- The approver's field list is pinned with `sourceText` and `reachText` after `source`.
- An approver just set by the owner on a connected machine: `sourceText` "Set by you.", `reachText` null.
- An approver whose Director is not connected: `reachText` is exactly "The Director on the-mac is not connected, so no
  sign-in can be approved there now."
- An approver on a machine that never reported: `reachText` is exactly "No Director on the-mac has reported to this
  account, so no sign-in can be approved there yet."
- A healthy registered approver: `sourceText` "Registered by the-mac's Director.", `reachText` null.

### Cockpit - `LicensesViewSignIns.test.tsx`

Only `fetch` is replaced. The approvers answer is the Gateway's own example (one healthy registered row, one owner-set
row on a machine that never reported and was never checked); the needs-fresh-sign-in row uses the Gateway's wording as
its tests pin it.

- **lists each approver with the Gateway's label, the profile, and the status in its tone.**
- **a healthy registered row**: "Ready", "Registered by the-mac's Director.", its health sentence, no reach sentence and
  nothing to do.
- **a row that needs a fresh sign-in**: "Cannot approve" in the warning tone, "Set by you.", the health sentence and the
  one thing to do, word for word.
- **a row that has not been checked, on a machine that never reported**: "Not reporting yet", the source, why the
  machine cannot be reached, the health sentence and what to do.
- **says each sentence once**: the status cell's whole text is exactly the Gateway's sentences in order, with nothing
  of the page's own, and the Gateway's `text` for the unreachable row ends with the `reachText` the row shows once.
- **never turns canApproveNow into words**: a row with `canApproveNow: true` and the Gateway's "Cannot approve" shows
  "Cannot approve" and the word "Ready" nowhere.
- **shows the profile's display name once a check has seen it, and always the identifier**; a row with no name shows the
  identifier alone.
- **offers Remove on a registered row**: nothing is sent until confirmed, then one DELETE naming that account.

The tests of the first pull request (the form) run unchanged.

## The screenshots - `health-layout/`

Made by `health-layout/health_layout_check.py`: the Cockpit dev server, the Licenses routes answered from the Gateway's
examples, every name made up (machine-a, machine-c; alice, bob, carol, dave at example.com).

| File | What it shows |
|---|---|
| `row-healthy-registered.png` | Ready, registered by machine-a's Director, can approve, last checked. |
| `row-not-checked-never-reported.png` | Not reporting yet, set by you, why, never checked, what to do. |
| `row-needs-fresh-sign-in.png` | Cannot approve, set by you, needs a fresh sign-in, what to do. |
| `row-not-connected-with-display-name.png` | Not connected, registered, the display name "Work Chrome" over "work-chrome", why, and its health. |
| `approvers-table.png` | All four rows together. |
| `registered-row-remove-confirmation.png` | Remove on the registered row, asking first with the Gateway's sentence. |
| `approvers-table-narrow.png` | The table at 900 pixels wide. |

## The gate

- **Cockpit**: `npm run typecheck` clean; `npm test` 116 files, 1288 tests, all passed.
- **Client core**: `npm test` 171 files, 2158 tests, all passed.
- **`.\scripts\test-local.ps1 -Parked -Filter FullyQualifiedName~License`**, TEMP and TMP on `D:\cc-gate-temp`, at
  `bdfcaa999`: **GREEN**, every suite Completed - Core.UnitTests 198, Avalonia 32, Gateway.Tests 23, Gateway.UnitTests
  1034 license tests executed and passed (the other suites hold no license test and ran none). The lock was held when
  it started; the run waited on the holder process and then ran.
- **`.\scripts\test-local.ps1`** (default), at `bdfcaa999`: 10 of 11 suites Completed and passed (Core.UnitTests 2186,
  Avalonia 1063, Engine 85, HostedAgent 85, Launcher 214, Terminal.Avalonia 71, Reclaim 305, DeliveryQualification 17,
  setup 39, setup-cli 74). `cc-director-setup-engine.Tests`: 912 of 913; the one failure is
  `SystemdUnitSafetyTests.GatewayUnitContent_CarriesTheStartLimitInTheUnitSection`, which looks for
  `"StartLimitBurst=5\n"` in a unit file this Windows checkout holds with CRLF line endings. It failed again on
  `-RerunFailed`, so it is a defect, and it is on main: the test came in with #4152 (a Linux change merged today) and
  this pull request touches nothing in the installer. Reported to the Tech Lead, not fixed here.
