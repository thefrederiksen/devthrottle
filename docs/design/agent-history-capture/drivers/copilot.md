# Driver plan: GitHub Copilot (history capture)

Implementation plan for capturing a GitHub Copilot CLI session's conversation history
into the canonical model and surfacing it in the History tab. PLAN (not yet built);
source for the Copilot GitHub issue.

Status: planned. SQLite driver (same shape as OpenCode). Copilot runs FULL screen.

---

## 1. Snapshot

| Property | Value |
| --- | --- |
| Terminal mode | Full screen / alternate screen (measured) - transcript required |
| Store | `~/.copilot/session-store.db` (SQLite, with -wal / -shm) |
| Tables | `sessions(id, cwd, repository, updated_at)`, `turns(session_id, turn_index, user_message, assistant_response, timestamp)`, `forge_trajectory_events(...)` for tool calls |
| Fidelity | FULL - the `turns` table already splits user and assistant text per turn |
| Pointer | Newest `sessions` row whose `cwd` matches the repo (`ORDER BY updated_at DESC`) |
| Launch wiring | None required |

---

## 2. Why a transcript driver

Copilot runs full screen, so its terminal scrollback is empty. Copilot stores its
sessions in a local SQLite database with a clean `turns` table - each row already has
`user_message` and `assistant_response` - so it is one of the easiest to map. Tool calls
live in `forge_trajectory_events`, linked by turn index.

---

## 3. Reading the store safely

The database is written by a live Copilot process (a `-wal` file is present). To read it
without contending with the writer, COPY `session-store.db` plus its `-wal` and `-shm`
sidecars to a temp file and open the copy read-only (the exact pattern we used while
surveying the stores). Never open the live file for writing.

---

## 4. The plan

### 4a. Pointer - find the current Copilot session
Query `sessions WHERE cwd = <repo> ORDER BY updated_at DESC LIMIT 1` to get the active
session id; cache it per Director session.

### 4b. Reader - turns to canonical
Add `CopilotHistoryReader.Read(repo)`:
- For the resolved session, `SELECT ... FROM turns WHERE session_id = ? ORDER BY turn_index`.
- Each turn becomes a `ConversationMessage(User)` (from `user_message`) followed by a
  `ConversationMessage(Assistant)` (from `assistant_response`).
- For each turn, attach matching `forge_trajectory_events` (by `turn_index`) as
  `ConversationPart(ToolUse)` / `ConversationPart(ToolResult)` (command / output).

| Copilot data | Canonical |
| --- | --- |
| turns.user_message | ConversationMessage(User) + Text |
| turns.assistant_response | ConversationMessage(Assistant) + Text |
| forge_trajectory_events (tool call) | ConversationPart(ToolUse) |
| forge_trajectory_events (output) | ConversationPart(ToolResult) |

### 4c. Wiring
Extend `SessionHistoryReader` with a Copilot branch (copy db, resolve session, read).
Introduce a small shared SQLite-read helper (copy + open read-only) reused by OpenCode.

---

## 5. Implementation steps
1. `SqliteSnapshotReader` - copy db + wal + shm to temp, open read-only (shared with OpenCode).
2. `CopilotHistoryReader.Read(repo)` -> `ConversationHistory` (mapping in 4b).
3. `SessionHistoryReader`: add the Copilot branch.
4. Tests: build a small SQLite fixture (sessions + turns + a tool event) programmatically
   and assert the canonical mapping; a newest-session-for-cwd test; a real-db smoke.
5. No UI change.

---

## 6. Capabilities and limitations
Captured: user prompts, assistant replies (clean, pre-split), tool calls + outputs, timestamps.
Limitations:
- Requires copying the DB on each change (cheap - the db is small); detect change via the
  db file size/mtime, like the transcript readers.
- `forge_trajectory_events` schema details to confirm (event types, command/output fields).
- Pointer heuristic (newest session for cwd).

---

## 7. QA and acceptance (HTML QA report must show)
1. Launch a Copilot session through the Director (requires GitHub auth - inject the token
   or log in inside the tab).
2. Send two prompts; the second forces a tool call.
3. Open the History tab; confirm the canonical thread shows prompts, replies, and the tool
   call + output, in order.
4. Screenshot and produce the HTML QA report.
Acceptance: a full-screen Copilot session shows a complete, structured conversation in the
History tab, sourced from the SQLite store.

---

## 8. Open questions / risks
- `forge_trajectory_events` exact schema and the turn linkage.
- GitHub auth for QA (token injection vs interactive login).
- Whether `turns` can hold streaming/partial rows mid-turn.
