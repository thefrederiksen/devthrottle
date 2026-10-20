Read `brief-template.md` beside this file and follow it, with ONE difference: the work is not one diff
against the mission branch but three pieces already merged into it. Review each:

1. **The Windows Developer's commits:** `git log --oneline 4f21e04a7..1fae87257` plus `6a86311d8`, `64c41f86e`,
   `96ca70087` (see `git show` of each) - the captured Windows events and their test, the change to
   `scripts/measure-rule-hook.ps1` (it now runs the hook through Git Bash, the way Claude Code on Windows
   runs a hook), the reclaim classification of `CcStorage.SessionRules`, two log lines in
   `ClaudeHookInstaller`. Their own account: `phase-1-windows-proof.md`.
2. **The Linux live rig:** `git diff 4f21e04a7 162f35a7f -- scripts docs` and `b2ceb977b` -
   `scripts/session-rules-rig.sh` and `live-rig.md`. Questions: can it ever touch the owner's real
   Director, Gateway, `~/.local/bin`, or Claude Code sign-in; can `reset` delete anything outside its own
   folder; does `down` force-kill anything; does copying the sign-in risk rotating the owner's own?
3. **Bringing the branch up to main:** `git show e722ad39b` (the merge, with a conflict resolved in
   `src/CcDirector.ControlApi/GatewayClient.cs`) and `git show 40124743d` (the migration
   `AddSessionRuleKindsAndHookFirings` regenerated after main's `AddMailDesk`, for Sqlite and Postgres).
   Questions: did the conflict resolution keep both sides' behaviour; do both model snapshots hold both
   migrations and nothing else changed; is the regenerated migration the same schema change as before?

- **Write the review to:**
  `/home/soren/ReposFred/devthrottle-session-rules-p1/docs/missions/session-rules-hooks-2026-10-18/reviews/phase-1-rest-review.md`
