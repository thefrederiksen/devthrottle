# Answers: review of the routed session's opening (pull request 4284)

The review (`opening-review.md`) approved the change with no findings. It made one observation, and this file answers it.

## Observation: the fourth test's name says less than it proves

**Accepted and fixed.** The test was named `RouteAsync_SeatOfAFactoryWithNoBrief_...`. What it sets up is a factory
with no published definition at all, because that is the only way a routed seat can have no brief: a defined seat's
brief cannot be blank (`seat.schema.json`), and the router starts only seats of defined factories. The test is
renamed `RouteAsync_SeatOfAFactoryWithNoPublishedDefinition_StartsNoSessionAsThatSeat_AndTheMailDeskSeatOpensAsItself`.
Its comment already says why there is no brief. What it asserts is unchanged.

## Also done with this answer

- Rebased onto `origin/mail-desk-phase-4` (at `1386a1fb1`). The branch's own edit of `CONTRACT.md` is dropped, and
  the phase branch's contract stands exactly as the Tech Lead wrote it.
- The review file is included as the Reviewer wrote it.
