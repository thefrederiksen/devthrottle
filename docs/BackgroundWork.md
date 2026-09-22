# The Director's background work register

Every recurring job the Director runs - a timer, a periodic timer, a file watcher, or a loop that
polls until it is cancelled - has a row here. **A job may not exist without a row.**
`BackgroundWorkRegisterTests` in `CcDirector.Core.UnitTests` fails the build when a source file
constructs one of these and has no row, when a row's site count no longer matches the code, or when
a row claims sites in a file that constructs nothing any more. A site is a constructed timer,
periodic timer or file watcher; a loop that runs until it is cancelled; or a forever loop whose body
sleeps, delays or waits between turns. The files scanned are every project the Director application
links, read from its own project references, so a project linked in tomorrow is scanned tomorrow.
A row with a site count of 0 is a job that has moved onto the scheduler (`BackgroundJobs`) and
constructs nothing of its own.

Why this document exists: on 22 September 2026 one Director ran 808,945 git commands in fourteen
hours, 51,864 of them network calls to GitHub, flat all night with nobody working, because nothing
said how often any of its background work may run and nothing noticed when the repository watcher
started feeding itself. The Director Optimizer mission (devthrottle_internal #2237) measured every
job, fixed the waste, and wrote this so the next stray timer has somewhere to be caught.

## The four tiers

1. **On change.** A file or directory changed, so recompute only that. Never a timer. A job's own
   writes must not count as changes.
2. **On view.** Runs when a screen opens and only while it is effectively visible, and stops when it
   closes. A page nobody is looking at does no work.
3. **Slow and steady.** A real timer, in minutes, for things with no change signal. Each run first
   asks "has this changed?" - a version, a hash, a modification time - and does nothing when the
   answer is no.
4. **Never on a timer.** Anything that leaves the machine to answer a question only a person cares
   about. On demand, cached, with a visible "last checked" time.

Three more kinds appear below and are not background work in the sense above: **once, then stop**
(a debounce or a one-shot delay that fires once after an event), **a bounded wait** (a loop that polls
for one thing to happen and stops at a deadline - a process to exit, a lock to free, a launcher to
report healthy), and **while a process runs** (a loop that reads a child process's output as it
arrives and ends when the process does). They are listed so the enforcement test can count them,
and they need no cadence.

## The rule for every row

Anything that touches disk, git, a process or the network is banned from the window's thread. A
job that runs on a timer names the timer's interval and says what stops it. A job with a "proposed"
cadence is not yet changed: changing how fresh what the owner sees is needs the owner's decision,
and the proposal stays here until he makes it.

"Today on this Mac" is measured from the Director log on devthrottle-mac-mini, 22 September 2026,
00:00 to 15:40, Director 2.9.0, unless the row says otherwise.

## The register

Columns: the job; the source file that constructs it (the enforcement key); how many timers,
watchers or polling loops that file constructs; what triggers a run today; the cadence today; the
tier it belongs in; what one run costs; the thread; what switches it off; what was measured.

### Repository and git

| Job | Source | Sites | Trigger today | Cadence today | Tier | One run costs | Thread | Off switch | Today on this Mac |
|---|---|---|---|---|---|---|---|---|---|
| Repository watcher: a root-folder watcher, a per-repository watcher, and the 5-minute full reconciliation | `src/CcDirector.Core/Git/RepositoryWatcher.cs` | 3 | A file event under a root or a repository; plus the timer | Recompute 2 s after a change; full rescan every 5 min unconditionally | On change (the rescan is slow-and-steady and should only run after a dropped event) | One recompute: the whole probe set for one repository, about 186 git commands on a 28-worktree repository, plus a fleet-list download | Pool | Never while the Director runs | 4,336 recomputes of one repository; fixed by #3298 (its own index writes no longer count) |
| Per-session uncommitted count | `src/CcDirector.Core/Git/SessionGitStatusMonitor.cs` | 1 | Polling loop | Every 15 s, 4 repositories at a time; its own cache lasts 10 s so it is always cold | Slow and steady; proposed: drive it from the watcher, or cache longer than the poll | One `git status --porcelain` per distinct repository | Pool | Stops with the session manager | 1,132 log lines |

### Gateway and network

| Job | Source | Sites | Trigger today | Cadence today | Tier | One run costs | Thread | Off switch | Today on this Mac |
|---|---|---|---|---|---|---|---|---|---|
| Tunnel re-push of the full snapshot, and the reconnect loop that re-dials the Gateway after a drop | `src/CcDirector.ControlApi/GatewayStreamClient.cs` | 2 | Timer; a loop that runs while the client is not disposed, dialling, waiting for the connection to close, delaying, dialling again | Every 10 s, changed or not; the reconnect waits its restart delay between dials | Slow and steady; proposed: send on change, a small keep-alive otherwise (#3301) | Full session list, a key registration per session, the full repository snapshot | Pool | Stream client disposed | 16,260 log lines; the "tick LATE" line is now written only for a second or more (#3315) |
| Repository snapshot push debounce, and the injected-text, workflow-index, skill-index and skill-store refresh cycle | `src/CcDirector.ControlApi/ControlApiHost.cs` | 2 | A repository change (debounced); the periodic timer | Debounce once per burst; the refresh cycle every 60 s | Once-then-stop; slow and steady - the skill store now downloads only a changed version (#3310); proposed for the three indexes: 5 min with a not-modified check | Three index downloads; the skill bodies only when the served version or hash changed | Pool | Host disposed | 960 refresh cycles; 15 skill directories rewritten per cycle before #3310, 0 after |
| Turn sweep | `src/CcDirector.ControlApi/TurnPusher.cs` | 1 | Periodic timer | Every 1 min | Slow and steady; keep | Push new turns; cheap when empty | Pool | Pusher disposed | Not separately counted |
| Activity event outbox | `src/CcDirector.ControlApi/ActivityEventUploader.cs` | 1 | Timer | Every 30 s | Slow and steady; keep | Drain the outbox; cheap when empty | Pool | Uploader disposed | Not separately counted |
| Instance registration heartbeat | `src/CcDirector.ControlApi/InstanceRegistration.cs` | 1 | Timer | Heartbeat interval | Slow and steady; keep | One small request | Pool | Registration disposed | Not separately counted |
| Repository state push, 6-hourly | `src/CcDirector.ControlApi/RepoStatePusher.cs` | 1 | Polling loop | Every 6 h | Slow and steady; keep now that the upstream probe reads locally (#3308) | Branch inventory plus worktree inventory for every repository | Pool | Cancellation | 44 log lines from the up-stream handler |
| Factory trigger poll | `src/CcDirector.ControlApi/Triggers/DirectorTriggerRunner.cs` | 1 | Periodic timer | Every 30 s | Slow and steady; keep, or push down the tunnel later | One request for due triggers | Pool | Cancellation | Not separately counted |
| Director stream producers: the screen-diff producer's loop, and the stream-read loop beside it that the scan does not count because it waits on arriving input | `src/CcDirector.ControlApi/DirectorStreamProducers.cs` | 1 | Runs while the stream is up | Continuous while the stream is up | While a process runs (the stream) | Streams what changed | Pool | Cancellation | Not separately counted |
| Tailscale serve re-assert | `src/CcDirector.ControlApi/TailscaleServeSelfProvisioner.cs` | 1 | Timer | Never started - dead code | None; proposed: delete | Nothing today | - | - | 0 |
| Sign-in wait on the Gateway connection panel | `src/CcDirector.Avalonia/Controls/GatewayConnectionPanel.axaml.cs` | 1 | A polling loop started while the panel waits for a sign-in to complete | Every **2 s** until the authenticated status read confirms signed-in (the first draft of this row said 30 s; the review corrected it) | A bounded wait in intent, unbounded in code; proposed: a backoff and a timeout on the wait, not a cadence | One status request | Pool | Cancellation | The 5,638 `GatewayAccountStatusClient` lines (379 an hour) are the account status read from the tunnel, a different caller; this loop only runs during a sign-in |

### Sessions, the terminal and the wingman

| Job | Source | Sites | Trigger today | Cadence today | Tier | One run costs | Thread | Off switch | Today on this Mac |
|---|---|---|---|---|---|---|---|---|---|
| Terminal buffer poll | `src/CcDirector.Terminal.Avalonia/TerminalControl.cs` | 2 | Render-priority timer (two construction sites, one control) | 20 times a second while the control exists | On view; keep the rate, do far less per tick (Stage 2, another session's pull requests #3305 and #3307) | Read new bytes, parse, repaint | Window | Control detached | Not logged |
| Deletion reaper | `src/CcDirector.Core/Sessions/SessionManager.cs` | 1 | Timer | Every 30 s | Slow and steady; keep | A directory enumeration; cheap when empty | Pool | Manager disposed | Not separately counted |
| Session pointer watcher and sweep | `src/CcDirector.Core/Sessions/SessionPointerWatcher.cs` | 2 | A file watcher, and a periodic sweep beside it | Sweep every 2 s | On change, with the sweep as the guarantee; keep | A directory enumeration | Pool | Watcher disposed | Not separately counted |
| Wingman per-session status timer | `src/CcDirector.Core/Wingman/SessionStatusWingman.cs` | 1 | Fires once after output | One shot | Once, then stop | A terminal read | Pool | Session ends | 24 log lines |
| Terminal state detector: quiet timer and content check | `src/CcDirector.Core/Wingman/TerminalStateDetector.cs` | 2 | Armed by output, fire once | One shot each | Once, then stop | A terminal read | Pool | Session ends | 19 log lines |
| Transient error auto-resume: scan debounce and retry | `src/CcDirector.Core/Wingman/TransientErrorAutoResume.cs` | 2 | Armed by an error line, fire once | One shot each | Once, then stop | A terminal read, then a keystroke | Pool | Session ends | Not separately counted |
| Windows pseudo-console read loop | `src/CcDirector.Core/ConPty/ProcessHost.cs` | 1 | The child process writes | Continuous while the process runs | While a process runs | Reads output as it arrives | Pool | Process exits | - |
| Unix pseudo-terminal read loop | `src/CcDirector.Core/UnixPty/UnixProcessHost.cs` | 1 | The child process writes | Continuous while the process runs | While a process runs | Reads output as it arrives | Pool | Process exits | 4 log lines |
| Agent process read loop | `src/CcDirector.Core/Claude/ClaudeProcess.cs` | 1 | The child process writes | Continuous while the process runs | While a process runs | Reads output as it arrives | Pool | Process exits | - |
| Agent client stream read loop | `src/CcDirector.Core/Claude/ClaudeClient.cs` | 1 | The stream delivers | Continuous while a request is open | While a process runs | Reads a response as it arrives | Pool | Request ends | - |
| Recording ingest loop | `src/CcDirector.Core/Recording/RecordingIngestService.cs` | 1 | Polling loop | While a recording is being ingested | While a process runs | Reads captured audio | Pool | Cancellation | - |
| GitHub Actions backend run watch, and the worker thread's wait for its first turn to be established | `src/CcDirector.Core/Backends/GitHubActionsBackend.cs` | 2 | Polling loop while a run is watched; a first-turn wait when the worker starts | While a run is in flight; the first-turn wait in 250 ms polls with a thirty-second ceiling | While a process runs (a remote run) | One status request per poll | Pool | Run ends or cancellation | 0 today |
| Engine scheduler loop | `src/CcDirector.Engine/Scheduling/Scheduler.cs` | 1 | Polling loop | Engine's own tick | Slow and steady; keep (engine, not the Director's screen) | Dispatches due work | Pool | Cancellation | - |

### The application's own cycle

| Job | Source | Sites | Trigger today | Cadence today | Tier | One run costs | Thread | Off switch | Today on this Mac |
|---|---|---|---|---|---|---|---|---|---|
| Auto-update cycle: the Director's own release check and the tools update, on the configured cadence | `src/CcDirector.Avalonia/App.axaml.cs` | 1 | A forever loop with `Task.Delay` on the configured interval, re-reading the setting each cycle; shortened while a release has no downloads attached yet | Hourly by default (the setting `autoUpdate.intervalHours`); not started on a development slot build | Slow and steady; keep. No cancellation token: it ends with the process | One GitHub release check, and a tools check | Pool | Never while the Director runs (the setting turns the work off, not the loop) | 30 `UpdateService` and 30 `ToolAutoUpdate` lines today |

### Bounded waits

Loops that poll for one thing to happen and stop at a deadline. Not background work; listed because the test counts them.

| Job | Source | Sites | Trigger today | Cadence today | Tier | One run costs | Thread | Off switch | Today on this Mac |
|---|---|---|---|---|---|---|---|---|---|
| Wait for a session's turn to end, for a chat request | `src/CcDirector.ControlApi/Chat/ChatService.cs` | 1 | A chat request | Poll interval until idle, or the request's timeout | A bounded wait | Reads the session's activity state | Pool | The turn ends, the timeout, or cancellation | - |
| Two bounded waits in the command executor: for a stopped session's process to go, and, on the create path, for a session to be ready before a pre-prompt is typed | `src/CcDirector.ControlApi/SessionCommandExecutor.cs` | 2 | A stop command; a create with a pre-prompt | Process exit poll interval until the settle window ends; 750 ms polls until the terminal is quiet, to the pre-prompt wait | A bounded wait | One liveness read; one activity and buffer read | Pool | The process is gone or the session is ready, or the window ends | - |
| Wait for the terminal to go quiet before an ask is typed, and wait for the reply to stabilise afterwards | `src/CcDirector.Core/Drivers/SessionAskRunner.cs` | 2 | A session ask | Poll interval until quiet, or the ask's timeout; then polls until the reply stops changing | A bounded wait | Reads the buffer clock; reads the buffer | Pool | Quiet, the timeout, or cancellation | - |
| Acquire the worktree-reap lock | `src/CcDirector.Core/Git/WorktreeReservationStore.cs` | 1 | A reap | Every 15 ms until the lock is free, or the lock wait | A bounded wait | One file open | Caller's thread (blocking sleep) | The lock is taken, or the wait ends | - |
| Append a shadow-log line under contention | `src/CcDirector.Core/Wingman/TurnDetectionShadowLog.cs` | 1 | A line to write | Retry until the append succeeds, within the contention budget | A bounded wait | One file append | Caller's thread | The write lands, or the budget ends | - |
| Wait for the launcher to report healthy | `tools/cc-director-setup-engine/LauncherHealthProbe.cs` | 1 | A launcher start | Every 1 s until healthy, or the ceiling | A bounded wait | One registration file read | Pool | Healthy, the starter exited, cancellation, or the ceiling | - |
| Progress poll during a tools install | `tools/cc-director-setup-engine/PythonToolsInstaller.cs` | 1 | A tools install | Every 1.5 s while the install runs | A bounded wait | One progress read | Pool | The install ends | - |
| Bring back sessions after a smart shutdown: poll the Gateway until the seats are back | `src/CcDirector.ControlApi/SmartRestart/GatewaySmartShutdownBringBack.cs` | 1 | A bring-back | Every 2 s, through an injected delay, until done or cancelled | A bounded wait | One Gateway read | Pool | Done, or cancellation | - |
| The drain's loops: its main collect-and-close loop, the wait for sessions to flag their handovers, and the wait for them to close | `src/CcDirector.ControlApi/Drain/DirectorDrain.cs` | 3 | A smart shutdown | The two waits on the drain's poll interval, through an injected delay, until the deadline; the main loop runs once through the seats | A bounded wait | One roster read per poll | Pool | The seats are done, the deadline, or cancellation | - |
| A bounded wait in the session write path | `src/CcDirector.ControlApi/SessionWriteExecutor.cs` | 1 | A write command | 500 ms polls to a deadline | A bounded wait | One state read | Pool | The condition, or the deadline | - |
| Wait for an automation browser to come up, and to go down | `src/CcDirector.Core/Browsers/AutomationBrowserService.cs` | 2 | A browser start or stop | 250 ms polls to a deadline | A bounded wait | One local probe | Pool | Up or down, or the deadline | - |
| Wait for the terminal to echo a submitted line | `src/CcDirector.Core/Drivers/TerminalSubmit.cs` | 1 | A submit | Polls to a deadline | A bounded wait | One buffer read | Pool | The echo, or the deadline | - |
| Two bounded waits inside a session: for a fresh transcript after a context clear, and for a compaction to finish | `src/CcDirector.Core/Sessions/Session.cs` | 2 | A context clear; a compact | The transcript wait in 250 ms polls to a deadline; the compaction wait on its own poll interval to its own timeout | A bounded wait | One transcript check; one driver query | Pool | A fresh transcript or a finished compaction, the deadline, or cancellation | - |
| Wait for a display to be available before an update relaunch | `src/CcDirector.Core/Update/DisplayAvailability.cs` | 1 | An update install on macOS | 500 ms sleeps to a deadline | A bounded wait | One display check | The updater's thread | A display, or the deadline | - |
| The update installer's waits: for the relaunched build to report healthy, and for a process to exit | `src/CcDirector.Core/Update/UpdateInstaller.cs` | 2 | An update install | 2 s and 200 ms sleeps to a timeout | A bounded wait | One health read, one process check | The updater's thread | Healthy or exited, or the timeout | - |
| Wait for playback to finish | `src/CcDirector.Avalonia/Voice/DesktopTtsPlayer.cs` | 1 | A spoken line | 80 ms sleeps while playing | A bounded wait | One state read | The player's thread | Playback ends | - |
| A Director update's two waits: for the running Director to report a version, and for the install target to become replaceable | `tools/cc-director-setup-engine/DirectorUpdateApply.cs` | 2 | A Director update | The version poll on its poll interval to a timeout, or cancellation; the replaceable wait in 500 ms sleeps to a deadline | A bounded wait | One version request; one file open | Pool; the installer's thread | Reported or replaceable, the timeout, or cancellation | - |
| A Gateway self-update's two waits: for the new Gateway to report healthy, and for its files to become writable | `tools/cc-director-setup-engine/GatewaySelfUpdate.cs` | 2 | A Gateway self-update | The health wait in 1 s delays to a deadline; the writable wait in 500 ms sleeps to a deadline | A bounded wait | One health request; one file open | Pool; the installer's thread | Healthy or writable, or the deadline | - |
| A bounded wait in the Gateway tray install | `tools/cc-director-setup-engine/GatewayTrayInstaller.cs` | 1 | A tray install | Sleeps to a deadline | A bounded wait | One check | The installer's thread | The condition, or the deadline | - |
| A bounded wait in the macOS launcher install | `tools/cc-director-setup-engine/LauncherMacInstaller.cs` | 1 | A launcher install | Polls to a deadline | A bounded wait | One check | The installer's thread | The condition, or the deadline | - |
| A launcher self-update's two waits: for the new launcher to report healthy, and for its files to become writable | `tools/cc-director-setup-engine/LauncherSelfUpdate.cs` | 2 | A launcher self-update | The health wait in 1 s delays to a deadline; the writable wait in 500 ms sleeps to a deadline | A bounded wait | One health request; one file open | Pool; the installer's thread | Healthy or writable, or the deadline | - |
| Wait for the launcher to stop | `tools/cc-director-setup-engine/LauncherStopper.cs` | 1 | A launcher stop | 250 ms sleeps to a timeout | A bounded wait | One process check | The installer's thread | Stopped, or the timeout | - |
| Wait for a condition while the launcher update is owned | `tools/cc-director-setup-engine/LauncherUpdateOwner.cs` | 1 | A launcher update | 250 ms sleeps to a timeout | A bounded wait | One check | The installer's thread | The condition, or the timeout | - |
| Standby slot provisioning retries | `tools/cc-director-setup-engine/StandbySlotProvisioner.cs` | 1 | A slot that is not yet settled | Retries until settled, with the caller's delay between attempts | A bounded wait in shape; unbounded as called - the application starts it with no cancellation token, so a slot that never settles retries for the life of the process (the review's note) | One provisioning attempt | Pool | Settled. Proposed: a cap on attempts | - |

### Screens and dialogs

| Job | Source | Sites | Trigger today | Cadence today | Tier | One run costs | Thread | Off switch | Today on this Mac |
|---|---|---|---|---|---|---|---|---|---|
| Main window: tools reconcile retry (one shot), terminal verification trailing check (one shot, #3302), paste card auto-hide (one shot), update status panel (20 s), screenshots watcher and its debounce | `src/CcDirector.Avalonia/MainWindow.axaml.cs` | 6 | Events for the one-shots; a timer for the panel; a file watcher for screenshots | Panel every 20 s | Once-then-stop for four; slow and steady for the panel (its read now runs off the window's thread, #3317); on change for the watcher | Panel: two file reads and a process lookup, on the pool | Window (paint only) / pool (read) | Window closes | 2,880 updater-state loads |
| Source Control page: status poll and sync tick | `src/CcDirector.Avalonia/Controls/GitChangesView.axaml.cs` | 2 | Timers while attached | Status every 15 s; sync every 30 s with a fetch once a minute - only while effectively visible (#3312) | On view | `git status`; a network `git fetch` once a minute | Pool (git) / window (paint) | Tab hidden, or Detach | Attached 10:56 to 12:40; zero work while hidden after #3312 |
| Context gauge | `src/CcDirector.Avalonia/Controls/SessionActionBar.axaml.cs` | 1 | Timer while a session with the capability is active | Every 4 s | On view; keep the rate now that a read is one length check (#3313) | Reads only what was appended to the transcript | Pool (read) / window (paint) | Session switched away or closed | 8,514 log lines; now logged only when bytes were read |
| Browsers rail | `src/CcDirector.Avalonia/Controls/BrowsersRailGroup.axaml.cs` | 1 | Timer | Every 30 s | On view; proposed: 60 s, only while the rail is visible | Registry file read, a local web probe per browser; browser detection is cached per process since #3302 | Pool | Rail detached | 15,040 launcher log lines before #3302 |
| Connections page poll | `src/CcDirector.Avalonia/Controls/ConnectionsView.axaml.cs` | 1 | Timer while the page is open | Every 5 s | On view; stops when the page closes - correctly scoped | One local read | Window | Page closes | - |
| Comm Manager poll | `src/CcDirector.Avalonia/Controls/CommManager/CommManagerViewModel.cs` | 1 | Timer while the screen is open | Every 10 s | On view; correctly scoped | One local read | Window | Screen closes | - |
| Workflow recorder poll | `src/CcDirector.Avalonia/WorkflowRecorderWindow.axaml.cs` | 1 | Timer while the window is open | Every 1 s | On view; correctly scoped | One local read | Window | Window closes | - |
| New session dialog: a delayed one-shot | `src/CcDirector.Avalonia/NewSessionDialog.axaml.cs` | 1 | An event in the dialog | Fires once after 1.5 s | Once, then stop | Nothing recurring | Window | Dialog closes | - |
| Smart restart progress clock | `src/CcDirector.Avalonia/SmartRestart/ShutdownProgressViewModel.cs` | 1 | Timer while the progress screen shows | Every 1 s | On view; correctly scoped | Repaint a countdown | Window | Screen closes | - |
| Speak dialog: level meter, equaliser animation, ready timeout | `src/CcDirector.Avalonia/Voice/SpeakDialog.axaml.cs` | 3 | Timers while the dialog is open | 100 ms, 33 ms, and a 6 s one-shot | On view; correctly scoped | Repaint | Window | Dialog closes | - |
| Wake word test dialog: equaliser animation and debounce | `src/CcDirector.Avalonia/Voice/WakeWordTestDialog.axaml.cs` | 2 | Timers while the dialog is open | 33 ms; a debounce | On view; correctly scoped | Repaint | Window | Dialog closes | - |
| Transcription component preview animation | `src/CcDirector.Avalonia/Controls/TranscriptionComponentPreviewDialog.axaml.cs` | 1 | Timer while the dialog is open | 100 ms | On view; correctly scoped | Repaint | Window | Dialog closes | - |

### Disk

| Job | Source | Sites | Trigger today | Cadence today | Tier | One run costs | Thread | Off switch | Today on this Mac |
|---|---|---|---|---|---|---|---|---|---|
| Backup cleaner | `src/CcDirector.Core/Utilities/BackupCleaner.cs` | 1 | Timer | Every 60 s | Slow and steady; keep | A directory enumeration | Pool | Cleaner disposed | Not separately counted |
| NUL-file watcher (Windows) | `src/CcDirector.Core/Utilities/NulFileWatcher.cs` | 1 | A file watcher per drive | On change | On change; keep | A delete when a stray NUL file appears | Pool | Watcher disposed | - |
| Screenshot capture watcher | `src/CcDirector.Core/Storage/ScreenshotCaptureWatcher.cs` | 1 | A file watcher | On change | On change; keep | Moves one file | Pool | Watcher disposed | - |
| Dictation dictionary reload | `src/CcDirector.Core/Dictation/DictionaryLoader.cs` | 1 | A file watcher on the dictionary file | On change | On change; keep | Reloads one file | Pool | Loader disposed | - |
| Shutdown signal listeners: the macOS and Linux request-file poll, and the Windows named-event pump | `src/CcDirector.Core/Lifecycle/LifecycleSignal.cs` | 2 | Polling loop on a request file, one thread per signal; on Windows a thread blocked in `WaitAny` on the kernel event | Every 500 ms on macOS and Linux; instantaneous, no polling, on Windows | Slow and steady on macOS and Linux; on change on Windows; **left as is, deliberately** - the code records a measured 1-in-5 lost event with a file watcher, and a missed shutdown is worse than a half-second-late one. Owner's decision (report 1, question 4) | One `File.Exists` on macOS and Linux; nothing per tick on Windows, where the thread sleeps in the kernel until the event fires | Its own thread | Listener disposed | About 4 stat calls a second |

## What the scan cannot see

Two shapes are outside the mechanical rule, and are written here so nobody mistakes the test for a
proof of exhaustiveness:

- **A wait reached through a call the scan cannot recognise as a wait.** The rule counts a delay,
  sleep or wait handle by name, including a delegate named like one (`_delay`, `retryDelay`). A loop
  that waits through a method named for what it does rather than for waiting is not counted.
- **A loop that waits on arriving input** - a listener blocked in an accept, a reader blocked on a
  stream. It does no work until something arrives, and it is not a job in the sense of this register.

## What is not yet enforced

The register is enforced by file and site count. It does not yet enforce the cadence column: a
job can still run faster than its row says. That is what the scheduler is for - one place every
recurring job registers with, naming its tier, cadence, freshness and off switch, so "off the
window's thread, parallelism-limited, stops when its screen closes" is a property of the
scheduler rather than a promise in every class - and the Background work page that shows each
job's runs in the last hour against its ceiling. Both are Stage 3 of the mission and are not in
this document's first version.
