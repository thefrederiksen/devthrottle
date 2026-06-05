# headless-brain - issue #172 spike harness

Drive a headless Claude Code session from the outside, purely over a
Director's Control API. Validates the warm-brain assumption under the
gateway turn-brief design. Findings: see RESULTS.md.

## Prerequisites

A running Director. For agent-driven testing use the slot-5 build via the
`cc-director-launch` scheduled task (see CLAUDE.md - never spawn
cc-director.exe from a Claude ConPty). Find its port in the director log
("Kestrel listening on ...").

## Usage

```
python harness.py create [--port 7886] [--repo PATH]   # spawn + wait ready
python harness.py send "prompt text" [--timeout 300]   # send, wait, print reply + latency + tokens
python harness.py clear                                 # /clear + relink + prove context reset
python harness.py status                                # state, idle clock, token usage
python harness.py buffer [--lines 40]                   # raw terminal tail
python harness.py bench "prompt"                        # warm session vs cold `claude -p`
python harness.py kill                                  # DELETE the session
```

State (port, session id, repo) persists in state.json, so `create` once and
keep calling the other verbs from any shell. Pure stdlib, ASCII output.

## The three rules this harness encodes

1. Wait for byte-silence (idleSeconds >= 2) before every send - prompts sent
   into a repainting composer lose their Enter.
2. Read replies from the JSONL (/summary, /usage), not the terminal - and do
   not wait for the activity-state flip unless you need it (10s quiet
   threshold).
3. After /clear, relink: find the newest .jsonl in
   ~/.claude/projects/<mangled-repo-path>/ and POST /sessions/{sid}/relink.
