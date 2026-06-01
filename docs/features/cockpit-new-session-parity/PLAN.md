# Cockpit New Session Dialog - Full Parity Plan

Goal: bring the Cockpit's "New session" modal (`Cockpit.razor`) up to full parity with the
desktop Avalonia dialog (`NewSessionDialog.axaml`/`.cs`).

## Why this is more than a UI job

The desktop dialog runs ON the machine that owns the repos/sessions, so it reads the local
filesystem directly:

- `RepositoryRegistry` (recent repos, add/remove, MarkUsed)
- `SessionHistoryStore.LoadAll()` + `ClaudeSessionReader.ScanAllProjects()` (Resume tab)
- handover-folder scan + frontmatter parse (Handovers tab)
- `StorageProvider.OpenFolderPickerAsync` (native Browse)
- `CcStorage.CoachingCategory(...)` (Assistant/Coach quick-launch paths)

The Cockpit is remote: Blazor Server -> Gateway -> Director REST. The repos/sessions/handovers
live on the **Director's** machine, not the Cockpit host. So each local read above must become
a Director REST endpoint, exposed through the Gateway, and consumed by `GatewayClient`.

The Gateway is NOT a generic proxy. Each Director route is explicitly mapped
(`/directors/{id}/repos`, `/directors/{id}/sessions`, ...) and forwarded via
`DirectorEndpointClient`. So every NEW Director endpoint needs: (1) the Director route in
`ControlEndpoints.cs`, (2) a `/directors/{id}/...` route in `GatewayEndpoints.cs`, (3) a
`DirectorEndpointClient` method, (4) a `GatewayClient` method in the Cockpit.

## Parity matrix (desktop feature -> Cockpit work)

| Desktop feature | Backend today | Work needed |
|---|---|---|
| Agent radio chips (Claude/Pi/Codex/Gemini/OpenCode) | values already in Cockpit dropdown | Frontend only: dropdown -> chips |
| Repo search box | client-side filter | Frontend only |
| Repo table (Name/Path/Last Used), sortable | `GET /repos` already returns LastUsed | Frontend only (Cockpit currently discards LastUsed) |
| Per-row remove (X) | none | NEW `DELETE /repos` (Director+Gateway+clients) |
| Browse... (folder picker) | none; native picker is local-only | NEW remote dir-browser (see decision below) |
| Path input | exists (`_nsRepoPathManual`) | keep |
| Bypass permission prompts | `Args` already honored | Frontend: map to `--dangerously-skip-permissions` |
| Enable Remote Control | `Args` already honored | Frontend: map to `remote-control ` prefix |
| Wingman toggle | `WingmanEnabled` already in contract | Frontend: bind checkbox |
| Assistant / Coach cards | none over REST | NEW `GET /coaching/categories` (resolve Director-local paths) |
| Resume Session tab | none over REST | NEW `GET /claude-sessions` + resume support in `POST /sessions` |
| Handovers tab (list + preview + start) | `POST /handover` exists; list/preview do not | NEW `GET /handovers`, `GET /handovers/content` |
| GitHub (Remote) tab | `POST /sessions/github` EXISTS | Frontend + `GatewayClient`/Gateway proxy method |

## Decision: the Browse... button (remote folder picker)

A native OS folder dialog cannot pick a folder on the Director's machine from a browser.
Options:

- A) Server-side directory browser: NEW `GET /fs/list?path=` on the Director returns
  subdirectories; the Cockpit shows a small folder-navigation modal. True parity, most work.
- B) Drop Browse; rely on the recents table + free-text path field. The path field already
  exists and covers the common case.

Recommendation: **A** for true parity, but build it last (Phase 4). The recents table + path
field already cover the 90% case, so Browse is the lowest-value, highest-effort item.

## Phasing

### Phase 1 - New Session tab, no backend changes (highest value, lowest risk)
Rebuild the modal body in `Cockpit.razor`:
- Agent dropdown -> radio chips.
- Repository dropdown -> searchable, sortable table (Name / Path / Last Used) using the
  `LastUsed` already returned by `GET /repos`. Row click fills the path field.
- Add checkboxes: Bypass permission prompts (default on), Enable Remote Control, Wingman.
  Build `Args` client-side exactly like `MainWindow.axaml.cs:1676-1688`
  (`remote-control ` + `--dangerously-skip-permissions`), set `WingmanEnabled`.
- Disable Bypass/Remote Control when the selected agent is not ClaudeCode (mirror
  `AgentRadio_CheckedChanged`).
- Keep Director picker and free-text path field.
Wire into existing `CreateSession()` (`Cockpit.razor:993`) — it already POSTs `NewSessionRequest`.

### Phase 2 - Repo management + quick-launch
- `DELETE /repos?path=` (Director) -> `RepositoryRegistry.Remove`; Gateway proxy; `GatewayClient.RemoveRepoAsync`. Wire the per-row X.
- `GET /coaching/categories` (Director) returns `[{key,label,description,path}]` from
  `CcStorage.CoachingCategory`. Render Assistant/Coach cards; clicking creates a session at
  that path with bypass on.

### Phase 3 - Resume + Handovers + GitHub tabs (turn the modal into a tabbed dialog)
- Resume: NEW `GET /claude-sessions` (merge `SessionHistoryStore.LoadAll()` +
  `ClaudeSessionReader.ScanAllProjects()` into a DTO). ADD `ResumeSessionId` to
  `NewSessionRequest` and pass it through `POST /sessions` (currently hardcoded
  `resumeSessionId: null` at `ControlEndpoints.cs:1648`).
- Handovers: NEW `GET /handovers` (list + parsed frontmatter) and `GET /handovers/content?path=`
  (preview). "Start from handover" = `POST /sessions` with `PrePrompt` (already supported).
- GitHub (Remote): frontend tab + `GatewayClient.CreateGitHubSessionAsync` ->
  `/directors/{id}/sessions/github` (Gateway proxy) -> existing Director `POST /sessions/github`.

### Phase 4 - Browse (optional, per decision above)
- `GET /fs/list?path=` (Director, sandbox to drive roots) + a Cockpit folder-picker modal.

## Files to touch

- Frontend: `src/CcDirector.Cockpit/Components/Pages/Cockpit.razor` (+ its `@code`), CSS in the
  Cockpit's stylesheet.
- Client: `src/CcDirector.Cockpit/Services/GatewayClient.cs`.
- Contract: `src/CcDirector.Gateway.Contracts/NewSessionRequest.cs` (add `ResumeSessionId`),
  plus new DTOs (ClaudeSessionDto, HandoverDto, CoachingCategoryDto, DirEntryDto).
- Director: `src/CcDirector.ControlApi/ControlEndpoints.cs`.
- Gateway: `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` +
  `src/CcDirector.Gateway/Discovery/DirectorEndpointClient.cs`.

## Tests

- Contract round-trip for new DTOs.
- Director endpoint tests in `CcDirector.Gateway.Tests/ControlApiHostTests.cs` style
  (repos delete, claude-sessions list, handovers list, coaching categories, resume passthrough).
- Args-building unit test (bypass/remote-control/wingman -> request) shared logic.

## Reference points in code

- Desktop dialog: `src/CcDirector.Avalonia/NewSessionDialog.axaml` (+ `.axaml.cs`).
- Desktop consumer / Args building: `src/CcDirector.Avalonia/MainWindow.axaml.cs:1649-1740`.
- Cockpit modal: `src/CcDirector.Cockpit/Components/Pages/Cockpit.razor:222-273`, `@code` ~909-1006.
- Director create/repos: `src/CcDirector.ControlApi/ControlEndpoints.cs:1599-1695` and
  `POST /sessions/github` at 1697+.
