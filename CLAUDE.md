# CC Director - Project Instructions

This is **enterprise-level software** requiring robust error handling, comprehensive logging, thorough testing, and responsive UI.

**Full coding standards:** [docs/CodingStyle.md](docs/CodingStyle.md)

**UI style guide:** [docs/VisualStyle.md](docs/VisualStyle.md) -- All UI changes must comply with this guide.

**Writing docs about a screen:** [docs/checking-docs-against-screen-dumps.md](docs/checking-docs-against-screen-dumps.md) -- Check the sentence against the committed accessibility dump for that screen, not the XAML. The XAML says what a screen CAN show; the dump says what it DID show. The dumps are committed in `devthrottle_internal` at `docs/qa/runs/<run>/report.html` and anyone can read them without a machine.

---

## Critical Rules

### 00. NEVER READ OR WORK FROM A STALE CHECKOUT - THIS IS RULE ZERO

**A checkout that is behind origin/main is a lie. Reading it and reporting what you find as fact is the single most expensive mistake we make. Verify freshness BEFORE you read a single file.**

This has already cost real time, repeatedly, in one day:

- A session read `GatewayEndpoints.cs` in a tree 53 commits behind, concluded a shipped feature "was never merged or was reverted", and escalated. It had shipped days earlier.
- Another session read `session_ops.py` in a tree 55 commits behind, concluded a fixed file was still broken, and wrote that into a GitHub issue comment as evidence. The fix was already on main.

Both sessions were confident. Both were wrong. Both were caught only because a third party double-checked. **Do not rely on being double-checked.**

**The rule:**

1. **Before reading code to answer a question, check the tree.** The `SessionStart` hook runs `scripts/check-tree-freshness.ps1` and fails loud when this tree is behind origin/main. If you see that banner, believe it. Run it yourself any time you are unsure:
   ```
   powershell -NoProfile -File scripts\check-tree-freshness.ps1
   ```
2. **To READ shipped code, read origin/main directly - never the working tree:**
   ```
   git fetch origin
   git show origin/main:path/to/file.cs
   git grep <pattern> origin/main
   ```
3. **To WORK - edit, build, open a pull request - cut a worktree from origin/main.** Never work in the shared checkout, and never `git checkout -b` in it:
   ```
   git fetch origin
   git worktree add ../<repo>-<task> -b <branch-name> origin/main
   ```
4. **Never `git pull` the shared checkout to "fix" staleness.** Other sessions and agents are working in it; pulling underneath them breaks them. Cut a worktree instead.
5. **A branch is stale the moment origin/main moves past it.** Rebase onto origin/main or cut a fresh worktree. Do not build on a base you fetched hours ago.

**When you state a fact about the code, it must come from origin/main or a worktree cut from it.** If you cannot say which, you do not know the fact - go and check. Saying "the code says X" when X came from a stale tree is reporting fiction as evidence.

### 0a. WRITE IN PLAIN ENGLISH - NO ABBREVIATIONS

**Never use abbreviations, acronyms, initialisms, or jargon. Write out the full words in plain English, everywhere.**

This applies to chat replies, issue text, commit messages, code comments, reports, documentation - all output. Spell out what you mean. Say "pull request" not "PR", "the readiness checklist" not "DoR", and so on. If a short form exists, do not use it - use the ordinary words. Clarity over brevity, always. Do not be clever or terse at the cost of being understood.

### 0. NEVER KILL RUNNING PROCESSES WITHOUT PERMISSION

**ABSOLUTELY NEVER use taskkill or any command to terminate cc-director.exe or any other running application without explicit user approval.**

The user runs multiple instances of cc-director simultaneously. Killing processes to "fix" build errors is NOT acceptable. If a build fails due to locked files:
- Tell the user the build failed because files are locked
- Ask the user if they want to close the application themselves
- NEVER automatically kill processes

This rule has NO exceptions.

### 0b. LAUNCH cc-director.exe VIA WINDOWS TASK SCHEDULER, NEVER DIRECTLY

**If you (the Claude agent) are running inside a Claude Code CLI session (you almost always are), DO NOT spawn `cc-director.exe` from your own process tree.** Use the `cc-director-launch` Windows scheduled task instead.

#### Why

When cc-director.exe is launched from inside your claude.exe ConPty, the child claude.exe processes IT spawns inherit a nested pseudo-console. Grandchild claudes detect this as a non-TTY environment and exit within ~3 seconds with:

> `Error: Input must be provided either through stdin or as a prompt argument when using --print`

This is claude.exe 2.1.143+ behavior on nested ConPty, not a CC Director bug.

#### The fix: Task Scheduler

Processes launched by Task Scheduler run under `svchost.exe` (the Schedule service), completely outside your ConPty. Grandchild claudes spawned by such a Director have clean stdio and survive.

**One-time setup** (idempotent, safe to re-run):

```powershell
# Point the task at your current test build. The WorkingDirectory must be set, or
# Avalonia's first-time resource resolution may fail with exit -1.
$exe = "C:\repos\cc-director\scripts\local-build\cc-director5.exe"
$wd  = "C:\repos\cc-director\scripts\local-build"
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $wd
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)  # far future, on-demand only
Register-ScheduledTask -TaskName "cc-director-launch" -Action $action -Trigger $trigger -Force
```

**To launch on demand:**

```powershell
Start-ScheduledTask -TaskName "cc-director-launch"
```

The Director boots with parent = `svchost.exe` and binds NOTHING - the remove-the-network-port mission deleted its Control API, so there is no port to find and no REST to drive. Readiness is the instance registration the running process writes (`<root>\instances\<slug>\config\director\instances\<directorId>.json`, whose `Pid` is your process); its log is at `%LOCALAPPDATA%\cc-director\logs\director\director-YYYY-MM-DD-<PID>.log`. Drive a test Director through the Gateway it is connected to, and stop it with the named signal described below.

#### Slot convention to avoid colliding with the user's running Directors

The `scripts\local-build` directory holds development slots `cc-director1.exe` through `cc-director4.exe` (the main daily-driver `cc-director.exe` is no longer a local build - it is INSTALLED from a release into `%LOCALAPPDATA%\cc-director\app` and auto-updates in place). The user keeps long-lived Director processes running from the installed app and from slots 1-4, and you MUST NOT kill any of them. Reserve **slot 5 or higher** for your own test Directors. Build to that slot with `scripts\local-build-avalonia.ps1 -Slot 5 -OutputDir "<repo>\scripts\local-build"` (the default `-OutputDir` is the dead repo-root `local_builds\` - always pass `scripts\local-build`) and point `cc-director-launch` at that path.

#### Cleaning up your own test Director

**Shut a test Director down cleanly, do NOT force-kill it.** Signal it — that makes the Director kill its own sessions and delete its crash journal, so it does not leave a phantom "interrupted" entry in the fleet (issue #960). A force-kill (`Stop-Process -Force`) gives the process no chance to clean up and DOES leave that phantom journal, so it is the last resort only — use it only if the signalled shutdown does not exit in time (the Director is genuinely stuck). The `scripts\agent-session-isolation.ps1 teardown` verb already does this.

**There is no `POST /shutdown` any more, and there is no credential to resolve.** Stopping a Director is a named signal, because it has to work when no socket is accepting connections. The signal is named for THAT Director's own identifier, which is the only string that names one process — this machine runs several Directors, so a request that could reach any of them would eventually reach the wrong one. Read the identifier out of the instance registration of the process you launched (`<root>\instances\<slug>\config\director\instances\<directorId>.json`, the file whose `Pid` is your Director), then:

```powershell
$evt = [System.Threading.EventWaitHandle]::OpenExisting("Local\cc-director-shutdown-$directorId")
$evt.Set(); $evt.Dispose()
```

`OpenExisting` throwing means nothing is listening — that Director cannot be asked to stop, and only then is a force-kill the answer. `scripts\agent-session-isolation.ps1` (`Get-DirectorIdForPid`, `Stop-DirectorCleanly`) is the working reference.

When you must force-kill as a last resort: only kill a process whose path matches the slot YOU launched (e.g. `cc-director5.exe`). Confirm via `Get-Process | Select-Object Id, ProcessName, Path` first. Never use a blanket `Stop-Process -Name cc-director*` — that would kill the main build and the user's working sessions.

For non-session-creating tests (HTML rendering, REST endpoint smoke, build-only verification) launching from your context is still fine. Only session-creation tests need the Task Scheduler path.

### 1. Responsive UI - MANDATORY

**Every user action MUST provide immediate visual feedback (<100ms).**

- Show dialogs/panels immediately, even if empty
- Display "Loading..." text or spinner for any operation >200ms
- Load expensive data (file I/O, API calls) asynchronously in background
- Use INotifyPropertyChanged to update UI when data arrives
- NEVER block the UI thread with synchronous I/O

```csharp
// BAD - Blocks UI
public MyDialog()
{
    InitializeComponent();
    var items = LoadFromDisk();  // FREEZES UI!
    ListBox.ItemsSource = items;
}

// GOOD - Immediate response
public MyDialog()
{
    InitializeComponent();
    LoadingText.Text = "Loading...";

    Loaded += async (_, _) =>
    {
        var items = await Task.Run(() => LoadFromDisk());
        ListBox.ItemsSource = items;
        LoadingText.Visibility = Visibility.Collapsed;
    };
}
```

### 2. Enterprise Logging - MANDATORY

**Every public method must log entry, exit, and errors.**

```csharp
public Session CreateSession(string repoPath)
{
    FileLog.Write($"[SessionManager] CreateSession: {repoPath}");
    try
    {
        var session = CreateSessionInternal(repoPath);
        FileLog.Write($"[SessionManager] Session created: {session.Id}");
        return session;
    }
    catch (Exception ex)
    {
        FileLog.Write($"[SessionManager] CreateSession FAILED: {ex.Message}");
        throw;
    }
}
```

### 3. No Fallback Programming

**Fix root causes, don't add fallbacks that hide problems.**

```csharp
// BAD
try { return GetValue(); }
catch { return "Unknown"; }  // Hides the real problem!

// GOOD
var value = GetValue();
if (value is null)
    throw new InvalidOperationException("Value not available");
return value;
```

### 4. Try-Catch at Entry Points Only

Put try-catch ONLY in:
- Event handlers (button clicks)
- Lifecycle methods (Loaded, Initialized)
- External event subscriptions

Do NOT put try-catch in helper methods or service methods.

### 5. Testing Required

- All public methods need unit tests
- All bug fixes need regression tests
- Use Arrange-Act-Assert pattern
- Name tests: `MethodName_Scenario_ExpectedResult`

### 5a. LOCAL RUN, THEN A REVIEW, THEN MERGE - NEVER WAIT FOR CONTINUOUS INTEGRATION

**The gate is two things, both of which happen here: `.\scripts\test-local.ps1` goes green, and a
reviewer from a different agent family reads the change. Then merge. Nothing waits on GitHub.**

There is no longer any case in which you hold a merge open waiting for a continuous integration
result - not a release, not a change to the build or to continuous integration itself, not a
cross-platform change. That exception list is gone deliberately. Waiting fifty minutes for an
answer you already have locally was the single largest source of dead time in this repository
(issue #1156), and every exception written into the rule was an invitation to pay it again.

1. **Run the local gate.** Use the script, not a hand-rolled `dotnet test` - it is the ONE place
   the whole fleet gets faster when the suite improves, and a convention each caller
   re-implements cannot be improved centrally. A red local run is a red change; fix it before
   going further.

   Know exactly what the default run covers, because it is not everything:
   - It runs every suite that fits the two-minute budget, roughly 3,400 tests.
   - **Two suites are PARKED and do NOT run by default** - `Gateway.Tests` (host-bound, takes a
     machine-wide lock) and `Core.Tests` (far outside the budget). Run them with `-Parked`.
   - `-Fast` is a **no-op**, retained only for callers that still pass it. The default is the
     fast run. Do not claim a change was gated by `-Fast`; it means nothing.
   - It runs **no web tests and no Python tests** at all.

   So a regression inside a parked suite, the browser shells, or the Python toolbelt can reach
   main with a green local run. If your change touches any of those, run `-Parked` or the
   relevant suite yourself - do not let "the gate was green" stand in for coverage it never had.
2. **Get the change reviewed by a different agent family.** The author is the last to see the
   defect, so the reviewer must not be the writer. Codex is the default reviewer, and it runs as
   a real tracked session, never backgrounded and never hidden:

       cc-devthrottle session spawn <repo> --agent Codex --prompt "<what to review>" --name "review: <what it is>"

3. **Merge.** `gh pr merge <number> --squash --delete-branch`, then park the checkout back on main.

**When continuous integration goes red afterwards, fix it forward immediately.** That is the whole
trade, and it only works if the red is actually chased: a red that is left standing turns the
backstop into noise, and then nobody looks at it at all. Chase it the moment you see it.

**Chasing a red is not the same as waiting for a green.** Nothing is ever held open pending a
check. But the web and Python jobs are the ONLY place those tests run at all, so if you touched
the browser shells or the Python toolbelt, go and read that result after merging rather than
walking away from it. Merge without waiting; come back for the answer.

**Releasing is the one place the missing coverage bites, and it cannot be fixed forward.** The
release workflow runs ZERO tests - it builds and publishes artifacts - and a pushed tag cannot be
un-pushed. So a defect that the default gate never looked at ships, and "fix it forward" is not
available to a release that is already out. **The release gate runs on merged `main` at the exact
commit about to be tagged, and it is ONE command:**

    .\scripts\test-local.ps1 -Parked -Configuration Release

`-Parked` adds the two skipped suites. `-Configuration Release` matches what users download,
because the script defaults to Debug while the continuous integration job it replaced ran Release.

**Corrected 2026-08-04, on evidence, by the remove-the-network-port mission.** This section used to
say the gate was THREE commands, because the two installer projects were not in `cc-director.sln`
and the script "runs nine projects, all under `src\`". That is no longer true of the script: it
names `tools\cc-director-setup.Tests` and `tools\cc-director-setup-engine.Tests` in its own project
list, its default run reports both (25 and 454 tests), and a `-Parked` run produces ELEVEN result
files. Verified twice - a Manager's eleven result files, and the Architect reading the project list
in `scripts\test-local.ps1` - rather than taken from either report. The two extra `dotnet test`
commands were re-running suites the gate had already run. **If you are about to release, run the one
command; the installer IS covered.**

An earlier run does not count: the version bump and anything merged since is untested by it, and a
run against a pull-request head is not a run against the squashed commit that gets tagged. This is
the release gate, it is local, and it replaces the old instruction to wait for a green continuous
integration run rather than reintroducing it.

If the Gateway suite says it is WAITING on a lock held by another run, that is not a hang - it is
one run at a time by design, and it prints its holder every 30 seconds. See issue #1156 for why that
queue exists and the work to remove it.

### 6. UI Thread Safety

```csharp
// ALWAYS dispatch to UI thread for ObservableCollection changes
Dispatcher.BeginInvoke(() =>
{
    _sessions.Add(newSession);
});
```

### 7. THE CLIENT IS DUMB - THE GATEWAY OWNS ALL RULING

**Every display verdict is computed on the Gateway and pushed; clients only render it, verbatim.** Colors, labels, triage buckets, and the voice-mode display state (badge, message, and which actions are offered) are all FOLDED once on the Gateway and stamped onto the session the client reads. A client never re-derives a verdict, never guesses, never branches to decide what a state "means".

**Why:** a client that rules for itself will, the moment the Gateway hands it something it did not expect, render something *plausible* instead of something *true*. That is exactly how the Voice screen came to show a red "Voice unavailable" badge next to a "Generate narration now" button that could never work: the Gateway sent no reason, so the phone GUESSED "offer a button". A dumb client cannot guess. See `docs/new_architecture/session-state.html`.

**How to apply:** compute the verdict in one Gateway place (e.g. `SessionOrdering` for color/label/triage, `VoiceDisplayFold` for the voice screen), put the finished strings and booleans on the DTO, and have the client read them. If you find yourself writing a conditional in a `.tsx`/`.xaml` view that decides *what a state means* (as opposed to *how to lay out* what the Gateway already decided), move it to the Gateway. Adding a new state is one edit in the fold, never a new branch in every client.

### 8. SETTINGS IS ONE PAGE ON TWO SURFACES - IT MUST STAY IN SYNC

**The Cockpit and the mobile app show the same Settings: the same tabs, in the same order, with the same names, and cards that look the same.** The desktop has more room and may show MORE DETAIL within a tab; the phone may show a reduced version of the same card. Neither may have a tab, a setting, or a name the other does not.

**Why:** they were allowed to drift and became two different products for one account. The Cockpit had Notifications / AI / Car Mode with the dictation checks on a separate page entirely; the phone had a single untabbed "AI settings" scroll with no notification settings, no Car Mode end phrase, and the two checks on standalone screens. Found on the phone, it read as broken. Two lists in two files drift apart by default; one list in one file cannot.

**How to apply:** the tab set and every card live ONCE, in `packages/client-core/src/settings/` (`tabs.ts`, `SettingsTabs.tsx`, and the four tab components), and both shells mount them. Each shell supplies only its own frame - page heading and left rail on the desktop, back link and app bar on the phone - and its own layout tuning: the phone re-tunes the shared `settings-*` CSS for touch under a `.screen` ancestor, so LAYOUT differs and CONTENT cannot. A route that exists on one surface only (the account page, the Transcription Health report) is passed in as an optional href, never hard-coded, so no surface renders a dead link. **Adding a setting means adding it to the shared component - if you find yourself editing a settings card inside `apps/cockpit` or `apps/mobile`, stop: it belongs in client-core.**

---

## Naming Conventions

| Type | Convention | Example |
|------|------------|---------|
| Classes | PascalCase + suffix | `SessionManager`, `ConPtyBackend` |
| Methods | Verb + Noun | `CreateSession()`, `SendTextAsync()` |
| Private fields | _camelCase | `_sessionManager`, `_sessions` |
| Async methods | Suffix Async | `KillSessionAsync()` |
| Tests | Method_Scenario_Result | `CreateSession_InvalidPath_Throws` |

---

## Logging Format

```
FileLog.Write($"[ClassName] MethodName: context={value}, result={result}");
FileLog.Write($"[ClassName] MethodName FAILED: {ex.Message}");
```

---

## CC Director CLI Tools

**Reference:** [docs/cli-reference.md](docs/cli-reference.md)

When using any cc-* tool, check `docs/cli-reference.md` for exact flags before calling. Key gotcha: use `--count` / `-n` for result limits, NOT `--limit`.

---

## When in Doubt

1. Log more, not less
2. Fail explicitly, not silently
3. Show UI feedback immediately
4. Write a test
5. Read [docs/CodingStyle.md](docs/CodingStyle.md)

## THERE IS EXACTLY ONE WAY TO DEPLOY THE GATEWAY, THE COCKPIT AND THE MOBILE APP

**Use the `deploy-hosted-gateway` skill. Never any other way.**

All three ship in ONE container image - the Cockpit and the mobile app are built into the Gateway image
(`wwwroot/c` and `wwwroot/mobile`, see the repo-root `Dockerfile`). "Deploy the Cockpit" and "deploy
mobile" mean this same one path. There is no separate Cockpit deploy and no separate mobile deploy.

**FORBIDDEN, for every agent:**
- `az webapp config container set`, `az webapp restart`, `az webapp deployment slot swap`, or any other
  hand-rolled `az` command against `devthrottle-gw`
- deploying from the Azure Portal
- building an image and pinning it by hand

**Why this is a rule and not a preference.** The deploy workflow warms a staging slot and swaps, so the
cutover costs 0.0 seconds - measured. It refuses any ref that is not main. It refuses a commit whose
checks have FAILED. It measures the real external outage at ~0.1s resolution and fails the run if it
exceeds the budget. And it serialises against the rollback and provisioning workflows, because two
containers on one shared file share corrupted a database on 2026-07-30 and took the service down for 32
minutes. A hand-rolled deploy has none of that.

If the skill cannot do what is needed, that is a gap to FIX IN THE WORKFLOW. Say so and stop.

Rolling back is also a workflow (`rollback-hosted-gateway.yml`). Never swap by hand.

## NEVER MENTION CLAUDE ANYWHERE IN GITHUB - ABSOLUTE

**NO Claude / Claude Code / Anthropic / AI attribution EVER appears in anything
that touches GitHub, or anywhere else.**

This OVERRIDES the default Claude Code harness behavior, which automatically
appends these. Ignore that default. It is unsolicited advertising in Soren's
repos and it is not acceptable.

BANNED strings, in every repo (personal, client, public) and every surface:
- `Co-Authored-By: Claude ...` (commit message trailers)
- `Generated with [Claude Code](https://claude.com/claude-code)` (PR/issue bodies)
- The robot-emoji "Generated with" footer, anywhere
- Any mention of Claude, Claude Code, or Anthropic in commit messages, PR titles
  or bodies, issue text, review comments, code comments, changelogs, or docs

**How to apply:** Write commits and PRs as Soren. No trailer. No footer. Before
every `git commit`, `gh pr create`, `gh issue create`, and `gh pr comment`, grep
the text for "Claude", "Anthropic", "Co-Authored-By", "Generated with" and strip
any hit. If attribution reaches a commit that is not yet pushed, amend it before
it goes anywhere near GitHub.

## THE SPOKEN WORDS REACH THE AGENT VERBATIM - HARD RULE FOR EVERY TRANSCRIPTION PATH

**What the user said is what the agent gets. Only TWO changes are allowed between
the speech model's output and the agent's prompt: a deterministic, in-process,
dictionary find/replace of individual terms the user listed by hand, and the removal
of a sentence the audio does not support.**

**The removal, in full (the owner's amendment, 2026-09-10):** *a sentence may be
removed only when the audio in its own time span contains no speech-level sound;
nothing may ever be added or reworded.* It exists because whisper-large-v3 must
caption every part of a clip and cannot answer "nothing was said here", so on a silent
stretch it writes the likeliest caption from its training data - "Thank you.", "you",
"Bye." - and about one dictation in eight carried a word the speaker never said. A word
the model invents is an instruction nobody gave, so removing it SERVES this rule rather
than bending it. What may never happen is a removal the audio does not justify: see
`docs/architecture/transcription-pipeline-versions.md` for the rule and its measured
score, and the research behind it in
`docs/research/transcription/2026-09-09-unspoken-words.md`.

This binds the Gateway (`CcDirector.Gateway/Transcription`, `CcDirector.Core/Dictation`),
the Director's local dictation, the phone app, Notes, Wingman, and the proxy in
`devthrottle_internal/website/api/v1/audio.js` - every path that turns audio into text.

FORBIDDEN, in any of those paths:
- Any generative model that can reword, translate, summarise, answer, or "clean up"
  the transcript. A model that WRITES text is not a transcriber. This includes
  post-processing passes (the o4-mini cleanup removed in July 2026) AND generative
  speech-to-text models used as the transcriber itself: on 2026-08-29 the primary
  provider was `gpt-4o-transcribe`, and it emitted invented German ("Koennt ihr dem
  Aufgabschornel noch geholfen?") and rewrote whole sentences ("Why do you use it
  that strong now to send email?") for an English speaker. The Gateway's stored
  RawText matched what hit the screen and CleanupApplied was false - the damage was
  done INSIDE the speech model, where no downstream rule can catch it. The primary
  transcriber must be a non-generative acoustic model (whisper-large-v3).
- Replacing a word the user did NOT list. The fuzzy matcher did this from
  2026-08-08 to 2026-08-17 ("sure" -> "Soren" 261 times, "many" -> "Banya" 138,
  "code" -> "Codex", "single" -> "SignalR", "focus" -> "Opus") before it was made
  opt-in and off (#2597). It stays off. A judge model may only ever answer with
  candidate ids - it never supplies text - and it does not make an unlisted swap
  allowed; the user's own list does.
- Sending the model a vocabulary `prompt`, a language override, or any bias the
  user did not configure. A hint changes what the model hears.

REQUIRED:
- `dictation_transcripts.RawText` is the speech model's FULL output byte-for-byte,
  including any sentence the evidence gate removed. `CleanedText` differs from it only
  by accepted `ChangedWords`, each a hand-listed correction, and by whole sentences the
  gate dropped - never by a reword and never by an addition. A test must prove
  `Raw + ChangedWords - DroppedSentences == Cleaned`.
- A removal is only permitted when it is RECORDED. The dropped sentences travel with
  the result, so a removal is answerable by query exactly as a dictionary edit is. A
  gate that quietly shrinks a transcript, leaving the evidence only in a log file, is
  the defect fixed in devthrottle#2805 - not the design.
- The proof of compliance is the stored rows, not the code comments: diff
  RawText vs CleanedText over the last 30 days
  (see the 2026-08-29 query in devthrottle_internal `docs/incidents/`). Any edit
  whose Find is not in the tenant's Corrections list is a defect, not a feature.
- Changing the transcription provider or model is a product decision made by the
  owner in writing. Never switch to a generative model for "reliability".

**Why:** the agent acts on the words. A wrong word from the speaker is the
speaker's problem; a wrong word from us is an instruction the user never gave.
