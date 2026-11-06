# resource-load-proof

The live load harness for the Resource Monitor (issue #2811, mission section 8). It turns "the tests pass"
into "the meter agrees with the machine": it puts a real load on the real machine and checks what the
Director's meter says against the operating system's own monitor. Run it by hand on Windows, Linux and macOS.
It is never shipped.

```
dotnet run --project tools/harnesses/resource-load-proof -- --idle --cpu --memory --report out/ \
    --diagnose-command "<the Director's diagnose command> --json"
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

## Where each figure comes from

**Ours** comes from the Director, through its diagnose command (`--diagnose-command`). The harness needs
exactly these fields and stops with the name of any that is missing:

```json
{ "meter": { "cpu":    { "couldRead": true, "percent": 28.0, "level": "Normal" },
             "memory": { "couldRead": true, "percent": 55.0, "level": "Normal" } },
  "diagnosis": { "couldDiagnose": true, "isSlow": false, "cause": "None" } }
```

`level` is one of `Unknown`, `Normal`, `Tight`, `Critical`. An unreadable bar has `couldRead` false and
`percent` null; a figure on an unreadable bar is refused as a made-up number. `cause` is `None`, or one or more
of `Cpu`, `Memory`, `Disk` separated by commas.

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
  `MachineMemoryProbe` and stops halfway between the meter's Tight and Critical lines, sizing each batch from a
  fresh reading so a cached one cannot make it overshoot. If the machine reaches Critical anyway, the step
  releases everything and fails.
- **It refuses to start the memory step on a machine that is already short of memory**, because the warning
  could not then be shown raising.
- The processor load runs at normal priority and stops the moment the step does, pass or fail.

## Options

| Option | Meaning |
|---|---|
| `--report <folder>` | Required. Where the readings and screenshots go. |
| `--diagnose-command "<cmd>"` | Prints the Director's diagnose JSON. Needed by steps 1 to 3. |
| `--screenshot-command "<cmd>"` | With `{path}`. The default is the platform's own: a .NET screen copy on Windows, `screencapture -x` on macOS, `gnome-screenshot -f` on Linux. |
| `--gateway-block`, `--gateway-unblock` | Commands that cut the Director off from its Gateway and restore it. |
| `--gateway-rows "<cmd>"` | Prints a JSON array of the UTC window start times stored for this machine. |
| `--gateway-minutes <n>` | How long to block. Default 10. |
| `--cost-pid <pid>` | The process running the sampler. |
| `--cost-minutes <n>` | How long to measure. Default 10. |

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
