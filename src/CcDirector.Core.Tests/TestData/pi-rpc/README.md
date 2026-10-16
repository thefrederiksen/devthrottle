# Pi wire logs (`pi --mode rpc`)

Real recordings of Pi 1.2.0 driven over its native protocol on devlinux, 16 October 2026, for the
API-mode mission (issue #4070). Recorded by `scripts/record-pi-rpc-fixture.py`; the design note
that maps them is `docs/missions/api-mode-pi-2026-10-16/event-model.md`. None is hand-built.

Each scenario has two files:

- `<scenario>.stdout.jsonl` - Pi's standard output, byte for byte (after redaction). For framing
  tests: split on line feed only.
- `<scenario>.transcript.jsonl` - both directions in order, with milliseconds since start: a `meta`
  record first, then `stdin` (what we wrote), `stdout` (what Pi wrote), `stderr`, `kill` and `exit`
  (Pi's exit code). A fake process replays it by answering each `stdin` record with the `stdout`
  records that follow it.

| Scenario | What it shows |
|---|---|
| `plain` | A prompt acknowledged (`disposition: started`), the reply `PONG` streamed, `agent_settled`; then `get_last_assistant_text`, `get_state`, `get_commands` (the fleet's skills loaded) and `get_session_stats`. The 17 kilobyte system message of a new session is on the wire. |
| `tool` | A prompt that runs the bash tool: `tool_execution_start`, two `tool_execution_update`, `tool_execution_end` (`isError: false`), two Pi turns inside one run. |
| `abort` | A long prompt stopped with `abort` while thinking streamed: `stopReason: "aborted"`, `agent_settled` with `aborted: true`, and the abort's response last. |
| `resume` | A new Pi process on the `plain` session's id answering from that conversation (`PONG`); no system message, no "No project session found" warning. |
| `refusal` | Real refusals: a prompt during a turn without `streamingBehavior` (`success: false` with Pi's reason), an unknown command, malformed JSON (a `parse` response with no id); then a prompt Pi queued (`disposition: queued`, `queue_update`, a second Pi turn in the same run), and `abort` while idle. |
| `exit` | Pi killed (SIGKILL, exit code -9) while the reply streamed: the output ends after a `message_update`, with no `agent_settled`. |
| `exit-before-ack` | Pi killed (SIGKILL, exit code -9) 50 milliseconds after the prompt was written, before it acknowledged: standard output is empty, so the prompt has no response. Made no model call. |

Redaction: the work folder is `/work` (and `work` inside Pi's session folder name), the home folder
is `/home/fixture`, the model provider's address is `https://model-provider.invalid`, and skill
descriptions read "redacted by the fixture recorder". Nothing else was changed.

This folder is marked `-text` in `.gitattributes` so line endings are never converted.
