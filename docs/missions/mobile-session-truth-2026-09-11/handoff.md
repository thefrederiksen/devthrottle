# Mobile session truth handoff

Mission: `4962cc51-2b35-4c26-abe8-81b199477fb3`

Branch: `fix/wingman-terminal-narration-1991`

Current state:

- Wingman slice merged through pull request `#2813`; `origin/main` contains repair head `3392a6f3b` through merge commit `3e04cc8d6`.
- Mutation proof is recorded in `narration-repair-report.md`: guard-only run `34664743385` failed only the new production-path regression; repaired run `34667658171` passed the regression and every hosted CI job.
- Fresh independent inspection passed with no critical, high, or medium findings. The first failed review and the repair review remain in `inspection-wingman.md` and `inspection-wingman-repair.md`.
- The full local shared-environment gate was not clean: Gateway projects encountered unavailable PostgreSQL and hosted tenant/auth contamination. The clean hosted checkout is the complete-suite proof.
- The mobile roster-card slice merged through pull request `#2814`: product commit `e51b92051` plus proof-repair commit `7499d8d76`, contained by merge commit `53b8abfc3`. Exact mutation runs and suite counts are recorded in `roster-card-report.md`.
- The first independent inspection failed because the claimed served-path test called `StampFleetRolesAndFold` directly instead of requesting the real `/sessions` route. The repair adds an authenticated, tenant-scoped, host-bound route test whose mutation run left its auth/tenant control green while both response-field facts failed.
- The repair also renders the assembled `Home` polling/grouping path and proves a newer live display value replaces an older retained value.
- Fresh reinspection passed with no high, medium, or low findings. Both the failed and passing reports remain in `inspection-roster-card.md` and `inspection-roster-card-repair.md`.
- Commit-bound hosted run `34672537780` passed on attempt 2. Attempt 1 ran all 2,462 Gateway tests successfully, including all three new route facts, but one unrelated Launcher process-identity fact failed and skipped the installer step. On the unchanged commit, attempt 2 passed the .NET build, the exact Launcher fact twice, all three route facts by name, 2,415 passed plus 47 skipped Gateway facts, and installer totals of 25 of 25 and 541 of 541. Web and tool-contract jobs were already green.
- Hosted deployment run `34679499149` deployed merge commit `53b8abfc3` successfully. Production `/healthz` answered HTTP 200; the workflow reported the site healthy in 35 seconds, 8.9 seconds total unavailability, a 4.4-second longest external outage, no swap gap, and no rollback.
- The explicitly approved browser activation registered one `Android phone` device for `soren@centerconsulting.com`. At a real 390 by 844 viewport the production roster rendered 23 cards and exactly 23 derived `.row-chip-agent` elements. The visible values included `Claude Code`, `Pi`, and `Codex`; screenshots and the full evidence account are in `production-live-proof.md`.
- The supplied `testing pi` session was not changed by this mission, but it cannot complete the final live Wingman check: production history says it closed at `2026-09-12T00:45:31.690641Z`, before deployment finished. Its old cached narration remains readable, while the live Voice route says it is reconnecting and has no terminal to read. Treating that stale clip as a pass would cover the wrong source-selection path.
- Next: obtain a live session already in the same newer-message/later-terminal-auth-failure state, without sending a prompt or changing its credential, then invoke the read-only Wingman explain/play path and record the spoken terminal-failure explanation. Only then mark the brief complete, land the final record slice, and complete the Mission.
