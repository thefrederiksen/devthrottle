# Proof: a Director sends a session's key before that session's id reaches the Gateway

devthrottle_internal#2311, #3552 review finding S2-F13. Director side only. No Gateway change, no schema change.
Personal and team Directors take the same path.

## The rule being proved

No session id goes up from this Director to the Gateway before that session's key has been sent on the same
connection. In a team the Gateway decides whose a session is from its key row, and the first Director to key an id
keeps it, so an id that arrives first is an id a colleague can take.

## The walk: every path by which a session id leaves this Director

Line numbers are on this branch.

| Path | Where | How the key goes first |
|---|---|---|
| Reseed (on connect, on reconnect, every re-push tick) | `src/CcDirector.ControlApi/GatewayStreamClient.cs:730` | Hello (`:746`), then every live key (`:823`), then the gate opens (`:865`), the Hello callback runs (`:874`), and only then the roster (`:893`). It used to be Hello, roster, keys. |
| Delta (every activity, hold, colour, owner change) | `GatewayStreamClient.cs:988`, gate at `:992`; raised from `ControlApiHost.cs:1754` onward | Held back until the CURRENT connection's reseed has sent its keys. The roster the reseed sends right after carries the same state. The connection count moves on every Reconnecting (`:399`) and Closed (`:411`), so a connection that comes back starts with the gate shut. |
| Turn push | `GatewayStreamClient.cs:639`, gate at `:644`; called from `ControlApiHost.cs:1159` | Refused (like a dropped tunnel) until that connection's keys are sent. The turn sweep the Hello starts (`ControlApiHost.cs:1107`, `SeedWatermarks`) now starts only after the keys, because the callback moved behind them (`GatewayStreamClient.cs:874`). |
| New, resumed and restored sessions | `src/CcDirector.Core/Sessions/SessionManager.cs:860` (key minted) before `:1120` (roster add); the send is `ControlApiHost.cs:607` | The key is minted and its registration started on the creating thread while the environment is built, before the session joins the roster. A resumed or restored session has a new id and takes this path. |
| A session created while the tunnel is down | as above | The registration at creation fails (not connected) and the key stays in the Director's key store. The reseed on the new connection sends it (`:823`) before the gate opens and before the roster, and the gate holds every delta and turn push for it until then. |
| A session created after Hello but before the key leg reads the key list | as above | Its key is sent at creation (the connection is bound by then) and again in the key leg; its delta waits at the gate. |
| GitHub Actions sessions | `SessionManager.cs:1390`, key at `:1421` before the roster add at `:1426` | FIXED HERE (Tech Lead ruling). It runs no local agent and used to get no key at all, so it was listed with none. It is now keyed like any other session, kept in the key store (refreshed every reseed, revoked when the session ends), never handed to anything, and revoked if creation throws. |
| Remove (`RemoveSession`) | `GatewayStreamClient.cs:1123` | Sends an id the Gateway already holds, to drop it. Nothing to key. |
| Owed revocations | `GatewayStreamClient.cs:925` | Ends a key; it runs after the roster as before. |
| Up-stream frames (`StreamUp`) | `GatewayStreamClient.cs:159` | Only in answer to a command the Gateway sent for a session it already lists. |
| Doorbell (HTTP) | `src/CcDirector.Gateway/Api/GatewayEndpoints.cs:1137` | Legacy same-machine path, refused on a hosted Gateway (teams are hosted) and recorded under the Local tenant only. Not a team path. |

Before Hello the Gateway refuses every push on a new connection (`DirectorHub.cs:975`, "send Hello first"), so
nothing can overtake the reseed before its Hello.

Named and left alone, as ruled: `CreatePipeModeSession` (`SessionManager.cs:1355`), `CreateEmbeddedSession`
(`:1447`) and `RestoreEmbeddedSession` (`:2001`) also add a session with no key, but none has a production caller.
`AdoptSession` (`:1969`) is a test seam.

What this does not cover: the order between the creation-time key send and a later delta on one connection rests
on the SignalR client sending one call at a time in the order they were started; that is not proved by a test here.

## Tests

- `src/CcDirector.Avalonia.Tests/Teams/DirectorKeysBeforeRosterTests.cs`: 13 tests against a hub that records every
  call in order (the order of keys and roster; a failing key, a failing key list and a failing roster; Hello failing;
  the Hello callback after the keys; the gate shut during the keys and open at the roster; a reseed on a lost
  connection; deltas and turn pushes held before the keys and sent after; held again after a reconnect).
- `src/CcDirector.Core.Tests/GitHubActionsSessionKeyTests.cs`: 4 tests (the key comes before the listing; the
  session is not yet in the roster when keyed; no Gateway, no key; a failed creation revokes the key).

## Runs

- `test-local-default.txt`: `.\scripts\test-local.ps1` (default), every suite outcome=Completed, 3,614 tests.
- `core-tests-filtered.txt`: Core.Tests filtered to `GitHubActionsSessionKeyTests` and `GitHubActionsBackendTests`, 15 passed.
- `gw-unit.txt`: Gateway.UnitTests filtered to the four classes that drive the stream client or the turn pusher, 46
  passed. Gateway.Tests was not run (per the brief).

## Revert checks

- `revert-1-roster-before-keys.txt`: the roster block moved back in front of the key leg. 3 red (the order test,
  the gate-at-roster test, the Hello callback test).
- `revert-2-gate-off.txt`: both `if (!SessionIdsMayGoUp)` lines made `if (false && ...)`. 3 red (the held-delta,
  held-after-reconnect and refused-turn-push tests).
- `revert-3-github-actions-no-key.txt`: the two key-mint lines in `CreateGitHubActionsSession` removed. 3 red.
- `restored.txt`: each restored from the commit and rebuilt (no `--no-build`), all green.
