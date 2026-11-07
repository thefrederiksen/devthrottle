# resource-load-proof

The live load harness for the Resource Monitor (issue #2811, mission section 8). It turns "the tests pass"
into "the meter agrees with the machine": it puts a real load on the real machine and checks what the
Director's meter says against the operating system's own monitor. Run it by hand on Windows, Linux and macOS.
It is never shipped.

```
dotnet run --project tools/harnesses/resource-load-proof -- --idle --cpu --memory --report out/
```

Every step writes its reading to `out/<step>.json` and a screenshot of the whole screen beside it, and the run
ends with `out/summary.json` and `out/summary.md`. The exit code is 0 only when every step that ran passed.

## The steps

| Flag | Step | Passes when |
|---|---|---|
| `--idle` | 1. The meter's CPU and MEM beside the operating system's own figures, at idle | Both within 5 percentage points |
| `--cpu` | 2. Pins every processor with the text-delivery rig's `CpuLoad`, then stops | Red within 70 s, the diagnosis names CPU, and back to normal within 70 s of stopping |
| `--memory` | 3. Holds memory down to the meter's Tight line, then frees it | Amber within 70 s, the diagnosis names memory, and back to normal within 70 s of freeing |
| `--gateway` | 4. Blocks the Gateway for 10 minutes, unblocks it, waits six minutes for the catch-up | Every five-minute window in the span has a stored row |
| `--cost` | 5. Watches the process running the sampler for 10 minutes | Under 0.5 per cent of one core |

The diagnosis check (mission item 6) runs inside steps 2 and 3, on the reading taken under load.

**Every time limit is measured on the recording's own monotonic timeline.** Each recorded line carries
`recordingSeconds`, which the recorder advances with the MONOTONIC clock inside a Director run - its own
documentation says the last hour is measured on it for exactly this reason. A step counts the difference
between the first sample taken at or after the change and the first one that meets the condition. No
wall-clock subtraction, and nothing that depends on when the harness happened to look.

*Why not the sample stamps.* The difference between the moment the load began and a sample's `takenAtUtc`
is the difference of two readings of the WALL clock, and `ResourceSample.TakenAtUtc` says on its own type
that comparing these stamps is the defect that interface was changed to remove. A ten-second
synchronisation step turns a real 68-second warning into 58 or 78. This harness decides whether the
product met a success criterion, so it is the last place that may measure with a clock that can move.

*Why not the time the harness took to see it.* That was tried and it cannot pass a correct product. The
sampler reads every five seconds and the processor turns Critical only once a whole sustained window is
over the limit, so the product's own red arrives 65 to 70 seconds after the load **by construction** -
the entire budget. Adding the recorder's one-minute write interval and the poll interval on top left a
correct product passing a few runs in a hundred. An instrument that fails a product which meets the
criterion is worse than one that measures with the wrong clock, because its failures look like the
product's.

*Order comes from the recording too, and so does membership once the baseline is known.* The baseline is
the earliest line on the recording's timeline whose **raw stamp or corrected stamp** is at or after the
change - corrected meaning its own step mark undone; from there on, every line at or after it on that
timeline is a member and the stamps are not consulted again. Ordering or selecting by the stamp alone would
hand a clock step the power to choose which two lines get subtracted, which is the same defect one level
up: the measurement monotonic, the choice of what to measure not.

**Review found that three times, and each stamp alone is wrong in the case the other gets right.** A
backward step bigger than the gap between the change and the next sample hid that sample AND the mark that
would have declared it, so a 75-second warning measured as 65 and passed clean; the corrected stamp
rescues it. A forward step just before the change made the change's own stamp eight seconds later than the
clock the correction reads, so the line that should have been the baseline was thrown out and the next one
took its place: 75 seconds measured as 70, inside the limit, no caveat; the raw stamp rescues that one.
Taking either is right whenever one of them is, because the line cannot say which clock stamped the change.
Dropping either half fails exactly one test.

*Where that rule is still imprecise, and in which direction.* A **backward** step shortly before the change
pulls the change's own stamp back, so unmarked lines taken before the step qualify by their raw stamps and
the earliest of them becomes the baseline. The measurement then starts **up to the size of the step** too
early - a sixty-second step with the change inside the following sixty seconds starts it up to sixty seconds
early. It OVERSTATES, which is the safe direction: it can turn a pass into a failure, never a failure into a
pass. Such a window reports the clock step, because the marked line sits inside the span - not because it is
the baseline, which it is not. The scan misses it in one geometry only: the qualifying sample comes before
the marked line, which needs the condition to have already held before the change. That cannot happen on the
processor wait or either clear wait; it can on the memory amber wait, where the meter may be amber while the
fill is still finishing, and there the state reported is true while the figure overstates. A step under the
recorder's roughly two-second tolerance carries no mark, so both sides are read on one clock and the only
error is the same overstatement bounded by that tolerance. Two steps inside one five-second sample interval
are not handled.

*What the measurement costs.* It overstates by at most one five-second sample interval, because the
baseline is the first sample at or after the change rather than the change itself. That is the safe
direction.

*When the run is not trustworthy, the run says so.* The recorder marks the exact line where the wall clock
moved by more than about two seconds (`clockStepSeconds`); a step anywhere inside the span is reported
beside the figure, which stands, because it is monotonic. A Director **restart** inside the span is
different: there the recorder bridges the gap with the wall clock, so the span is not a monotonic
measurement at all and the step never counts as met.

**Which sample is read is a separate question from which clock measures the wait,** and the stamps still
answer it. The Director writes its recording once a minute, so the newest line the diagnose command can
see may be up to a minute old. A step counts a sample only if it was TAKEN after the change - a comparison
for order against one reference, which is selection, not measurement. The harness polls for up to 145
seconds (the limit, plus the one-minute write interval, plus three polls) to SEE that sample; that budget
bounds the waiting and never the verdict. Because the recorder appends a whole minute of lines at once,
each poll reads every sample of the last five minutes and judges on the EARLIEST one after the change that
meets the condition, not the newest. So a stopped Director whose last line was red can never pass "red",
one whose last line was normal can never pass "clear", and a meter that turned red at 65 seconds is not
reported as red at 110. The idle step fails outright when the newest sample is more than 70 seconds old: a
Director that is not recording has nothing to compare. For the memory step the clock starts once the
machine is held short of memory, not while it is being filled.

## Where each figure comes from

**Ours** comes from the Director, through its own diagnose command, `cc-devthrottle machine diagnose --json --samples --minutes 5`
(change it with `--diagnose-command`). That command reports the newest sample the Director recorded, with the
verdict and the diagnosis it recorded beside it, so the harness reads exactly what the meter was handed. It
reads these fields and stops with the name of any that is missing:

```json
{ "newest": { "cpu":    { "couldRead": true, "totalBusyPercent": 28.0 },
              "memory": { "couldRead": true, "totalBytes": 17179869184, "availableBytes": 7666225480 },
              "verdict":   { "resources": [ { "kind": "Cpu", "level": "Normal" }, { "kind": "Memory", "level": "Normal" } ] },
              "diagnosis": { "couldDiagnose": true, "isSlow": false, "causes": [] } } }
```

MEM is total minus available over total, the same figure the bar shows. A figure on a reading that could not be
taken is refused as a made-up number, and a recording with no sample yet is an error, not an empty meter. A unit
test parses every line of the recorder's committed recording, so the harness cannot drift from the real format.
The recording is written by the sampler, so the meter steps need a Director build that has it running.

**Theirs** is the operating system's own monitor:

- **Linux** - scripted: `free -b` (total minus available) for memory, `vmstat 5 2` (100 minus idle, from the
  second reading) for the processor.
- **Windows and macOS** - Task Manager and Activity Monitor cannot be read by a script. The harness PRINTS
  OURS AND ASKS YOU TO TYPE THEIRS, and the report says the figure was typed by a person reading that monitor.
  For a run with nobody at the keyboard, read them yourself and pass `--non-interactive --os-cpu <n> --os-mem <n>`.
  On Windows read Performance, Memory: In use divided by the total; on macOS read Memory Used divided by
  Physical Memory.

## Safety

- **The memory step never takes the machine to Critical.** It reads memory through Core's real
  `MachineMemoryProbe` and aims halfway between the meter's Tight and Critical lines. It allocates at most half a
  gigabyte between two fresh readings, never more than the room left above that target, and never more in total
  than the room the first reading showed. A batch that does not show up in the next reading as at least half its
  size releases everything and stops, because the reading can no longer be trusted to stop the load. If the
  machine reaches Critical anyway, the step releases everything and fails.
- **It refuses to start the memory step on a machine that is already short of memory**, because the warning
  could not then be shown raising.
- The processor load runs at normal priority and stops the moment the step does, pass or fail.

## Options

| Option | Meaning |
|---|---|
| `--report <folder>` | Required. Where the readings and screenshots go. |
| `--diagnose-command "<cmd>"` | Prints the Director's diagnose JSON for steps 1 to 3. Default `cc-devthrottle machine diagnose --json --samples --minutes 5`. |
| `--screenshot-command "<cmd>"` | With `{path}`. The default is the platform's own: a .NET screen copy on Windows, `screencapture -x` on macOS, `gnome-screenshot -f` on Linux. |
| `--gateway-block`, `--gateway-unblock` | Commands that cut the Director off from its Gateway and restore it. |
| `--gateway-rows "<cmd>"` | Prints a JSON array of the window start times stored for this machine, each with its offset ("Z"); a time with none is refused. |
| `--gateway-minutes <n>` | How long to block. Default 10. |
| `--cost-pid <pid>` | The process running the sampler. |
| `--cost-minutes <n>` | How long to measure. Default 10. |

An argument the harness does not know stops the run with exit code 2, so a misspelt step is never skipped
quietly while the run reports PASS.

## What is not proved by this harness alone

- **Step 5 measures the whole process.** Pointed at a Director it is an upper bound on the sampler's own cost:
  under the limit there proves the sampler is under it, over the limit does not prove the sampler is over.
- **Step 4 depends on phase 3** for the block and unblock commands and the rows query; the shapes above are
  what the harness expects of them.
- The 5-point agreement on Windows and macOS rests on a figure a person typed. The report says so on every
  such line.

The judgements themselves - agreement, cost, gaps, the diagnosis match and the parsing of `free`, `vmstat` and
the diagnose JSON - are pure functions in `ProofChecks.cs`, linked into `CcDirector.Core.UnitTests` so the
local gate tests them.
