# Pull request 3868 - Director fix-round review

## Scope

Read `missions/license-switching/contract.md`, `review-3868-director.md`, and `review-3868-answers.md`. Compared the `origin/main...HEAD` change summary and reviewed commit `68e939126` in detail, including the command parser, switch coordinator/result store, setting, startup and reconnect wiring, reporter, and the added tests.

Ran only:

- `dotnet test src/CcDirector.Core.UnitTests/CcDirector.Core.UnitTests.csproj` — passed, 1,546 tests, 0 failed (43 seconds).

I did not run the external `cc-devthrottle license` tool against a real Claude config, a live Gateway, or the live-swap proof. The unit tests use fakes for those boundaries.

## Findings

### P2 - Replayed switch-in-progress refusal can clear the Gateway's active account

In `src/CcDirector.Core/Licenses/LicenseSwitchCoordinator.cs:85`, a different SwitchId received during a running switch is refused with `active = ""` and then saved. `Refuse` copies that value to `ActiveAccountEmailAfter` (`LicenseSwitchCoordinator.cs:220`). The new persistence means a retry after the other switch ends replays this same empty value, without reading the current active account.

Concrete sequence: account A is active while switch S1 to B is running; S2 is refused and its reply is lost; S1 completes and the after-switch report tells the Gateway that B is active; then the Gateway retries S2 with the same SwitchId. The stored S2 response says `ActiveAccountEmailAfter = ""`. Under contract sections 3.4 and 3.5, that field is the Director's read-back/current state and is an input to the Gateway's machine state, so this later replay can replace the known B state with an empty active-account value. This violates the contract's requirement that a refusal report the active account as it is now.

The refusal needs a replay-safe active-state value, or the Gateway must be told that this refusal does not update active state. The current stored result does neither.

## Findings addressed

- Unknown `readError` values are replaced by `unexpected-answer`; tool detail is discarded. Unknown `reasonCode` values become `exchange-failed-unknown` before being logged or returned.
- The coordinator now refuses a spare whose known sign-in expiry is at or before its clock.
- `switch-in-progress` refusals are saved and replayed by SwitchId.
- With the setting off, application startup does not start the reporter or prune the result store; reconnect only queues a trigger; the command coordinator returns before accessing the result store or invoking the CLI. I found no current production path around these gates in the startup, reconnect, and command wiring inspected.
