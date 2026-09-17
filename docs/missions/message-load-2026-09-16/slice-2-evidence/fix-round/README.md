# Slice 2 fix round evidence - the doorbell after inspection 4

Collected on the Mac mini (devthrottle-mac-mini) on 17 September 2026, the same way as the evidence one
folder up (see `../README.md`): `src/CcDirector.Gateway.Tests/DoorbellEndToEndProof.cs`, with
`CC_DOORBELL_E2E_OUT`, `CC_DOORBELL_E2E_TOOL` and `CC_DOORBELL_E2E_CLAUDE` set, a scratch Python
environment holding this branch's `tools/cc-devthrottle`, `tools/cc_shared` and `tools/cc_storage`, and
Claude Code 2.1.274.

Real: a Gateway host from this branch (tunnel mode, SQLite, loopback port), a Director host
(`ControlApiHost`, in process, with the real terminal state detector), the Director's tunnel client and
command dispatcher, real Claude Code sessions in real terminals, and minted session keys. Not real: the
desktop window, the five-minute grace (the proof uses one minute), the hosted Gateway, the owner's
installed Director. The proof stopped its own Director by disposing the in-process host; there is no
`POST /shutdown` in this harness (the Director's network control surface was removed on main), no
process was killed, and none of the owner's Directors was touched.

## Results

| Run | Result | What it shows |
|---|---|---|
| `run1` | PASSED (1 minute 34 seconds) | A message sent mid-turn: nothing typed until the turn ended; one doorbell, verified from the screen; one ring counted; the agent's own record shows all three lines; read. |
| `run2` | PASSED (2 minutes 33 seconds) | Owner text held the doorbell for 50 seconds, untouched. **New:** the draft was erased and three spaces left behind. The live frame (`02b-whitespace-draft.json`) shows row 27 as `❯` alone - the spaces are trimmed away - with the visible cursor at column 5, read as "holds text". Two `composer-holds-text` deferrals in 35 seconds, no doorbell. After the spaces were cleared, the next heartbeat rang and the message was read. |
| `run3` | PASSED (4 minutes 58 seconds) | A worker that never reads: three rings (08:38:20, 08:39:32, 08:40:32 UTC), each answered with the one word `IGNORED` and each verified from the screen - no nudges, no fourth line; stuck at 08:41:47 with the notice queued in the same write; the sender was rung for the notice and read it. |
| `run4-snooze` | PASSED (1 minute 43 seconds), second attempt | **New (item 8).** An idle worker snoozed for twelve hours was rung. Its Working pushes carried `WorkingOrigin=agent`, and the Gateway logged "armed snooze kept (ruling 15)" five times; after the doorbell turn settled the snooze was still armed with the same deadline. The owner then typed a prompt: the first Working push, origin `owner`, deleted the snooze (`snooze-log-lines.txt`, 04:46:43.258 local time). |
| `run4-snooze-attempt1` | FAILED, kept on purpose | The snooze half was the same as the second attempt (armed snooze kept through the doorbell turn, then deleted at the owner's working push). The proof itself failed afterwards: its owner prompt asked for one word, and the SHARED submit check - not the doorbell's - pressed Enter six more times on quiet beats and then threw, because a one-word answer never reaches its 2,048 bytes (`failure.txt`, `log-lines.txt`). That is the pre-existing nudge defect the Architect filed separately; the prompt was made to produce a long answer and run 4 was rerun alone. |

Every doorbell in all four runs was verified from the screen: the log has 15
`DoorbellSubmit: submitted, the screen shows the turn` lines and not one `NOT verified`, `not-submitted`,
`REFUSED` or `TIMED OUT` line.

A note on `log-lines.txt`: the proof mirrors the product log into one list for the whole test process, so
a run's `log-lines.txt` also holds the lines of runs that ran before it in the same process (the order was
run 4, run 1, run 3, run 2). Read each file for its own session ids, which the timeline names.

## guards-watched-failing

`mutate.py` (copied from the slice 2 folder) applies each break in a `*.mutations.json`, runs the named
tests, and restores the file. `*.result.txt` is what the tests said. `item3-whitespace-reverted.result.txt`
is the reader as it was before item 3 run against the new tests. `*-rerun.*` files answer breaks that
stayed green or did not compile in the first pass. Every break is red in its final state except the two
named in the handoff as unobservable by design.
