# Phase 4 gate: the transport test hang (pull request #4102)

Run by the phase 4 Tech Lead's gate seat on SORENLAPTOP (Windows), 17 October 2026.

- Commit tested: `15f5ba1aa`, a clean local merge of origin/main with the pull request branch at `b1b7ec320`.
  Nothing was pushed from the gate. Merged to main as `7a66635a7`.
- Command: `.\scripts\test-local.ps1 -Parked` (Debug), then `-RerunFailed` once.

**The per-suite counts are not on record.** The gate seat's report reached the Tech Lead cut to its first line
twice, and the seat closed before it could land this file; the counts were on SORENLAPTOP only, which the owner
has since shut down. What the Tech Lead received and can stand behind:

- All 65 API-mode transport tests (`ApiModeTransportEndingTests`, `ApiModeTransportTests`, in Core.UnitTests)
  passed, the longest in 0.6 seconds. No suite hung.
- The only failures were nine tests with the "cannot create a symbolic link" permission error, every one failing
  again on the rerun with the same error. They fall under the Delivery Lead's standing ruling (decision 14). The
  same nine, by name, are listed in `phase-4-event-push-gate.md`, run on the same machine half an hour later.

**Verdict: passes the gate under the standing rulings.**
