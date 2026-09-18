# Slice 2 evidence - the doorbell

Collected on the Mac mini (devthrottle-mac-mini) on 17 September 2026, by
`src/CcDirector.Gateway.Tests/DoorbellEndToEndProof.cs`. That test is skipped in every suite run. It
runs only when `CC_DOORBELL_E2E_OUT`, `CC_DOORBELL_E2E_TOOL` and `CC_DOORBELL_E2E_CLAUDE` are set. The
command used:

```
env -u CC_SESSION_ID -u CC_GATEWAY_SESSION_KEY -u CC_GATEWAY_URL -u CC_DIRECTOR_ID \
  CC_DOORBELL_E2E_OUT=<dir> CC_DOORBELL_E2E_TOOL=<venv>/bin/cc-devthrottle \
  CC_DOORBELL_E2E_CLAUDE=$HOME/.local/bin/claude \
  dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~DoorbellEndToEndProof"
```

`<venv>` is a scratch Python environment holding this branch's `tools/cc-devthrottle`,
`tools/cc_shared` and `tools/cc_storage`, installed in editable mode.

## What was real and what was not

**Real:**
- A Gateway host built from this branch: tunnel mode, SQLite, a loopback port the operating system
  picked, heartbeat on.
- A Director host (`ControlApiHost`), which runs the real terminal state detector.
- The Director's tunnel client and command dispatcher.
- Two real Claude Code 2.1.274 sessions in real terminals, a supervisor and its worker.
- Session keys minted and registered the way a Director does it.
- This branch's `cc-devthrottle` as the first program on each session's PATH.

**Not real:**
- **The desktop window.** This Mac's screen was locked, and a graphical Director cannot start while it
  is (see the Mac notes). The Director host above is the same code without the window.
- **The five-minute grace.** The proof set the ring grace to ONE MINUTE through
  `GatewayHost.FleetDoorbellLimitsOverride`, so three rings take three minutes instead of fifteen. The
  five-minute product schedule is proven on a fake clock in `FleetDoorbellTests`.
- **The hosted Gateway** and **the owner's installed Director**, neither of which was touched.

The proof stopped its own Director by disposing the in-process host. No process was killed. The owner's
Directors were never touched.

## run1-mid-turn - a message sent mid-turn

1. The worker is given a turn that streams 250 lines. Once its screen shows `esc to interrupt`, the
   supervisor's key posts a three-line message (`02-...`).
2. Nothing is typed while the turn runs. The Gateway skips a session its roster calls Working, and no
   ring appears in the log before the turn ends.
3. The turn ends at 06:21:22.9 (`03-...`, the marker is gone).
4. The settled edge reaches the Gateway about ten seconds later, which is the detector's silence timer.
   At 06:21:32.9 exactly one doorbell line is on screen (`04-...`).
5. The Director's log shows one `RUNG` for this session, and the record shows `RingCount` 1.
6. The agent ran `cc-devthrottle message inbox`. `agent-record-Proof---Worker.txt` is the command output
   the agent received, taken from its own conversation record, with all three lines of the text. The
   record was marked read at 06:21:35.5.
7. The agent then answered `ACK` on screen, as the message asked (`05-...`).
8. `inbox-all-as-Proof---Worker.txt` is a later `message inbox --all` with the worker's own key. It shows
   0 unread, and the one message as read.
9. `log-lines.txt` holds the Gateway and Director lines for the send, the ring, the answer and the read.

## run2-owner-text - owner text in the composer holds the doorbell

1. An unsent owner draft is typed into the worker's composer, then a message is queued (`01-...`).
2. For 50 seconds, every heartbeat asks, and every answer is `DEFERRED (composer-holds-text)`. The log
   shows three of these, at 02:24:11, 02:24:26 and 02:24:41 local time.
3. After those 50 seconds the draft is still on screen, untouched, and no doorbell line was typed
   (`02-...`).
4. The draft is erased, as the owner would erase it. The next heartbeat's answer is `RUNG`, and the
   message is read (`03-...`).

The ring was recorded as `counted=0` because the agent read its inbox 0.9 seconds before the Gateway
wrote the ring. The store refuses to count a ring on a message that is already read, which is the guard
`A_ring_is_not_recorded_on_a_message_already_read` proves.

## run3-stuck - a worker that never reads

1. The worker is told never to run any `cc-devthrottle` command. It answers each doorbell with `IGNORED`
   (`01-...`, three doorbell lines).
2. The Director logged three `RUNG` answers, at 02:26:42, 02:27:54 and 02:29:09. They are a little more
   than a minute apart, because the grace is one minute and the heartbeat ticks every 15 seconds.
3. At 02:30:21 the message was marked `STUCK` with `rings=3` and was never read.
4. A system notice from the Gateway was queued to the supervisor. The supervisor was rung for it and read
   it (`agent-record-Proof---Manager.txt`, `02-...`).

## defect-double-doorbell-before-fix - found by the proof, then fixed

The first full run of run3 showed FOUR doorbell lines on the worker's screen, while the record counted
three rings (`01-worker-after-three-rings.txt`, two of the lines at 2:11). The cause is the Director's
submit check (`SubmitVerifier`):

- It calls a submit proven only when the agent prints 2,048 bytes.
- A one-word answer (`IGNORED`) is shorter than that, so the check threw although the line had been
  submitted.
- The Director answered the ring as failed, so the Gateway did not count it, and rang again.

The fix is in `FleetDoorbellRinger`. When the check throws and the composer now reads empty, the line
left the composer, so the ring is answered `rung`. After the fix, run3 shows exactly three lines and
three counted rings.

This run's product log mirror was off (a harness defect, since fixed), so only the test's own timeline
and the screens were kept.

## guards-watched-failing

Each `*.mutations.json` lists deliberate breaks of the product code. `mutate.py` applies them one at a
time, runs the named tests, and restores the file. Each `*.result.txt` is what the tests said.

| File | Code it broke |
|---|---|
| `mut-safety` / `mut-safety2` | `DoorbellSafety` |
| `mut-doorbell` / `mut-doorbell2` | `FleetDoorbell` |
| `mut-store` / `mut-store2` | `FleetMessageStore` |
| `mut-host` | The wiring in `GatewayHost` |

In the first rounds, some breaks stayed GREEN or failed to compile. Each of those was answered with a new
test or a compiling break in the second round, and every entry in the second rounds is red. The
handoff's Slice 2 section lists them.
