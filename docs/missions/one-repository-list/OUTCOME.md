# One repository list, held on the Gateway - the outcome

Closed 20 September 2026. **Eighteen pull requests, all merged to `main`.** Nothing is deployed; a
release is the owner's decision and was never part of this mission.

## The goal, against what was delivered

Section 3 asked that all three New Session screens, pointed at the same machine, list the same
repositories in the same order, most recently used first, and that the order be correct because it
counts every session start however it was started.

| Goal | State |
|---|---|
| 1. Starting a session from the Cockpit or the phone moves that repository to the top of the Director's list | **Met.** One recorder on session creation replaced a single write on one button. |
| 2. A never-opened repository under a registered root appears on all three screens, below everything used | **Met.** The catalogue holds both halves; the Gateway serves never-opened beneath, and the Cockpit says "Never opened" rather than leaving a blank cell. |
| 3. The Cockpit's New Session tab is the Director's layout, plus a machine row | **Met, with two corrections.** It is a modal, not a tab. The visual style guide does not govern the browser shells, so parity is structure and wording, never colour. |
| 4. The phone's flow is unchanged in shape and shows the corrected list | **Met.** Same step, same search box, same rows; three client rulings deleted. |
| 5. The Director still lists repositories when the Gateway cannot be reached, and says so on screen | **Met, and photographed.** |

## What it cost beyond the six phases

Five pieces of work the mission was not chartered for, each of which had to land for the six phases to
be honest:

- the web suites made independent of the Node version (they were red on Node 26, green on the pinned 22);
- fourteen macOS .NET failures, which surfaced **four real product defects**;
- a shared client reader that re-sorted the list the Gateway had just ordered;
- a red on `main` from another mission, which was the **fifth sighting** of this mission's own defect
  family;
- the catalogue's completeness and its forgetting rule, without which phase 6 would have regressed the
  one screen that already worked.

## The defect family this mission kept meeting

Six instances, all the same shape: **code deciding case, separators or a folder name from the machine
running it rather than from the path's own shape.** The Gateway is a Linux container holding paths pushed
up by Windows and macOS Directors, so it is never the machine the path describes.

The fifth was written **after** this mission had built and merged the helper that fixes it. The helper
existing is not enough; the habit of reaching for `Path.GetFileName` outlives it. **At least seventeen
more lines across eleven files are catalogued and deliberately deferred** in
`proofs/green-check-dotnet/`.

## What is proven, and what is not

Proven: the mission check green on every phase, run by the Delivery Lead independently of every seat;
four screenshotted QA reports; every phase watched failing with its symptoms predicted before the revert.

Not proven, and stated rather than implied:

- **Windows.** Five path fixes are proven on macOS and argued from documented behaviour on Windows. The
  continuous integration job that would cover it **has been failing on every commit for hours**, across
  four missions - a hang, not a test failure. See `proofs/the-windows-backstop-is-not-running.md`.
- **PostgreSQL.** No Docker on the machine this was built on, so the migration proofs behind phase 2 and
  the forgetting rule did not run. The release gate's `-Parked -Configuration Release` is the only thing
  that will exercise them.
- **The deployed Gateway.** The hosted Gateway predates phase 2, so it holds no discovered half yet.
  None of this is proven against it until it is deployed.

## Handed up, not fixed

- `Gateway.Tests` is red on `main` on macOS - nineteen failures in six unrelated families, in a parked
  suite nothing in the working loop runs.
- Continuous integration's .NET job is failing for every mission.
- **An aborted test run reports `Passed!`** - it printed success over a quarter of a suite and nearly
  carried a merge.
- **Five tests measure how busy the machine is**, not the product - a throughput assertion and seven
  wall-clock timeouts, reproduced deliberately rather than re-run until green.
- Removing a repository from the one catalogue is something the product **cannot do yet**.

## The lesson worth keeping

**A revert proves the guard catches that defect; it does not prove it catches the class its name claims.**
Three seats validated one guard by reinstating the exact line that had been deleted, all honestly, and
all missed the same defect in the other direction. This mission leaned on revert-proofs throughout, and
they caught real defects - including a registry race that could silently wipe the user's repository list.
The blind spot is real too, and after it was found seats began attacking their own guards with wrong
rules nobody had removed.
