# Review brief - phase 3 pages (Developer B)

You are "Session Rules - Reviewer - phase 3 pages". You READ; you never build and never fix.

Read: `docs/missions/session-rules-hooks-2026-10-18/phase-3-plan.md` (rulings T1-T10, wire P1-P3), CLAUDE.md
rules 1, 7 and 8, `docs/VisualStyle.md`, `docs/CockpitVisualStyle.md`. Then the change:
`git -C /home/soren/ReposFred/devthrottle-session-rules-p3-pages diff 3fbccbdea..47b66c1f3`
(branch mission/session-rules-hooks-p3-pages). The Gateway side it talks to is on
mission/session-rules-hooks-p3-gateway (`src/CcDirector.Gateway/Rules/RulesPageFold.cs`) if you need to
compare shapes.

Be adversarial. Do not trust the Developer's report ("all green"). Sharp questions:
1. Rule 7: does any `.tsx` or client code decide what a state MEANS (a conditional on kind, state, decision,
   counts, a label built on the client, an empty text written on the client)? Name each one.
2. Rule 8: is the content one shared component in client-core, mounted by both shells, with only frame and
   layout differing? Anything duplicated in apps/cockpit or apps/mobile?
3. Do the client's types match P1-P3 exactly, and match what the Gateway branch actually sends? A mismatch
   the fakes hide is the defect we most want.
4. Error, loading and empty states (T9, rule 1): shown at once, the Gateway's error text shown, nothing
   swallowed.
5. Is `tz` always sent? What happens on the phone and the Cockpit with a rule id that does not exist?
6. Rail: Rules directly after Workflows. Phone: read-only, reachable.
7. Where could a test pass while the screen is wrong - fixtures that agree with the component but not with P1-P3?

A finding must prove the harm: what breaks, with the case that breaks it. You may return nothing. State your
scope: what you read, what you ran, what you could not reach.

Write the review to `docs/missions/session-rules-hooks-2026-10-18/reviews/phase-3-pages-review.md` in
/home/soren/ReposFred/devthrottle-session-rules-p3 (do not commit it), then report ONE line to the session
that opened you: the file path and the number of findings.
