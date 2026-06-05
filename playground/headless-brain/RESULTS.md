# Headless-brain spike results (issue #172)

Date: 2026-06-05. Slot-5 Director (cc-director5.exe, launched via the
cc-director-launch scheduled task), Control API on port 7886. Session:
ClaudeCode / ConPty in playground/headless-brain/sandbox, wingman disabled.
Claude Code v2.1.165, Opus 4.8 (1M), Claude Max subscription.

## Verdict: Approach A works

The Director's existing machinery (ConPty session + Control API) is fully
sufficient as the warm brain. Zero new PTY code was needed; the entire spike
ran through five existing REST endpoints plus the new /usage endpoint:

    POST /sessions                    create the headless session
    POST /sessions/{sid}/prompt       send a prompt (appendEnter: true)
    GET  /sessions/{sid}              activity state + idleSeconds (turn detection)
    GET  /sessions/{sid}/summary      lastAssistantText (the reply)
    GET  /sessions/{sid}/usage        token accounting (context-reset proof)
    POST /sessions/{sid}/relink       repoint at the post-/clear JSONL
    GET  /sessions/{sid}/buffer       raw terminal tail (debugging)

## Exit criteria

| Criterion | Result |
|---|---|
| Send prompts on demand | PASS - POST /prompt round trip verified repeatedly |
| Read output / detect turn end | PASS - two ways, see "two clocks" below |
| /clear remotely, context provably reset, no restart | PASS - codeword forgotten, verification reply CONTEXT-EMPTY, same PID/session GUID throughout |
| Auth / subscription from a managed process | PASS - banner shows "Opus 4.8 (1M context) - Claude Max"; no API key involved |
| Latency warm vs cold | MEASURED - table below |
| Survives >= 24h under a service | NOT YET - session left running; check after 24h. Service-context (session 0) run is the remaining phase |

## Latency numbers

"Reply-in-jsonl" = time from POST /prompt until the assistant message appears
in the transcript (what a gateway brief agent would wait on). "Turn-end" =
time until the terminal-state detector flips back (includes its 10s quiet
threshold).

| Prompt | Warm reply-in-jsonl | Warm turn-end | Cold `claude -p` |
|---|---|---|---|
| One-word PONG | 4.3s | 14.5s | 7.8s |
| One-sentence summary | 8.2s | 18.8s | 8.2s |
| Turn-brief JSON (3 fields) | ~7.5s | 17.5s | n/a |

Takeaways:
- Latency is model-dominated. Warm beats cold by ~3.5s of process-spawn
  overhead on trivial prompts; on longer generations they converge.
- The REAL wins for warm are not speed: (1) interactive-session billing on the
  Max subscription - `claude -p` and Agent SDK move to the separate monthly
  credit pool on June 15, 2026 ($100/mo Max 5x, $200/mo Max 20x), while a
  persistent interactive session stays on normal subscription limits;
  (2) no cold-spawn failure modes (issue #142: claude --print exits 1 when
  spawned from the Director).
- A brief consumer MUST poll the JSONL (via /usage assistantMessageCount or
  /summary), not the activity state: the detector's 10s quiet threshold adds
  a flat ~10s if you wait for the state flip.

## Gotchas found (load-bearing for the gateway brief agent)

1. /clear starts a NEW claude-internal session id. The Director keeps serving
   /summary and /usage from the OLD JSONL until relinked. Fix used here: the
   external driver finds the newest .jsonl in the repo's claude projects dir
   and POSTs /relink. For production, the Director should re-link
   automatically when it sees a /clear go through (or the brain driver owns
   relinking, as the harness does).

2. Prompts sent immediately after /clear get their Enter swallowed by the
   composer redraw - the text sits unsubmitted in the composer and the turn
   never starts. Fix: gate every send on real byte-silence using the
   server-side idleSeconds clock (harness waits for >= 2s quiet). A blind
   sleep is not needed; the Director already serves the idle clock.

3. Baseline context after /clear is ~62k tokens (system prompt + global
   CLAUDE.md + skills). /clear wipes conversation HISTORY, not the system
   prompt. Per-brief cost is dominated by cache reads of that baseline; the
   conversation growth is what /clear prevents.

4. The bypass-permissions default the Director uses meant the brand-new
   sandbox dir produced no trust-folder prompt. A gateway deployment should
   pin the brain's working dir to a fixed, pre-trusted sandbox anyway.

## Remaining for the issue

- 24h soak (session is running - observe tomorrow).
- Service-context test: this run used a user-session Director launched by
  Task Scheduler. The Gateway-as-service (session 0 / SYSTEM) run still needs
  to validate the nested-ConPty and USER-profile-credentials risks called out
  in the issue. Candidate mitigation is already known: the service delegates
  the spawn to a user-session Director - which is exactly Approach A.
