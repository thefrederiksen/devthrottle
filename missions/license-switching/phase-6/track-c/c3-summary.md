# Track C, Developer C3 - summary for the track report

17 October 2026, by the Track C Developer for the approver form and the approver health on the Licenses page of the
Cockpit. Two pull requests, both merged to main, then this summary:

| Pull request | What it did | Proof and review |
|---|---|---|
| 4111 | A usable approver form: four pick lists, the machine's own browser profiles, Save that says what is missing | `c3-approver-form.md`, `form-layout/`, `review-4111.md`, `review-4111-answers.md` |
| 4165 | Each approver row shows who made it, why a sign-in cannot reach the machine, the profile's health and the one thing to do, in the Gateway's words | `c3-approver-health.md`, `health-layout/`, `review-4165.md` (the review found nothing) |
| this one | This summary | - |

## What was built

**The owner sets an approver by choosing, never by typing.** "Set the approver" on an account card, or on the
approvers table, opens a form of four pick lists: the Claude account, the approving machine, the operating system user
on that machine, and the browser profile. The account is filled in only when the form is opened from that account's
card. The browser profiles are the chosen machine's own, read from that machine's Director
(`GET /directors/{id}/browsers`); the page knows which Director that is because the Gateway now names it on each machine
(`directorId`). Loading, a Director that is not connected and a Director with no profiles are each said in words, and
the not-connected sentence is the Gateway's (`browserProfilesText`). Changing the machine clears the user and the
profile. Save is never greyed out: pressed with a choice missing, it sends nothing and lists one line per missing
choice. A refusal from the Gateway is shown word for word. What is saved is the profile's identifier, never its display
name, as the Tech Lead decided in "Which string a browser profile is".

**Each approver row says what the Gateway says, and nothing of the page's own.** The status in its colour, who made
the record ("Set by you." or "Registered by machine-a's Director."), why a sign-in cannot reach the machine (only when
it cannot), the profile's health, and the one thing to do in bold (only when there is one). The profile's display name
is shown once a check has seen it, with the identifier under it. A registered row has Change and Remove like any
other. The page never reads `canApproveNow`, because the relay still asks a connected approver whatever its health, so
nothing on the page promises a refusal.

## The Gateway changes, and why (repository rule 7)

The client renders verdicts and never works them out, so every sentence the page needed was added to the Gateway's
fold rather than built in the page:

- On each machine of the Licenses overview: `directorId`, `canListBrowserProfiles` and `browserProfilesText` (4111).
- On each approver: `sourceText`, because the Gateway sent only the word `owner` or `registered`; and `reachText`,
  because the row's existing `text` already joins the reach sentence or the health and what to do, so drawing it next
  to them would have said a sentence twice (4165). `text` is unchanged.

Each new string is pinned word for word by a Gateway test. `cockpit-api.md` documents every field. Its approver
examples were given the two new fields by a short script following the fold's rule, because I found no producer for
them; the Cockpit fixtures were then regenerated from the document by `copy_fixtures.py`.

Two checks the brief asked for:

- **`GET /directors/{id}/browsers` answers the owner's Cockpit sign-in.** The route finds the Director within the
  signed-in tenant only, and `SessionKeyGuard` applies to session keys alone, so an owner's sign-in reaches it.
- **Repository rule 8 does not apply.** The approver form is on the Licenses page only; Settings and the phone app
  have no approver code.

## The tests, in plain words

The Cockpit tests render the real Licenses page with only `fetch` replaced, answered from the Gateway's documented
examples, which `fixtures.test.ts` holds to the document.

- **The form** (about 20 tests): the four pick lists and no free text; the account filled in only from its card; the
  profiles read from the chosen machine's Director; each of the loading, not-connected and no-profiles states said in
  words; changing the machine clearing the user and the profile; Save with a choice missing sending nothing and naming
  each one; a Gateway refusal shown word for word; the identifier sent, never the display name. Three client tests
  cover the browsers read, and two Gateway tests the new machine fields.
- **The rows** (8 tests): a healthy registered row, a row that needs a fresh sign-in, a row never checked on a machine
  that never reported, each sentence said once, `canApproveNow` never turned into words, the display name over the
  identifier, and Remove on a registered row asking first and then sending one removal. Four Gateway tests pin the
  source and reach sentences.

## The gate

- **4111**: Cockpit type check clean, the Licenses Cockpit tests passed, and the Gateway unit tests named by the review
  (225) passed on main. The License-filtered `-Parked` was run by the Tech Lead on the branch merged with main: green.
- **4165**: `.\scripts\test-local.ps1 -Parked -Filter FullyQualifiedName~License`, TEMP and TMP on `D:\cc-gate-temp`:
  GREEN at `bdfcaa999`, 1,287 tests. Cockpit type check clean, Cockpit 1,288 and client-core 2,158 tests passed. The
  Tech Lead's own runs agreed.

## Left for others

- `SystemdUnitSafetyTests.GatewayUnitContent_CarriesTheStartLimitInTheUnitSection` (in `cc-director-setup-engine.Tests`,
  from #4152) fails on a Windows checkout with CRLF line endings, and failed again when re-run alone. It is on main, not
  part of this work, and was reported to the Tech Lead for an owner.
- `ReclaimRefusalTests.Reclaim_APathThatIsNotCanonicalAfterResolution_IsRefused` cannot run with the temporary folder on
  D:, which has no short folder names; it refuses itself there and passes with TEMP on C:. Anyone running the gate with
  TEMP on D: will see it.
- Seen in the review of 4165, not changed: at 900 pixels wide the status column is narrow and its sentences wrap word by
  word; and "Registered by {label}'s Director." reads oddly when the label carries a user in brackets.
