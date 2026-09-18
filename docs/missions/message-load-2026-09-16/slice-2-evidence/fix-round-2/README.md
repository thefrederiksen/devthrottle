# Slice 2 fix round 2 - guards watched failing

One file per break. Each break was made on the finished fix, the named selection was run, and the file was
restored before the next break. The file holds the failing test names and the summary line.

- `item1-*`: Core unit `DoorbellSafetyTests|FleetDoorbellRingerTests`.
- `item2-*`, `item3-*`: Gateway unit `FleetDoorbell*|FleetMessage*|Snooze*`.

`item1-squeeze-restored.txt` was run before the last five tests of item 1 were written (79 tests); the other
`item1-*` files are from the final test set (82 to 84 tests). Four of the item 1 breaks
(`any-whitespace-at-break`, `leading-space-segment-allowed`, `empty-segment-allowed`, `prefix-match`) and
`indent-not-required` were GREEN at first. Tests were added for them, and the files hold the red rerun; the
green first runs were not kept.

`item3-hold-never-reset.txt` also shows `SnoozeLandingObserverTests.Work_no_submission_explains_still_ends_an_armed_snooze`
failing. That test does not touch the doorbell and passed six reruns on the restored code. It is attributed to
the pre-existing disposed-SQLite race (handoff, "Disposed SQLite investigation"), which reproduced once in eight
reruns of the same selection on the restored code, on `FleetMessageStoreTests.Recent_at_exactly_the_cap_returns_all_200_and_is_not_truncated`.
The run did not keep that test's error message, so the attribution is an inference.

`core-tests-failures.txt`: the 62 failing Core test names from the full `CcDirector.Core.Tests` run of this round.
