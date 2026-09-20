# The Windows backstop is not running, so the gap it was supposed to close is still open

Recorded by the Delivery Lead on 20 September 2026, after going and reading a continuous integration
result rather than assuming it.

## What I promised, and why it is void

Twice in this mission a seat proved a path-comparison fix on macOS and could not prove it on Windows -
no Windows machine was reachable, and SORENLAPTOP has been off the tunnel all night. Both times I said
the same thing, in a message and in a merged pull request:

> Continuous integration runs Windows and Linux, and the rule here is merge without waiting and then go
> and READ that result. I will do that and record what it says.

**I have now read it. It says the .NET job did not run to completion.**

## What was measured

| Run | Result |
|---|---|
| `Build & Test (.NET)` on the fifth-sighting merge | **failure** |
| `Build & Test (.NET)` on the shared-reader merge | **failure** |
| Every other continuous integration run on `main` in the same window, across four different missions | **failure** |

The failures go back at least to 05:58 and cover Reclaim the Disk, Smart Director Restart phases 1 and
2, a Gateway daily-report change, and both of this mission's merges. **It is not this mission's defect -
it predates every commit I merged tonight.**

**It is not a test failure.** The job log contains no `[FAIL]` line and no failing assertion. It ends:

    Done Building Project "cc-director.sln" (VSTest target(s)) -- FAILED.
    Build FAILED.
        0 Warning(s)
        0 Error(s)
    Time Elapsed 01:36:01.93

One hour thirty-six minutes, then a build failure with zero errors and zero warnings. That is a hang or
a crash in the test host, not a red test. The web job and all six Python jobs passed in the same runs.

## Why it matters to this mission specifically

This mission fixed **five** instances of one defect family: code deciding case, separators or a folder
name from the machine *running* it rather than from the path's own shape. The Gateway is a Linux
container holding paths pushed up by Windows and macOS Directors, so this family is about cross-platform
behaviour by its nature.

Every one of those fixes was proven on macOS. The Windows halves - `RuleCandidateFilter`'s case
sensitivity, and the drive-root case in `SmartShutdownSessionReader` - were argued from .NET's own
documented rules and from the helper's tests, and every seat said so plainly rather than implying
execution it did not have. That was honest, and the mitigation I attached to it was that continuous
integration would cover what we could not.

**That mitigation does not exist.** The Windows .NET job has not completed successfully in hours. So the
Windows half of this mission's path work is not merely unwitnessed by us - it is uncovered by anything.

## Also uncovered, for the same reason

- The PostgreSQL-backed proofs behind phase 2's migration. There is no Docker on the machine this was
  built on, and the release gate's `-Parked` run is the only thing that would exercise them.
- Whatever else the .NET job is the only runner for.

## What this does not say

It does not say the fixes are wrong. Each is a one-expression change to use a helper that decides from
the path's own shape, each carries a test that fails when it is reverted on the platform available, and
each was reviewed by a different agent family. It says the second line of defence is down, and that a
green `main` currently means less than it looks.

## Not fixed here

Repository infrastructure affecting every mission, not this one's to repair, and not something to fix
blind at four in the morning underneath four other missions' live seats. It goes to the owner by name,
with the recommendation that it be looked at before anything is released - because the release gate is
the one place the missing coverage cannot be fixed forward.
