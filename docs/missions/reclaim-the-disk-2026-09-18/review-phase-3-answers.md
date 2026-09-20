# Answers to the phase 3 review

The review returned approved with no blocking findings and three observations. The Tech Lead seat
that built the proof had finished, so the Delivery Lead, which opened it, answers.

1. **The plain-text test builds the real rule set with the apply flag, and holding would land at the
   root of the real volume if a rule ever selected something inside its fixture.** Accepted. The
   Reviewer judged that it cannot move a real file today, so it does not hold the merge, but a test
   that is safe only by which rules happen to match is leaning the wrong way. It gets a holding root
   inside its own fixture in the first change after this merge, tracked on issue 3120.
2. **The wiring tests' comment says the collection never runs beside another test without saying what
   carries that guarantee.** Accepted. The comment will name the mechanism - the runner schedules a
   collection that disables parallelization on its own - in the same follow-up change as answer 1.
3. **Refusal 4 stands alone on platforms whose path resolution does not follow links, and nothing
   proves it there.** Accepted, and it changes no code in this phase. This mission ships Windows rules
   only. The requirement is carried into the mission state file so that a mandate for a macOS or Linux
   rule set must prove refusal 4 on that platform before any removal is offered there.
