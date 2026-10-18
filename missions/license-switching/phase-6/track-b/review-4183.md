# Review of pull request 4183 - Forget this machine (Track B)

Reviewed at head a2eb54c6f (branch license-forget-machine, based on 16acc700b). Reviewer: a separate review session.

**Verdict: not ready to merge.** One defect inside the feature's own purpose (finding 1) and a stale base that
conflicts with origin/main (finding 2) must be fixed first. Finding 3 should be fixed or at least said in the
sentence. Finding 4 is a wrong sentence in the proof. Everything the brief asked me to look hardest at and that I
could verify is listed under "Verified" at the end.

## Scope

- Read: the whole diff (14 files), the proof file, the pull request body, and the surrounding license code on the
  pull request head: `LicenseEndpoints`, `LicenseDisplayFold`, `LicenseStore`, `LicenseSwitchService`,
  `LicenseSignInService`, `LicenseSnapshotSource`, `LicenseApproverAlarm`, `LicenseApproverReadiness`,
  `LicenseRenewalEmailService`, `LicenseSignInFold`, `SessionKeyGuard`, the entity configuration in
  `GatewayDbContext`. Where 4172 changed the code, I read origin/main (8994f5e1a): `LicenseMachineHealthAlarm`,
  the silence watch, and the minute clock in `GatewayHost`.
- Ran, out of tree: a `git archive` of a2eb54c6f in my scratchpad, `dotnet test` of `Gateway.UnitTests` filtered
  to the Developer's new class and a reproduction class I wrote. 14 tests: the Developer's 12 passed, and my 2
  reproductions passed, which means they confirmed finding 1. I did not run the gate, did not build in or edit
  either worktree, and touched no real store or Gateway.
- Could not reach: a live Gateway; the Cockpit tests (not rerun, the diff is small and pinned at the wire).

## Finding 1 (must fix) - a machine that went silent while a switch was recorded on it can never be forgotten

The fold refuses Forget whenever `SwitchInProgress` is set (`LicenseDisplayFold.ForgetVerdict`, first branch), and
the store refuses while a switch row is open. For a machine that is GONE, two shapes of "switch in progress" never
end, so the refusal is permanent. Both reproduced against the pull request head
(`ReviewForgetStuckMidSwitchTests`, in my scratchpad, not in the tree):

- **A switch was sent and the tunnel dropped before an answer, then the machine was decommissioned.** The row is in
  state `unknown` (what `RecordResult` writes on Timeout or TunnelDropped). Every minute check skips it: PlanTick,
  case 1, `if (machine.LastReportAtUtc <= (row.LastSentAtUtc ?? row.CreatedAtUtc)) continue;` - a row whose machine
  never reports again is never settled, not even after `SwitchNotAfter`. After three days of ticks the switch is
  still open, the machine is `not-reporting`, the silence watch has opened a `machine-silent` row (so with 4172 the
  red "Machine stopped reporting" banner is up), `canForget` is false, the route answers 409, and `forgetText` says
  "it can be forgotten once the switch is over" - a day that never comes.
- **The machine's last accepted report carried `SwitchInProgressId`, and then it died.** Nothing clears the field but
  a later accepted report (`LicenseStore.ApplyReport`, line 192), and the fold reads it when no row is open
  (`LicenseDisplayFold.SwitchInProgress`, line 625). Same result: not-reporting, never forgettable, no switch row at
  all.

Why it must change: this is the very case the pull request exists for - a machine that is gone holding a red banner
on every page for ever - and the owner is shown no button and a sentence that misleads. There is no remedy short of
the database.

What to change: for a machine the fold calls `not-reporting` (or `report-refused`), a recorded switch is not a
reason to refuse. `ForgetMachine` should END the machine's open switch rows in the same save (state `failed`,
message "the machine was forgotten"), which also removes the reason the Developer refused in the first place - the
minute check's throw on a row naming a missing machine (PlanTick line 439) can no longer happen, because no open
row names it. A late answer from a Director is harmless: `TrySettle` on an ended row changes nothing, and
`ApplySwitchReadBack` updates no machine row. A send cannot be in flight for such a machine: the decision engine
never sends to a stale or refused machine, and Switch now skips them. The report-sourced `SwitchInProgressId` needs
no handling once the machine row is deleted. A sign-in, by contrast, is only a delay: `SettleOneAsync` ends a
sign-in on a machine not seen for `MachineNotSeenAfter`, so that refusal is fine as it is.

## Finding 2 (must do before merge) - the branch is behind origin/main and conflicts with it

Main is at 8994f5e1a; 4172 (the alarms) and 4178 merged after this branch's base. `git merge-tree origin/main HEAD`
reports a content conflict in `src/CcDirector.Gateway/Licenses/LicenseStore.cs` (4172 added `EndSilenceForRefusal`
and `RefusalReason` in the same region as `ForgetMachine`). The gate in the proof ran on 16acc700b, so it says
nothing about the code that would actually land. Rebase onto origin/main, resolve, and rerun the gate.

Once rebased, the proof's "once 4172 is merged, the alarms close on the next minute" stops being a promise and
becomes a test: after `ForgetMachine`, `LicenseMachineHealthAlarm.ConditionsNow(tenant)` should answer two empty
lists. Please add that assertion - it is the one thing the owner will look for.

## Finding 3 (should fix, or say it) - the approver record outlives the machine, and its alarm does not close

Approvers are keyed by machine name and user (`LicenseApprover.NamesMachine`), not by machine id, and
`ForgetMachine` does not touch them. For a machine that approves one or more accounts, after forgetting:

- the Approvers table shows it as "Not reporting yet" (`LicenseSignInFold.Approver`, line 116: the status of a
  machine that has never reported), as if it were new;
- Track A readiness says every account it approved "cannot approve", and `LicenseApproverAlarm` raises (or keeps)
  one alarm per such account. Those alarms are keyed by account, with no machine id, so forgetting closes none of
  them, and the "not checked" one that opens 26 hours later does not say its cause is the forgotten machine.

The offered sentence promises to "end its open problems and their alarms". For an approver machine that is not
true. Either remove the approver records naming the machine in the same save and say so in the answer, or make
`forgetText` name the accounts it approves so the owner knows what to do next. The owner can remove the approver by
hand, so this is not blocking on its own, but the sentence must not over-promise.

## Finding 4 (correct the sentence) - there is no transaction around the store's second check

The proof and the pull request body say the store checks for an open switch or sign-in "inside its own
transaction". It does not: `ForgetMachine` is a read of the open rows and then one `SaveChanges`. The only
`BeginTransaction` in the file is in `ApplySwitchReadBack`. The window is real but the harm is bounded: the switch
service's per-tenant gate is not shared with the route, but the engine never sends a switch to a stale or refused
machine, so the only writer that could slip in between the read and the save is the readiness service starting a
sign-in on a `report-refused` machine whose Director is connected; the loser of that race is a sign-in row that
`SettleOneAsync` skips for ever (machine null, returns false) - open in the log, nothing thrown. Say "in one save"
rather than "in its own transaction", or take the switch service's tenant gate in the route if the Tech Lead wants
the window closed. Not blocking.

## Verified (nothing to change)

- **Owner only.** `OwnerOnly` (`FleetManagerOwnerDevice.Require`) runs before the body is read, like the other two
  writes. `SessionKeyGuard` is an allow-list by exact path; the new route is not in it, so a session key gets 403
  without any change to the guard. `LicenseGuardTests` and the self-hosted machine-token test cover both.
- **Tenant isolation.** Every read and write in `ForgetMachine` goes through `CreateContext(tenant)` with the tenant
  query filter (`ApplyTenantScope` on all three license tables). The route looks the machine up in this tenant's
  folded overview first (404 otherwise). A stand-in id is a hash of name and user, so the same machine name in
  another tenant has the same id, but the refusals read and ended are only this tenant's. The cross-account test
  covers the route and the store.
- **Deleted versus ended.** Holdings and the machine row are deleted; every log row is kept - there is no foreign key
  and no cascade on `license_switch_log` (checked the model configuration), and the test reads the earlier silence
  row back as `ended`. Usage history is per account, not per machine. An open renewal warning closes itself
  (`LicenseRenewalEmailService`, line 161: the machine no longer holds the sign-in). The readiness alarm and the
  refused and silent alarms are raised by condition from current rows every minute, and
  `LicenseMachineHealthAlarm.TickEveryTenantAsync` is not gated on `HasMachines`, so forgetting the LAST machine
  still closes its alarms (the switch tick alone goes inert).
- **Comes back whole.** A report after forgetting creates a new row under the (name, user) unique index, with new
  holdings; the test shows it `reporting` with nothing to forget.
- **The right alarms, through the one mechanism.** Refusals are ended by `IsRefusalOf`, the same rule the page and
  the alarm use; silences by machine id. No alarm is touched directly; the rows are the state.
- **The Cockpit decides nothing.** It draws the button on `canForget`, shows `forgetText` in the confirmation, sends
  one `application/json` POST, and shows the answer or the refusal as given; the three tests pin it at the wire.
- **Documents and names.** `cockpit-api.md` matches the code (fields, log kind, answers, the three-write table).
  The diff carries no real email, machine name, address or account name.
- **Input checks.** A body that is not an object is 400 `invalid_body`; an id that is not 32 hexadecimal digits is
  400 `invalid_machine` before anything is read (null body is untested but trivial).
