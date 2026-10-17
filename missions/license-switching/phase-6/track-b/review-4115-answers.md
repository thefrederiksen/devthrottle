# Answers to the review of pull request 4115

The review is `review-4115.md`. It found no code defect, made two findings about the evidence, and listed six
observations. Every point is answered below, in the review's order. The branch was rebased onto origin/main
0702c88ce first, which brought in Track A's license alarm (#4110).

## The rebase

#4110 touched five of the same files: `LicenseStore`, `LicenseDisplayFold`, `GatewayDbContext`, both migration
snapshots, and `contract.md`. How each conflict was resolved:

- **The two log-kind lists** (`LicenseLogKinds` and the fold's kind texts) gained both sides: the alarm kinds and
  the cannot-switch kinds.
- **`contract.md`:** #4110 added its own section 9 (the license alarm), so this pull request's section is now
  **section 10**, "Phase 6 - whether a machine can run a switch". Every code comment and document that cited
  "contract section 9.1" for this change now cites 10.1. Track A's citations of its own section 9 were left alone.
- **The migrations were made again, not merged.** The old `20261017093812` and `20261017093932` migrations were
  deleted, both snapshots were taken as they are on main, and `dotnet ef migrations add AddLicenseMachineCannotSwitch`
  was run again for SQLite and for Postgres. The new `20261017121400_...` (SQLite) and `20261017121456_...`
  (Postgres) come after #4110's alarm migrations. Each adds only the one column (`license_machines.CannotSwitchReason`,
  32 characters). The snapshots gain only those four lines.

## Finding 1 - the Gateway unit suite was not run on the commit that merges

**Agreed, and it was run on the new head.** The gate rule from the Delivery Lead is the default gate plus
`.\scripts\test-local.ps1 -Parked -Filter License`, with TEMP on drive D. Both were run on the rebased head, and the
results are in `tool-health.md`, section "The gate", which replaces the evidence from the earlier commits.

## Finding 2 - the proof does not say what a crash during the swap leaves behind

**Agreed, and it now does.** `tool-health.md` has a new section, "A crash in the middle of the swap".

- **What is on disk:** it gives the state for each place the process can die, and whether the tool is there.
- **Why the next tools refresh repairs it:**
  - the bundle version is recorded only after the swap, so `ToolUpdater` sees the machine behind and runs the
    installer again;
  - `RemoveLeftoverTrees` removes the leftover folders, and it runs only after a verified install, so the old venv
    is never deleted first.
- **What the machine reports meanwhile:** "cannot switch", and the Gateway sends it nothing.
- **What the repair depends on:** the Director being alive, which #4118 and #4117 cover.

I checked the reviewer's reading against the code before writing it (`PythonToolsInstaller.cs` steps 8 to 11,
`ToolUpdater.cs` `versionBehind` and `unhealthy`, `LeftoverSuffixes`), and it holds. As the reviewer allowed, this is
stated rather than tested. Interrupting the process between two directory renames needs a seam in the swap, and that
is new work, not this pull request's.

## Observations

1. **A Director that restarts while the tool is missing reports nothing, so it shows "Not reporting", not "Cannot
   switch".** Agreed, by design and kept. An empty holdings list would tell the Gateway every holding was removed,
   and that is worse than a less specific status. The machine is still skipped, so no wrong switch is sent.
2. **The "not-known" reason showed its code word in the log row's `reasonText`.** **Fixed.**
   `LicenseDisplayFold.ReasonText` now maps `not-known` to "The machine's Director says it cannot run a switch, for
   a reason this Gateway does not know." The test
   `AReasonThisGatewayDoesNotKnow_IsStoredAsNotKnown_AndTheMachineIsStillNotSentASwitch` asserts that sentence on the
   logged row. With the mapping removed, both cases of that test fail.
3. **The relocation matches the staged path byte for byte, ordinal.** Kept. Both strings come from the same
   `InstallLayout` value, and a mismatch fails safe: the update is refused, and the tools are untouched and named in
   the setup log. Making the match case-insensitive on Windows would make the rewrite match text it did not write.
   The measured Windows run went through the real path.
4. **A 46 ms blip can produce two log rows.** Agreed. They are rare, and they are honest about what was seen. Kept.
5. **The install lock finding is real and older than this change.** Agreed. It stays finding 1 in `tool-health.md`,
   for the Tech Lead to schedule.
6. **No real name in either diff.** Checked again after the rebase and the edits: nothing.

## What the review read but did not run

The reviewer ran nothing from this pull request. The gate runs on the new head are mine, as noted under finding 1.
