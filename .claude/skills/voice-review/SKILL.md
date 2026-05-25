---
name: voice-review
description: Review the voice-turn comparison log for fidelity drift (what the user said vs what was cleaned, and what the agent said vs what the wingman spoke), archive flagged turns locally, and file sanitized GitHub issues. Triggers on "/voice-review", "review voice log", "review wingman fidelity", "review voice turns".
disable-model-invocation: true
---

# Voice Review

Review the voice-mode turn log, judge how faithfully each turn was handled, archive
the problem turns somewhere personal (NOT the repo), and file content-free GitHub
issues recommending what to fix and why.

This skill is the large-language-model judgment layer over the per-turn log added in
issue #141. It runs nightly via a local Windows Scheduled Task and can be run on demand.

## THE PRIVACY RULE (read first, never break)

Everything the user says by voice is personal. Therefore:

- A GitHub issue MUST NEVER contain recording content: no raw transcript, no cleaned
  transcript, no agent reply text, no spoken-reply text, no audio, and no quotes or
  paraphrases of any of them. Not even a snippet.
- A GitHub issue MAY reference a turn ONLY by its turn id and the archived local file
  path (which lives outside the repo). The path and id are safe; the content is not.
- The detailed analysis WITH the actual quotes lives ONLY in the local archive under
  `%LOCALAPPDATA%\cc-director\voice-review\`, never in the repo and never on GitHub.

If you cannot describe a defect without quoting the recording, describe the defect
CLASS in your own abstract words (e.g. "numeric facts dropped from the spoken reply")
and point to the archived turn for the human to inspect privately.

## Data locations

- Source log (auto-purges after 5 days):
  `%LOCALAPPDATA%\cc-director\voice-turn-logs\<timestamp>_<turnId>\`
  containing `audio.<ext>`, `inbound.json`, `outbound.json`.
- Personal archive (created by this skill, persists, OUTSIDE the repo):
  `%LOCALAPPDATA%\cc-director\voice-review\flagged\<turnId>\`  - full copy of a flagged
  turn plus `review.md` (the detailed analysis with quotes).
  `%LOCALAPPDATA%\cc-director\voice-review\digests\<YYYY-MM-DD>.md`  - the run digest.

On Windows expand `%LOCALAPPDATA%` to `C:\Users\<user>\AppData\Local`. In bash it is
`$LOCALAPPDATA`.

## What to judge (the rubric)

For each turn read `inbound.json` and `outbound.json` and assess two hops:

1. INBOUND fidelity - `RawTranscript` vs `CleanedTranscript`:
   - Did the cleanup change the user's intent, drop a clause, answer/retract something
     differently, or over-condense rambling into a different meaning?
   - Fixing an obvious mishearing or removing filler is GOOD. Changing what was asked
     is BAD.

2. OUTBOUND fidelity - `AgentReply` vs `WingmanSpoken`:
   - Were concrete facts dropped (names, numbers, yes/no, the decision/result)?
   - Was the topic reframed, the answer softened, or the question left unanswered?
   - Speaking facts ear-friendly (e.g. "SaaS" -> "software as a service", verbalizing
     an address) is GOOD. Losing the fact is BAD.

Assign each turn a verdict: `ok` (faithful), `minor` (cosmetic, no meaning lost), or
`flag` (meaning changed or a fact lost). Record a one-line reason in your own words.

## Steps

1. **Find unreviewed turns.** List `voice-turn-logs/*/`. A turn is unreviewed if it has
   no `reviewed.json`. Review only those (so re-runs are incremental).

2. **Judge each turn** on the rubric above. Be a strict reader: completeness and
   intent-preservation are the bar, not vibes.

3. **Archive flagged turns.** For every `flag` (and `minor` if you think it is worth
   keeping), copy the whole turn directory to
   `%LOCALAPPDATA%\cc-director\voice-review\flagged\<turnId>\` and write a `review.md`
   there containing your full analysis WITH the relevant quotes (this file is personal
   and stays local). This is the durable evidence, because the source purges at 5 days.

4. **Write the digest.** Append a section to today's digest at
   `%LOCALAPPDATA%\cc-director\voice-review\digests\<YYYY-MM-DD>.md`: how many turns
   reviewed, the count per verdict, and a one-line-per-flag summary (quotes allowed
   here - it is local and personal). Keep it skimmable.

5. **Mark reviewed.** Write `reviewed.json` into each source turn dir with
   `{ verdict, reason, reviewedUtc }` so it is not re-reviewed before it purges.

6. **File GitHub issues - but only for things worth fixing, and SANITIZED.**
   - Decide what is systematic. A single odd turn is usually evidence to watch, not an
     issue. A repeated pattern (same defect class across multiple turns) is an issue.
   - Categorize each as `bug` (a fidelity regression, label `bug`) or `enhancement`
     (a quality/robustness improvement, label `enhancement`). Always also add the
     `voice-review` label (create it once if missing:
     `gh label create voice-review --description "Filed by the voice-review skill" --color BFD4F2`).
   - DEDUP before creating: `gh issue list --label voice-review --state open`. If an
     open issue already covers this defect class, add a brief comment that it recurred
     (referencing new turn ids/paths only) instead of opening a duplicate.
   - Each issue MUST state, content-free:
       - the defect class (abstract description, no recording content),
       - WHY it matters (the fidelity consequence to the user),
       - the RECOMMENDED fix and where (e.g. tighten the inbound cleanup prompt in
         `src/CcDirector.Core/Wingman/WingmanService.cs`, or the outbound prompt in
         `src/CcDirector.Core/Voice/Services/ClaudeSummarizer.cs`),
       - a REFERENCE block: the turn ids and their archived local paths under
         `voice-review/flagged/` so the human can inspect the evidence privately.
   - Use a HEREDOC for the body. Run the sanitization self-check below before creating.

7. **Report back** (to the console / digest, local only): list issues created or
   commented, and the archive paths. Quotes are fine here since it is local.

## Sanitization self-check (run before every `gh` call)

Before creating or commenting on any issue, re-read the exact text you are about to send
and confirm: it contains NO transcript text, NO agent reply text, NO spoken-reply text,
and NO paraphrase of any of them - only the abstract defect class, the why, the fix, and
turn ids + archived paths. If any recording content is present, rewrite it abstractly.

## Issue body template (content-free)

```markdown
## Defect class
[Abstract description of the fidelity problem. NO recording content.]

## Why it matters
[The consequence to the user: meaning changed / fact lost / question unanswered.]

## Recommended fix
[What to change and where, with file path. Why this fix addresses the class.]

## Evidence (local, personal - not included here)
Turn ids: <id1>, <id2>
Archived at: %LOCALAPPDATA%\cc-director\voice-review\flagged\<id1>\ (and <id2>)
Reviewed by the voice-review skill on <date>.
```

## Notes

- The agent running this skill IS the language model doing the judgment; do not shell
  out to another model. Read the JSON, reason, decide.
- Never delete source turns yourself; the 5-day purge handles retention. Your archive is
  the long-lived copy.
- If there are no unreviewed turns, write a one-line "nothing new" digest entry and stop.
  Do not create issues when there is nothing to report.
- This skill performs external actions (GitHub issues). When run interactively, show the
  sanitized issue text before creating it if the user is present.
