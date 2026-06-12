## Problem / Value

Branch `issue-347-voice-dictation-recording-indicator` (commit `044e1c3`) added `VoiceTurnAsyncEndpoint.cs` and `TurnJobStore.cs` to `CcDirector.ControlApi` as a temporary async submit/poll implementation while the Gateway architecture was being designed. The correct home for this logic is the Gateway (issue #NNN). These Director-side files must be removed before the branch merges so we do not ship a duplicate, wrong-location implementation alongside the correct Gateway version.

The Director's SSE endpoint (`VoiceTurnEndpoint.cs`) and any helpers it depends on are **not** removed -- they are the internal engine the Gateway drives.

## Scope

**In:**
- Delete `src/CcDirector.ControlApi/VoiceTurnAsyncEndpoint.cs`
- Delete `src/CcDirector.ControlApi/TurnJobStore.cs`
- Remove from `src/CcDirector.ControlApi/ControlApiHost.cs`: the singleton registration of `TurnJobStore` and the `VoiceTurnAsyncEndpoint.Map()` call (and any `using` directives that become unused)
- Verify `dotnet build` is clean after deletion

**Out:**
- `VoiceTurnEndpoint.cs` (SSE endpoint) -- stays on Director, unchanged
- `VoiceTurnHelpers.cs` (if present) -- stays on Director, unchanged
- Phone code changes
- Gateway code changes

## Acceptance Criteria

- [ ] `src/CcDirector.ControlApi/VoiceTurnAsyncEndpoint.cs` does not exist in the repo
- [ ] `src/CcDirector.ControlApi/TurnJobStore.cs` does not exist in the repo
- [ ] `src/CcDirector.ControlApi/ControlApiHost.cs` contains no reference to `TurnJobStore` or `VoiceTurnAsyncEndpoint`
- [ ] `dotnet build cc-director.sln` succeeds with 0 errors and 0 warnings caused by these deletions
- [ ] `dotnet test --filter VoiceTurn` still passes (the SSE endpoint tests in `VoiceTurnEndpointTests.cs` are unaffected)

## Affected Containers

- `CcDirector.ControlApi`

## Proof Target

Screenshot of `dotnet build cc-director.sln` terminal output showing `Build succeeded` with 0 errors, confirming the removed files left no dangling references.

## Assumptions

- If `VoiceTurnAsyncEndpoint.cs` or `TurnJobStore.cs` were already removed from the branch before this issue is picked up, this issue is a no-op; close it with a comment stating the files were not found.
- This issue does not require the Gateway implementation (issue #NNN) to be merged first -- the deletion is safe as a standalone change.
