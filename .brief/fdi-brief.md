# Factory Design and Improvements - brief (from Malik Grant, boss of Website Business, 8 Oct 2026)

The owner (Soren) asked for this session in a talk on 8 Oct 2026. He will talk with you directly.
Work in your OWN worktree of the devthrottle repo off origin/main (never in the shared checkout D:/ReposFred/devthrottle).

## Step 1 - a dev report FIRST, before any code
Read the dev-reports skill (cc-devthrottle skill get dev-reports) and the devthrottle-method skill, then write ONE dev report
(published with cc-dev-reports) covering both items below: the problem, the design, what changes where, the proof, and the
decisions the owner must make. Show it to the owner and wait for his go before building.

## Item A - a factory's boss can see its own Factories screen (devthrottle#3685)
On 8 Oct the owner saw Website Business marked FAILING on the Factories screen. Its boss could not see that, because a
session key gets 403 session_key_out_of_scope on GET /gateway/factories and GET /gateway/factories/{id}
(SessionKeyGuard.IsAllowed; factory/activity, factory/registry and factory/goal-numbers are already allowed, ~line 337).
Build:
1. Allow a session key to GET /gateway/factories and /gateway/factories/{id} (read only, own account). Handled, Talk,
   archive stay owner-only.
2. cc-devthrottle factory status [--factory <id>] [--json]: prints exactly what the screen shows, from the same
   FactoriesScreenFold - the status word (FAILING / NEEDS YOU / PAUSED / RUNNING), the waiting-on-you count, and every
   failing and waiting item with its activity row id, so a boss can act and then mark it handled
   (factory record --corrects <id>).
3. The boss of every factory runs it at the start and end of each run and in every owner talk (the factory boss
   prompts/skills, and the Talk seed the Gateway sends).
Done when a boss session runs `cc-devthrottle factory status --factory website-business` and sees the same word and
items the owner sees, and sees it turn to RUNNING after it marks the last resolved item handled.

## Item B - rename the factory "CEO" to "Boss"
The owner's reasoning: not every factory is a company, but every factory has a boss. Scope it in the report: product
words (terminology skill), Gateway/Cockpit labels ("Talk to the CEO"), the registry manifest key ceoSeat, seat ids,
the CLI, docs, and the per-factory files that say CEO (each factory's own repo; list them, do not edit other repos
without the owner's go). No fallback aliases: rename and migrate, per the owner's rules. Recommend an order.

## Rules
Foreground only. No assistant attribution anywhere. Committing and merging need the owner's explicit yes in this
session. Review by a separate session: Codex first, else Claude Code on Fable (never Pi, Grok or Gemini).
