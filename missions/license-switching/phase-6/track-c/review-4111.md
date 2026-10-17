# Review of pull request 4111 - License Switching Track C, the approver form

Reviewer session "License Switching - Reviewer - Track C 4111 approver form", 17 October 2026.
Head `refs/review/4111` = 620e6f4e1 (three commits), base 08dec1140. Read through git objects only;
nothing built, nothing run, nothing checked out.

## Scope

What I read:

- The whole diff 08dec1140..620e6f4e1 (30 files): the Gateway fold and contract, both Gateway tests, the
  client-core client and its tests, the four Cockpit source files, the Cockpit test file, the fixtures, the
  api document, the proof file, the layout script, and six of the fifteen screenshots (form-filled,
  missing-everything, director-not-connected, saved, narrow-window-missing-user, refused-by-the-gateway),
  each extracted from the ref into my own scratch folder and opened.
- On the base commit: `GatewayEndpoints.cs` (the browsers routes and `TryResolveOwnedDirector`),
  `SessionKeyGuard.cs` and `AuthMiddleware.cs` (where the guard is applied and to whom),
  `LicenseEndpoints.cs` (the connectedness closure), `LicenseStore.ApplyReport` and `LicenseMachineView`
  (where a machine's `DirectorId` comes from), `BrowserExecutor.cs` and `SessionCommandExecutor.Serialize`
  (the real shape and casing of the Director's browsers-list answer), `AutomationBrowserRegistry.Slugify`
  (what a profile identifier looks like), `LicenseApproverStore.Set` (the profile check),
  `browser_ops.DrivenBrowser` and `license_signin.director_browser` (how the approving Director resolves a
  profile), `errorDisplaySites.test.ts` (the exemption rule), the Cockpit tsconfig and package scripts.
- On the Track C1 branch `origin/license-c1-approver-registration` (not on main yet):
  `LicenseApproverRegistration.Decide`, `LicenseApprover.Names`, `LicenseApproverRemoval.Matches` and the
  hub contract `LicenseApproverMessages.cs`, to check what this form stores against what C1 compares.
- I fetched origin (remote refs only): origin/main is 6c783e3d4, two commits past the base, with no file in
  common with this pull request. `git merge-tree` of origin/main and the head is conflict-free, and so is a
  merge-tree of the C1 branch and the head.

What I could not do: run the gate, the Cockpit tests or `tsc`. The proof's gate claims are reported as
claims below, not verified.

## Answers to the brief's questions

- **The owner's three complaints.** Fixed. The form is four `<select>` elements and no text input
  (`LicenseSignIns.tsx` 196-240; the test asserts zero `input`/`textarea`). The account is pre-filled only
  when `ApproverEditorOpen.accountEmail` is set, and only the card's button sets it
  (`LicensesView.tsx` 311 passes the card's email; the "Set an approver" button passes null at 345). Save is
  disabled only while a write is in flight (`disabled={busy}`, line 243); pressed with a choice missing it
  returns before `onSave` and renders one line per missing choice (`missingChoices`, lines 49-57, and
  `save`, lines 148-152). A Gateway refusal is shown through `describeAndReport`, and the test asserts the
  text equals the Gateway's `error` word for word.
- **The browser-profile list.** The Director is found by machine name AND operating system user
  (`overview.machines.find(m => m.machineName === machineName && m.osUser === osUser)`, line 99), so a
  machine with two reporting users asks the right Director, and no Director is asked until both are chosen.
  Loading, not-connected and no-profiles each have a sentence (lines 166-173); the not-connected sentence is
  the Gateway's (`browserProfilesText`) and the Director is not called (`canList` false short-circuits the
  effect, lines 112-115). `chooseMachine` clears user and profile (lines 134-138); `chooseUser` clears the
  profile. The request is aborted on change (`AbortController`, lines 116-131).
- **Does an owner's Cockpit sign-in reach `GET /directors/{id}/browsers`? Yes.** The route resolves the
  Director with `TryResolveOwnedDirector` (base `GatewayEndpoints.cs` 6317-6341): the request's own tenant
  from its authenticated device key, `registry.Get(reqTenant, id)`, 403 with no tenant, 404 for a foreign
  id. `SessionKeyGuard.Check` is called only inside `AuthenticateSession` (base `AuthMiddleware.cs` 652-659)
  and in the refusal branch for an out-of-scope SESSION key (446); a Cockpit device key never meets it. The
  diff touches no file under `src/CcDirector.Gateway/Api/` and none under `src/CcDirector.Gateway/Util/`:
  no route was opened or widened and the guard is unchanged.
- **The Gateway additions.** `directorId`, `canListBrowserProfiles` and `browserProfilesText` are set in
  `LicenseSignInFold.Apply` (lines 172-176) from `machine.DirectorId` and the same `connected` value that
  already decides `canAddSpare`; `connected` is the tenant-bound closure the route builds
  (`LicenseEndpoints.cs` 117-118). Two new Gateway tests cover connected and disconnected, and the
  field-order pin in `LicenseEndpointsTests` was updated. **Does the Director identifier leak anything?** No:
  `GET /directors` is already readable by every device key in the tenant and by every session key (the guard's
  read list, `SessionKeyGuard.cs` 343), so the identifier is already visible to everyone who can read
  `GET /gateway/licenses` (also in that list, line 398). `DirectorId` is written by `LicenseStore.ApplyReport`
  (133) from the hub-authenticated caller of the report, never from a payload.
- **Rule 7.** Every verdict about the fleet (connected or not) is the Gateway's. The page composes three
  kinds of sentence about its own state: "Loading the browser profiles on X...", "X's Director has no browser
  profiles." (line 172, from an empty list the Director itself returned), and the missing-choice lines. The
  Developer's brief asked for exactly these. I raise no finding on them.
- **The tests reach the real caller.** The Cockpit tests render `LicensesView` with only `fetch` stubbed
  (`beforeEach`, lines 120-140 of the test file); the stubbed Director answer `{ browsers: [{ id, name }] }`
  is the real shape, because the Director serializes with `JsonSerializerDefaults.Web` (camelCase,
  `SessionCommandExecutor.cs` 88 and 2028) and `AutomationBrowserView` has `Id` and `Name`. The not-connected
  test takes `canListBrowserProfiles` and `browserProfilesText` from the Gateway's own example overview rather
  than typing them. The per-test `overview` is rebuilt in the outer `beforeEach` before the inner one pushes
  the second user, so machines do not accumulate across tests. Loading, no profiles, a 502, each missing case,
  the lines going away one by one, one PUT with four values, the identifier being sent, refusal kept open,
  Change from a row, Change on a machine that never reported, Cancel: each is a test, as the proof says.
- **Public repository.** A grep of the added lines for emails outside example.com, machine names, owner
  names and IPv4 addresses found nothing. The six screenshots I opened show only machine-a/b/c and
  alice/bob/carol at example.com. The layout script renames the example data before the page sees it.
- **Rule 8.** Not a Setting; nothing under settings or the phone references an approver. Confirmed by grep.

## Findings

### 1. Medium - the profile string an approver record holds is now sometimes a name and sometimes an identifier, and Track C1 compares it ordinally

`packages/client-core/src/licenses/licensesClient.ts` 373-380 and 534; `apps/cockpit/src/licenses/LicenseSignIns.tsx`
125 and 232; against the C1 branch `LicenseApproverRegistration.cs` 136-155 and `LicenseApproverStore.cs` 29 and 45.

This form sends the profile's slug identifier ("work-chrome") where every record set before it holds the
profile's name ("Work Chrome"), and nothing in the design, the hub contract (`LicenseApproverAssignment.BrowserProfile`
and `LicenseApproverCheckRow.BrowserProfile` carry no comment saying which), or `cockpit-api.md` says which one
`browserProfile` is. The approval path survives either: `DrivenBrowser` resolves id first, then name
(`browser_ops.py` 288-290). The health and registration path does not: C1's `Decide` updates a record's health
only when `existing.Names(machine, user, row.BrowserProfile)` is true, an ORDINAL string compare (`LicenseApprover.Names`),
and otherwise answers `RecordNamesAnotherApprover` or `OwnerSet` and stores nothing.

The harm, concretely: the owner sets alice@example.com to profile "Personal" on machine-a through this form, so
the record holds "personal". The approving Director (Developer C2's code, not written yet) checks that profile daily
and reports a check row. If C2 reports the profile by name, the row says "Personal", `Names` is false, the owner's
record is never updated, and after 26 hours it is `not-checked`; Track A then reads the account as not ready and
no sign-in or renewal for that account proceeds without a person - the exact outcome the design exists to end. If
instead C2 reports by identifier, the same happens to every record set before this change by name, including any
the owner set by hand. One of the two populations is orphaned unless the two tracks agree in writing.

What must change: the Tech Lead decides which string `browserProfile` is (my recommendation: the identifier, as
this pull request does - it is stable across a rename and `DrivenBrowser` already accepts it) and writes it into
the design, the hub contract's two `BrowserProfile` comments and `cockpit-api.md`; C2 reports that string; and
C1's `Names` and `Matches` should accept the other spelling for records that predate the rule, or the stored
records are migrated once. This pull request's part of that is one sentence in `cockpit-api.md` saying the
PUT's `browserProfile` is the profile's identifier. The rest is C1 and C2 work, but it has to be decided before
either of them lands.

### 2. Low - the approver row and the Gateway's sentences now show the slug for records set by this form

`apps/cockpit/src/licenses/LicenseSignIns.tsx` 370; `LicenseSignInFold.Approver` (unchanged) builds "in the browser
profile {BrowserProfile}".

A profile the owner named "Work Chrome" on the Browsers page is listed in the approver table and in every health
sentence as "work-chrome". The form itself shows "Work Chrome (work-chrome)" (line 234), so the owner can map
one to the other, but the row and the sentences cannot. If finding 1 settles on the identifier, the Gateway's
`Approver` fold should carry the display name too (it would need the Director's list, or C2's check rows to
include the name), or the row should say what it shows. Not blocking.

### 3. Low - the proof's gate is narrower than it reads, and the type check was not run

`missions/license-switching/phase-6/track-c/c3-approver-form.md`, "The gate".

- `npm test` in the Cockpit is `vitest run`, which does not type-check; `tsc --noEmit` is the `typecheck`
  script and is not reported as run. I read the new TypeScript for the usual traps (the `Pick` on
  `OVERVIEW_SIGN_IN_FIELDS` exempts it from the three new required fields; `DOM.Iterable` is in the tsconfig
  lib for the spreads in the test; `Button` is already imported in the card) and found nothing, but a reader
  of the proof should not take "1270 tests passed" as a type check. One command closes it.
- `CcDirector.Gateway.Tests` and `CcDirector.Core.Tests` were not run, as the proof says. The six
  `Gateway.UnitTests` failures are attributed to the base; main has since merged #4107 ("the scripted
  terminal draws the composer a send now reads before typing"), which by its title is that defect, so a rebase
  onto origin/main should turn them green and would be the cleanest evidence.

## Not findings, noted for the Tech Lead

- The branch is two commits behind origin/main with no overlapping files and a clean merge-tree; it also
  merges cleanly with the C1 branch (merge-tree, no conflicts).
- `Slugify` yields `[a-z0-9-]+` with trimmed edges and a "browser" fallback, so every identifier passes the
  store's `BrowserProfileShape` unless a name is longer than 64 characters after slugging.
- The proof's reading of the route and the guard is correct, and it says so in the proof as the brief asked.
- The `error-report-exempt` reason on the missing-choice list is the written form the scan requires and is
  true: nothing failed when a choice is missing.

REVIEW DONE
