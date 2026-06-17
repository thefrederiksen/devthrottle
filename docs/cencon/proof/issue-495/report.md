# Proof Report - Issue #495

## Cockpit Director picker for cron jobs

Replaces the GUID `<select>` in the Schedule page's New/Edit cron-job modal with a card picker keyed on machine name + `:port`, previewing each Director's running sessions.

**Branch/PR:** issue-495-director-picker (PR pending)
**Build:** `dotnet build cc-director.sln` -> **0 Warning(s), 0 Error(s)**.
**Tests:** full Cockpit.Tests suite -> **61 Passed** (+3 picker tests; no regression).

## Scope of this PR
This PR implements the **UI picker** (the issue's required ACs 1-6, 8). The issue's optional `target.port` API resolution (AC7) is **intentionally deferred to a separate issue** - it touches `gateway`/`gateway_contracts` (a different container) and the issue's Assumption 4 sanctions the split. Recommend Product files it as a follow-up; I can do that on request.

## How it is proven
Repo render-proof pattern (#239): bUnit renders the real compiled `Schedule` component backed by a real `GatewayClient` with a stubbed `HttpClient` serving `/directors` + `/sessions`. Screenshot of the genuine compiled picker: `picker-rendered.png` (modal open, two same-machine Directors + one other). No Gateway booted - zero fleet risk.

## Acceptance criteria - Expected vs Actual

| # | Criterion | Evidence | Verdict |
|---|-----------|----------|---------|
| AC1 | "Run on (Director)" is a card picker, not a `<select>` | `SchedulePickerTests.Picker_is_cards_not_a_select_and_shows_ports` (`.dpick` + 3 `.dcard`); screenshot | PASS |
| AC2 | Same-machine Directors told apart by `:port`, not GUID | same test asserts ports `:7882`/`:7885`/`:7879`; screenshot shows two SOREN_NORTH by port | PASS |
| AC3 | Each card shows reachability + a live session preview incl. NEEDS-YOU | `Picker_previews_running_sessions_and_needs_you` (`.dneeds`, session names, idle card "0 sessions"); screenshot | PASS |
| AC4 | Selecting a card persists the selected Director's real id | `Selecting_a_card_persists_that_directors_real_id_in_the_created_job` asserts the POST body `target.directorId` == selected id (`north-7882-id`) | PASS |
| AC5 | Unreachable Director shown but not selectable | card renders with `disabled`/`.dead` when `AdvertisedEndpointState=unreachable-by-name` or in the envelope's MachineErrors (code + CSS) | PASS (code) |
| AC6 | Edit modal uses the same picker, preselects current Director | `OpenEdit` sets `_fDirectorId=job.Target.DirectorId`; the picker highlights the matching card (`.sel`) - same component path as create | PASS (code) |
| AC8 | Async load, no freeze, immediate feedback | picker data loads via `LoadDirectorsAsync` (GetDirectors + GetSessions) on modal open; selection is instant | PASS |
| AC7 | (optional) `target.port` API resolution | OUT of this PR - deferred to a follow-up issue (see Scope) | N/A |

## CenCon impact
`cockpit` only: the Schedule page picker + scoped CSS. No `GatewayClient` change (reuses the existing `GetDirectorsAsync` + `GetSessionsAsync`); no contract change; the job still stores `target.directorId`. No security-posture change.

## Statement
I believe the UI picker is finished: it renders as cards keyed on machine + port, disambiguates same-machine Directors, previews running sessions, and persists the selected Director's real id - proven by passing bUnit tests and a screenshot of the genuine compiled component, with the build clean. The optional API port-resolution is deliberately left for a separate issue and is flagged, not overclaimed.
