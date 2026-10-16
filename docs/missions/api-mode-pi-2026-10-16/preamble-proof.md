# Phase 1: the preamble proof

Mission: API mode for agent wrappers, proven on Pi (issue #4070), section 5, "Lessons from
`StudioBackend`": the fleet preamble, Pi's extensions and the fleet's skills must reach Pi in API mode.
This is the one real Pi run phase 1 owes. Recorded on devlinux, 16 October 2026, Pi 1.2.0.

## What was run

A throwaway program (not committed) made one API-mode Pi session the way the Director makes every
session: `SessionManager.CreateSession(..., connectionMode: Api)` with a `PiAgent`, so the launch
line, the preamble file and the environment were composed by the Director's own create, not by hand.
The only differences from a Director: the storage root was a scratch folder (so the preamble file was
written there), there was no Gateway (so no Gateway address or key was stamped), and the process
factory wrapped the real `SystemApiModeProcess` in a recorder that wrote every line in both
directions to `preamble-proof/wire.jsonl`.

The session then did three things:

1. The host wrote `get_state` first, as it always does; Pi named its conversation (`ConnectionReady`).
2. One prompt went through the session's own send (`Session.SendTextAsync`): "Reply with exactly the
   word PONG". Pi acknowledged it (Delivered), ran its turn and ended it done.
3. After the turn, while Pi was idle, the program wrote one `get_commands` straight to Pi's input,
   only to list what Pi loaded. The host never sends it; its response reached the dialect as a
   response to a command the Director did not write, which the dialect logs as a defect. That is
   expected here and is the only reason the program wrote it. `get_commands` makes no model call.

Then the session was stopped the ordinary way (`Session.KillAsync`: Pi's input closed, Pi exited 0).

## What it shows

From `preamble-proof/summary.txt` and `preamble-proof/wire.jsonl` (redacted, below):

| Claim | Evidence |
|---|---|
| Pi ran in API mode with terminal mode's own launch line | `arguments: --mode rpc --session-id 2118... --append-system-prompt <director-root>/pi-preamble/<session>.txt`: `--mode rpc` ahead of exactly what the terminal path composes. Working folder: the session's. |
| The environment is terminal mode's | `variables set: CC_FLEET_TOOLS, CC_SESSION_ID` (the session's own stamped set; no Gateway here, so no Gateway address or key), `inherited prefixes removed: CC_`. |
| **The fleet preamble reached Pi** | Pi's system message (`message_start`, `role: system`, the first turn of a new session) has an `addendum` section. With its tags removed it is **byte for byte the preamble file** the Director wrote (`preamble-proof/preamble-file.md`, checked by a script comparing the two). |
| **The fleet's skills reached Pi** | The same system message's `skills` section lists all 20 fleet skills (`browsers`, `checks-that-fail-open`, ..., `user-input-terminal`), and `get_commands` returns the same 20 with `source: skill`. |
| **Pi's extensions loaded** | `get_commands` returns 2 commands with `source: extension`: `llama` (`builtin:llama.cpp`) and `mcp` (`builtin:mcp`). They are Pi's built-in extensions; this machine configures no others, and the phase 0 recording shows the same two. |
| Delivered from Pi's acknowledgement | `send outcome: confirmed=True`; `events.jsonl` has `ConnectionReady`, `PromptDelivered` (the acknowledgement is Pi's response line verbatim), `TurnStarted`, the reply `PONG` (`MessageText` deltas and final), `TurnEnded` done, `ConnectionEnded` exit code 0. |

## Redaction

As phase 0 did: the scratch work folder is `/work`, the scratch storage root is `/director-root`, the
home folder is `/home/fixture` (also inside Pi's session folder name), the model provider's address is
`https://model-provider.invalid`, the model's display name reads "model display name redacted by the
fixture recorder", and every skill description reads "redacted by the fixture recorder". Skill names
and the extension paths stay on purpose: they are the proof. A search of the four files for the user
name, the company, the provider's host and the scratch path finds nothing.

## Spend

Two runs were made: the first without `get_commands`, then this one. Both are in `spend.md`: 0.05
dollars at the record's deliberately high rates (Pi itself reports a cost of 0).

## Files

- `preamble-proof/wire.jsonl`: every line both ways, with milliseconds since start (`stdin`, `stdout`, `stderr`).
- `preamble-proof/events.jsonl`: the session events the host published, as JSON (`SessionEventJson.Options`).
- `preamble-proof/preamble-file.md`: the preamble file the Director wrote for the session.
- `preamble-proof/summary.txt`: how Pi was started, and the outcome.
