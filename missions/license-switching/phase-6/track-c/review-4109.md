# Review of pull request 4109 - License Switching Track C, approver registration (C1)

Reviewer: a separate review session ("License Switching - Reviewer - Track C 4109 approver registration").
Head reviewed: 06fcd8604 in `D:\ReposFred\devthrottle.worktrees\wt14`, diff `08dec1140..HEAD`.
Date: 17 October 2026.

## Scope

What I read, in full:

- The diff of all eleven files (contracts, `LicenseApproverRegistration.cs`, `LicenseApproverStore.cs`,
  `DirectorHub.cs`, `GatewayHost.cs`, `TenantSettingKeys.cs`, the three test files, the contract section 9 and
  the C1 proof file).
- On the base commit, for comparison: `LicenseApprovalRelay.cs` (how the relay looks up the reporting Director),
  the old `LicenseApproverStore.cs`, `LicenseStore.ApplyReport` (how a usage report binds a Director to a machine
  row), `LicenseReadModel` field shapes, the hub's `RequireBoundDirector` / `RequireBoundTenant` /
  `EnterBoundTenantScope`, the hub's reflection-built `HubMethods` list, and where `/director-stream` is mapped
  (behind the host-wide bearer-token middleware).
- The Track C design and the Developer C1 brief.

What I ran: read-only `git diff`, `git show` and `grep` in the worktree. A regular-expression scan of the whole diff
for emails outside example.com, dotted IPv4 addresses, and the owner's real machine and company names: no hits.

What I did not do: build, run tests, or run the gate (the Tech Lead's gate run owns the worktree). The proof file's
gate output is taken as reported: every license test green; seven unrelated Gateway unit-test failures, six of
which it says fail the same way on an untouched `origin/main` at `08dec1140`.

## The security questions, each answered

- **Machine or user from the payload?** No. `Report` and `Assignments` take the tenant and Director from the hub
  binding and resolve the machine and user with `ReportingMachine(overview, directorId)` over the Licenses read model,
  the same lookup the relay makes. The row's only identity fields are the profile name and the account, and both are
  shape-checked before use. Tests `Report_CanApproveForAnAccountTheTenantHolds_...`,
  `Report_FromADirectorThatReportsForNoMachineOfTheTenant_...` and
  `Report_FromADirectorThatNoLongerReportsForItsMachine_...` exercise the real registration over the real read model.
- **A Director that is not the one reporting for the machine?** Refused every row, `not-the-reporting-director`,
  before any row is inspected, and nothing of the row is echoed back (the answer carries an empty profile and a null
  account).
- **A health row for a record naming another machine, user or profile?** `Names()` requires machine, user AND profile
  to match (`record-names-another-approver` otherwise). Tested for another machine and for another profile of the
  same machine.
- **Another tenant's records?** Every read and write goes through `TenantSettingsStore` and `LicenseReadModel.Read`
  with the bound tenant; the same Director identifier in another tenant resolves to no machine there. Tested
  (`Report_FromTheSameDirectorIdInAnotherTenant_...`, `Assignments_ForADirectorThatReportsForNoMachine...`).
- **A session key reaching anything owner-only?** This pull request adds no HTTP route and changes none. The two
  new hub methods live on `/director-stream`, which is token-gated by the host-wide middleware and bound at Hello
  like every other Director method; a session key never opens a Director stream. The owner-only PUT/DELETE routes are
  untouched.
- **Payload text logged or echoed unchecked?** No. Logging happens after `Checked()`; an invalid row logs
  `account=-`; the whole-report refusals log only counts and the Director identifier; the `HubException` carries the
  registration's own sentence. The test with the injected line-break marker proves the marker never reaches the log.
  The answer echoes only the regex-checked, trimmed profile and the normalised, validated email.
- **Noted, not a finding (pre-existing trust model):** within one tenant, any Director can send a usage report naming
  another machine's name and user and `ApplyReport` makes it that machine row's reporting Director. That Director could
  then update the health of, or register for, that machine name. This is exactly the relay's existing behaviour on
  main (it sends `license-approve` to whichever Director last reported for the named machine), every Director in a
  tenant is the owner's own, and the design explicitly binds to "the Director that last reported for the machine".
  No new exposure from this pull request.

## The design rules, each checked

| Rule | Where | Verdict |
|---|---|---|
| Owner-set never overwritten | `Decide`: `existing?.Source == Owner` -> `owner-set`, checked before the incumbent test, so the not-checked window never hands an owner's record to a registration | OK, tested at 3 days old too |
| Owner removal sticks until the owner sets that account again | `Remove` writes a removal only for a registered record; `Set` clears that account's removals; `Decide` refuses `owner-removed` on a match of account, machine, user and profile | OK, tested both halves and that another profile may still register |
| Healthy incumbent kept | `LicenseApproverHealth.CanApprove(existing, now)` -> `healthy-incumbent` | OK, boundary tested at exactly 26 hours and one minute past |
| Replaced only when it cannot approve or is unchecked 26 hours | same test; `StateOf` returns `not-checked` past the window | OK |
| Unchecked reads not-checked | `StateOf`: null state or null time -> `not-checked`; older than 26h -> `not-checked` | OK, boundary tested |
| Limits and field shapes | 32 rows refused whole (33 tested, 32 taken); profile regex (64, safe characters); emails via `IsAccountEmail` after a 384-character pre-check; state in `Reportable`; check time within [-26h, +5min]; detail at most 200 with no control characters; can-approve must name its account and show no other | OK, 15 shape cases tested |
| Old record with no source reads owner-set | record default plus `WithSource` | OK, tested |
| A stored value that cannot be read is an error | bad JSON throws; unknown source word throws | OK for source; see finding 1 for state |
| Decided under the store lock | `Change<T>` holds `_gate` across read, decide and write | OK |
| Health update from a non-offering machine | the `Names()` branch runs before the `offers` check | OK, tested |
| 26 hours in one named constant | `LicenseApproverContract.NotCheckedAfter`; a test pins it to 26 hours | OK |

## Do the tests exercise the real caller?

Yes. The registration tests drive `LicenseApproverRegistration.Report` over the real `LicenseStore`,
`LicenseReadModel`, `TenantSettingsStore` and `LicenseApproverStore` on a throwaway SQLite file; machines are
created by real `ApplyReport` calls, not hand-built views. The hub tests drive the real `DirectorHub` through
`Hello` with the real registration behind it, so the binding rule is tested where it lives. The store tests
build a registered record through the store's own `Change` (the same path the registration uses). The proof file
reports a disable-the-rule check (owner-set and healthy-incumbent switched off, four tests went red), which is the
right kind of evidence that the tests carry weight.

## Findings

### 1. A stored state word outside the six is read back as-is (low)

File: `src/CcDirector.Gateway/Licenses/LicenseApproverStore.cs`, `Read` / `WithSource` (lines 280-293 of the
new file), and `LicenseApproverHealth.StateOf` (lines 49-56).

`WithSource` throws on a source word the code does not know, honouring "a stored value that cannot be read is an
error". `State` gets no such check: a stored `"state":"anything"` with a recent `checkedAtUtc` is returned by
`StateOf` verbatim. In this pull request the only consumer is `CanApprove`, which compares to one word, so the
harm here is nil. The harm arrives with pull request 2: `LicenseApproverReadiness` folds a sentence per state word,
and an unknown word must either throw at fold time (a read error surfacing on the Licenses page rather than at the
store) or be folded into something plausible, which rule 7 forbids. The fix is one line in the same place as the
source check: refuse a non-null `State` that is not in `LicenseApproverStates.All` when reading. Not blocking; it
could equally be done in pull request 2 when the fold lands, but it belongs beside `WithSource`.

### 2. Contract section 9.3 says a report with no rows fails the call; the code takes an empty list (nit)

File: `missions/license-switching/contract.md` section 9.3, first sentence. Code: `LicenseApproverRegistration.Report`
line 316 (`report?.Rows is not { } rows`).

The code fails the call only when the row LIST is missing; an empty list is taken and answered with an empty list,
which is the sensible behaviour for a machine with no profiles. The proof table says "a report with no row list",
which matches the code. The contract says "has no rows", which does not. Developer C2 builds the Director side from
the contract and could reasonably add a "do not send when empty" branch that is not needed, or expect a failure
that never comes. Change the sentence to "has no list of rows".

## Nothing else

No high or medium findings. The design's refusals are each present, each a test, and the tests reach the real
callers. No real names, emails or addresses in the diff. No assistant attribution in either commit.
