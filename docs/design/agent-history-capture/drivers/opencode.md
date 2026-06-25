# Driver plan: OpenCode (history capture)

Implementation plan for capturing an OpenCode CLI session's conversation history into the
canonical model and surfacing it in the History tab. PLAN (not yet built); source for the
OpenCode GitHub issue.

Status: planned. SQLite driver (same shape as Copilot). OpenCode runs FULL screen.
The DB row format was VALIDATED by running a real session.

---

## 1. Snapshot

| Property | Value |
| --- | --- |
| Terminal mode | Full screen / alternate screen (measured) - transcript required |
| Store | `~/.local/share/opencode/opencode.db` (SQLite, with -wal / -shm) |
| Tables | `session(id, directory, time_updated, ...)`, `message(id, session_id, data, time_created)`, `part(id, message_id, session_id, data, time_created)` |
| Format | `message.data` and `part.data` are JSON blobs |
| Fidelity | FULL (validated): message role + ordered parts (text, reasoning, tool, step-start/step-finish) |
| Pointer | Newest `session` row whose `directory` matches the repo (`ORDER BY time_updated DESC`) |
| Launch wiring | None required. Alt live sources also exist: `opencode run --format json`, an event bus, and a server |

---

## 2. Why a transcript driver

OpenCode runs full screen, so its terminal scrollback is empty. It persists conversations
in a local SQLite database (`opencode.db`). We validated the format by running a real
session: a `message` row per turn (role + model/token metadata) and ordered `part` rows
carrying the content (text, reasoning, tool, step markers).

---

## 3. Reading the store safely

Same as Copilot: COPY `opencode.db` plus `-wal`/`-shm` to temp and open the copy
read-only, because a live OpenCode process is writing it. Reuse the shared
`SqliteSnapshotReader`.

---

## 4. The plan

### 4a. Pointer - find the current OpenCode session
Query `session WHERE directory = <repo> ORDER BY time_updated DESC LIMIT 1`; cache the id.

### 4b. Reader - message/part to canonical
Add `OpenCodeHistoryReader.Read(repo)`:
- `SELECT data FROM message WHERE session_id = ? ORDER BY time_created` - `data.role` gives
  user/assistant.
- For each message, `SELECT data FROM part WHERE message_id = ? ORDER BY time_created` and
  map parts:

| OpenCode part.data.type | Canonical |
| --- | --- |
| text | ConversationPart(Text) |
| reasoning | ConversationPart(Thinking) |
| tool (call) | ConversationPart(ToolUse) (tool name + input) |
| tool (result) | ConversationPart(ToolResult) |
| step-start / step-finish | skipped (turn markers) |

`message.data.role` -> `ConversationMessage(User|Assistant)`.

### 4c. Wiring
Extend `SessionHistoryReader` with an OpenCode branch (copy db, resolve session, read parts).

---

## 5. Implementation steps
1. Reuse `SqliteSnapshotReader` (from the Copilot work).
2. `OpenCodeHistoryReader.Read(repo)` -> `ConversationHistory` (mapping in 4b).
3. `SessionHistoryReader`: add the OpenCode branch.
4. Tests: a programmatic SQLite fixture (session + message + ordered parts incl. a tool
   call/result) asserting the canonical mapping; a newest-session-for-directory test; a
   real-db smoke from one captured run.
5. No UI change.

---

## 6. Capabilities and limitations
Captured: user prompts, assistant replies, reasoning, tool calls + results, timestamps.
Limitations:
- Exact `part.data` tool fields (name / input / result) to confirm against a real run.
- Pointer heuristic (newest session for directory).
- Copy-on-change cost (small db).

---

## 7. QA and acceptance (HTML QA report must show)
1. Launch an OpenCode session through the Director in a known repo.
2. Send two prompts; the second forces a tool call.
3. Open the History tab; confirm the canonical thread shows prompts, replies, and the tool
   call + result, in order.
4. Screenshot and produce the HTML QA report.
Acceptance: a full-screen OpenCode session shows a complete, structured conversation in the
History tab, sourced from the SQLite store.

---

## 8. Open questions / risks
- `part.data` tool shape (name/input/result fields).
- Whether to prefer the DB vs `opencode run --format json` / the event bus for liveness.
