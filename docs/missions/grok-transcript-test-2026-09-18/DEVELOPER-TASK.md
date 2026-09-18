# Developer task - make the Grok transcript test prove what it says it proves

Written by the Delivery Lead of the mission "A test that does not depend on what this machine has
ever run" (issue 3029). You are the Developer on it. You have one task, below.

**Read first, in this order:**

1. `cc-devthrottle skill get devthrottle-method` - the rules. Read "If you are a Developer".
2. `docs/missions/grok-transcript-test-2026-09-18/MISSION.md` - the job. It wins over everything
   except the owner's own words.
3. Issue 3029 (`gh issue view 3029`) - the diagnosis.

## Your task

`src/CcDirector.Gateway.UnitTests/TurnsVerbUnresolvedTranscriptTests.cs` builds its session on
`Path.GetTempPath()` and states that no locator can resolve a transcript for it. On this machine
that is false - `%USERPROFILE%\.grok\sessions\C%3A%5CUsers%5Csoren%5CAppData%5CLocal%5CTemp` exists,
so the Grok locator resolves a real transcript and the verb correctly answers `ok`.

Give the test a working directory that **no locator can ever resolve**: a fresh directory the test
creates under its own temporary path, unique per run, removed afterwards. Not `Path.GetTempPath()`
itself.

Check whatever that test shares its fixture with, and fix the same assumption there if it is made.

## What you may not do

- **Do not weaken the assertion and do not skip the Grok case.** It guards issue 2561 - a Pi session
  that sat silent for 48 minutes because this verb reported an unresolved transcript as a successful
  read of an empty conversation, and voice narration recorded "nothing to narrate", which is never
  retried.
- **No fallback programming.** Fix the root cause; never add something that hides it.
- **Do not change product code.** The product is right. If the fix cannot be made without touching
  product code, stop and tell the seat that opened you - that would mean the diagnosis in issue 3029
  is wrong and this mission's premise with it.
- Do not touch the `HostedSchemaRefusesAnUnownedRowTests` failures or the concurrency test. They are
  named out of scope in the mission document.

## The check you run and must pass

Measured by the Delivery Lead on this machine on 18 September 2026, before any change, at
`origin/main` = `ba867ddf3`:

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~TurnsVerbUnresolvedTranscriptTests"
```

Before: `Failed: 1, Passed: 5` - the Grok case fails with `Expected: "no_transcript" Actual: "ok"`.
The full log is in `evidence/before-filtered.txt` beside this file.

After your change, the same command must give `Failed: 0, Passed: 6`.

**Then prove the fix can fail.** The mission's own words: a test that has never been watched failing
is decoration. Show that your new test still fails when the product is wrong - for example by
temporarily making the verb answer `ok` for an unresolved transcript, watching the Grok case go red
with the reported symptom, and restoring. Rebuild between every step: a `--no-build` run certifies
the binary, not the source.

## What you owe

- The change, committed and pushed on the mission branch `mission/grok-transcript-test-3029`.
  Commit as the owner - no assistant, model or vendor named anywhere, no "Co-authored-by", no
  "Generated with".
- A short fix report at `docs/missions/grok-transcript-test-2026-09-18/FIX-REPORT.md`: what you
  changed, why that directory can never be resolved by any locator, the before and after runs with
  their real numbers, and the revert proof. Name what is NOT proven.
- The raw run logs in `docs/missions/grok-transcript-test-2026-09-18/evidence/`.
- One `cc-devthrottle session report` line when you are finished, to the seat that opened you.

Do not run the whole suite - the Delivery Lead runs that itself. Do not arrange your own review; the
Delivery Lead sends your code to a Reviewer running a different agent.
