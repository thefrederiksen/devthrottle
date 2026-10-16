# Phase 5 review: choosing API mode (pull request #4095)

Mission #4070, phase 5. Reviewer seat, opened by the Delivery Lead (session 102). Reviewed on 16 October 2026 against
commit `a22638490` of branch `apimode-phase5-choosing`, in its own worktree. The reviewer runs a different agent from the
one that wrote the work. The reviewer read and ran; it built nothing and fixed nothing.

## Verdict

**One finding, with the harm proven by a probe run against the real create doors on the in-memory test server.** With
the one switch off, the machine create door still starts an API-mode session for any device caller that adds a restore
claim to its request. Everything else judged against (below) held within the scope stated.

## Scope: what was read

- The whole diff of the pull request against `origin/main` (51 files). The branch is three commits behind main (phase 2's
  two pull requests and #4072); the two shared files, `SessionDto.cs` and `GatewayEndpoints.cs`, merge with no conflict,
  and phase 2's rulings (`ApiModeRowStamp`, `ApiModeSessionFold`, the live endpoint) read the mode word independently of
  this phase's `ConnectionTag`, so nothing in the two phases contradicts the other.
- Gateway: `ConnectionChoiceFold.cs`, both create doors (`GatewayEndpoints.DirectorSpawn.cs`, `MachineEndpoints.cs`), the
  agents leg and the roster fold in `GatewayEndpoints.cs`, the contract additions in `SessionDto.cs` and
  `AgentChoiceDto.cs`, and the path a machine-door body takes to the Director (`MachineSessionSpawner.cs`).
- Director: `CatalogReadExecutor.DeclaredConnectionModes`, the create verb's parsing of the word in
  `SessionCommandExecutor.cs`, and `SessionManager.RequireApiModeSupported`.
- Desktop: `NewSessionConnection.cs`, `NewSessionDialog.axaml` and its code-behind, `MainWindow.axaml.cs`, and the
  Agent row beside the new Connection row, against `docs/VisualStyle.md`.
- Web: `connectionChoice.ts`, `ConnectionChoiceRow.tsx` and its stylesheet in client-core, `client.ts`, the Cockpit's
  `NewSessionDialog.tsx` and `SessionRoster.tsx`, the phone's `NewSession.tsx` and `Home.tsx`, both shells' stylesheets.
- Command line: `cli.py`, `session_ops.py`, `docs/cli-reference.md`, against `docs/axi-standard.md`.
- The proofs in `proofs/phase-5/`: the README, every accessibility dump, `command-line.txt`, `shoot-web.mjs`, and the
  pictures of the desktop dialog, the Cockpit dialog and the phone roster.
- The tests the pull request adds, in all four places.

## Scope: what was run, on this Linux machine

| What | Result |
|---|---|
| client-core, Cockpit and phone test suites (`vitest run`) | 2137, 1261 and 150 passed, none failed |
| Web typecheck (`npm run typecheck`, all four packages) | clean |
| Web lint (`npm run lint`) | 5 errors, all "rule react-hooks/exhaustive-deps was not found", in five files this pull request does not touch; not this change's |
| `tools/cc-devthrottle/tests/test_session_connection.py` | 13 passed |
| Gateway unit tests `ConnectionChoiceTests` and `SessionConnectionFieldsTests` | 47 passed |
| Desktop `NewSessionConnectionTests` (headless Avalonia, both switch positions) | 13 passed |
| A reviewer's probe against both create doors (below; never committed, deleted after the run) | machine door failed the probe, Director door passed it |

## Scope: what was not reached

- `scripts/test-local.ps1` and its `-Parked` run: a Windows run of the gate is still owed, as the pull request says.
- A real Gateway serving the switch's ON position, a real Director starting an API-mode session, and the Director's own
  refusal for an agent without API mode: read in `SessionManager.RequireApiModeSupported`, not exercised.
- The live Cockpit and phone against a real Gateway: the proofs were taken against a stub, and the README says so.

## Finding 1: with the switch off, the machine create door starts an API-mode session for a device caller carrying a restore claim

**Where.** `src/CcDirector.Gateway/Api/ConnectionChoiceFold.cs`, `RefusalOf`, line 49:

```csharp
if (apiModeOffered || req.RestoreClaim is not null)
    return null;
```

and its caller at `src/CcDirector.Gateway/Api/MachineEndpoints.cs`, line 467.

**Why the exemption is safe on one door and not the other.** On the Director door
(`GatewayEndpoints.DirectorSpawn.cs`, lines 105 to 125) a restore claim is validated after this check: a caller that is not
a Director is refused with 403, and a Director sending it on the wrong credential is refused too. So there the exemption
lets through exactly one thing, a real restore. The machine door never reads a restore claim at all: `MachineEndpoints.cs`
has no mention of it, and `MachineSessionSpawner.SpawnOnMachineWithOutcomeAsync` (lines 112 to 157) sends the request
object to the Director as it arrived. A restore never comes through the machine door, so on that door the exemption has no
legitimate caller and only one effect: any body with a non-null `restoreClaim` skips the switch.

**The probe.** A throwaway test with the same harness as `ConnectionChoiceTests` (real `GatewayEndpoints.Map` and
`MachineEndpoints.Map`, a fake Director behind them, the switch at its real position, `false`) sent, with a device
credential, to each door:

```json
{ "repoPath": "<a folder>", "agent": "Pi", "connectionMode": "api",
  "restoreClaim": { "workspaceId": "forged", "seatSessionId": "forged" } }
```

Result, word for word from the run:

```
PROBE machine door: status=201 ... directorSawCreate=True connectionModeForwarded=api
```

The Director door refused the same request (the probe passed there). The pull request's own test
`PostSessions_ApiModeWhileNotOffered_Is400WithTheReasonAndNothingIsSent("machine")` passes because it sends no claim.

**The harm.** The pull request's stated guarantee, and the mission's merge plan (section 8), is that while the switch is
off no surface can create an API-mode session, so that no session exists in a mode the product cannot yet finish (there
is no take the wheel until phase 4). This door breaks that guarantee for every caller holding a device key:
`cc-devthrottle session spawn` itself goes through `POST /machines/{machine}/sessions`, and although the shipped command
line never sends a claim, any script with the same key can. The Director then ignores the unknown claim and, because Pi's
wrapper declares API mode, starts Pi in API mode. The pull request's description ("both Gateway create doors refuse
`connectionMode: api`") is not true of the machine door.

**What the reviewer does not decide.** Whether the exemption belongs on the machine door at all, or whether that door
should refuse a restore claim outright as the Director door refuses one from a non-Director, is for the seat that built
the work. The reviewer notes only that a restore reaches the Gateway through the Director door, and that
`RefusalOf_ARestoreOfAnApiModeSeatWhileNotOffered_GoesOnSoTheSeatComesBackAsItRan` tests the fold in isolation, with no
door around it.

## Judged against, and what held

- **Section 5 and phase 5's row.** The Connection row is on all three surfaces, the roster tag is on both web rosters,
  `--connection` is on spawn and on list, with the filter applied to `--json` (seen in `list_sessions`, and the test ran).
- **Rule 7, the Gateway rules.** The labels "Terminal" and "API mode" come from `ConnectionModes` on the Gateway, travel
  on `AgentChoiceDto.Connections` and `SessionDto.ConnectionTag`, and every client renders them verbatim. No client names
  Pi. The one client-side conditional, `offersConnectionChoice` (draw the row when more than one connection is offered),
  decides layout, not meaning. The desktop reads the same `ConnectionModes.Offered` and the same declared list as the
  Gateway's agents leg, so it cannot drift from the web by construction. The command line's `connection` field and filter
  read the contract's mode word, which is a value, not a verdict.
- **Rule 8, one component for both web shells.** `ConnectionChoiceRow` lives in `packages/client-core` and both shells
  mount it; the phone's placement in its review step and its touch sizing under `.screen` are layout.
- **AXI.** An unknown `--connection` value exits 2 and names `terminal, api` on both commands (test ran, and
  `command-line.txt` shows it); the filter narrows `--json`; an empty filtered list says `count: 0 of N total`; a
  refusal's sentence reaches the caller as the Gateway or Director said it (test ran against a stubbed refusal).
- **Switch off, nothing offers it.** The agents leg folds every agent to Terminal alone (test ran), so neither web shell
  draws the row; the desktop hides its row and reports Terminal (tests ran); the Director door refuses `api` (test ran,
  and the probe). The exception is finding 1.
- **Restores still work.** A Director's restore of an API-mode seat goes through the Director door, where the exemption
  is guarded by the existing claim validation.
- **Terminal mode's path unchanged.** With no word or `terminal`, the body reaching the Director carries the same
  `connectionMode` the caller sent, including none (test ran); the web client sends no `connectionMode` unless a choice
  other than terminal is made; the command line sends the field only when the flag is given.
- **The proofs show what they claim.** The three accessibility dumps and the three pictures viewed match the README's
  table: Pi offered both with API mode pressed and the summary line ending ". API mode"; the roster tag on the API-mode
  session only, on both rosters; the desktop row beside the Agent row with API mode checked. The README states the stub
  and names what the pictures do not show. One thing the README does not say: on the Cockpit the tag, like the model tag,
  renders only in the dense-tags density, and the script seeds `cockpit.density=everything`. No harm; noted so the next
  reader is not surprised.
- **Visual style, desktop.** The row uses the same foreground, size, spacing and radio family as the Agent row above it;
  `docs/VisualStyle.md` lists `#CCCCCC` as the primary text value for the desktop palette.

## Not findings (no harm proven)

- `readConnections` in `client.ts` writes the label "Terminal" itself for a Gateway that sends no connections. That label
  is never drawn, because a single connection draws no row.
- `ConnectionModes.TagOf` reads the mode word case-insensitively while `RefusalOf` and phase 2's folds compare exactly.
  The Director always sends the lower-case word, so no row is affected.
