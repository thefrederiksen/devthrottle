# PR 2919 inspection — head 97a64fb1a

MERGE DECISION: HOLD.

The phone Chat verdict panel is removed. The judged-row guard passed on the PR (3/3); a temporary re-mount made its `.verdict-panel` assertion fail (1 failed, 2 passed), then the worktree was restored. The removed `compact` prop has no remaining caller; workspace typecheck passed. The Cockpit still mounts its panel: its mount test passed, its full suite passed (40 files, 340 tests), and the client-core VerdictPanel suite passed (21 tests). The mobile suite passed (13 files, 77 tests). Voice, Terminal, and roster were unchanged by this diff; no live-phone layout check was run.

Blocking finding: the deleted Chat notice was the only phone rendering of `manage.sessionProblem`. A failed roster poll clears the session row but retains prior snooze state and sets that problem; a missing or invalid `triageBucket` likewise leaves the previous `snoozed` verdict while setting the problem. `SessionAppBar` can therefore continue showing a stale or false Snoozed pill with no visible explanation. Restore a read-failure notice without restoring the verdict panel, or otherwise make this failure visible.

Review comment: https://github.com/thefrederiksen/devthrottle/pull/2919#issuecomment-5695888287
