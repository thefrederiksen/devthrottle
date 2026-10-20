# Brief - Developer, the apostrophe in a grounded citation

You are a Developer on the Session Rules mission, phase 1 follow-on. Your seat:
`cc-devthrottle skill get devthrottle-method` (Developer section) - follow it. You report to the phase 1
Tech Lead (the session that opened you). Work only in this worktree, on branch
`mission/session-rules-hooks-p1-grounding`, cut from `mission/session-rules-hooks-p1-windows-rows`.

## Why

Read `phase-1-windows-rows-proof.md` section 3, "Finding A" and "Finding B", and the evidence it names
(`evidence/windows-rows-usage-limit-firings.json`). The September usage-limit rule fired live on Windows,
the model quoted the screen correctly - `'You've reached your Fable 5 limit'` - and the act was refused as
ungrounded, because `RuleReasonGrounding.QuotedPassages` (in `src/CcDirector.Gateway/Rules/RuleReasonGrounding.cs`)
pairs straight single quotes naively: the apostrophe inside `You've` closes the quotation, and the next pair
produces a passage that is not on the screen. The September notice itself contains `You've`, so this rule
cannot act on its own target text.

## The Tech Lead's ruling

**G1. An apostrophe is not a quotation mark.** A straight single quote, or a right single quotation mark
(U+2019), with a letter or digit immediately on BOTH sides is an apostrophe inside a word and never opens or
closes a quoted passage. Every other rule of the gate stays exactly as it is: the 8-character floor, the other
quote pairs, and the outcome that a reason citing text the screen does not contain is refused. The gate may
only become more accurate, never looser: a fabricated citation that contains apostrophes must still be refused.

## The task

1. Tests first, watched failing:
   - firing 2's exact reason against its exact stored screen, from the evidence file: grounded;
   - a reason quoting `'You've reached your Fable 5 limit'` against a screen that does NOT contain it: refused,
     naming that passage;
   - a quotation ending with a possessive or a plural apostrophe at a word end (for example `'the agents' turn'`)
     - decide what G1 does with it, and pin it with a test either way;
   - the curly-quote equivalents.
2. Fix `QuotedPassages` to G1. Keep it readable; log as the file already does.
3. Finding B: the Gateway's `no-candidates` line repeats the hook-rule reason once per hook rule. Make it say
   the reason once with the count, test first if the line is tested anywhere.
4. Run `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Rules"` and quote the counts.
   Environment: .NET 10 is `~/.dotnet/dotnet` (not on PATH); set `DOTNET_USE_POLLING_FILE_WATCHER=1`.
5. Write what you did in `reviews/grounding-apostrophe-answers.md` beside this brief.

## Rules

- Commit with `git -c user.name=thefrederiksen -c user.email=soren@duksrevo.com commit`. Never any assistant,
  model or vendor attribution, nor a Co-Authored-By or Generated-with line; grep before each commit.
- Plain English, no abbreviations. No sub-agents, no pull request, no merge.
- Push your branch and hand back in one line with `cc-devthrottle session handback`.
