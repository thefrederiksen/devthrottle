# Scheduler quick pause proof

Drives the REAL Cockpit Schedule page, Factories screen, a factory's Seats tab and the schedule editor in a real
Chromium against a fake Gateway (issue #3910). Run it from this directory:

```
node run-proof.mjs
```

It prints PASS or FAIL per claim, exits non-zero on any failure, and writes `evidence-<date>.json` plus nine
screenshots beside it. When this machine does not have the Chromium this Playwright expects, name one that is
installed in `PLAYWRIGHT_CHROMIUM`.

## What is real and what is not

REAL: the shipping `ScheduleView`, `ScheduleEditor`, `FactoriesView`, `FactoryView` and `SchedulerPause` components,
every client-core call they make, and the Cockpit's own stylesheets, bundled from source.

SIMULATED: the Gateway. An in-page fetch shim keeps the pause-everything switch and three schedules in memory, applies
each write, and records every request so the driver can assert what a tap actually sent. Its words end in "(fake)",
so a screenshot shows the page rendered the Gateway's words rather than its own.

This proves the CLIENT flow. That nothing fires while everything is paused, that no schedule is written, that
resuming starts no burst of late runs, and that the switch survives a Gateway restart are proven in C# against the
real engine, store and host: `CronPauseEverythingTests` and `CronPauseEverythingHostTests`.

## The claims

- **A** - one word: the status chip says Active and Paused, and nothing on the page - titles included - says Enabled
  or Disabled.
- **B** - one tap on the Schedule page sends `PUT /cron/pause {paused:true}`; the banner says so in the Gateway's
  words; no schedule was written and every schedule's own enabled flag is untouched.
- **C** - the Factories screen shows the same banner and control; one tap there sends `{paused:false}`, and every
  schedule is exactly as it was before the pause.
- **D** - a seat's schedule has Pause beside Edit; one tap pauses that schedule and no other, the line then reads
  "(paused)" and offers Resume, and one tap resumes it. The write carries none of the Gateway's own state.
- **E** - the editor opens a paused schedule as Paused; an edit that leaves the control alone saves it still paused;
  picking Active is what resumes it.
