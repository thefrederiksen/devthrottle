# Codex input routing - inspection brief (round 1)

Given to the independent Inspector (Claude Code, a different agent family to the
Codex Manager that built this) by the Architect, 2026-09-12.

You are the Inspector on the mission workflow (`cc-devthrottle workflow
instructions mission`). Be adversarial. Do NOT trust the mission's own report -
it is self-testimony by the people who did the work. Verify every claim against
the code and the evidence yourself.

## What is under inspection

Worktree `D:\ReposFred\worktrees\devthrottle-codex-input-routing`, branch
`fix/codex-input-routing`, commit `629db45` - exactly one commit ahead of
`origin/main`. Diff: `git show 629db45`. The mission brief is at
`docs/missions/codex-input-routing-2026-09-12/brief.md`; the Manager's claims
are in `manager-handoff.md`; live-proof artifacts are under `evidence/`.

## Claimed behavior (verify each against the code, not the claim)

1. Single-line input up to and including 1000 characters is submitted directly
   to the Codex terminal (no payload file) - for EVERY submit path a user
   message can take to a Codex session, not just the one the tests exercise.
2. Multiline or over-1000 input still uses the payload file, with the new short
   instruction.
3. Readable history resolves Director-owned payload references (current
   instruction, legacy instruction, bare @-reference) back to the original
   message, for the owned `input_YYYYMMDD_HHMMSS_xxxxxx.txt` pattern only, only
   when the file exists in the repository; never substitutes an unowned file;
   never rewrites anything other than a whole match.
4. Payload files are not deleted.

## Sharp questions - answer each explicitly

A. Trace the callers. Which code paths submit user text to a Codex terminal,
   and does every one of them reach `ShouldUseInstructionFile`? Is there any
   remaining path (echo submit, paste mode, another driver tag spelling, the
   Gateway's own message framing) that still routes short single-line Codex
   input through a file, or that bypasses the change?
B. Where could a constant be substituted and the suite stay green? Are the new
   tests asserting behavior or implementation details? Name any test that
   would NOT fail if the feature it claims to cover were broken.
C. What is unguarded in `StoredPromptPayloadResolver`?
   - The regexes carry a 50ms timeout; `Regex.Match` THROWS
     `RegexTimeoutException` on timeout - trace what happens to that exception
     in every caller of `SessionHistoryReader.Read`. Who reads history, and how
     often? Does one adversarial message break a screen?
   - A user message that happens to BE the instruction text for a file the
     Director owns: is substitution there correct, and can the substituted
     content then be substituted again (re-resolution)?
   - The 4000-character cap: the original message replaces the instruction, but
     a long original is cut at 4000 with "..." appended. Is the truncation
     identifiable to a reader who sees only the transcript?
   - Synchronous file reads inside a history read: which thread calls this, and
     does that violate the responsive-UI rule in CLAUDE.md?
D. `ShouldUseInstructionFile` for Claude Code, Gemini, Grok: the old code
   returned false for them when under 300 chars and not large; the new code
   returns false for them ALWAYS. Is that the same behavior in every case that
   previously reached them, including large and multiline input for those
   agents? If any agent previously received the instruction file for large or
   multiline input and now gets it typed raw, name it and say whether that is
   proven safe.
E. The live proofs ran at 10:07-10:11 local; commit `629db45` is dated 11:03.
   Is there anything in the evidence that pins the proofs to the exact code in
   `629db45`, or could the proofs have run against a slightly different working
   tree? The fallback run's `created-payload-*.txt` files and the screen text
   are in `evidence/` - do they actually prove the file route was taken (not
   just that the agent answered)?
F. The Manager reported "revert proof: boundary test failed 4/4 before". Can
   that be reproduced from the code alone (read the OLD `ShouldUseInstructionFile`
   and confirm the new test would fail against it), or does it rest only on the
   Manager's word?

## Output

Write your full review to
`docs/missions/codex-input-routing-2026-09-12/inspection-1.md` in the worktree:
findings numbered, each with severity (blocker / should-fix / note), the file
and line evidence, and an explicit verdict per claim (holds / does not hold /
cannot tell). If you changed nothing else in the tree, say so. Then reply to
the Architect with ONE single line (fleet messages truncate at the first
newline): verdict + count, e.g. "Inspection 1 written: 2 blockers, 1
should-fix, 3 notes". Do not fix anything - an inspector who picks up a hammer
is no longer an inspector.
