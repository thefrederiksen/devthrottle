# Codex input routing - Manager handoff (final)

Verbatim final handoff from the Codex Manager (session 49eeea04), received
2026-09-12:

> Branch fix/codex-input-routing is clean and pushed at
> 629db45b393ca432456b41c30fed84f511c9efa4. Change: direct single-line input
> through 1000 chars; short file fallback for multiline or over 1000; strict
> history resolver shows stored original; no deletion because the file may be
> the only durable prompt. Revert proof: boundary test failed 4/4 before and
> passed 4/4 after; history bypass failed then passed restored. Final tests:
> focused 46 passed; full Core 4415 passed and 8 skipped; ordinary nine-project
> gate passed. Live installed Codex 0.153.4: 301, 500, 750 passed; 1000 passed
> 6/6; 1348 and multiline fallback passed. Proof summaries are under
> D:\ReposFred\proof\codex-input-routing-*. Not proven: live outer Gateway
> framing. Database-polluted broad run was cancelled cleanly; no remaining
> process and lock is free. User said go for inspection and landing.

Follow-up answers from the same Manager, same day:

1. No GitHub issue number is known for this work.
2. Owner's words for the why: "Why are we getting all these input read files in
   these Codex sessions? Where are all those temp input read files coming from?
   It makes it really confusing and hard to understand what's going on."
3. Both the 1000-character direct threshold and the no-deletion ruling were
   inferred by the Manager during the run, then accepted when the owner agreed
   to the recommendations. The 1000-character threshold matches the existing
   LargeInputThreshold and was live-tested.

A note for the record, visible in the ask transcript: while answering the
Architect's follow-up question, the Manager's own Codex terminal received that
question wrapped in the legacy "Read file input_..._ttpmnk.txt" instruction -
live evidence of the defect this mission removes.
