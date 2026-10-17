# Review of pull request 4106 - heal a hand sign-in over a spare, report "switched by hand"

Reviewer: a separate review session (Track B, License Switching). Head reviewed: 08d4dc744, based on
08dec1140; origin/main has moved one commit (the v2.23.0 notes correction), no overlap.

## Scope

Read in full: the whole of `tools/cc-devthrottle/src/license_store.py` as it is on the branch (the heal,
the two new journal steps, the roll back, recovery), the `license_ops.py` and `license_usage_ops.py`
changes, the usage reader's store access (`StoreKey.exclusive`, `held_keys`), the Director's
`CcDevthrottleLicenseCommandLine` and `LicenseUsageReporter` changes, the contract type, the Gateway's
`LicenseStore.ApplyReport` and `LicenseUsageIngest.Malformed` changes, contract.md section 2.6, the
proof file, the pull request body, and every test in the diff (Python store, command line, Director,
Gateway). Checked the mission rules against it: MISSION.md trap 2, spare-refresh.md decisions 3 and 4.

Ran: the Python key store tests on the branch, both backends (file and faked Keychain):
`test_license_store.py` 159 passed in 28.6 seconds on Python 3.11.6. Grepped the whole diff for real
emails, machine names, addresses and user names: none.

Could not reach: I did not build or run the .NET tests (a reviewer never builds into the worktree).
I did not run a real Claude Code against a healed folder. I did not test the wire across versions: the
backward-compatibility judgement below is from reading the SignalR set-up, not from a run.

## What I checked hardest, and found sound

- **Two copies or a lost key.** On the file backend the key moves with its folder in one `os.replace`:
  there is never a second copy. On the Keychain backend the item is written under the backup's name and
  deleted under the spare's only after the new item is proven to hold the same fingerprint, the same
  rule as every other move in the store. The only deletion of key material is the duplicate case (the
  spare holds the byte-identical key as the default folder), and `drop` deletes it only when the default
  folder is proven to hold it. That case IS trap 2, so removing the duplicate is the correct outcome.
- **Races.** The heal runs inside `_settle`, under the Python store lock, after `recover()`. A switch, a
  spare renewal (`StoreKey.exclusive` is the same reentrant lock), an add and a sign-in's write all hold
  that lock, so the heal cannot run inside any of them; a reader that finds the lock taken fails whole
  with `switch-in-progress` and heals next round (test
  `test_heal_LockTaken_DoesNothing_AndTheNextRoundHeals`). The transient switch state "default holds X
  and spares/X holds X" (between the bring-in step and the drop) looks like the heal condition, but
  `recover()` finishes or rolls back any journal before the heal looks, so the heal never sees it. A
  journal that can neither finish nor roll back raises out of `recover()` and the heal never runs.
- **The spare refresh in the held-twice state.** Before this change, with X active and X a spare,
  `held_accounts()` listed X twice, active first, and a spare refresh of X would have found the ACTIVE
  entry first and renewed the key under running sessions. With this change the heal runs inside that
  listing first, so the refresh sees X once, as active, and refuses. An improvement the proof does not
  mention.
- **A broken active sign-in.** The store can only reach "X active and X a spare" through a sign-in made
  outside it, which is by construction the newer one, so moving the spare is right whatever state the
  active key is in. If the active key is dead the report's active row carries the read error, the machine
  is reported as needing a sign-in, and the backup is still on disk for a person.
- **Crash safety.** Every change point of the heal is covered by
  `test_heal_CrashAtEveryChangePoint_TheNextRunFinishesTheHeal` on both backends, and a heal finished by
  recovery is still reported (`recover()` appends it to `last_heals`). Every heal step names its undo, so
  the roll back never hits "rollback-impossible".
- **Report once.** The pending hand switch is cleared only when a report carrying it was sent; a failed
  send keeps it; a switch that moved the machine on drops it rather than sending a report the Gateway
  would refuse. The ingest refuses a `SwitchedByHand` that is not the active holding. The Gateway names
  the previous account as lost only when its last ACCEPTED report had it active and the new report no
  longer holds it, which is exactly the 16 October case, because every held-twice report was refused and
  never updated the machine row.
- **Backward compatibility.** The report travels on the SignalR MessagePack protocol
  (`GatewayHost.cs`, `AddMessagePackProtocol()` with no custom resolver), which serialises by property
  name and skips unknown names, so an old Gateway ignores `SwitchedByHand` and an old Director's report
  arrives with it null (`ApplyReport_AReportWithoutTheField_WritesNoHandSwitchEntry`). The `healed`
  field is additive to the `--json` envelope, and an old tool with no `healed` field is read as "healed
  nothing". Judged from reading, not from a cross-version run.

## Findings

**1. (low) A heal whose rename raises an operating-system error escapes as a traceback instead of the
"does nothing, next round" path.** `_step_rename` calls `os.replace` on the spare folder. On Windows a
directory rename fails with `PermissionError` while any file inside it is open by another process (a
virus scanner, an Explorer window, a backup agent). `_run_journal` catches only `_StepFailed`, and
`license_ops._run` catches only `LicenseStoreError`, so the `OSError` propagates out of
`held_accounts()` and the tool exits with a Python traceback, not an AXI failure line, with the heal's
journal left on disk at step 0. The Director's `RunToolAsync` turns that into "no report sent" for that
round. The next round's `recover()` re-runs the rename and succeeds once the handle is gone, so it
self-corrects; the harm is a lost report round and a traceback in the Director log where the brief asked
for a quiet retry. The switch already handles exactly this: `switch()` wraps `_start` in `except OSError`
and calls `recover()`. Concrete case: a Windows machine with a scanner holding `.credentials.json` in
`spares/<slug>` open at the moment of the 2-minute read.

**2. (process, not code) The parked gate result for this pull request is not recorded.** The change
touches `src/CcDirector.Gateway` (ApplyReport, Malformed) and adds `LicenseHandSwitchTests` in
`Gateway.UnitTests`, which the default gate does not run. The pull request body says the parked run was
waiting for the gate lock and its result would follow in a comment; there is no comment. Until that run
is recorded, the only evidence the five Gateway tests pass is the Developer's word.

## Not findings, noted for the record

- `remove`, `sign_in_target`, `finish_sign_in` and `store_refreshed_key` call `recover()` rather than
  `_settle()`, so they do not heal. No harm: `_find` returns the active entry first, so a sign-in for X
  in the held-twice state plans a renewal into the default folder, and the next listing heals the spare.
  `remove X` in that state says "X is the active account", which is the right refusal.
- The contract says the heal "runs under the store's lock like every other store write"; the C#
  `LicenseKeyHolderLock` only decides which Director runs the tool. That is consistent with the brief,
  which named both locks, and the Python lock is the one that serialises store writes.
- `HandSignInHeal.from_journal` parses `startedAt` with `fromisoformat`; the journal writes it with a
  UTC offset at second precision, which Python 3.7 and later parse.
