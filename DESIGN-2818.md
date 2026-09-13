# Design - issue #2818: the Director survives memory pressure

Branch `fix/director-survives-memory-pressure-2818`, cut from `origin/main` at `678b8ec4`.

## The problem in one paragraph

Three mechanisms in the Director are keyed on fixed wall clock deadlines and all three assume the
process gets to run when it wants to. On a machine that is paging it does not, so the tunnel drops
(the Gateway stops hearing keep-alive pings), the pushed roster goes stale (the ten second re-push
misses its window against a twenty second staleness cut), and typed prompts are destroyed (a four
second composer echo deadline expires, the Director decides the terminal interface is broken, sends
Escape over the owner's text and throws the prompt away). Nothing in `src` reads the machine's
memory, so not one of those deadlines can know the machine was the reason it was missed.

## Shape of the change

Four pieces, smallest first. Nothing changes any timeout on a healthy machine: every adjustment is
gated on a measured reading that says the machine is short of memory.

### 1. A machine memory probe - new, `src/CcDirector.Core/Machine/`

Deliberately NOT placed in the existing `CcDirector.Core.Memory` namespace, which is about terminal
buffers (`CircularTerminalBuffer`) and would be muddied by a second meaning of "memory".

```
MachineMemoryReading   record: TotalBytes, AvailableBytes, TakenAtUtc, CouldRead, UnreadableReason
MemoryPressureLevel    enum:   Unknown, Normal, Tight, Critical
IMachineMemoryProbe    interface: MachineMemoryReading Read()
MachineMemoryProbe     the platform implementation
MemoryPressure         PURE fold: MachineMemoryReading -> MemoryPressureLevel
```

- Windows reads `GlobalMemoryStatusEx`; Linux reads `/proc/meminfo` (`MemTotal`, `MemAvailable`);
  macOS reads `sysctlbyname("hw.memsize")` with `host_statistics64` for the free and inactive pages.
  All three are direct calls - no subprocess is spawned on a timer.
- A platform or call that cannot be read returns `CouldRead = false` with a reason, and the reason is
  logged once rather than every few seconds. **It never returns a zero or an invented value**, which
  is the failure mode issue #2811 also calls out.
- `MemoryPressure` is a pure function of the reading, so it is unit testable with no clock. Thresholds
  are on available memory as a fraction of total, with an absolute floor so a large machine with a
  small fraction free is not called Critical while it still has many gigabytes:
  Tight at under 15 percent available or under 2 gigabytes; Critical at under 7 percent or under 800
  megabytes. **These two numbers are the part of this design I would most like challenged.**
- Readings are cached for a short interval so callers on hot paths do not re-read per keystroke.

This is the same reading issue #2811 wants for the toolbar meter. It is built here so that issue
consumes this type rather than writing a second one.

### 2. Keep the tunnel alive when the thread pool is congested

`ThreadPool.SetMinThreads` appears nowhere in `src`, so the floor is the processor count and the pool
injects only about one or two extra threads per second beyond it. The keep-alive ping that holds the
tunnel open is a timer callback on that pool, behind eighteen sessions of console reads.

Raise the worker and completion port minimums once at Director startup (`CcDirector.Avalonia/Program.cs`,
beside the existing logging and crash handler setup), log the old and new values, and never lower a
floor somebody else already raised.

**The silence tolerance itself does not move**, for the reasons already written on
`DirectorStreamLimits.SilenceTolerance`: shortening it hangs up on a merely busy peer, lengthening it
delays noticing a genuinely dead one. Neither is the fix; letting the ping actually fire is.

### 3. Never destroy a prompt because the machine is slow

In `TerminalSubmit.EchoVerifiedInlineSubmitAsync`:

- The echo deadline becomes a function of measured pressure rather than a constant four seconds:
  Normal keeps four seconds exactly as today, Tight and Critical extend it. A caller that passes an
  explicit `echoTimeout` still wins, so tests and drivers are unaffected.
- **Escape is only sent when there is positive evidence the composer does not hold the text.** Today a
  timeout alone is enough to clear it, which is how a slow repaint deletes the owner's sentence. When
  the machine is under pressure and the rendered screen cannot prove the text is absent, the attempt
  waits again instead of clearing.
- The exception message and the delivery ledger reason name memory when the probe says the machine was
  short, instead of the present "the composer never echoed the typed text", which sends the reader
  looking for a broken terminal interface.

This is the piece with the most user risk in both directions, so it is the piece I most want read
carefully: being too reluctant to clear the composer risks appending to stale text, and being too eager
is the bug being fixed.

### 4. Say which it was

A re-push tick that is skipped or slow, and a delivery that fails, both record the pressure level at the
time. `RePushTick` and `ReseedAsync` already log; they gain the reading, not new machinery.

## Explicitly not in this change

- The toolbar meter - issue #2811, which consumes the probe from piece 1.
- Back-pressure refusing a new session on a machine already short of memory.
- A cheaper reseed (delta or chunked push). Worth doing and the reason the failure amplifies itself,
  but it changes the push protocol and does not belong in a fix.

## Testing

New unit tests go in `CcDirector.Core.UnitTests`, which is in the DEFAULT local gate. That project
forbids wall clock dependence, which is why `MemoryPressure` and the deadline selection are pure
functions over an injected reading rather than anything that sleeps. Tests cover: each threshold
boundary, the cannot-read reading folding to `Unknown` and never to `Normal`, `Unknown` and `Normal`
both leaving the four second deadline untouched, and the escalation for Tight and Critical.

Behaviour tests that need a real backend go in `CcDirector.Core.Tests` beside the existing
`TerminalSubmitEchoMissCountTests`, and that suite is parked, so it is run explicitly with `-Parked`
rather than assumed covered by the default gate.

Gate before merge: `.\scripts\test-local.ps1` green, then this review.

---

# Revision after the design review (REVIEW-2818-design.md, verdict CHANGES NEEDED)

The review found four defects and raised three more on a follow-up read. All seven changed the design;
this section is what was actually built, and where it differs from the draft above, THIS section wins.

## 1. The threshold predicate was wrong, and the fraction is gone entirely

The draft said "under 15 percent OR under 2 gigabytes", with the absolute floor described as the thing
that stops a large machine being called Critical while gigabytes remain. A branch of an OR can only
make a verdict MORE severe, so it could never do that: a 128 gigabyte machine with 8 gigabytes free
- 6.25 percent - came out Critical.

Fixed by REMOVING the fraction from the verdict rather than rearranging it. What decides whether the
next allocation pages is how many bytes are actually available, not what share of the machine they
are. Tight under 2 gigabytes, Critical under 800 megabytes, on any size of machine. The review's
counterexample is a permanent test.

## 2. The prompt-recovery contract, in full

- **Evidence is three-valued** (`ComposerEvidence`): Present, Absent, Unknown. The old boolean returned
  false both for "the screen does not show it" and "there is no screen", and the destructive Escape
  fired on either.
- **An unreadable screen is Unknown, never Absent.** No rows, blank rows, or a snapshot that throws all
  mean the instrument did not answer.
- **Two samples, 120 milliseconds apart**, because a single capture can be taken mid-repaint.
- **Under measured memory pressure, Absent is refused outright.** The review's sharpest point: two
  samples 120 milliseconds apart prove nothing when the renderer itself is stalled by paging, since
  both can be the same stale frame. On a starved machine the destructive verdict is simply not
  available.
- **The extra watch is finite** - one further deadline - and it OBSERVES ONLY; it never retypes.
- **Giving up without clearing records the retained text**, and the next send to that terminal looks
  for exactly that text before typing: Present clears it, Absent leaves it alone, Unknown clears. That
  last branch is the one deliberate act without proof, and the reason is written at
  `ComposerRetention.ShouldClearBeforeTyping`: one prompt the owner was already told did not arrive is
  a smaller harm than two prompts welded into one instruction they never gave (pull request #1513).

## 3. THE NEW BEHAVIOUR IS SCOPED TO A MEASURABLY STARVED MACHINE

This is the largest correction and it came from the tests, not the review.

An earlier build took the preserve-and-watch path on ANY unknown evidence. Most driver call sites -
`ClaudeDriver`, `CodexDriver`, both backends - pass no screen snapshot at all, so that silently removed
the long-proven clear-and-retype recovery from them on HEALTHY machines too. Three long-standing tests
caught it.

On a healthy machine, and on one whose memory cannot be read, the submit path now behaves EXACTLY as it
did before this change. Only a machine measured to be short of memory takes the new path. That is a far
smaller and more defensible change than the draft described.

## 4. The thread-pool floor is sized, not picked

Sized from the measurement the review supplied: `ProcessHost.StartDrainLoop` and
`ProcessHost.StartExitMonitor` each block a pool worker for the session's whole life, on Windows and on
Unix, so eighteen sessions pin 36 workers. The floor is `processorCount + 2 x 32 sessions + 16 spare`.
The completion-port floor is raised separately and by much less, because no session blocks one.

A refusal from the runtime is reported rather than swallowed. And the limit is stated plainly: this
addresses WORKER STARVATION only - if the machine is paging hard enough to evict the Director's own
pages, no pool setting gets a callback onto a processor in time. It narrows the window; it does not
close it. Heartbeat timing under real paging was NOT measured, and nothing here claims it was.

## 5. Timer lateness is measured from the schedule

- Measured from the previous tick's SCHEDULE, before any other path can return, so a callback that runs
  late and then pushes quickly is no longer invisible.
- **Seeded when the timer is armed**, so a delayed FIRST tick is measured too.
- Held as a long and exchanged atomically, because `System.Threading.Timer` callbacks CAN overlap when
  one runs past its period. An earlier comment asserted the runtime prevented that; it does not.
- The line reports the lateness separately from the memory reading, and says the reading was taken
  AFTER the delay, so nobody reads it as evidence about conditions during the stall.

## 6. Tests must pin the machine they assume

Because the submit path now reads the machine, any test that does not pin a reading depends on how much
memory the build agent happens to have free. That is not hypothetical: this was written on a laptop with
2.53 gigabytes available of 15.7 - directly on the Tight threshold - and the suite's own allocations
pushed it across mid-run, failing three unrelated tests. `PinnedMachineMemory.Healthy()` /
`.Starved()` / `.Unreadable()` is the seam, and every terminal-submit test now uses it.
