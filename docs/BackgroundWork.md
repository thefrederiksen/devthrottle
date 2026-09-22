# The Director's background work register

Every recurring job the Director runs - a timer, a periodic timer, a file watcher, or a loop that
polls until it is cancelled - has a row here. **A job may not exist without a row.**
`BackgroundWorkRegisterTests` in `CcDirector.Core.UnitTests` fails the build when a source file
constructs one of these and has no row, when a row's site count no longer matches the code, or when
a row points at a file that constructs nothing any more.

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

Two more kinds appear below and are not background work in the sense above: **once, then stop**
(a debounce or a one-shot delay that fires once after an event) and **while a process runs** (a loop
that reads a child process's output as it arrives and ends when the process does). They are listed
so the enforcement test can count them, and they need no cadence.

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
| Tunnel re-push of the full snapshot | `src/CcDirector.ControlApi/GatewayStreamClient.cs` | 1 | Timer | Every 10 s, changed or not | Slow and steady; proposed: send on change, a small keep-alive otherwise (#3301) | Full session list, a key registration per session, the full repository snapshot | Pool | Stream client disposed | 16,260 log lines; the "tick LATE" line is now written only for a second or more (#3315) |
| Repository snapshot push debounce, and the injected-text, workflow-index, skill-index and skill-store refresh cycle | `src/CcDirector.ControlApi/ControlApiHost.cs` | 2 | A repository change (debounced); the periodic timer | Debounce once per burst; the refresh cycle every 60 s | Once-then-stop; slow and steady - the skill store now downloads only a changed version (#3310); proposed for the three indexes: 5 min with a not-modified check | Three index downloads; the skill bodies only when the served version or hash changed | Pool | Host disposed | 960 refresh cycles; 15 skill directories rewritten per cycle before #3310, 0 after |
| Turn sweep | `src/CcDirector.ControlApi/TurnPusher.cs` | 1 | Periodic timer | Every 1 min | Slow and steady; keep | Push new turns; cheap when empty | Pool | Pusher disposed | Not separately counted |
| Activity event outbox | `src/CcDirector.ControlApi/ActivityEventUploader.cs` | 1 | Timer | Every 30 s | Slow and steady; keep | Drain the outbox; cheap when empty | Pool | Uploader disposed | Not separately counted |
| Instance registration heartbeat | `src/CcDirector.ControlApi/InstanceRegistration.cs` | 1 | Timer | Heartbeat interval | Slow and steady; keep | One small request | Pool | Registration disposed | Not separately counted |
| Repository state push, 6-hourly | `src/CcDirector.ControlApi/RepoStatePusher.cs` | 1 | Polling loop | Every 6 h | Slow and steady; keep now that the upstream probe reads locally (#3308) | Branch inventory plus worktree inventory for every repository | Pool | Cancellation | 44 log lines from the up-stream handler |
| Factory trigger poll | `src/CcDirector.ControlApi/Triggers/DirectorTriggerRunner.cs` | 1 | Periodic timer | Every 30 s | Slow and steady; keep, or push down the tunnel later | One request for due triggers | Pool | Cancellation | Not separately counted |
| Director stream producers | `src/CcDirector.ControlApi/DirectorStreamProducers.cs` | 1 | Polling loop while connected | Continuous while the stream is up | While a process runs (the stream) | Streams what changed | Pool | Cancellation | Not separately counted |
| Tailscale serve re-assert | `src/CcDirector.ControlApi/TailscaleServeSelfProvisioner.cs` | 1 | Timer | Never started - dead code | None; proposed: delete | Nothing today | - | - | 0 |
| Account status | `src/CcDirector.Avalonia/Controls/GatewayConnectionPanel.axaml.cs` | 1 | Polling loop | Every 30 s on a brand-new connection each time | Slow and steady; proposed: every 5 min, reusing one connection | One web request | Pool | Cancellation | 5,638 log lines (379 an hour) |

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
| GitHub Actions backend run watch | `src/CcDirector.Core/Backends/GitHubActionsBackend.cs` | 1 | Polling loop while a run is watched | While a run is in flight | While a process runs (a remote run) | One status request per poll | Pool | Run ends or cancellation | 0 today |
| Engine scheduler loop | `src/CcDirector.Engine/Scheduling/Scheduler.cs` | 1 | Polling loop | Engine's own tick | Slow and steady; keep (engine, not the Director's screen) | Dispatches due work | Pool | Cancellation | - |

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
| Shutdown signal listener (macOS and Linux) | `src/CcDirector.Core/Lifecycle/LifecycleSignal.cs` | 1 | Polling loop on a request file, one thread per signal | Every 500 ms | Slow and steady; **left as is, deliberately** - the code records a measured 1-in-5 lost event with a file watcher, and a missed shutdown is worse than a half-second-late one. Owner's decision (report 1, question 4) | One `File.Exists` | Its own thread | Listener disposed | About 4 stat calls a second |

## What is not yet enforced

The register is enforced by file and site count. It does not yet enforce the cadence column: a
job can still run faster than its row says. That is what the scheduler is for - one place every
recurring job registers with, naming its tier, cadence, freshness and off switch, so "off the
window's thread, parallelism-limited, stops when its screen closes" is a property of the
scheduler rather than a promise in every class - and the Background work page that shows each
job's runs in the last hour against its ceiling. Both are Stage 3 of the mission and are not in
this document's first version.
