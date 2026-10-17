# Review of pull request 4127 - License Switching Track C, approver readiness and health on the approvers route

Reviewer session bbdadc30, 17 October 2026. Head reviewed: refs/review/4127 = b7e74dddc (one commit).
Merge base with origin/main: 36e514237. origin/main has moved six commits past the base; none of them
touch any license file, Cockpit license screen or client-core license type.

## Scope

Read, through git objects only (no checkout, no build, no test run, no edit of the worktree):

- The whole diff of the head against the merge base (12 files, 703 added, 33 removed).
- In full at the head: LicenseApproverReadiness.cs (new), LicenseSignInFold.cs, LicenseSignInEndpoints.cs,
  LicenseApproverStore.cs, LicenseApproverRegistration.cs, LicenseApproverMessages.cs, the changed parts of
  LicenseSignInCockpitDtos.cs, LicenseEndpoints.cs and GatewayHost.cs.
- All four changed test files and the new LicenseApproverReadinessTests.cs in full; the hosted fixture's
  clock pin (GatewayHost.LicenseClock is set to a fixed 2026-10-12 12:00 UTC clock before start).
- The relay (LicenseApprovalRelay.cs) for how it picks an approver; the Cockpit approver table
  (apps/cockpit/src/licenses/LicenseSignIns.tsx) and the client-core LicenseApprover type, for what reads
  status, canApproveNow and the sentences.
- The design note "Which string a browser profile is", design points 2 to 5, and the Developer brief's
  pull request 2 section; both docs in the diff (cockpit-api.md section 7, c1-readiness.md).
- A scan of every added line for email addresses, IP addresses, real machine or account names and
  non-ASCII bytes.

Ran: nothing. Could not reach: nothing I needed. I did not reproduce out of tree; every question was
answerable from the code.

## What I checked against the brief, and what I found

**"Can approve" is a positive reading.** Yes. In LicenseSignInFold.Approver (line 105) `Ready` is given only
when `health.WhatToDo is null`, and LicenseApproverReadiness.Verdict returns a null WhatToDo only in the
`LicenseApproverStates.CanApprove` arm. That arm is reached only through LicenseApproverHealth.StateOf, which
answers `not-checked` when CheckedAtUtc is null, when State is null, or when the check is older than 26
hours, and otherwise the stored state word. A state word that is not one of the six throws (and the store
already refuses to read such a record). No approver at all gives the "No machine is set..." sentence. So no
approver, unchecked, stale, and each failed state all produce a sentence, and none of them is ready.
The hosted test and the endpoint test both show a just-set owner record answering `cannot-approve` /
"Not checked", and the hosted test turns it `ready` only after a real can-approve check arrives over the hub.

**Every sentence.** All are plain ASCII English, name the account, the machine (through the page's label,
with the user in brackets when two machines share a name - tested) and the profile (display name when a
check has named one, else the identifier - tested), and end with one thing to do. No page text is in any
sentence. One payload field does reach a sentence: see note N1 below.

**Rule 7 and callers of Ready / canApproveNow.** The new fields are folded on the Gateway in one place.
The Cockpit table reads `statusText`, `statusTone` and `text` verbatim and never branches on `status`
(it only stamps it as a data attribute). No client code reads `canApproveNow`. The relay does not read the
DTO at all; it has its own connected check and does not consult health (see note N2 - that is correct,
not a defect). Add a spare and Renew now are folded from the machine's connection alone, unchanged. So the
narrowing of `ready` breaks no caller. The client-core TypeScript type still lacks the six new fields and
its comment still lists three statuses; the brief assigns the Cockpit to Developer C3, so that is not a
finding here, but C3 must update `packages/client-core/src/licenses/licensesClient.ts`.

**Owner-only routes.** PUT and DELETE still run `OwnerOnly` before a byte of the body is read, unchanged.
The new hosted test proves a session key and a raised session key both get 403 `session_key_out_of_scope`
on PUT and on DELETE and that nothing is stored. DELETE on a registered record writes the removal record
(unit test through the route handler, hosted test over real HTTP), and the next check is refused
`owner-removed`. An owner-set record leaves no removal (tested).

**Tests exercise the real fold and route.** The endpoint tests call the route handlers (List, Set, Remove)
and read the JSON; health is planted through the store's `Change`, which is the same write the
registration uses. The hosted test goes through the real SignalR hub method `ReportLicenseApproverChecks`
to register and to update health. Sentences are pinned character for character. Good.

**Public repository.** Only example.com addresses, "the-mac", "machine-a", "MAC_OF_ALICE" and a test GUID.
No real email, machine, account or address. Non-ASCII: none in code; one literal tab character in the
proof document (finding F2).

**Gate evidence.** The proof file says the gate ran at 9f79abe41. That commit is not on origin/main (the
file says it is); it is the branch's earlier commit, and its tree differs from the head's tree only in
c1-readiness.md itself. So the gate covered exactly the code under review. The one red test is in
Reclaim, explained as the TEMP-on-D: short-name condition; it is not this pull request's test.

## Findings

### F1 - WhyNotReady with a blank or non-email account hands Track A a sentence with a hole (low)

File: src/CcDirector.Gateway/Licenses/LicenseApproverReadiness.cs, WhyNotReady (lines 44 to 53) and
NoApproverText / NoApproverWhatToDo (lines 65 to 67).

`LicenseStore.NormalizeEmail(null)` is "" and the store's Find simply finds nothing, so
`WhyNotReady(tenant, null)` or `WhyNotReady(tenant, "   ")` returns
"No machine is set to approve sign-ins for . Set an approver for  on the Licenses page." That is a sentence
folded from nothing, shown verbatim by Track A's readiness, and it reads as "no approver" when the real
fault is a caller that passed no account. Every Licenses route refuses a non-email with `invalid_account`
through `LicenseApproverStore.IsAccountEmail`; this public entry point should do the same - throw an
ArgumentException naming the parameter - rather than answer. One line plus one test.

### F2 - The proof document carries a literal tab in the gate command, and misplaces the gate commit (low)

File: missions/license-switching/phase-6/track-c/c1-readiness.md, line 103 of the file (diff line 161).

The command reads `.\scripts<TAB>est-local.ps1 -Parked`: the `\t` of `\test-local.ps1` was interpreted as a
tab when the text was written. It is the only non-printable byte in the pull request and the command
cannot be copied and run. The same line says 9f79abe41 is "on origin/main"; it is the branch's previous
commit (tree identical to the head's except for this file), so the gate evidence is sound but the sentence
is not. Fix both before merge; this is the record Track A and the Tech Lead will read.

### F3 - A wrong-account row whose shown account IS the account folds a self-contradicting sentence (low)

Files: src/CcDirector.Gateway/Licenses/LicenseApproverReadiness.cs, Verdict, WrongAccount arm (line 81);
src/CcDirector.Gateway/Licenses/LicenseApproverRegistration.cs, Checked (line 219).

Checked() refuses a `can-approve` row whose ShownAccount differs from AccountEmail, but accepts a
`wrong-account` row whose ShownAccount equals AccountEmail. That record then folds to "...is signed in to
claude.ai as alice@example.com, not as alice@example.com. ...sign out ... and sign in as alice@example.com
once." A Director that reports it is defective, but the Gateway should refuse the row as `invalid-row`
(the mirror of the existing rule) rather than store it and say nonsense for a day. The registration file is
from 4109; the fix is one condition and one test, and belongs with this pull request because this is the
pull request that turns the record into a sentence.

## Notes (not findings - no harm proven)

N1. The `check-failed` sentence embeds `Detail`, which is payload text from the Director, screened only for
length (200) and control, format and separator characters. The design asks for exactly this ("with the
Director's fixed detail"), and the Gateway cannot verify "fixed words". The guarantee that no page text
reaches the owner therefore rests on Developer C2's Director check; whoever reviews C2 should check that
Detail is built only from constants.

N2. The relay still sends `license-approve` to a connected approver whatever its health row says. That is
right: a freshly set approver must work before its first daily check, and the design gives readiness to
Track A's not-ready reason, not to the relay. But it means `canApproveNow = false` ("Not checked") does not
mean the relay will refuse. The DTO comment says "a sign-in can be approved there now"; Developer C3 should
not render anything from `canApproveNow` that promises the relay's behaviour. Nothing reads it today.

N3. `StateOf` answers `not-checked` for a record with CheckedAtUtc set and State null; the sentence then
says "was last checked <time>, more than 26 hours ago" even if that time is recent. Unreachable through the
two writers (Set preserves the pair, the registration writes the pair), so no finding.

## Verdict

No blocking finding. Three low findings: F1 and F3 are one condition and one test each; F2 is a text fix.
The positive-reading question the brief asked is answered correctly by the code and proved by tests at
both the fold and the wire.
