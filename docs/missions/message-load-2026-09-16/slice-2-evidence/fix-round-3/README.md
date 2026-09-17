# Slice 2 fix round 3 - guards watched failing

One file per break. Each break was made on the finished fix, the named selection was run, and every touched
file was restored before the next break. Each file holds the failing test names and the summary line.

- `item1-*` other than the two contract breaks: Core unit `DoorbellSafetyTests|FleetDoorbellRingerTests` (87 tests).
- `item1-literal-changed`: that Core selection AND Gateway unit `FleetDoorbell*|FleetMessage*` (142 tests).
- `item1-not-in-contract`, `item2-*`: Gateway unit `FleetDoorbell*|FleetMessage*`.

| Break | What was done | Red |
|---|---|---|
| `item1-take-back-restored` | the round 2 ringer and `Session.cs` put back verbatim (erase path included); the scripted target given an erase that it records | 19 |
| `item1-erase-restored-labelled-parked` | the same, with the erase branch answering `parked`, so only the erase itself can fail the tests | 19 (message: `Expected "frame", Actual "erase"` on the exact-line frames; the extended-line frames on their round 2 reason) |
| `item1-parked-reported-as-not-submitted` | a non-empty composer after an unverified submit answered `not-submitted` | 19 |
| `item1-parked-reported-as-composer-text` | answered `composer-holds-text` | 19 |
| `item1-literal-changed` | `Parked = "parked-line"` | 20 Director, 3 Gateway |
| `item1-not-in-contract` | `Parked` left out of `FleetRingDeferReasons.All` | 2 |
| `item1-parked-line-rung-again` | the safety check lets a composer holding the doorbell line ring again | 1 |
| `item2-floor-not-enforced` | `MaxTextLength` accepts any positive value | 1 |
| `item2-floor-without-id` | the floor is the notice opening only, without the id | 2 |
| `item2-notice-cap-not-checked` | `StuckNoticeText` accepts any positive cap | 1 |

The script that made the breaks lived in the session's scratch directory and is not committed; each break is
described exactly in the table.
