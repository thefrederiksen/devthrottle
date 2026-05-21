# Phase 0 Report

Verdict: FAIL (8/9 expected company-term occurrences recovered in the final variant)

## Method

Generated 3 synthetic clips with OpenAI tts-1 (voice=alloy).
Each clip transcribed with gpt-4o-transcribe in three variants:

1. No prompt parameter (baseline).
2. With the prompt parameter packed with the company term glossary.
3. Variant 2 transcript run through Claude Haiku with the term list in the system prompt.

Pass criterion: every expected company term appears in the variant 3 transcript for every clip (case-insensitive substring match).

## Results

### Clip 1

Expected sentence: `I sent the cc-director patch to mindzie before the CenCon review.`

Expected company terms: mindzie, CenCon, cc-director

**no_prompt** [MISSING: mindzie, CenCon, cc-director]

> I sent the CC director patch to Minzy before the SENCON review.

**with_prompt** [OK]

> I sent the cc-director patch to Mindzie before the CenCon review.

**with_prompt_plus_cleanup** [OK]

> I sent the cc-director patch to mindzie before the CenCon review.

### Clip 2

Expected sentence: `Soren Frederiksen needs the Avalonia changes for ConPTY tested by Friday.`

Expected company terms: ConPTY, Avalonia, Soren Frederiksen

**no_prompt** [MISSING: ConPTY, Soren Frederiksen]

> Søren Frederiksen needs the Avalonia changes for ContiUI tested by Friday.

**with_prompt** [MISSING: ConPTY]

> Soren Frederiksen needs the Avalonia changes for Contui tested by Friday.

**with_prompt_plus_cleanup** [MISSING: ConPTY]

> Soren Frederiksen needs the Avalonia changes for CenCon tested by Friday.

### Clip 3

Expected sentence: `Tell mindzie that the CenCon report is ready and ping the cc-director gateway team.`

Expected company terms: mindzie, CenCon, cc-director

**no_prompt** [MISSING: mindzie, CenCon, cc-director]

> Tell Minzi that the Sencon report is ready and ping the CC Director Gateway team.

**with_prompt** [OK]

> Tell mindzie that the CenCon report is ready and ping the cc-director gateway team.

**with_prompt_plus_cleanup** [OK]

> Tell mindzie that the CenCon report is ready and ping the cc-director gateway team.

## Interpretation

Variant 3 did not recover every expected company term. Inspect transcripts.json. Possible follow-ups before committing to Phase 1:

- Refine the Haiku cleanup prompt with explicit positive and negative examples for the terms that slipped through.
- Try gpt-4o-mini-transcribe for comparison (cheaper and may behave differently with the prompt parameter).
- Note: TTS pronunciation may not match how a human says these terms. Real-voice Phase 2 testing could land closer to one side or the other.
- Reconsider AssemblyAI keyterm boosting if the gap is irreducible (out of scope per PLAN.md, but a known fallback).
