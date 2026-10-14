# Review 2 - checks and factory skills (the F5 and F6 additions)

Reviewer: a separate review session, 14 October 2026. Read only. Nothing in this worktree was changed except this
file.

## Scope

What I read: the additions `REVIEW-BRIEF-2.md` names in `MISSION.md` - decisions F5 and F6, A2, A9, A10, sections
5.2, 5.5 step 3, 5.5a, 5.5b, 5.9, goal lines 15 and 16, phase 0(c) and (g), phase 5b and their QA lines - and the
changed `diagram-where-it-is-saved.svg` (text extracted from the SVG). Nothing else in the document was re-reviewed.

The code, at origin/main `344c31a8e`: the Director's trigger runner (`src/CcDirector.ControlApi/Triggers/`), the
Gateway's trigger service, contract, definition and store (`src/CcDirector.Gateway/Factory/Triggers/`), the trigger
section of `docs/cli-reference.md` (lines 2078-2137), the cron fire path (`CronEngine`, `CronTenantSweep`,
`DirectorCronSessionStarter`, the sweep timer in `GatewayHost`), the Director command channel
(`DirectorCommandMessages`, `DirectorCommandRouter`, `SessionCommandExecutor`, the hello capabilities in
`DirectorStreamMessages`), the Director's `cc-secrets` runner and the `cc-secrets` tool's `run` command
(`tools/cc-secrets/src/cli.py`, `runner.py`, `store.py`, `redact.py`), the skills code (`src/CcDirector.Core/Skills/`),
`InheritedSessionEnvironment`, `FactoryMemoryFiles`, `CcStorage`, `ClaudeArgBuilder`, the problems fold, and the
in-repository agent reference (`.claude/skills/agent-expert/agents/claude-code.md`, `codex.md`).

This worktree is at `cc3cbdfcc`, three commits behind origin/main. I read every file above from origin/main with
`git show origin/main:<path>`. The three newer commits touch licenses, the tools updater and the Python installer
only (`git diff --name-only cc3cbdfcc origin/main`); none of the files cited below changed, so every line number
holds at both commits.

What I ran: searches over origin/main. No tests, no live Gateway, no check script.

What I could not reach: whether Claude Code or Codex can be pointed at a per-session skills folder (phase 0(c) is the
proof; I report what the repository already documents about each); the hosted Gateway's actual sweep duration.

## Findings

Ordered by how hard each would bite the build.

### F1. The `state` and `context` keys cannot pass through today's runner: the Director cuts a check's output at 8,192 characters before it travels (section 5.5a "The contract", A9)

Evidence:

- The Director caps what a check printed before it is sent: `OutputCap = 8192`, "the contract needs a small JSON
  object; the rest is only ever quoted in a failure reason" (`src/CcDirector.ControlApi/Triggers/DirectorTriggerRunner.cs:34-36`),
  applied to standard output and standard error (`:172-173, 182-186`).
- The Gateway parses the WHOLE capped output as one JSON object (`src/CcDirector.Gateway/Factory/Triggers/TriggerCheckContract.cs:51-63`);
  anything that does not parse is "the output is not JSON" and the check is `failed`.
- The design keeps "today's plus three optional keys" (5.5a) with `context` "at most 8 KB" and `state` "at most 64 KB",
  and A9 says "the same no-model runner on the Director and the same contract".

Harm: a check whose `count`, `context` and `state` together pass 8,192 characters is cut mid-string on the Director,
arrives as broken JSON, and is recorded `failed - the output is not JSON`. A check that remembers the last message
it saw starts small and grows, so it passes the phase 5b QA and fails weeks later in production, as a factory
problem that names nothing wrong with the factory. The 64 KB and 8 KB limits as written are unreachable by eight
times.

What must change: say where the cap moves. Either the Director's cap is raised to hold the three keys (at least the
sum of the three limits plus the JSON around them, and the Gateway route's body limit with it), or `state` leaves the
standard-output contract - the check writes it to the file named in `CC_CHECK_STATE_FILE` and the Director sends the
file - and the output contract stays small. Either way `OutputCap` is Director code, which phase 5b already is.

### F2. A timed start's check run inside the cron fire holds every other schedule of every account for up to five minutes (section 5.5a "On a schedule", phase 5b)

Evidence:

- The sweep fires due jobs one after another, awaiting each: `foreach (var job in due) { ... await FireAsync(...) }`
  (`src/CcDirector.Gateway/Running/CronEngine.cs:139-169`), and `FireAsync` awaits the starter
  (`:243`). On the hosted Gateway the sweep runs every account in turn (`CronTenantSweep.cs:29-37`).
- A tick is skipped while the previous sweep is still running (`src/CcDirector.Gateway/GatewayHost.cs:6615-6622`), the
  guard is released only when the whole fan-out is done (`:6625-6634`), and the tick is one minute (`:1041, 6118`).
- A check may run five minutes (5.5a: "a timeout of half the interval (at most five minutes)"; `TriggerDefinition.cs:78-81`).
- A window schedule reached after its deadline is not started: it is recorded "did not run" and skipped
  (`CronEngine.cs:150-161`).
- The command channel's default wait is 30 seconds (`src/CcDirector.Gateway/Api/DirectorCommandRouter.cs:35`); its
  own rule is that any override must be strictly longer than the inner bound it sits outside, or the backstop masks
  the real message (`:43-52`).

Harm: "the Gateway asks the seat's machine to run the check first" inside `StartRunAsync` means one slow or hung
check (five minutes is the design's own ceiling) stops every other schedule in the account - and on the hosted
Gateway every other account's - from firing for that long. Schedules fire late and are labelled catch-up; a window
schedule crosses its deadline and is recorded as a run that did not run, which is a factory problem on a factory that
did nothing wrong. The design also gives a timed check no timeout at all: "half the interval" has no interval on a
`cron:` start.

What must change: the check runs OFF the sweep, the way the trigger service already runs a start - take the hold,
record the run as checking, answer at once, finish on the Gateway's own lifetime
(`src/CcDirector.Gateway/Factory/Triggers/TriggerService.cs:242-253, 319-323, 391-447`). Then say what holds the
one-at-a-time rule for that run, because the engine's in-flight guard is released when `FireAsync` returns
(`CronEngine.cs:320-323`) and would no longer cover the check (see F6). Give a timed check a fixed timeout, and make
the command-channel wait, if the channel is kept (F3), strictly longer than it.

### F3. "The Director ... exactly as today ... one runner, one contract, for both" describes two runners, and the second has a failure the first does not (section 5.5a "Who runs it", "On a schedule")

Evidence:

- Today the Director only RUNS and REPORTS: "It decides NOTHING from the result: the Gateway reads it under the check
  contract and decides whether a session starts" (`DirectorTriggerRunner.cs:13-18`; `TriggerCheckContract.cs:23-24`;
  `TriggerService.cs:58-70`). The Director learns its checks by an HTTP PULL every 30 seconds
  (`DirectorTriggerRunner.cs:20-24, 32, 92-124`) and reports by an HTTP POST (`ITriggerGateway.cs:29-35`;
  `cli-reference.md:2135-2137`).
- The design's timed path is a PUSH down the command channel with a correlated reply. That channel returns null when
  the Director is not tunnel-connected (`DirectorCommandRouter.cs:59-68`), and its two Gateway-made outcomes both say
  "It is not known whether the command was carried out" (`:133-141, 151-154, 161-164`).
- The machine is resolved to "the first reachable Director" and one may be LAUNCHED for the purpose
  (`src/CcDirector.Gateway/Running/IDirectorTargetResolver.cs:22, 147, 210`; `MachineSessionSpawner.cs:11, 70`). The
  trigger path avoids a second resolution by pinning `Director = directorId` on the start request
  (`TriggerService.cs:471-473`).
- The Director's verb table takes session-less verbs today (`SecretTransferExecutor`, `LicenseSwitchExecutor` in
  `src/CcDirector.ControlApi/SessionCommandExecutor.cs:100-130`; unknown verbs refused at `:171-179`), so a new verb is
  buildable; none of the existing verbs runs a process for minutes, and the three that run a model get a three-minute
  backstop as the stated exception (`DirectorCommandRouter.cs:37-54`).

Harm: on the interval path a check whose report is lost is simply run again at the next poll; on the pushed path a
check that RAN, found work, and whose reply was lost (timeout, tunnel drop) is recorded `failed` with its outcome
unknown, its `state` is lost, and no session starts - a quiet miss that reads as a red check. A Director that was
offline at the due minute fails the fire outright, where the pull path runs the check within 30 seconds of the
Director coming back. And if the check runs on one Director and the start resolves to another, `check.json` written
on the first Director's disk is not where the second one builds the kit.

What must change: either state the two transports and their different failure shapes (and pin the start to the
Director that ran the check, as the trigger path does), or make "one runner" true: the cron fire of a timed start
with a check writes a due-check row that `GET /directors/{id}/triggers` hands the Director once, the Director runs
and reports through the POST it already uses, and the Gateway decides and starts - the same code for both starts,
the sweep never blocked (which also answers F2). Also reword "Who runs it": the Director runs the check; the Gateway
reads the contract and decides (rule 7). The new `context` and `state` keys are read by the Gateway, not the Director.

### F4. "A broken check is a factory problem" names a place a failed check does not reach today, and the design adds no work to take it there (goal 15, 5.5a, phase 5b QA)

Evidence:

- The factory's open problems are folded from cron RUN rows only: `CronRunResultFold.Problems(runsByJob ...)`
  (`src/CcDirector.Gateway/Running/CronRunResultFold.cs:99-100`); the page's problems card takes that answer
  (`src/CcDirector.Gateway/Factory/FactoryProblemsFold.cs:8-24`); the 08:00 email reads
  `_cronRunResults.ProblemsAndFactoryDays` (`GatewayHost.cs:2556`). No file under `Factory/Email/` and neither fold
  mentions a trigger (`git grep -i trigger` over them: nothing).
- A failed trigger check today is a RED status word on the trigger (`TriggerStatusFold.cs:9-18`) and one activity row
  (`TriggerService.cs:71-75, 511-541`). That is where it stops.
- On the timed path the only shape today for "a fire that started nothing" is `Result = problem, Problem = did-not-run`
  (`CronEngine.cs:266-272`; `CronRunRecord.cs:113-128, 130-160`), so a check that correctly finds NOTHING would be
  recorded as a problem unless a new result value is added and the problems fold and `_onResultRecorded`
  (`CronEngine.cs:304-306`) are taught about it.
- Phase 5b's row lists download, Python, secrets, the two keys, `factory_check_state`, the timed start and the
  interpreter question. Nothing about the problems fold or the email.

Harm: the goal line and the QA line ("recorded as failed with its reason and raised as a factory problem") will be
proven against the trigger's run row and the activity row and pass, while the morning email and the Open problems
card - the two places the owner actually reads - never carry a broken check. The design's own sentence, "never
silence", is exactly the gap. And without the new result value the timed path's `nothing-to-do` is a "did not run"
problem every quiet morning.

What must change: phase 5b names the wiring - a failed check on either path writes a problem the problems fold
reads, and a `nothing-to-do` result value that is neither pending nor a problem - or goal 15 and the QA line say
"recorded, and shown red on the seat" and drop "factory problem".

### F5. Two things the design says about `cc-secrets run` are not what the tool does (section 5.5a "Secrets", 5.5 step 5, A6)

Evidence:

- The variable a secret lands in depends on how many are supplied. ONE entry: `--env-name`, else the entry's own
  configured variable, else `CC_SECRET` (`tools/cc-secrets/src/cli.py:689-690`). SEVERAL: each entry's configured
  variable, else its name upper-cased with dashes and dots made underscores (`:691-692`; `runner.py:116-132`).
- "Exists" is not "may be run": `entry_for_agent(name, "run")` refuses an entry whose uses do not include `run`
  with "Secret 'x' is not allowed for 'run'" and a different exit code from "No secret named 'x' is on this machine"
  (`tools/cc-secrets/src/store.py:327-336`; `cli.py:678-682`).
- The design: "the Director runs the check under `cc-secrets run` with those names, so the values are in the check's
  environment" (5.5a), and the publish-time and start-time question is whether each name "exists in cc-secrets on
  that machine" (5.5 step 5; A6).

Harm: the email check written in phase 0(g) with one secret reads `CC_SECRET`; the same check with two declared
secrets reads `OUTREACH_IMAP_PASSWORD`, or the reverse - a check breaks when a second secret is added or the first is
removed, with "missing key" from Python rather than a named reason. A secret that exists but is marked for `login`
only passes both the publish and the start question and fails at the first run as a factory problem the checks
claimed to have ruled out.

What must change: one variable-name rule in the design (the entry name upper-cased, dashes to underscores, as the
machine's own convention has it), with the Director passing `--env-name` when there is exactly one; and the secret
question asks "exists and is allowed for run". The `cc-secrets` output scrub is fine for this use: it replaces only
forms of the secret itself, never the username alone (`redact.py:108-140`), so a well-behaved check's JSON survives,
and a check that prints its secret is withheld whole (`redact.py:48-51`) and fails as not JSON, which is right.

### F6. "A check never runs twice at once, so there is one writer" is true today on the interval path and conditional on the timed path (section 5.5a "The contract", `factory_check_state`)

Evidence:

- Interval path: a trigger whose check is running is not started again by the same Director
  (`DirectorTriggerRunner.cs:43-45, 119, 141-145`); a trigger is CLAIMED by one Director per machine and a report from
  another is refused unrecorded (`TriggerStore.cs:211-229, 233-238`; `TriggerService.cs:277-280`); a claim lapses after
  two intervals plus a minute (`TriggerStore.cs:241`), and a check's timeout is at most half an interval
  (`TriggerDefinition.cs:78-81`), so a running check cannot outlive the claim it runs under. One writer, by
  construction.
- Timed path: the only hold is `PreventOverlap`, held for the duration of `FireAsync` (`CronEngine.cs:216-219,
  320-323`). It defaults to true (`src/CcDirector.Gateway.Contracts/CronJobDto.cs:83`) but it is a per-schedule
  setting the publish writes, and if F2 moves the check off the fire this hold no longer covers it.
- The state row is "one row per seat and start", so an `every:` start and a `cron:` start on one seat never share a
  row. Good.

Harm: a published timed start whose schedule has `PreventOverlap` off, or whose check runs off the sweep after F2,
has two writers of one state row the moment a check runs past its interval. Low, because today's defaults are right,
but the design claims a property and does not say what makes it hold.

What must change: say that a published start with a check always writes `PreventOverlap` on (and a publish that would
turn it off is refused), and name the hold that covers an off-sweep check - the trigger service's started-session
lock is the pattern.

### F7. Goal lines 15 and 16 each have a phase and QA lines, with five cases the QA does not reach (section 7)

Evidence: goal 15 is delivered by phase 5b (section 6) and goal 16 by phase 5's last clause ("factory skills in the
kit, visible to that session only"); the QA lines are at 438-445 of `MISSION.md`. Missing from them:

- A timed start whose check finds WORK (only "a timed start whose check finds nothing" is listed), so the whole timed
  path to a running session is unproven.
- "A start with an interval MUST have a check" (5.5a) - no refusal is proven at publish.
- "Given only the secret names it declares" (goal 15) - no case shows a check cannot read a secret it did not declare.
- "A factory skill and a fleet skill with the same name: the definition check refuses it" (5.5b) - phase 1's
  "dangling reference refused" is a different check.
- "A seat on that agent that names a factory skill is refused at publish, with the reason" (5.5b) - no case.

Harm: section 7 says every goal is "shown in a QA report with the flow AND the failure cases"; these are the failure
cases, and two of them (the same-name refusal and the undeclared secret) are the ones that bite silently.

What must change: five QA lines.

### F8. Wording: "a timeout of half the interval (at most five minutes)" is also at least thirty seconds (section 5.5a "What exists today")

`TimeoutSecondsFor` is `Math.Min(300, Math.Max(30, intervalSeconds / 2))` (`TriggerDefinition.cs:78-81`). A one-minute
trigger gets thirty seconds, not thirty. No harm beyond the number; say "between thirty seconds and five minutes".

## Checked and found sound

- **The claims about today's trigger checks (brief item 1).** A trigger is "a check with no model in it that a
  Director runs on an interval" (`cli-reference.md:2080-2082`). The Director runs the command in the trigger's
  repository folder through the shell with a timeout, and a missing folder is a check that could not start
  (`DirectorTriggerRunner.cs:148-180`; `ProcessJob.cs:106-126`). Exit 0 plus `{"count": N}` is the contract, N above
  0 starts one session with `{count}` replaced (`TriggerCheckContract.cs:14-25, 31-80`; `TriggerDefinition.cs:73-76`;
  `TriggerService.cs:448-486`). Every check is one of `nothing-to-do`, `started`, `paused`, `skipped-running`,
  `failed` (`TriggerDtos.cs:8-28`) and one activity row (`TriggerService.cs:71-75`; `cli-reference.md:2118-2122`).
  What it cannot do is as stated: the script must be in a folder on the machine (`RepoPath`, checked at
  `DirectorTriggerRunner.cs:156-161`); it gets no environment beyond the Director's (`ProcessJob` is started with
  the command only, `:165`); only `count` is read; nothing is kept between runs; and a cron job carries no check
  (`CronJobDto` action fields, as REVIEW-1 recorded). The agent is pinned to Claude Code on both paths
  (`TriggerService.cs:467`; `DirectorCronSessionStarter.cs:87`).
- **The command channel can carry a new Director verb and gate it on a hello capability (brief item 2, the buildable
  half).** A verb with no session is ordinary (`SessionCommandExecutor.cs:100-130`), an unknown verb fails loud
  (`:171-179`), and the hello already carries per-feature booleans the Gateway reads before sending
  (`CreatesFleetManagerHome`, `ChangesOwner`, `ChangesOwnerIfExpected`, `DirectorStreamMessages.cs:48-70`). The
  publish-time interpreter and secret questions of 5.5a and 5.5 step 5 are the same shape. The Director already runs
  `cc-secrets` as a process from a verb handler (`src/CcDirector.ControlApi/CcSecretsRunner.cs:14-38`, used by the
  secret transfer and license verbs), so "the Director runs the check under cc-secrets run" has a worked example.
- **The download folder is per Director, not per machine.** `CcStorage.Root()` is this Director's own instance
  folder (`src/CcDirector.Core/Storage/CcStorage.cs:28, 43-60`), so `<storage>/factory-checks/<factory>/<version>/`
  on a machine with several Directors is one folder per Director and two Directors never write one folder. The
  factory-memory folder the design copies is `Root()/factory-memory/<session id>/` (`CcStorage.cs:554-558`;
  `FactoryMemoryFiles.cs:53-54`); "same rule and same code path" (5.5 step 2) still holds for the kit.
- **The skills code does not make a per-session factory skill harder than the document says (brief item 4).** Fleet
  skills are materialized once under the user's home and linked per agent, never into a repository
  (`src/CcDirector.Core/Skills/SkillInstallTargets.cs:25-40, 60-70`), and the installer itself states the
  consequence the design is avoiding: "an installed skill is visible to every session on this machine"
  (`:36-40`). Placement replaces or removes only folders carrying its own marker and source, in the two shared roots
  and its own store (`SkillDirectoryInstaller.cs:487-496`; `SkillStoreRefresh.cs:157-168`), and its safety check
  for staging looks only at folders agents read skills from (`SkillDirectoryInstaller.cs:733-765, 774-803`). A folder
  under `<storage>/factory-run/<session id>/skills/` is none of those: nothing reconciles it, nothing deletes it,
  nothing stages beside it. 5.5 step 3's "a factory skill is NOT installed there, because that folder is shared by
  every session" is the right reading of the code. What the Director does NOT have is any way to point ONE session at
  an extra skills folder: `ClaudeArgBuilder` composes `--add-dir` and `--settings` and nothing else about skills
  (`src/CcDirector.Core/Claude/ClaudeArgBuilder.cs:130, 138`). The repository's own agent reference says `--add-dir`
  does not load the added folder's `.claude` configuration (`agent-expert/agents/claude-code.md:59`) and that
  `--plugin-dir <path>` loads a plugin - skills included - for one session (`:148`); for Codex it names only
  `CODEX_HOME` as the configurable location (`codex.md:28, 99`), which moves the whole home, sign-in included. So
  phase 0(c) is the right place for the answer, and the design already says what happens when an agent has none
  (refused at publish). Two things 0(c) should write down when it runs: the name a plugin-loaded skill gets inside
  the session (a plugin namespaces its skills), because the brief has to name the skill the way the agent sees it;
  and, if Codex is answered with a per-session `CODEX_HOME`, that the session also sees the user's configuration
  and sign-in, since `InheritedSessionEnvironment` deliberately keeps `CODEX_HOME` as configuration
  (`src/CcDirector.Core/Sessions/InheritedSessionEnvironment.cs:33-42`). Children do not inherit a parent SESSION's
  environment - the Director stamps each child from its own environment (`:3-11, 62-80`) - so "children of the seat
  do not get its skills" (5.5b) is consistent with a per-session variable or flag.
- **A2 and F5 against the approved design.** The override is named and bounded (section 4 of the mission): skills
  only, fleet skills still named, workflows unchanged. 5.9 says what moved and why. No contradiction left unstated.
- **A10.** Nothing in the code gives a check a way to act; it is a process with an exit code and standard output, read
  for a count. The rule is a design rule, correctly placed as one.
- **The diagram matches the text.** `checks/*.py` under the definition; `check state` under records with "Records
  are never part of a definition version"; `<storage>/factory-checks/<factory>/<version>/` on the machine with
  "Prints {count, context, state}" and "Count 0: no agent, recorded as nothing to do"; `skills/` in the kit "seen by
  this session only"; `check.json` in the kit. Every name on the diagram is a name in 5.2, 5.5 step 3 or 5.5a.
- **Phase 0(g) is the right first proof.** Running the email check by hand under `cc-secrets run` is exactly what
  finds F5's variable-name rule before phase 5b builds on it - provided it is run once with one secret and once with
  two.
- **The interpreter rule.** "A machine with no Python, or a check that declares packages on a machine with no uv, is
  a failed check with that reason" is a start error in today's shape (`TriggerCheckReport.StartError`,
  `TriggerDtos.cs:184-185`; read at `TriggerCheckContract.cs:35-36`), so it is recorded and red without new contract
  work.
