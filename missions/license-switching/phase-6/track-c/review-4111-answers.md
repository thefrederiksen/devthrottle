# Answers to the review of pull request 4111 (the approver form)

The review is `review-4111.md` beside this file. Answered by the Developer, 17 October 2026, on the branch rebased
onto `origin/main` at `0702c88ce`.

## Finding 1 - which string `browserProfile` is

**Accepted, as the Tech Lead decided it.** The design now has a section "Which string a browser profile is": everywhere,
`browserProfile` is the profile's identifier (the Director's slug), compared exactly, with the display name carried
separately. This pull request already sends the identifier and keeps doing so. Its part of the fix is the sentence now
in `cockpit-api.md` section 7.2: "`browserProfile` is the browser profile's identifier (the Director's slug, for example
"work-chrome", as `GET /directors/{id}/browsers` lists it in `id`), never its display name."

One thing in this pull request that touches the rule: when the form opens on a stored approver, it matches the stored
string against a profile's identifier or its name, and shows that profile chosen by its identifier. That is only how
the form shows an old record, not a comparison anything relies on: saving it sends the identifier, which replaces a
record set by name with one that follows the rule. Records nobody re-saves read `not-checked` under the rule, as the
design says. The hub contract comments and C1's and C2's comparison are C1 and C2 work, not this pull request.

## Finding 2 - the row and the sentences show the slug

**Accepted, and it goes in my second pull request.** C1 now carries the display name on the approver record. The
second pull request (approver health) renders it, as the Gateway gives it, in the row and in the Gateway's sentences.
Nothing changes here: on this pull request's base the record has no name to show.

## Finding 3 - the gate was narrower than it read

**Accepted, and closed.**

- **The type check.** `npm run typecheck` (`tsc --noEmit`) in `apps/cockpit` on the rebased branch: no errors. The proof
  now says that `npm test` is `vitest run` and that the type check was run separately.
- **The six Gateway.UnitTests failures.** Rebased onto `origin/main` (`0702c88ce`, which includes #4107). Run directly,
  `PromptOnlyWhenWaitingForInputTests`, `FleetManagerEventServiceTests` and both Licenses test classes: 225 tests, 225
  passed, 0 failed. The six that failed on the old base pass.
- **Gateway.Tests and Core.Tests.** Still owed: `-Parked` has been refused by a held release-gate lock each time it was
  tried. It is run when the lock is free, with the temporary folder on D: as the Delivery Lead asked.
