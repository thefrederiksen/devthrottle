# Mobile roster-card slice report

Date: 2026-09-11

## Outcome

Every main mobile roster card now carries a compact agent-tool chip sourced from the Gateway's
`AgentToolDisplay` verdict. The Gateway derives that verdict only from the Director-reported `Agent`
token, never from `CurrentModel`. Known display spellings include `Claude Code`, `GitHub Copilot`, and
`Custom CLI`; other reported tokens remain visible verbatim. A missing token is rendered loudly as
`Agent tool not reported`.

The value travels through the shared browser schema, survives the mobile roster's last-known retention
merge, and reaches the rendered card independently from model data. The existing card hierarchy and the
`testing pi` live session were not changed.

## Committed product and test slice

Initial product commit: `e51b9205172f876defdcd4910d20ceac14e0f8e5`

Inspection repair commit: `7499d8d76ec6624a477c6804dcdc7abbbe4fd68d`

Pull request: [2814](https://github.com/thefrederiksen/devthrottle/pull/2814)

The repair commit merged to `main` through merge commit
`53b8abfc3167125b6d2b7ab5ef33a1e7014ced53`. Hosted continuous integration run
[34672537780](https://github.com/thefrederiksen/devthrottle/actions/runs/34672537780) passed on
attempt 2, and hosted deployment run
[34679499149](https://github.com/thefrederiksen/devthrottle/actions/runs/34679499149) deployed that
merge commit successfully.

Exactly these eight files are in the commit:

- `apps/mobile/src/pages/Home.tsx`
- `apps/mobile/src/pages/HomeRosterCard.test.tsx`
- `apps/mobile/src/styles.css`
- `packages/client-core/src/api/schema.ts`
- `src/CcDirector.Gateway.Contracts/AgentToolDisplayFold.cs`
- `src/CcDirector.Gateway.Contracts/SessionDto.cs`
- `src/CcDirector.Gateway.UnitTests/AgentToolDisplayFoldTests.cs`
- `src/CcDirector.Gateway/Api/GatewayEndpoints.cs`

Mission-record files are being preserved on the mission branch while the final live Wingman check
remains blocked.

The repair commit adds exactly these four test/proof files and no product behaviour change:

- `src/CcDirector.Gateway.Tests/AgentToolDisplayRouteTests.cs`
- `src/CcDirector.Gateway.UnitTests/AgentToolDisplayFoldTests.cs`
- `packages/client-core/src/fleet/rosterRetention.test.ts`
- `apps/mobile/src/pages/HomeRosterCard.test.tsx`

The new Gateway host facts start the real Gateway in hosted mode, enroll two device keys, bind two
tunnel Directors to different tenants, push their session snapshots, and request the real
`GET /sessions?envelope=true` route with the caller's Bearer credential. They assert the serialized
field from the response body, not an object serialized by the test. The separate control proves a
missing credential receives 401 and tenant A sees its own session but not tenant B's.

## Mutation evidence

### Gateway route stamp bypassed

Temporary production mutation, restored immediately after the run:

```csharp
s.AgentToolDisplay = "";
```

Exact command:

```powershell
dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~AgentToolDisplayRouteTests" --nologo
```

The mutation run exited 1 with 2 failed, 1 passed, and 0 skipped out of 3. The two host-bound route
facts observed the missing-field symptom directly in the actual response body:

- With `Agent=ClaudeCode` and `CurrentModel=gpt-5.6-sol`, expected `Claude Code`; received an empty string.
- With `Agent` absent and `CurrentModel=gpt-5.6-sol`, expected `Agent tool not reported`; received an empty string.

The authentication-and-tenant-selection control remained green, proving the host, route, authentication,
and tenant-selected roster still ran while the stamp alone was bypassed. After restoring
`AgentToolDisplayFold.For(s.Agent)`, the same command exited 0 with 3 passed, 0 failed, and 0 skipped.

The original unit facts remain useful but are now named at their actual boundary: they exercise the pure
fold, the shared in-memory enrichment helper, and ordinary web JSON serialization. Their class comment and
missing-agent test name no longer call that a served route.

### Mobile card: model substituted for folded tool label

Temporary production mutation, restored immediately after the run:

```typescript
const agentTool = (session.currentModel ?? "").trim() || "Agent tool not reported";
```

Exact command:

```powershell
npm test --workspace @devthrottle/mobile -- src/pages/HomeRosterCard.test.tsx
```

The mutation run exited 1 with 3 failed and 1 passed out of 4. The retained Pi card showed
`gpt-5.6-sol`, the mapped-label card showed `claude-fable-5`, and the absent-stamp card also showed
`gpt-5.6-sol`. The existing-session-name control remained green. After restoring the read from
`session.agentToolDisplay`, the same command exited 0 with 4 passed across 1 test file.

## Final verification

All commands ran in the foreground.

| Exact command | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj --filter "FullyQualifiedName~AgentToolDisplayRouteTests" --nologo` | 3 passed, 0 failed, 0 skipped. |
| `dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --filter "FullyQualifiedName~AgentToolDisplayFoldTests" --nologo` | 16 passed, 0 failed, 0 skipped. |
| `npm test --workspace @devthrottle/client-core -- src/fleet/rosterRetention.test.ts` | 24 passed across 1 test file. The new fact proves a newer live `agentToolDisplay` replaces the retained older value in both the rendered roster and next cache. |
| `npm test --workspace @devthrottle/mobile -- src/pages/HomeRosterCard.test.tsx` | 5 passed across 1 test file. The new fact renders `Home`, lets its real poll call `GET /sessions?envelope=true`, runs the real retention and grouping path, reaches `Other sessions`, and renders the real card's agent-tool chip. |
| `npm run typecheck --workspace @devthrottle/client-core` | Exited 0 with no type errors. |
| `npm run typecheck --workspace @devthrottle/mobile` | Exited 0 with no type errors. |
| `npm run build --workspace @devthrottle/mobile` | Production build completed; 172 modules transformed and 3 application-shell entries precached. |
| `git diff --check` | Exited 0 before commit. |

The final local build ran before the repair commit was created, so `apps/mobile/dist/build.json` named the
then-current initial product commit `e51b92051`; that local stamp did not prove the later commit identity.
Hosted continuous integration run [34672537780](https://github.com/thefrederiksen/devthrottle/actions/runs/34672537780)
is the commit-bound check for `7499d8d76`.

The first hosted attempt completed with the web and tool-contract jobs successful. Its .NET command
ran every Gateway test and reported 2,415 passed and 47 skipped out of 2,462; the three new
`AgentToolDisplayRouteTests` passed by name. One unrelated Launcher fact failed before those route
tests because it observed an ambiguous process identity instead of the running identity it expected:
`RestartAsync_OnlyIfEmpty_RefusesWhenTheMachineHoldsAResolvedConflict`. That exact fact then passed
locally in both target frameworks. The aggregate failure skipped the hosted installer step, so both
installer projects were run locally and passed 25 of 25 and 541 of 541. Attempt 2 reran only the failed
.NET job on the unchanged commit and passed: the exact Launcher fact passed twice, the new route facts
passed by name, Gateway reported 2,415 passed and 47 skipped out of 2,462, and the installer projects
reported 25 of 25 and 541 of 541.

Production verification at a 390 by 844 viewport derived all `li.row` elements from the served page and
then asserted the presence of `.row-chip-agent` on each: 23 roster cards and 23 agent-tool chips. The
enumerated values included `Claude Code`, `Pi`, and `Codex`. The screenshots
`mobile-roster-production-390x844.png` and `mobile-roster-pi-production-390x844.png` preserve the rendered
surface; the latter centers a real Pi card whose separate chips read `Pi`, `SOREN_NORTH`, and `cc-consult`.

## Named gaps

- The local browser proof uses the document-object test renderer. Production was also verified in a real
  browser at a 390 by 844 viewport, but not on physical phone hardware.
- The repository-wide local gate and complete local backend/browser suites were not clean proof for this
  repair. The focused host, unit, retention, and mobile facts passed, and the clean hosted run executed the
  complete commit-bound suites.
- The production build succeeded but retained the existing large-chunk warning: the main JavaScript asset
  is 856.27 kilobytes before compression and 244.17 kilobytes after compression. Bundle splitting was not
  part of this slice.
- Fresh repair inspection passed with no high, medium, or low findings. Merge, hosted deployment, and
  production narrow-browser verification are complete.
