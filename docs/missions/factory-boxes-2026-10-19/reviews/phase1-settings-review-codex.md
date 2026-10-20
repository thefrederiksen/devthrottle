# Phase 1 settings workflow review

## Scope

Read pull request 4293, its full three-file change, the current staff-email workflow on `origin/main`, `CLAUDE.md`, and sections 5 through 8 of the Factory Boxes mission on `origin/main`. Compared the new workflow with the staff-email workflow and read `BoxesSwitch.cs` and the workflow contract tests.

Ran the focused `BoxesSettingsWorkflowContractTests`: 4 passed, 0 failed, 0 skipped. The first attempt did not run because the worktree had no NuGet assets; the restored run completed. For a regression check, temporarily weakened the desktop-origin expression to accept any HTTPS value, including a path and query string. The same four tests still passed. Restored the workflow afterward; the only change in this worktree is this review file.

The workflow uses the same OpenID Connect login, concurrency group, environment, and production wait step as the staff-email workflow. Its setting commands target only the two Boxes names. The input values are read from the event file rather than inserted into shell source; tenant ids are masked before subsequent output and are not echoed. The desktop-origin check rejects the Gateway host without regard to letter case and with a port or trailing slash, and its current expression rejects paths, queries, and fragments. `none` selects deletion. Tenant ids are split and trimmed like `BoxesSwitch`, then checked against its lowercase hyphenated identifier format.

Could not reach a live run of the new workflow because running a GitHub workflow was expressly prohibited. I did not verify the deploy guide's claims about GitHub's public run record or log against a live run or API response. No Azure command was run.

## Findings

1. [src/CcDirector.Core.UnitTests/Release/BoxesSettingsWorkflowContractTests.cs:57](/home/soren/ReposFred/devthrottle-review-settings-codex/src/CcDirector.Core.UnitTests/Release/BoxesSettingsWorkflowContractTests.cs:57) — The guard checks for the Gateway host string, lowercasing, an identifier pattern, and an exit command, but does not assert that desktop origins must have no path, query, or fragment. Evidence: replacing the current origin expression with `^https://.*$` admits those invalid values, yet all four contract tests still pass. A future regression could therefore write a desktop origin the Gateway cannot parse; box desktops would fail to open while the guard reports success, leaving an operator to diagnose and correct a production setting after a restart. Add a check that fails when a path, query, or fragment is accepted.

## Verdict

One finding; the workflow meets the checked settings, serialization, input handling, and current validation requirements, but its contract tests do not protect the no-path, no-query, and no-fragment requirement.
