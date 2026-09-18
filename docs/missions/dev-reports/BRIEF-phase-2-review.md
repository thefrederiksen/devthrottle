# Review brief - phase 2, the Gateway report record and delivery

You are an independent reviewer from a different agent family. You did not write this code. Be adversarial and
do not trust the mission's own reports - they are self-testimony. You REVIEW ONLY: change no code, commit
nothing, push nothing.

**Worktree:** `D:\ReposFred\devthrottle-dev-reports-p2-review` (detached at the commit under review, `0cd8b2db9`).
**The change:** `git diff 37700cc27 0cd8b2db9 -- . ':!tools/cc-devthrottle' ':!tools/cc-director-setup-engine*'`
is the mission's phase 2 work plus a merge of main; focus on the files under `src/CcDirector.Gateway/DevReports`,
`src/CcDirector.Gateway/Api/DevReportEndpoints.cs`, `src/CcDirector.Gateway/Data` (entities, context, both
`AddDevReports` migrations), `src/CcDirector.Gateway/Util/SessionKeyGuard.cs`, the dev report parts of
`src/CcDirector.Gateway/GatewayHost.cs`, `src/CcDirector.Core/Sessions/SubmissionProvenance.cs`, their tests, and
`tools/cc-dev-reports` plus the `tools/cc_shared/gateway.py` change.

**The spec it must meet:** `docs/missions/dev-reports/PLAN-phase-2.md`, `HANDOFF-phase-2.md`, rulings in `STATE.md`,
and `packages/client-core/src/devreports/CONTRACT.md` section 3 (item shapes, "a host MUST treat an id it has
already accepted as the same item", "a later answer to the same question as replacing an earlier one").

## The sharp questions

1. **Can anyone but the owner read or answer a report?** Another account, a session key (own or other session),
   a device key on session routes. Is every route tenant-scoped at the store, not just filtered at the endpoint?
   Does a 404 or any timing or count leak a report's existence?
2. **Can an item be delivered twice, or never?** Resend, a send racing a turn end, a send racing another send, a
   Gateway restart mid-drain, a delivery whose answer is lost, a failure between marking and sending (is the item
   marked delivered before or after the send, and what happens on a crash in between?).
3. **Does the owner's text reach the agent verbatim?** Any trim, truncation, normalisation or escaping in the
   prompt fold or the parser? Could text in a note forge structure in the prompt that misleads the agent about
   what the owner said (for example a fake "Answer to ..." line)? Is that worth guarding?
4. **Ended vs merely offline.** Is the ended verdict right? Can a live session be refused, or an ended one hold
   items forever?
5. **Where could a constant be substituted and the suite stay green?** Tests that hand-build their input and
   never watch the real caller; hosted tests that pass for a reason other than the one claimed.
6. **Migrations**: both providers match the model, indexes and collation right, nothing SQLite-only.
7. **The tool**: does it match the Gateway's real answers (422 body, 413 body, list order, key)? Unknown flag,
   timeouts, missing environment.
8. Logging, error handling at entry points only, no fallbacks (repo `CLAUDE.md`).

## Output

Write your review to `D:\ReposFred\devthrottle-dev-reports\docs\missions\dev-reports\REVIEW-phase-2.md` (the only
file you may write): each finding with severity (critical / high / medium / low), file and line, the concrete
failure scenario, and how you established it (read, ran, reproduced). Say plainly what you checked and found
nothing in. Then reply to the Manager, session 2ba644bd, with ONE line:
`cc-devthrottle message send 2ba644bd "<counts by severity> - review written"`.
