# Windows: is a detached child still counted in its session's tree?

Resource Monitor mission, phase 1, Windows Developer. Measured on SOREN_NORTH (Windows 11 Enterprise
10.0.26200) on 2026-10-15 at 09:19 local time.

## The answer

**No, not if attribution walks parent process ids.** When a parent exits on Windows, its child keeps
the dead parent's id. Walking down from the session's root by parent ids stops at the gap, and the
child is lost. The parent id can also be **reused** by an unrelated, younger process, which would
attribute the child to the wrong owner.

**A Job Object created by the Director per session does keep it.** A process created inside a job
stays in that job after its parent exits. `QueryInformationJobObject` lists it, and the job's own
accounting keeps the CPU time and disk input/output of processes that have already exited.

**The Director creates no Job Object today**, so the reliable method is not available yet without a
change to how sessions are started (see "What the attribution should use" below).

## Evidence

The experiment is a scratch console program using the same native calls the probe uses (Toolhelp
snapshot, `OpenProcess`, `GetProcessTimes`, `IsProcessInJob`, `CreateJobObject`,
`AssignProcessToJobObject`, `QueryInformationJobObject`). Its full output:

```
Machine SOREN_NORTH, Microsoft Windows 10.0.26200, now 2026-10-15T09:19:44.6830875-04:00
Experiment process id 62168

=== 0. Ancestry of this experiment, and whether each is in a Job Object ===
  pid  62168 parent  79504 DetachProbe.exe              created 2026-10-15T13:19:44.5776495Z inJob=True
  pid  79504 parent  76028 dotnet.exe                   created 2026-10-15T13:19:42.8445907Z inJob=False
  pid  76028 parent  11900 bash.exe                     created 2026-10-15T13:19:42.8222859Z inJob=True
  pid  11900 parent  56892 bash.exe                     created 2026-10-15T13:19:42.6237247Z inJob=True
  pid  56892 parent  16096 bash.exe                     created 2026-10-15T13:19:42.6066746Z inJob=True
  pid  16096 parent  65476 claude.exe                   created 2026-10-15T13:17:27.9605076Z inJob=True
  pid  65476 parent  61504 cc-director.exe              created 2026-10-15T00:28:03.2192079Z inJob=True

=== 1. No Job Object: cmd starts ping and exits ===
  intermediate cmd.exe pid 79556, created 2026-10-15T13:19:44.8292098Z
  cmd.exe exited with 0; still openable: True
  child PING.EXE pid 45640, parent id still says 79556, created 2026-10-15T13:19:44.8660863Z
  parent id 79556 present in snapshot now: False
  walk down by parent ids from the experiment (62168): 1 processes, includes the ping: False

=== 2. Job Object: cmd created suspended, assigned, resumed; it starts ping and exits ===
  intermediate cmd.exe pid 77480 in job: True
  after cmd exited, the job lists 2 process(es):
    pid 21024 PING.EXE parent id 77480 (parent alive: False)
    pid 78624 conhost.exe parent id 21024 (parent alive: True)
  job accounting: total processes ever 4, active 2, user CPU 46 ms, kernel CPU 15 ms, read bytes 1641, write bytes 65

=== 3. Whole-machine scan of parent ids ===
  810 processes: parent alive and older 438, parent id dead 108, parent id REUSED by a younger process 3, could not open 261
    cmd.exe pid 15676 (created 2026-10-13T18:59:09.7851008Z) names parent 26436 = ChatGPT.exe (created 2026-10-13T18:59:32.6515570Z, AFTER the child)
    cmd.exe pid 16316 (created 2026-10-13T18:59:09.7918294Z) names parent 26436 = ChatGPT.exe (created 2026-10-13T18:59:32.6515570Z, AFTER the child)
    powershell.exe pid 47716 (created 2026-10-14T18:23:55.1438052Z) names parent 47940 = mcp-server-windows-x64.exe (created 2026-10-15T10:17:50.9447880Z, AFTER the child)
```

What each part shows:

1. **Without a job, the child is lost.** `cmd.exe` started `ping` and exited. `ping`'s parent id still
   said 79556, a process that no longer existed, and a walk down by parent ids from the experiment
   found 1 process (itself), not the `ping`. "Still openable: True" for the exited `cmd.exe` is only
   the experiment's own handle keeping the kernel object alive; it was gone from the process list.
2. **With a job, the child is kept.** The same thing, but `cmd.exe` was created suspended, put in a
   new job, then resumed. After it exited, the job still listed `ping` (and its console host), and
   the job's accounting counted 4 processes ever, including the exited `cmd.exe`, with their CPU time
   and bytes read and written. That last point matters: a per-process sampler at 5 seconds never sees
   a child that lives for 200 milliseconds, but the job's accounting does.
3. **Parent id reuse is real on this machine, today.** Of 810 processes, 108 name a parent id that is
   dead, and 3 name a parent id now held by an unrelated process created AFTER the child (a `cmd.exe`
   from 13 October naming a `ChatGPT.exe` started 23 seconds later). Comparing creation times catches
   all three: a parent cannot be younger than its child. 261 processes could not be opened (other
   users' and protected processes), so their reuse could not be checked.
0. **The Job Object the Director already sits in is no use.** `cc-director.exe` and every session's
   `claude.exe` report being in a job, but the Director never creates one: searching `src` for
   `CreateJobObject`, `AssignProcessToJobObject` and `JobObject` finds nothing. The job is inherited from
   whatever launched the Director (its own parent, 61504, has already exited). It is ONE job for the
   whole Director and every session in it, and the Director holds no handle to it, so it cannot tell
   sessions apart. Nested jobs work on this machine (Windows 8 and later): the experiment's process was
   already in a job and the new job was still assigned without error.
   Also visible: `dotnet.exe` is NOT in the job while its parent `bash.exe` is. A process can break
   away from a job when the job allows it (`JOB_OBJECT_LIMIT_BREAKAWAY_OK`) and the creator asks for
   it (`CREATE_BREAKAWAY_FROM_JOB`). A per-session job must therefore NOT allow breakaway, or a tool
   that asks for it escapes attribution.

## How the Director starts a session today

`src/CcDirector.Core/ConPty/ProcessHost.cs` calls `CreateProcessW` with
`EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT` and the pseudo console attribute, and
keeps the returned process id. There is no job. Ending a session calls
`Process.Kill(entireProcessTree: true)`, which also walks parent ids, so **a detached child also
survives the session being killed today**. That is a separate defect, out of this mission's scope,
and the same job would fix it.

## What the attribution should use

**Recommended: one Job Object per session, created by the Director, and attribution reads the job.**

- `ProcessHost` creates the session process with `CREATE_SUSPENDED` added, assigns it to a new job,
  then resumes it. Assigning after the process has started would race: the agent can start children
  before the assignment lands.
- The job must NOT set `JOB_OBJECT_LIMIT_BREAKAWAY_OK` or `JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK`, so
  nothing can leave it. Whether any tool the agents run refuses to start without breakaway is NOT
  proved here; the change must be run against a real Claude Code and Codex session before it merges.
- Name it (for example `Local\cc-director-session-<sessionId>`) so a Director that restarts can
  reopen it with `OpenJobObject` rather than lose every running session's tree. An unnamed job is
  reachable only through the handle of the Director that made it.
- Do NOT set `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`: a Director crash would then kill every session,
  which the Director today deliberately does not do.
- The session's tree is then `QueryInformationJobObject(JobObjectBasicProcessIdList)`, and its total
  CPU and input/output is `JobObjectBasicAndIoAccountingInformation` (including processes that have
  already exited).

That is a change to how sessions start, which is outside the Windows probe. It needs a decision from
the Tech Lead on where it goes (its own task, phase 1 or phase 2).

**Until the job exists, and for processes not in a session job: walk parent ids, with two guards.**

- A process is a child of its named parent only when the parent's creation time is EARLIER than the
  child's. Otherwise the parent id has been reused and the link is false (example 3 above).
- Keep membership sticky across samples: a process once attributed to a session, keyed by its id AND
  creation time, stays attributed after its parent exits. This catches a detached child that was seen
  at least once while its parent lived. It cannot catch one whose parent lived less than a sampling
  interval (`cmd /c start ...` exits in milliseconds), which is exactly the case the job catches.

This is an interim, not a second method kept beside the first: when the per-session job merges, the
attribution reads the job and the parent walk is deleted for Windows sessions. Until then the
detail screen must not present a parent-walk figure as complete.

Either way the probe supplies the same thing for every process: id, parent id, creation time, and
the figures. The Windows probe reads creation time with `GetProcessTimes`, so `ProcessReading` needs
a start time field if it does not already carry one.
