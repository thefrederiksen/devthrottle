---
name: wingman-brief-reviewer
description: Adversarial cold-reader review of saved wingman turn briefs - blind-judges the brief alone, then checks it against TurnPackage terminal evidence, scores content + visual presentation, emits an HTML verdict report with a should-be rewrite. Triggers on "/wingman-brief-reviewer", "review briefs", "review the wingman", "wingman review loop".
---

# Wingman Brief Reviewer

Automated cold-reader review of wingman turn briefs (epic #203; feeds #208 wingman-learn
and #209 eval harness). The reviewer simulates the person the brief exists for: someone
who knows NOTHING about the session, wants AS LITTLE information as possible, but enough
to make the determination. It judges every saved brief twice - once blind (brief only),
once against the terminal evidence - then renders an HTML verdict report and records a
labeled verdict. Run wingman + reviewer in a loop until briefs stop failing; only then
do humans review.

## Quick Reference

| Action | What happens |
|--------|--------------|
| Review one case | `/wingman-brief-reviewer <sid> <turn>` - full A/B/C review of that brief, HTML report opened |
| Review a round | `/wingman-brief-reviewer round [N]` - the N most recent briefs fleet-wide (default 5), one report per case + a round summary |
| Review a session | `/wingman-brief-reviewer session <sid>` - every captured turn of one session |
| Distill | `/wingman-brief-reviewer distill` - aggregate verdicts.jsonl into proposed TurnBriefContract changes (STOPS for approval) |

## Where cases live (read-only - never write these stores)

| Artifact | Path |
|----------|------|
| TurnPackage (evidence) | `%LOCALAPPDATA%\cc-director\gateway-turnbriefs\<sid>.packages\t<N>.json` |
| Brief produced | `%LOCALAPPDATA%\cc-director\gateway-turnbriefs\<sid>.jsonl` (line where `turnNumber == N`) |
| Human feedback | `%LOCALAPPDATA%\cc-director\brief-feedback\` (downvotes/upvotes; review these FIRST when present) |
| The contract under review | `src/CcDirector.Core/Wingman/TurnBriefContract.cs` |

TurnPackage fields: `lastUserPrompt` (what the user asked - the turn's trigger),
`firstUserPrompt`, `rollingIntent`, `screenTail` (verbatim terminal, NO color - the
capture is plain text, do not penalize the brief for terminal aesthetics),
`transcriptDelta`, `lastAssistantText`, `replyPending`, `priorRailLines`,
`currentHeadline`.

A package without a matching brief line means the brief was superseded mid-generation -
skip it, note it in the round summary.

## Phase A - THE BLIND READ (subagent, brief only)

**HARD RULE: the blind reader must not see the TurnPackage.** Spawn a subagent (Agent
tool, no file access needed - inline everything) whose prompt contains ONLY what the
real pane shows, IN THE PANE'S SHAPE (fidelity lesson from rounds 1-6: a card field the
pane does not render produces phantom "noise" findings):
- YOU'RE DOING (intent), YOU ASKED (brief.youAsked if present, else the package's
  lastUserPrompt - the one package field allowed in), NEEDS YOU statement + options
  (+recommended) + ifIgnored, or ALL CLEAR; CLAUDE DID bullets last.
- Do NOT show: turnTitle, railLine (rail-only), headline as a separate line.
- The evidence quote is COLLAPSED behind a "Claude's words (verbatim)" button on the
  real pane - present it as: 'collapsed behind a button: "<quote>"' so the reader
  weighs it as optional depth, not card clutter.
Frame it like this, verbatim in spirit:

> You are a busy operator running many agent sessions: a VETERAN software developer,
> but NOT versed in this particular technology stack - any term of art the card does
> not explain (or make safely ignorable) is a defect, name it. This card is ALL you
> get - you have never seen this session before and you cannot open the terminal. You
> want the simplest possible presentation of ALL the information the decision needs -
> missing-but-necessary is as bad as surplus. Answer:
> 1. DECIDABLE: Can you confidently pick one of the offered actions right now? yes/no.
> 2. WHAT'S MISSING: every question you'd still have to ask before acting (e.g. "what
>    did I originally ask this session to do?", "what is X?", options whose
>    consequences you can't predict).
> 3. JARGON: every unexplained term of art on the card.
> 4. NOISE: every sentence/option you did not need for the decision.
> 5. In one line: what do you believe this session's overall job is, from the card alone?

Return structured JSON (use the Agent tool's schema option). The blind reader's
"what's missing" list is the single most valuable output of this skill - it is the
literal cold-reader bar from contract v3 being tested for real.

## Phase B - GOAL CHECK + EVIDENCE CHECK (reviewer sees everything)

**B0 - THE GOAL CHECK (the reviewer's anchor, per Soren's spec):** before judging the
turn, establish what this session was ORIGINALLY asked to achieve and how it is going
about it. Read the session's FULL brief history (`<sid>.jsonl` - every chapter headline,
intent evolution, prior needsYou lines), the package's firstUserPrompt (beware: restored
sessions carry seed boilerplate - the human goal may be inside the referenced handover or
later prompts), and when still ambiguous, the actual Claude transcript. Write the goal
down in one sentence. Then judge: does the card situate THIS decision in that goal -
what we are trying to achieve and how this choice serves it? A brief can be locally
perfect and still fail because the user cannot connect it to why the session exists.

Then read the TurnPackage and judge the brief against the ground truth:

- **Faithful?** Does every claim in the brief trace to the screenTail/transcript? Flag
  invented facts, wrong emphasis, stale state (e.g. text already parked in the composer
  that the brief ignores).
- **The real blocker?** Is needsYou pointing at what is ACTUALLY blocking on screen, or
  at something secondary? If the screen shows an interactive prompt/parked text/error,
  the brief must lead with it.
- **Options real and complete?** Every option must map to a real action the agent
  offered or an obvious operator move; the recommended one must match the agent's
  literal ask; missing-obvious-option is a finding.
- **ifIgnored concrete?** Names the actual consequence, not a generic "agent waits".
- **YOU ASKED grounded?** The brief's intent must answer "what did the user ask for" -
  cross-check lastUserPrompt + firstUserPrompt + rollingIntent. A cold reader who cannot
  reconstruct WHY this session exists from the card = failure (this is the complaint
  that created this skill).
- **TIGHT beats complete** - per contract v3, surplus prose is a defect, not a bonus.

## Phase C - VERDICT, SCORES, AND THE AFTER

Score 1-5 on each dimension (5 = no finding). The governing bar, in Soren's words: the
card must give, in the SIMPLEST form possible, ALL the information necessary to make
the decision. Never reward brevity that drops decision-relevant context - tightness and
completeness are separate dimensions and both must hold.

| Dimension | Question |
|-----------|----------|
| goalAlignment | Does the card say what the session is trying to achieve (its TRUE original goal, from the B0 goal check) and how this decision serves it? |
| decidability | Could the blind reader act without asking anything? |
| context | Could the blind reader say what the session's job is and what the user asked? |
| faithfulness | Everything traceable to evidence, nothing stale or invented? |
| blocker-accuracy | needsYou = the actual on-screen blocker? |
| options | Real, complete, consequences predictable, recommended correct? |
| jargon | Every term of art explained or safely ignorable, for a veteran developer NOT versed in this stack? |
| tightness | Zero sentences the decision didn't need (with completeness already satisfied)? |
| presentation | Rendered in the real pane: scannable in <10 seconds? Hierarchy right? |

Then write **THE AFTER**: the full should-be brief (same JSON shape as the contract
output). If the brief was good, AFTER == ACTUAL and you say so. The AFTER is the label
that becomes the #209 golden set - write it with the care of shipping code.

Overall verdict: `pass` (all >= 4) / `weak` (any 3) / `fail` (any <= 2).

`blind.jargon` joins the verdict record (see schema below); blind reads from before the
jargon dimension existed score it during the evidence check instead.

## The HTML verdict report

One case per page, written to `.temp/wingman-review/case-<sid8>-t<N>.html`, opened in
the browser. Generate it with a Python script in `.temp/wingman-review/` (extend the
existing `build_case.py` lineage). Layout, left to right:

1. **TERMINAL** - black pane, `screenTail` verbatim, monospace, with
   `lastUserPrompt` shown above it labeled "what the user asked to start this turn".
2. **THE BRIEF AS SHIPPED** - rendered using the REAL BriefPane structure and styles.
   Read `src/CcDirector.Cockpit/Components/BriefPane.razor` for the markup shape
   (YOU'RE DOING -> YOU ASKED -> NEEDS YOU w/ option buttons + "(rec)" + ifIgnored +
   evidence toggle) and extract the `.brief*` / `.blabel` rules from
   `src/CcDirector.Cockpit/wwwroot/app.css`. The point: visual criticism must be
   criticism of the actual product surface. Do NOT invent a layout.
3. **THE AFTER** - the should-be brief rendered in the SAME BriefPane styling, so the
   judgment is "is the AFTER better?" at a glance.
4. **VERDICT strip** - blind-reader answers (decidable/missing/noise), dimension
   scores, findings. Reviewer commentary in BLUE (#5b9bd5 family) and visually
   separate - it must never be mistaken for brief content.

Round summary page (`round-<date>-<n>.html`): table of cases, scores, links, and the
recurring-failure themes.

## The verdict record (proto-golden-set)

Append one line per reviewed case to `.temp/wingman-review/verdicts.jsonl`:

```json
{"sid": "...", "turn": 221, "reviewedAtUtc": "...", "verdict": "fail",
 "scores": {"decidability": 2, "context": 1, "faithfulness": 4, "blockerAccuracy": 3,
            "options": 4, "tightness": 3, "presentation": 2},
 "blind": {"decidable": false, "missing": ["..."], "noise": ["..."]},
 "findings": ["..."], "after": { ...full should-be brief JSON... },
 "reportPath": "case-3c3f406b-t221.html"}
```

Never overwrite a verdict; re-reviews append a new line. This file IS the input to
#208 distillation and the seed of the #209 golden set - treat it as data, not scratch.

## The loop

1. Run a round (5+ cases, prefer red/needsYou turns and any human downvotes).
2. After each round, `distill`: cluster findings into the smallest set of
   TurnBriefContract prompt-rule changes that would have fixed them. Presentation
   findings cluster into BriefPane change proposals instead (those need before/after
   HTML mockups per the house rule).
3. **STOP. Show Soren the proposed changes with the failing cases as receipts. Never
   edit TurnBriefContract.cs or BriefPane without explicit approval.**
4. After an approved change deploys (Gateway restart picks up the contract), the next
   round reviews FRESH briefs only - tag verdicts with the contract version (grep the
   version marker in TurnBriefContract.cs) so rounds are comparable.
5. Loop until a full round passes. Then, and only then, bring cases back to Soren for
   human review - the reviewer is the coarse filter, Soren is the gold standard.

## Standing rules

- Stores under `%LOCALAPPDATA%\cc-director` are READ-ONLY to this skill; all output
  goes to `.temp/wingman-review/`.
- Never commit anything without being explicitly asked.
- The blind reader NEVER sees evidence; the report ALWAYS shows it. No exceptions.
- One case per page; one verdict per case; ask "is the AFTER better?", never "is it
  right?".
- screenTail has no ANSI color - judge brief quality, not terminal rendering fidelity.

## Example

**User:** `/wingman-brief-reviewer 3c3f406b-9002-4381-b090-6be07d0a755e 221`

**Agent:**
1. Loads `t221.json` + the `turnNumber: 221` brief line.
2. Spawns blind reader with ONLY the brief fields -> JSON: decidable=false,
   missing=["what game? what session is this?", "why is posting manual?"].
3. Evidence check -> finding: composer already held "It's posted, log the URL" at
   capture; brief briefs as if the user had not acted.
4. Writes AFTER (leads with the parked reply), scores, appends verdicts.jsonl,
   generates `case-3c3f406b-t221.html` with real BriefPane styling, opens it.
5. Reports verdict + one-line summary in chat.

---

**Skill Version:** 1.1
**Last Updated:** 2026-06-06
**Changes in 1.1 (Soren's spec, dictated):** the reviewer anchors on the session's
ORIGINAL GOAL - new B0 goal check (full brief history + true first ask, not seed
boilerplate); two new scored dimensions: goalAlignment and jargon (persona = veteran
developer NOT versed in this stack); rubric bar reworded to "simplest form of ALL
necessary information" - never reward brevity that drops decision-relevant context.
