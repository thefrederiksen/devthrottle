# Human QA Test Plan - Issue #508: Named Sessions

This plan lets a human tester verify every acceptance criterion in issue #508 by clicking through
the running application. No knowledge of the code is required - follow the steps in order.

The feature: a "named session" is a saved launch preset that ties together a repository, an agent,
and a model under a fixed name. You save the combination once and launch it again in one click from
the New Session dialog.

---

## What was built (so you know what to look for)

- A new per-file store, `NamedSessionStore`, saves each preset as
  `config/director/named-sessions/{slug}.named-session.json`.
- The New Session tab gained ONE compact row at the top: a "Named session" dropdown (placeholder
  "(none)") and a small "Manage..." button.
- A compact "Model:" row sits with the Agent picker. It defaults to the selected agent's default
  model; a preset fills it with its saved model.
- A "Save as named session..." button sits to the LEFT of the Start button, enabled only when a
  repository, an agent, and a model are all set.
- A dedicated "Manage Named Sessions" dialog (opened by "Manage...") lists presets and supports
  rename and delete. Orphaned presets (missing repository folder or removed agent) appear only here,
  greyed and labelled, never in the start flow.

---

## Preconditions

1. CC Director is running (a normal build; this feature needs no special mode).
2. At least one agent is configured (Settings > Agents). For the model checks, an agent whose
   "Default model" is set is convenient but not required.
3. Have at least two real repository folders on disk that you can pick.
4. Storage location to inspect saved files (optional, for the file-write check):
   `%LOCALAPPDATA%\cc-director\config\director\named-sessions\`

If you want to start from a clean slate, you may delete any existing `*.named-session.json` files in
that folder before you begin.

---

## Test 1 - The compact row exists and the primary flow keeps its place

Verifies: "The New Session tab shows a single compact 'Named session' row (dropdown + Manage); the
Agent picker, repository list, and Start button keep their position."

Steps:
1. Open the New Session dialog (the "+ New Session" entry point).
2. Confirm the "New Session" tab is selected.

Expected result:
- At the very top there is a single row reading "Named session:" with a dropdown showing "(none)"
  and a small "Manage..." button to its right. That is the only named-session control in the dialog
  body.
- Below it, the Agent picker, the "Model:" row, the "Select a repository:" search and list, and the
  "Start Session" button are all present and in their normal positions. Named sessions did NOT push
  them down behind a large panel.

PASS if the compact row is present and the primary controls are unchanged in position. FAIL
otherwise.

---

## Test 2 - "Save as named session..." is disabled until repository + agent + model are all set

Verifies: "'Save as named session...' is disabled until a repository, an agent, and a model are all
selected; saving writes one preset file capturing repository + agent id + model + name."

Steps:
1. In the New Session dialog, look at the bottom button row. There is a "Save as named session..."
   button to the LEFT of "Start Session".
2. Clear the "Model:" box if it has text. Do not select a repository yet.
3. Observe the "Save as named session..." button state.
4. Select a repository from the list (or Browse to one).
5. Make sure an agent radio is selected (the first is selected by default).
6. Type a model into the "Model:" box (for example `claude-opus-4` - any non-empty value).
7. Observe the "Save as named session..." button state again.
8. Click "Save as named session...". In the small "Save named session" prompt, type a name such as
   `QA Director Opus` and confirm.

Expected result:
- At step 3 (no repository and/or empty model), "Save as named session..." is DISABLED (dimmed).
- At step 7 (repository + agent + model all set), the button is ENABLED.
- After step 8 a preset file appears at
  `%LOCALAPPDATA%\cc-director\config\director\named-sessions\qa-director-opus.named-session.json`.
  Open it and confirm it contains the repository path you chose, an `AgentId` value, the model text
  you typed, and the name.

PASS if the enable/disable behaviour matches and the file is written with all four values. FAIL
otherwise.

---

## Test 3 - Selecting a preset fills the repository, agent, and model fields

Verifies: "Selecting a saved preset from the dropdown fills the repository, agent, and model fields
with the saved values (the user can see them before starting)."

Steps:
1. Still in the New Session dialog (with the `QA Director Opus` preset saved from Test 2), change
   the fields away from the saved values: pick a DIFFERENT repository, select a different agent if
   you have one, and change the "Model:" box text.
2. Open the "Named session:" dropdown and pick `QA Director Opus`.

Expected result:
- The repository path returns to the one saved in the preset (and the matching repository row
  becomes selected if it is in the list).
- The agent radio returns to the agent saved in the preset.
- The "Model:" box returns to the model saved in the preset.
- No session starts - the dialog stays open. The user can see the filled values before pressing
  Start.

PASS if all three fields fill with the saved values and nothing launches. FAIL otherwise.

---

## Test 4 - Start launches the saved repository, agent, and model

Verifies: "Pressing Start after selecting a preset launches a session in the saved repository, with
the saved agent and model (verified by the launch log line and the running session)."

Steps:
1. With `QA Director Opus` selected in the dropdown (fields filled as in Test 3), press
   "Start Session".
2. Watch the new session appear and open the Director log at
   `%LOCALAPPDATA%\cc-director\logs\director\` (the newest `director-*.log`).

Expected result:
- A new session starts in the repository saved in the preset.
- In the log, the line `[MainWindow] ShowNewSessionDialog: path=...` shows the saved repository path
  and the saved agent. The resolved command line for the agent includes the saved model (look for
  the model string you saved, passed as the agent's `--model` argument).
- The running session is working in the saved repository (its title/path matches).

PASS if the launched session uses the saved repository, agent, and model. FAIL otherwise.

---

## Test 5 - Manage dialog lists presets and supports rename and delete; no delete in the start flow

Verifies: "The Manage dialog lists presets and supports rename and delete; deleting removes the
preset file. There is NO delete control in the New Session start flow itself."

Steps:
1. In the New Session dialog, confirm there is NO delete (trash / X) control on the "Named session:"
   row or anywhere in the start flow for named sessions.
2. Click "Manage...". The "Manage Named Sessions" dialog opens.
3. Confirm `QA Director Opus` is listed with its agent, model, and repository on the detail line.
4. Select it and click "Rename...". Enter `QA Director Renamed` and confirm.
5. Confirm the list now shows `QA Director Renamed`.
6. Select it and click "Delete". Confirm the deletion in the prompt.
7. Confirm the row disappears from the list. Close the Manage dialog.
8. (Optional) Confirm the matching `*.named-session.json` file is gone from the named-sessions
   folder.

Expected result:
- There is no delete control in the New Session start flow; deletion is only inside the Manage
  dialog, away from the launch buttons.
- Rename updates the displayed name (and the underlying file slug).
- Delete removes the preset from the list and removes its file from disk.

PASS if rename and delete work in the Manage dialog and no delete control exists in the start flow.
FAIL otherwise.

---

## Test 6 - Orphans appear only in Manage, greyed and labelled, launch disabled

Verifies: "A preset whose repository folder or agent id no longer exists appears only in the Manage
dialog, greyed, labelled with the reason, with launch disabled - it does not appear as a row in the
New Session start flow."

Set up an orphan (choose ONE of the two cases, or do both):

Case A - missing repository:
1. Save a new preset (Test 2 steps) pointing at a repository folder you can delete or rename.
2. Close CC Director.
3. On disk, rename or delete that repository folder so the saved path no longer exists.
4. Reopen CC Director and open the New Session dialog.

Case B - removed agent:
1. Save a new preset using a specific agent.
2. Go to Settings > Agents and delete (or disable then remove) that agent.
3. Reopen the New Session dialog.

Steps (after either case):
1. Open the "Named session:" dropdown in the New Session tab.
2. Click "Manage..." and look at the orphaned preset's row.

Expected result:
- The orphaned preset does NOT appear in the "Named session:" dropdown in the start flow.
- In the Manage dialog the orphaned preset IS listed, shown greyed/dimmed, with a small label
  reading "repository missing" (Case A) or "agent removed" (Case B).
- The orphan cannot be launched from the start flow (it is simply not offered there).

PASS if the orphan is hidden from the start dropdown but visible, greyed, and labelled in Manage.
FAIL otherwise.

---

## Test 7 - Logging format (spot check)

Verifies: "Entry, exit, and failure are logged in the [ClassName] Method: context format."

Steps:
1. After running Tests 2-5, open the newest Director log under
   `%LOCALAPPDATA%\cc-director\logs\director\`.
2. Search for `[NamedSessionStore]`, `[NewSessionDialog]`, and `[ManageNamedSessionsDialog]`.

Expected result:
- You see lines such as `[NamedSessionStore] Save: ...`, `[NewSessionDialog] LoadNamedSessions`,
  `[NewSessionDialog] NamedSessionCombo_SelectionChanged: applying preset ...`,
  `[ManageNamedSessionsDialog] BtnDelete_Click: ...`, all in the `[ClassName] Method: context`
  format.

PASS if the log lines are present in that format. FAIL otherwise.

---

## Test 8 - Store unit tests (automated, no app)

Verifies: "The store has unit tests covering create, list, delete, and the orphan (missing repo /
missing agent) cases."

Steps:
1. From the repository root run:
   `dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj --filter "FullyQualifiedName~NamedSessionStoreTests"`

Expected result:
- All `NamedSessionStoreTests` pass, including the create/list/delete cases and the orphan cases
  (`LoadAllWithStatus_MissingRepository_IsRepositoryMissingOrphan`,
  `LoadAllWithStatus_RemovedAgent_IsAgentRemovedOrphan`, and the doubly-broken case).

PASS if all tests pass. FAIL otherwise.

---

## Acceptance criteria coverage map

| Acceptance criterion (issue #508) | Test |
|-----------------------------------|------|
| Single compact "Named session" row; primary flow keeps position | Test 1 |
| Save disabled until repository + agent + model; writes one preset file | Test 2 |
| Selecting a preset fills repository + agent + model | Test 3 |
| Start launches the saved repository + agent + model | Test 4 |
| Manage dialog lists, renames, deletes; no delete in start flow | Test 5 |
| Orphans only in Manage, greyed, labelled, launch disabled | Test 6 |
| Logging in `[ClassName] Method: context` format | Test 7 |
| Store unit tests: create / list / delete / orphan cases | Test 8 |
