# Next experiment - a BIGGER, more complicated feature to find the crossover

Status: PLANNED, not started. Paused here on 2026-07-12 by the user. Tracked in a
devthrottle_internal GitHub issue (link added below once filed).

This is the follow-up to `FINDINGS.md`. Read that first for the round-1 result and the cost
model. This file is the standalone brief so the research can be picked back up cold.

## Why we are doing another round

Round 1 (feature #1112, small - 3 files) found the multi-agent fleet cost ~2.2x the tokens
of a single session, and that ~66-77% of ALL cost in both methods is the fixed ~69K
per-session context floor being re-read every turn. On a SMALL feature that floor dominates
and multi just multiplies it.

The unanswered question: **at what feature size does multi-agent stop losing?** The
hypothesis (FINDINGS.md sec 3-5) is that single-session cost grows roughly QUADRATICALLY
(turns x context, and context itself grows with turns), while multi keeps each worker's
context bounded and short. If true, there is a CROSSOVER feature size above which multi is
cheaper AND better - and locating that knee is the whole point.

One small data point (1x, done) cannot show a curve. We need at least one much bigger,
genuinely more COMPLICATED point.

## What "more complicated" must mean (not just more lines)

The round-1 feature was clean and shallow. To get an honest answer we want a feature that is
big in the way real work is big:

- Multiple genuinely independent slices (so a fleet can fan out), BUT
- With a real integration surface / shared contract between slices (so coordination is
  actually exercised, not trivial), AND
- Enough per-slice depth that each worker runs long (so the floor amortizes).

If the feature is just "many trivial disjoint files", multi wins too easily and the result
does not generalize. The complication is the point.

## Candidate feature (leading pick): GitHub #547, backend-scoped

DevThrottle issue #547 - the Wingman evaluation harness - scoped to its GATEWAY BACKEND
only (the Cockpit page is explicitly OUT of scope; the user only cares about evaluating the
wingman's speech quality, not a UI).

Why it fits:
- Big: the largest self-contained feature in the backlog, roughly 6-8x #1112 once
  UI is dropped.
- Cleanly decomposable into ~5 near-independent slices: golden-dataset store + headless
  curation, the LLM-judge runner, scoring dimensions, baselines + regression-threshold
  logic, tests.
- Real integration surface: the slices share a golden-record contract and a judge
  interface - coordination is non-trivial, which is what we want to stress.
- Stays in the SAME clean rig as #1112: verifiable by `dotnet build` + xunit, because the
  judge sits behind an interface with a deterministic test fake (real-LLM judging is a
  runtime step, not part of the pass/fail gate). No live cloud/device/browser state needed.

Crucial finding that makes it viable - we HAVE real data to evaluate on:
- `wingman_training_capture` has been ON since 2026-06-19.
- `%LOCALAPPDATA%\cc-director\wingman-training\*.jsonl` holds **5,823 captured real wingman
  turns, 284 MB, 2026-06-19 to 2026-07-12**.
- 5,562 are `source:"generate"` = the main fidelity-translation (speech) path - exactly what
  the user uses daily. Each record is a ready-made eval pair: `terminal` (what the wingman
  saw), `reply` (agent reply), `recentContext`, `spoken` (the wingman's actual spoken
  output), plus `model` and `replySeconds`. Median spoken output ~1,056 chars.

What already exists (do NOT rebuild; keep the new work honest):
- Capture layer `WingmanTrainingStore` - built, on, producing the data above.
- `WingmanGoldenTests` (#209) IS GONE - deleted by pull request 2916 on 2026-09-16, with the
  turn-brief contract it replayed golden `TurnPackage`s through. **There is no mechanical
  regression gate to build on any more, so the next experiment has to supply its own fixture.**
  It never had goldens in any case: it passed vacuously with an empty set, which is the same
  shape of false green this study exists to avoid.
  Build the fixture from the LABELLED CORPUS instead - `corpus/labelled/` in the internal
  repository, the turns the judge grading runs against, where every record carries a verdict
  word agreed by two reviewers of different model families or corrected by the owner. That is a
  fixture with known answers rather than an empty gate, and it is already maintained daily.
  The contract to replay through is now `TurnVerdictContract`, not the deleted one.
- The NEW work is the QUALITY-judgment layer: curate a labeled golden subset from the raw
  captures, run a G-Eval-style judge (different model family, temperature 0) over dimensions
  (faithfulness / completeness / speakability / reference-resolution / terse-expansion),
  store baselines per fidelity-instructions version, flag regressions. Plus a HEADLESS way
  to promote captures into a golden set (replaces the dropped review-queue UI).

Open item before drafting the spec: confirm the exact greenfield surface so the feature is
truly ~6-8x of NEW work and not padded by what already exists.

## Experimental design (keep it identical to round 1)

1. Write ONE frozen `SPEC.md`: WHAT + Definition of Done + verified code pointers, NO
   prescribed HOW - handed identically to both runs (this is what kept round 1 honest).
2. Two worktrees off the same base commit, e.g. `exp/wingman-eval-single` and
   `exp/wingman-eval-multi`.
3. Run A - single: one session builds the whole thing.
4. Run B - multi: architect -> manager -> workers on disjoint slices.
5. Measure with the SAME scripts in `analysis/` (per-session usage totals, start floor,
   per-turn context, floor-share). Add the new run as a second point next to round 1.
6. Also capture what round 1 did not: WALL-CLOCK time-to-done for each method, and whether
   the single session hit context compaction/summarization (where quality risk shows up).
7. If the result shows convergence or crossover, add an intermediate ~4x point to locate the
   knee.

## Deliverable of the research

A short, HONEST article: "When is a multi-agent fleet worth the extra tokens?" - with the
cost curve, the crossover point (if any), and a decision rule for choosing single vs fleet
by feature size and decomposability. Not hype; the round-1 data already shows multi is NOT
cheaper on small work, and the article must say so.

## Where the round-1 raw material lives

- This folder: `research/multi-vs-single-token-study/` in the `devthrottle-ctxtax-multi`
  worktree (branch `exp/ctxtax-multi`). Contains `FINDINGS.md`, `raw-sessions/` (all 6
  transcripts), `analysis/` (3 scripts), and the #1112 `SPEC.md`/`PLAN.md`/`PROOF.md`.
- NOT yet committed to git as of 2026-07-12 (user decision pending).

## GitHub tracking issue

devthrottle_internal #345:
https://github.com/thefrederiksen/devthrottle_internal/issues/345
