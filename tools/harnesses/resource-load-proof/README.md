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

**Every time limit is judged on the recorded sample's own time.** The Director writes its recording once a
minute, so the newest line the diagnose command can see may be up to a minute old. A step counts a sample only if
it was TAKEN after the change (the load starting, or stopping), and the 70 seconds are measured from the change to
that sample's `takenAtUtc` - never to the moment the harness happened to read it. The harness polls for up to 145
seconds (the limit, plus the one-minute write interval, plus three polls) to see that sample. So a stopped Director
whose last line was red can never pass "red", and one whose last line was normal can never pass "clear". For the
memory step the clock starts once the machine is held short of memory, not while it is being filled.

## Where each figure comes from

**Ours** comes from the Director, through its own diagnose command, `cc-devthrottle machine diagnose --json`
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
| `--diagnose-command "<cmd>"` | Prints the Director's diagnose JSON for steps 1 to 3. Default `cc-devthrottle machine diagnose --json`. |
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
