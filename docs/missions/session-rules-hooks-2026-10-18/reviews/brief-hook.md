Read `brief-template.md` beside this file and follow it. For this review:

- **PIECE:** Developer B, the hook - `src/CcDirector.RuleHook` (the evaluator `cc-rule-hook`: P3
  resolver, W2 reader, W3 decision writer, W4 evaluation log), the PreToolUse registration in
  `ClaudeHookInstaller`, the Director embedding the evaluator and writing it beside the hook scripts
  (P16), the build script changes, and the timing scripts. The Developer's own account is
  `phase-1-hook-proof.md` - self-testimony.
- **Questions for this piece:** can the evaluator EVER block a command, print to standard output, exit
  non-zero, or hang past the hook timeout - on a malformed event, a malformed or huge rules file, a
  missing directory, a full disk, an unwritable log? Is P3 right in every tie and rank case, and P13?
  Does the match follow P1 exactly? Can a decision file be half-written or collide? Does writing the
  embedded executable race another Director instance or a running hook, and can it leave the hook
  registered against a missing or partial file? Does the registration change anything for sessions
  outside a Director, or for a seat's own settings file? Do the timing scripts measure what they say?
- **Write the review to:**
  `/home/soren/ReposFred/devthrottle-session-rules-p1/docs/missions/session-rules-hooks-2026-10-18/reviews/phase-1-hook-review.md`
