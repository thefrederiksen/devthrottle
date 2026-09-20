# Question two: how long does an interrupted session need to write a handover?

Measured on 2026-09-20 on SOREN_NORTH, on the isolated rig built from `origin/main` = `ab2770c4a`
(Gateway, launcher and Director all `2.8.1+ab2770c4af29740d13a5483a032f1e94b873dbfa`). The mission
document guesses three minutes at section 4.5. Nobody had measured it. These are the numbers.

## The answer in one line

Three minutes is SAFE for the case measured - a standalone session, mid-turn, with no seats
reporting to it. The slowest such run took **57.9 seconds**, which is a margin of about 3.1 times.
Keep three minutes; do not shorten it. The case that could break it was not measured and is named
below.

## The runs

Nine runs were started; seven produced a number. Only the five marked "mid-turn" answer the question
the mission asked, because the mission's two-thirds interrupt lands on a session that is still
working. The rest are kept because each says something.

| Run | Agent | Stopped with | Mid-turn when stopped | Seconds to a document the drain would accept | Document bytes at that moment |
|---|---|---|---|---|---|
| 1 | Claude Code | interrupt | yes | **43.8** | 5945 |
| 2 | Claude Code | interrupt | yes | **23.9** | 3131 |
| 3 | Claude Code | interrupt | yes | **28.5** | 4632 |
| 4 | Claude Code | interrupt | yes | **57.9** | 5884 |
| 9 | Claude Code | interrupt | yes | **22.5** | 3609 |
| 5 | Claude Code | interrupt | no - work already finished | 13.5 | 654 |
| 6 | Claude Code | interrupt | no - work already finished | 22.5 | 2067 |
| 8 | Pi | escape | no - work already finished | 6.8 | 2437 |
| 7 | Pi | interrupt | yes | no number - the interrupt was REFUSED (see below) | - |

**The five mid-turn runs, which are the answer:**

- fastest 22.5 s
- slowest 57.9 s
- median 28.5 s
- mean 35.3 s
- spread 35.4 s (22.5 to 57.9)

Every one of the five was under a minute. None came close to three minutes.

## The conditions, so the numbers can be read honestly

- **The clock.** Started the instant `POST /sessions/{sid}/interrupt` was sent to the rig Gateway.
  Stopped when a file existed at the exact path `DrainPaths.HandoverFor` computes AND was over
  `DirectorDrain.MinimumHandoverBytes` (500), which is the condition the drain itself applies.
  Polled every 250 ms.
- **The words.** The handover request was the product's own, produced by calling
  `DrainMessages.HandOverNow(handoverPath, TimeSpan.FromMinutes(3.3))` from a scratch program that
  references `CcDirector.ControlApi`, so the session was sent exactly what the Director sends at the
  two-thirds point, down to "about 3 minutes are left". It was sent 1.2 s after the interrupt.
- **The path.** Also the product's own: `DrainPaths.HandoverFor(directory, sessionId, name)`, which
  produced e.g. `...\rig-timing-run-4\d89abbab - handover timing run 4.md`. The session was told that
  exact path and wrote to it; no run failed on the path.
- **The work.** Real work, not a sleep: read ten real `.cs` files (the `Drain` folder of this repo,
  copied into the session's working directory) one at a time and append a paragraph of summary for
  each to a notes file. Four of the five mid-turn runs had already produced between 6.5 KB and 18 KB
  of that notes file when they were interrupted; run 1 was interrupted while still reading, before
  it had written anything.
- **The agent.** Claude Code 2.1.278 on `claude-sonnet-5`. Getting there needed a workaround: the
  interactive path ignores the `--model` argument, and the account's configured default model is at
  its monthly spend limit, so a plain new session cannot run at all on this machine today. Each
  timing session was therefore created with a `resumeSessionId` pointing at a one-line transcript
  seeded by `claude --print --model sonnet`, which carries the model into the reopened session. The
  companion document `reopen-test.md` records that finding in full.
- **The shape of the session.** Standalone. No subordinates, no mission, no workflow seat, a small
  context, and one turn of work behind it.

## What was not measured, and it is the case most likely to break three minutes

**A lead with seats reporting to it.** `DrainMessages.SmartShutdown` tells such a session: "collect
them FIRST and wait for them before you finish yours." That is a serial dependency - the lead cannot
finish until its subordinates have written theirs - and none of these runs had one. A lead whose
three subordinates each take up to a minute, plus its own document, is the shape that could exceed
three minutes, and it is the shape a real fleet has.

Also not measured: a session with a large context (these were near-empty), a session mid-way through
an edit it has not saved, a session that was already waiting on the owner when the interrupt
arrived, and any agent other than Claude Code and Pi.

## Two findings that matter more than the numbers

### 1. The interrupt is REFUSED for Pi, by design

Run 7 sent `POST /sessions/{sid}/interrupt` to a Pi session that was genuinely working. The rig
Director answered `Conflict`, three times:

```
[GatewayStreamClient] Command received: verb=interrupt, sid=67a3156a-7163-4522-94e9-f58ccfcc2799
[Session] InterruptAsync: session=67a3156a-7163-4522-94e9-f58ccfcc2799, driver=Pi
[SessionCommandExecutor] DispatchAsync result: verb=interrupt, ..., status=Conflict
```

This is deliberate and documented in `PiDriver`:

```
public Task InterruptAsync(ISessionBackend backend) =>
    throw new NotSupportedException(
        "[PiDriver] pi has no safe hard interrupt: Ctrl+C clears the editor and " +
        "Ctrl+C twice QUITS pi. Use CancelAsync (Esc).");
```

`PiDriver` does not declare the `Interrupt` capability at all, and `SessionCommandExecutor` turns the
`NotSupportedException` into a typed `Conflict`.

**What this means for the feature.** The smart shutdown interrupts whatever is still mid-turn at two
thirds of the time allowed. On a Pi session that interrupt does nothing but return `Conflict`, so the
session is never told to hand over and is simply shut down when time runs out, with no document. The
escape verb is the path that works: run 8 used `POST /sessions/{sid}/escape` on a Pi session and the
document arrived in 6.8 seconds. **The two-thirds step must choose its verb from the driver's
declared capabilities, not send `interrupt` to everything.** Codex and the other drivers were not
checked for the same gap.

### 2. The 500-byte floor let a half-written document through, once in eight

`DirectorDrain.MinimumHandoverBytes` exists so "a seat that writes its file in pieces would otherwise
be read half-done and closed on it". In run 5 the document crossed 500 bytes at 13.5 seconds, at
**654 bytes**, and went on growing to **2808 bytes** - so the drain's acceptance point was reached
when 23 per cent of the document existed. The other seven documents were written in one write and
their accepted size was their final size.

A drain that read run 5 at its acceptance moment would have had the heading and the start of the
exact next action, and none of what is proven, what is believed, what is uncommitted, or the closing
`drain-report` block - so it would have recorded that seat as having no declared state. Raising the
floor is one answer; requiring the closing block to be present and parseable is a better one, because
that is the thing the drain actually needs and it cannot be reached by a document still being
written.

## How the interrupted sessions behaved, which the mission also lists as unverified

- **They obeyed.** All five mid-turn Claude Code sessions stopped the work, did not go back to it,
  and wrote the document. None asked a question instead of writing.
- **They put the exact next action first, as asked.** Runs 2, 3 and 4 open with a heading of that
  name and a specific, resumable instruction - run 2, for instance, names the two remaining files and
  the order to read them in. The instruction in `HandOverNow` to write that first is being followed.
- **Every document carried a parseable closing block.** All eight had `state: drained`. Restore
  answers split sensibly: `restore: yes` where work remained (runs 1 and 4), `restore: no` where the
  session judged the work finished (runs 2, 3, 5, 6, 8).
- **A document can contain the words `drain-report` in ordinary prose.** Run 1's session had been
  summarising `DirectorDrain.cs`, so its handover describes the drain-report block in a sentence
  before carrying the real one. `DrainReportBlock` is safe here - it matches only
  `<!-- drain-report` and takes the LAST match - but anything else that greps for the phrase is not.
- **A session interrupted after its work had finished writes a thin document.** Runs 5, 6 and 8 all
  open with some form of "Nothing. The task is complete." That is honest and correct, and it is worth
  knowing that the restart will collect a number of documents that say nothing except that there is
  nothing to say.

## Recommendation

**Leave the three minutes as it is.** The measured worst case for the shape the feature will most
often meet is 57.9 seconds, and the guess has a margin of roughly three times over it. Shortening it
buys a handful of seconds off a restart and risks the thing this whole feature exists to prevent.

If a number has to be defended, defend it as: three minutes is about three times the slowest
standalone handover measured, and the untested case (a lead waiting on its subordinates) is serial,
so it wants the margin more than a standalone does.

**The two-thirds point itself is not mine to change and has not been changed.** The numbers say it is
not the pressure point; the interrupt verb is.

## Raw results

`scratchpad/rig/timing-results.jsonl`, one line per run, is not committed because it is a scratch
file on a temporary path. Its contents are the table above plus the session id, the handover path,
and the interrupt timestamp for each run. The handover documents themselves were written to the rig
root `%LOCALAPPDATA%\cc-director-restart-qa-rig\vault\handovers\director-restart\rig-timing-run-N\`,
which goes when the rig root is reset.
