# Review: phase 2, the session connection fields (pull request #4077)

Mission: API mode for agent wrappers, proven on Pi (issue #4070). Reviewed on 16 October 2026 against
commit 0309093bf, the head of pull request #4077 on branch `api-mode-p2-session-fields`, which sits
directly on origin/main (48cf4d760, the phase 1 merge). Reviewed by an agent of a different family from the
Developer's.

## Scope

What I read:

- The whole diff of the pull request: `src/CcDirector.Gateway.Contracts/SessionDto.cs` (the two
  properties and the two values classes), `packages/client-core/src/api/schema.ts` (two lines) and the new
  `src/CcDirector.Gateway.UnitTests/Api/SessionConnectionFieldsTests.cs`.
- On origin/main, every path a session row takes from a Director to a client, looking for a copy that
  names fields one by one and would drop the new ones:
  - `SessionDto.Clone()`: `MemberwiseClone` plus a fresh `DriverCapabilities` list, so every scalar,
    including the two new strings, comes along.
  - `PushedSessionStore`: `ApplySnapshot` and `ApplyDelta` store the decoded row itself and every read
    hands out `Clone()` copies through `RecomputeClocks`, which touches only `IdleSeconds`.
    `DiscardInboundRole` nulls seven Gateway-owned fields and does not touch the new ones.
  - `DirectorHub.PushSnapshot` and `PushDelta`: pass the decoded rows straight to the store.
  - `GatewayEndpoints`, the `GET /sessions` route: serves the clones, with or without the envelope the
    Cockpit and phone ask for (`/sessions?envelope=true&clockFields=absolute`).
    `RosterClockFields.Strip` works on the serialised JSON and removes only the two clock fields.
  - `packages/client-core/src/fleet/rosterRetention.ts`: keeps whole rows and spreads them, no field
    list.
  - The session history (`SessionHistoryRecorder`, `SessionHistoryFold`) maps per field, but into
    `WorkHistorySessionDto` for the Work History page, not into a session row the roster reads.
  - The Director's one mapper, `ControlEndpoints.Map`, and the second one in `SessionManager`: neither
    sets the new fields, so a current Director sends `terminal` and `none`, which is what it does.
- The wire protocols: the Gateway registers `AddMessagePackProtocol()` with default options and the
  Director's `GatewayStreamClient` does the same, so the `MessagePackHubProtocol` the test constructs with
  no options is the protocol the tunnel speaks. `SessionDto` carries no MessagePack or JSON attributes,
  so both readers use property names and keep an initialiser when a key is absent.
- Issue #4070 (sections 3, 5, 6 and 8), `event-model.md` (sections 1 and 2, and the `PromptQueued`
  reasons), the mission record `README.md`, and the pull request description.
- The schema: `backendType?: string` is how the generator writes a non-nullable string with an
  initialiser, `workingOrigin?: null | string` is how it writes a nullable one, and the two new lines take
  the first shape, at the position the class declares them (between `backendType` and
  `driverCapabilities`).

What I ran, on devlinux with the .NET 10.0.301 toolkit:

- `dotnet test src/CcDirector.Gateway.UnitTests --filter FullyQualifiedName~SessionConnectionFields`:
  6 passed, 0 failed, 0 skipped.
- The whole `CcDirector.Gateway.UnitTests` suite on the pull request head, twice. The first run reported
  185 failures out of 13,459, every one I could read being `IOException: The configured user limit (128)
  on the number of inotify instances has been reached`: this machine held 127 of its 128 inotify instances
  at the time (another seat was running the same suite beside mine), and each in-memory test host adds a
  configuration file watcher. The five failing classes I re-ran alone passed, 73 of 73, with the watcher
  off (`DOTNET_hostBuilder__reloadConfigOnChange=false`). The second run, with the watcher off throughout:
  13,454 passed, 3 failed, 2 skipped. The three (`VoiceAutoOffLineTests`, `VoiceUnheardCountTests` and
  `HeldDeliveryDriverTests`, one case each) simulate a failed save by opening a file with `FileShare.None`,
  a lock Linux does not enforce, so the save succeeds and the assertion fails. None touches a session row
  or a file this change adds. So nothing that reads a session row changed its answer: terminal mode's
  behaviour is unchanged as far as this suite can see.

What I could not reach:

- I did not re-run `openapi-typescript`. The documented way needs a Gateway listening on 127.0.0.1:7878,
  which I would not start on this shared machine, and `openapi-typescript` is not installed under
  `packages/client-core/node_modules` here. The pull request says the two lines are byte-identical to the
  output of the pinned generator run against an in-memory host; I checked only that their shape and position
  are what that generator produces for these property types. No test in the repository ties `schema.ts` to
  `SessionDto`, and the pull request itself lists fourteen older fields already missing from it.
- The local gate script (`scripts/test-local.ps1`) is Windows-bound and was not run here.
- Nothing fills the fields yet (the Director phases do), so "an API-mode session reaches the clients" is
  proved only with hand-built rows pushed into the real store and read from the real route, which is what
  the tests do.
- The pull request's mutation check (removing the `terminal` default fails three tests) is the
  Developer's statement; I did not repeat it.

## Verdict

Approve. The two fields are plain constrained strings in the style of the neighbouring `WorkingOrigin`
and `VerdictState`, every value is documented, and nothing on the Gateway's path from the Director tunnel to
the roster copies a session row field by field, so the values a Director pushes arrive at the clients
unchanged and a row from an older Director reads `terminal` and `none` on both wire formats. Terminal
mode's behaviour is unchanged: no route, fold or client reads the fields yet, and the only visible
difference is two more keys in every session row. The six tests prove what their names say: the older
Director case on JSON and on the real MessagePack hub protocol, the pushed values surviving the store and
the real `GET /sessions` route on an in-memory test server, and the clone. They use no port, no process,
no sleep and no clock, and their names follow Method_Scenario_Result.

One finding, on the words of the contract, which the Developer may decline. Two observations follow, which
are not findings because nothing breaks today.

## Findings

### 1. The documented meaning of `WheelHolders.None` is false for every terminal-mode session

`WheelHolders.None` is summarised as "Nobody: no agent process holds the conversation", and the property
summary repeats it: `none` means "no agent process holds it". But `none` is also, by design, what every
terminal-mode row reads, and the mission's own words are "Exactly one process holds a conversation at any
moment": in terminal mode the agent's terminal process holds it, and so does every running session an
older Director reports. So the contract says of the most common row on the roster that nothing holds its
conversation while a live process does.

The harm is to the seats that build on this contract next. Phase 4 writes the ownership lock whose whole
point is that two processes never hold one Pi conversation, and phase 2's fold decides what to show from
these two fields. A reader who takes `none` at its word ("no process holds it, so the protocol process
may start") on a terminal-mode row reaches the one state the mission forbids. The fix is a sentence, not
code: say that the wheel is an API-mode fact, that in terminal mode the row always reads `none` because
there is no wheel to hold, and that `none` on an API-mode row means the protocol process has stopped and
no terminal has taken over. The values themselves need not change.

Developer's answer:

## Observations (not findings)

- **The two raw fields are inputs to the Gateway's fold, not a verdict.** Rule 7 of the project instructions says a
  client never decides what a state means. When phase 5 adds the roster's "API mode" tag and phase 4 the
  Terminal tab's "Take the wheel" and "Hand back" buttons, the label and which button to offer should be
  stamped on the row by the Gateway, as `stateLabel` and `voiceDisplay` are today, with `connectionMode`
  and `wheelHolder` read only by that fold. Nothing in this pull request breaks the rule; this is a note
  for the seats that will.
- **The mission record still lists phase 2 as "Not started".** `docs/missions/api-mode-pi-2026-10-16/README.md`
  is the Delivery Lead's record, not this pull request's, and this pull request is one slice of phase 2
  ("no routes, no ruling", as its description says). Whoever lands the record should say which slices of
  phase 2 have merged.
