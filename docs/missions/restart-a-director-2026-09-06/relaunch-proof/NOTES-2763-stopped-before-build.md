# Issue 2763 relaunch proof - stopped by the owner's order before any script was written

Status: STOPPED 2026-09-07 on the owner's order, before scripts/director-relaunch-proof.ps1 existed.
Nothing below is proven. These are the findings from reading the code on branch
fix/guard-before-update-actions (head bf850b83d) while designing the rig.

## What was established by reading

1. The single-instance mutex is keyed by SHA256 of the full exe path plus the instance slug
   (DirectorIdStore.DefaultSlotKey), so an isolated rig under its own root has its own guard and
   cannot collide with the installed Director or the development slots.

2. BLOCKER FOR AN ISOLATED RIG: both relaunch paths strip CC_DIRECTOR_ROOT from the child
   (UpdateInstaller.BuildRelaunchStartInfo removes it; ApplyUpdate's Relaunch uses the same method).
   The relaunched build then resolves its shared root from the platform data directory
   (CcStorage.Base -> Environment.GetFolderPath(LocalApplicationData)), which the LOCALAPPDATA
   environment variable does not redirect on Windows. So a rig Director started with
   CC_DIRECTOR_ROOT pointing at an isolated root would, after EITHER relaunch, come back on the
   REAL root %LOCALAPPDATA%\cc-director - registering in the real fleet and reading the real
   Gateway configuration. That is the widening onto the real root the mandate forbids. The proof
   cannot be driven on this tree without either (a) the relaunch carrying InstanceContext.SharedRoot
   explicitly instead of removing the variable, or (b) a different isolation mechanism. This is
   also a product finding: a Director serving a non-default shared root forgets that root across
   its own update relaunch.

3. The rollback path (TryRollBackFailedUpdate) fires only when PendingHealthCheckVersion differs
   from the RUNNING version and a .old backup exists. A build that was installed and never came
   healthy is, on the next start, the running version - so pending equals running and no rollback
   fires. The state the rig must arrange is: target = one build, .old = a build of a DIFFERENT
   version (so the restore is witnessed by version), pending = a third version string. Four
   published builds were planned: installed 2.0.4, staged 2.0.99, backup 2.0.3, and a control
   copy of the backup with the wait in Program.Main neutered.

4. Both the rollback notice and the busy-guard notice are native MessageBoxW calls that block
   until dismissed (Program.Main: "Director - Update rolled back" before the child is started;
   "Director" after a refused guard). An unattended rig has to dismiss them, and must do so only
   for windows owned by process ids running from under the rig root, never by title alone -
   the real Directors' windows also carry the word Director.

5. Control design chosen: neuter the WAIT in Program.Main (keep the --wait-for-exit argument),
   because that reproduces "handshake present but not functioning" - the macOS shape from 2762 -
   and that control build still passes every RelaunchHandshakeTests test, which is the point.

6. Concern to measure, not assume: after Process.Start of the child the parent only calls
   FileLog.Stop() and returns, releasing the mutex within milliseconds, while the child needs
   process creation plus runtime start to reach TryAcquire. The control may therefore PASS (the
   child wins the race). The mandate says a passing control means the proof is not measuring the
   reordering; several control runs with log timestamps were planned to characterise the margin.

## Real processes at the time of stopping (must remain untouched)

cc-director pid 18692 at %LOCALAPPDATA%\cc-director\app (the installed Director),
cc-launcher pid 69640 at %LOCALAPPDATA%\cc-director\launcher, plus a separate restart-qa-rig
Director 74264 and launcher 52752 under cc-director-restart-qa-rig. None were touched.
