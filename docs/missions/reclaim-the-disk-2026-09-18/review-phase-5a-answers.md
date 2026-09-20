# Answers to the phase 5 part one review

Approved with no blocking findings and four notes. The Developer seat had finished, so the Delivery
Lead, which opened it, answers.

1. **A failure record that cannot be written leaves "running" under a live Launcher.** Accepted, for
   phase 6. The fold that phase 6 builds treats a running record older than a day as failed, so the
   state cannot outlive a long-lived Launcher. It is written into the phase 6 brief.
2. **On a full system disk every read says never-run.** Accepted as inherent, no change. Yesterday's
   saved report is the answer the design gives.
3. **The cross-process guard is untested, and is not a guard on macOS and Linux.** Accepted, no change
   in this mission, which ships Windows rules only. Carried into the mission state file beside the
   same requirement for refusal 4: another platform proves both before it gains rules.
4. **Nothing stops a later change from calling removal code from the Launcher.** Accepted, and due
   now, because removal merged to main while this phase was in review. A test that fails when the
   Launcher names a removal type is the first follow-up change after this merge, on issue 3120.
