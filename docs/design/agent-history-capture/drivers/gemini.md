# Driver plan: Gemini (history capture)

Implementation plan for the Gemini CLI - the exception case. PLAN (not yet built);
source for the Gemini GitHub issue.

Status: planned. Terminal-derived (Gemini provides NO usable transcript).

---

## 1. Snapshot

| Property | Value |
| --- | --- |
| Terminal mode | Normal buffer (measured) - the terminal scrollback DOES contain the conversation as text |
| Store | None usable. `~/.gemini/tmp/<cwd-hash>/logs.json` records USER PROMPTS ONLY - never assistant responses |
| Telemetry | OTLP, but measured to log prompts only (there is a `--telemetry-log-prompts`, no log-responses) - does not help |
| Fidelity | LOW - unstructured text only; no role separation, no tool structure |
| Pointer | N/A (no transcript). Source is the session's own terminal buffer |
| Launch wiring | None |

---

## 2. Why Gemini is different

Every other agent either writes a structured transcript (Claude, Codex, Pi, Grok,
Copilot, OpenCode) or, failing that, at least lets us read structure. Gemini does
neither: it persists only the user's prompts (no model responses) and exposes no
response-logging telemetry. So there is nothing to parse into structured turns.

The saving grace: Gemini runs in the NORMAL terminal buffer, so the full conversation is
already present as text in the session's terminal scrollback - which the user can already
read in the existing Terminal tab.

---

## 3. The plan (honest, best-effort)

Because there is no transcript, the History tab cannot show structured turns for Gemini.
The plan is a deliberately minimal, clearly-labeled fallback:

- Take the session's terminal buffer (`CircularTerminalBuffer.DumpAll`), clean the ANSI
  with `AnsiCleaner`, and present it in the History tab as a single, unstructured
  "terminal transcript" block, labeled "raw terminal text - Gemini provides no structured
  transcript."
- Represent it in the canonical model minimally: one `ConversationMessage` carrying the
  cleaned text (or special-case the History tab to render Gemini's cleaned scrollback
  verbatim).

What we explicitly do NOT do:
- We do not fake structure by parsing Gemini's TUI markers into turns (fragile, breaks on
  every UI change).
- We do not claim tool fidelity - Gemini gives us none.

This is the floor (the universal "screen text" path) applied to Gemini specifically.

---

## 4. Decision to confirm

Given the Terminal tab already shows Gemini's conversation, the History tab's added value
for Gemini is marginal. Two reasonable options:
- (A) Implement the minimal terminal-text History (above) so the tab is consistent across
  all agents (it always shows *something*).
- (B) Defer: for Gemini, the History tab shows a notice ("Gemini has no structured
  transcript - use the Terminal tab"), and we invest the effort elsewhere.

Recommendation: (A) for consistency, but it is low priority relative to the structured
drivers. The issue should call out this trade-off.

---

## 5. Implementation steps (option A)
1. `GeminiTerminalHistory` - clean the session buffer into a single canonical text block.
2. `SessionHistoryReader`: add the Gemini branch (terminal text, not a file).
3. The History tab renders it as one block with the "raw / unstructured" label.
4. Tests: cleaned-buffer-to-text.

---

## 6. Capabilities and limitations
Captured: the conversation TEXT (whatever is in the terminal scrollback).
Not captured: role separation, tool calls/results, anything that scrolled out of the
buffer's ring, structured turns.

---

## 7. QA and acceptance (HTML QA report must show)
1. Launch a Gemini session through the Director.
2. Send a couple of prompts.
3. Open the History tab; confirm the conversation TEXT is visible (not structured turns).
4. Screenshot and produce the HTML QA report.
Acceptance (option A): the History tab shows Gemini's conversation text, clearly labeled
as raw/unstructured - consistent presence across all agents, with honest fidelity.

---

## 8. Open questions / risks
- Whether to ship option A or B (consistency vs effort) - flagged for the issue.
- The terminal ring is bounded (2 MB); very long Gemini sessions lose the earliest text.
- If Gemini later adds a transcript or response-logging telemetry, swap to a real reader.
