# Driver plan: Pi (history capture)

Implementation plan for capturing a Pi CLI session's conversation history into the
canonical model and surfacing it in the History tab. PLAN (not yet built); source for
the Pi GitHub issue.

Status: planned. JSONL-file driver (same shape as Codex).

---

## 1. Snapshot

| Property | Value |
| --- | --- |
| Terminal mode | Normal buffer (measured) |
| Store | `~/.pi/agent/sessions/<encoded-cwd>/<timestamp>_<session-id>.jsonl` (verified against real data; Pi groups session files under a sanitized per-cwd directory) |
| Format | JSON lines; `type` in { session, model_change, thinking_level_change, message } |
| Fidelity | FULL - user + assistant messages, tool results, id/parentId tree |
| Pointer | Newest `~/.pi/agent/sessions/**/*.jsonl` whose `session` line cwd matches the repo and which is at/after launch (no hooks). The per-cwd directory name is a lossy sanitization, so we match on the authoritative `cwd` inside the `session` record, not the directory name |
| Launch wiring | None required. Pi also offers live event capture (see open questions) as a future authoritative source |

---

## 2. Why a transcript driver

Pi renders in the normal terminal buffer, but it also writes a clean per-session event
log as JSON lines - this is the "event capture" Pi provides. Reading that log gives us
structured user / assistant / tool turns directly, which we map into the same canonical
`ConversationHistory` as every other driver.

---

## 3. The store and its format

Each line is a JSON object with a `type`:

- `session` - one per file; carries `id`, `cwd`, `timestamp`. Used to match a file to a
  Director session (by cwd) and to order files.
- `model_change`, `thinking_level_change` - bookkeeping; skipped.
- `message` - the conversation. `message.role` is user / assistant / toolResult, and
  `message.message` carries the content (text, thinking, tool result). Each entry has an
  `id` and `parentId` (a tree, like Claude's parentUuid).

---

## 4. The plan

### 4a. Pointer - find the current Pi session file
Pi has no hook that pushes the active session to us. Resolve on demand: scan `~/.pi`
for `*.jsonl`, keep those whose `session` line `cwd` equals the Director session repo
and whose timestamp is at/after the session launch, pick the newest, and cache the path
on the session (re-scan only if it disappears). Same newest-for-cwd rule as Codex.

### 4b. Reader - Pi JSONL to canonical
Add `PiTranscriptReader.Read(path)` mirroring `ClaudeTranscriptReader` (FileShare.ReadWrite,
tolerate a truncated final line):

| Pi line | Canonical |
| --- | --- |
| message role=user (text) | ConversationMessage(User) + Text |
| message role=assistant (text / thinking) | ConversationMessage(Assistant) + Text / Thinking |
| message role=assistant (tool use in content) | ConversationPart(ToolUse) with name + input |
| message role=toolResult | ConversationMessage(User) + ToolResult, paired by id |
| session, model_change, thinking_level_change | skipped |

### 4c. Wiring
Extend `SessionHistoryReader` with a Pi branch (resolve path, read). No launch change.

---

## 5. Implementation steps
1. `PiSessionLocator` - newest-for-cwd resolution + per-session cache.
2. `PiTranscriptReader.Read(path)` -> `ConversationHistory` (mapping in 4b).
3. `SessionHistoryReader`: add the Pi branch.
4. Tests: a fixture file (session + message user/assistant/toolResult + thinking +
   truncated line) asserting the canonical mapping; a locator test (newest-for-cwd); a
   real-file smoke (not committed).
5. No UI change.

---

## 6. Capabilities and limitations
Captured: user prompts, assistant replies, thinking, tool uses + results, timestamps.
Limitations: pointer heuristic (newest-for-cwd); exact `message.message` content shape
must be confirmed against a real file; tree (parentId) is read linearly for the first cut.

---

## 7. QA and acceptance (HTML QA report must show)
1. Launch a Pi session through the Director in a known repo.
2. Send two prompts; the second forces a tool call.
3. Open the History tab; confirm the canonical thread shows the prompts, replies, and the
   tool call + result, in order.
4. Screenshot the History tab and produce the HTML QA report (same format as the Claude proof).
Acceptance: the History tab renders a Pi conversation identical in shape to Claude's.

---

## 8. Open questions / risks
- Exact `message.message` content schema (text / thinking / tool) - confirm on a real file.
- Pi's live event-capture mechanism as a future authoritative pointer (vs newest-for-cwd).
- toolResult-to-call pairing key.
