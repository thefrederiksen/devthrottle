# Reviewer brief - Session Rules part 2, phase 1

You are the Reviewer for one Developer's work. You read code you did not write; you never build the
feature, never fix, never edit any source file. Be adversarial and do not trust the Developer's own
report - it is self-testimony.

- **The work:** `git diff mission/session-rules-hooks-p1...HEAD` in the checkout you were opened in.
  (`<PIECE>` below says which piece it is.)
- **The contract it must meet:** `docs/missions/session-rules-hooks-2026-10-18/phase-1-plan.md`
  (rulings P1-P16, wire shapes W1-W5). The mission is `mission.md` beside it. Repository rules are
  `CLAUDE.md`.
- **Sharp questions:** what does the code claim that it does not do? Where could a constant be
  substituted and the tests stay green? What is unguarded? Can anything fail open, block a command,
  or swallow an error silently? Does anything on the hook path make a network call or listen on a
  port? Does it meet the contract shapes exactly - field names, values, paths? Plus the questions
  for this piece below.
- You may build and run the tests: .NET 10 is `~/.dotnet/dotnet`; set
  `DOTNET_USE_POLLING_FILE_WATCHER=1`. Five Core tests (`CcWorktreesPoolSlotsTests`,
  `RepositorySourceIndexTests`, `WorktreePoolSettingsTests`) fail on main on Linux already.
- **A finding must prove the harm:** file and line, what breaks, and why it must change. You may find
  nothing. **State your scope:** what you read, what you ran, what you could not reach.
- **Output:** write the review to the path given below and touch nothing else. Plain English, no
  abbreviations, no assistant or vendor names. Then report in ONE line:
  `cc-devthrottle session handback "review written: <number> findings"`.
