# Review 1 - Factory Definition mission document

Reviewer: a separate review session, 14 October 2026. Read only. Nothing in this worktree was changed except this
file.

## Scope

What I read: `MISSION.md`, both diagrams (text extracted from the SVG), `REVIEW-BRIEF.md`; the code named in the
brief at origin/main `cc3cbdfcc` (this worktree is at that commit, confirmed with `git ls-remote`); the approved
Teams design (`devthrottle_internal/docs/missions/teams-and-agents-2026-09-19/DESIGN.md`, rulings R7, R9, R13, R20
and section 5.2); Factory Seats (`docs/missions/factory-seats-2026-10-13/MISSION.md`, P3 and P5);
`D:/ReposFred/devthrottle-factory/factory/AUTONOMY.md`; the command line under `tools/cc-devthrottle/src`; the
Cockpit and phone shells under `apps/`.

What I ran: searches over the tree; a measurement of `%LOCALAPPDATA%\cc-factory`; one collection-only run of the
section 7 Python line from the repository root (exit 0; it took longer than two minutes to collect).

What I could not reach: whether the hosted Gateway deploy of Factory Seats 2c happened (merge confirmed, deploy
not); how Claude Code behaves with two `--settings` flags on one line (not run); Codex's rules surface (phase 0
work).

Every line number below is at commit `cc3cbdfcc`.

## Findings

Ordered by how hard each would bite the build.

### F1. The pooled worktree a repository seat "starts in" is switched by a per-machine setting the definition cannot see (section 5.5 step 6, goals 8 and 10)

Evidence:

- `NewSessionRequest.PooledWorktree`, which the text cites, is the handle a Director sends back when it RESTORES a
  seat into a slot it already holds: "Null on every ordinary create, and the ordinary create is the only one that
  may take a slot ... it does not even read the repository's setting"
  (`src/CcDirector.Gateway.Contracts/NewSessionRequest.cs:11-26`).
- Whether an ordinary create takes a slot is decided by the repository's pooled-worktree setting on THAT machine,
  read from the Director's `config.json`, default OFF (`src/CcDirector.Core/Git/WorktreePoolSettings.cs:43-51`;
  `src/CcDirector.Core/Sessions/SessionManager.cs:1270-1296`). It is turned on by hand with
  `cc-devthrottle worktree pool on` (`tools/cc-devthrottle/src/worktree_pool_settings.py:1-15`).
- When the setting is off the session's working directory is `RepoPath` itself
  (`SessionManager.cs:722-725`): the shared checkout.

Harm: a seat whose definition names a repository starts in the shared checkout on every machine where nobody has
turned the pool on for that repository, and nothing refuses or reports it. Goal 10 ("nothing of the factory is
written inside a repository checkout") and goal 8 ("a machine that has never seen the factory's folder") fail
exactly on the machine goal 8 is about, while the phase 5 QA passes on the owner's machine where the pool is on.

What must change: the design has to say which of two mechanisms it builds. Either a field on the start request
that says "this seat's definition declares a repository; take a pooled slot regardless of the machine setting",
honoured by the Director with the tool's own pool-full refusal unchanged (and gated by a hello capability, as
`FleetManagerHome` is at `NewSessionRequest.cs:52-60`), or a publish-time check of the seat's machine setting
that refuses the publish. Also: a repository is named "by known repository name" (5.2), but the Director's
repository registry is per machine (`RepositoryDto` at `NewSessionRequest.cs:321-326`); say how a name resolves
to a path on the seat's machine and what happens when it is not registered there.

### F2. A child session inherits a factory, and nothing else - there is no seat or version to download a kit from (section 5.5 steps 1, 2 and 4; goal 9)

Evidence:

- `briefVersion` today is stamped on the cron RUN row (`src/CcDirector.Gateway/Data/Entities/CronRunEntity.cs:87`)
  and on a talk (`src/CcDirector.Gateway/Api/FactoryTalkEndpoints.cs:223`). It is not on the session.
- The session carries `Factory` and nothing about a seat or a version (`src/CcDirector.Core/Sessions/Session.cs:317`;
  `src/CcDirector.Gateway.Contracts/SessionDto.cs:836`; `NewSessionRequest.cs:203`).
- Inheritance reads ONE column of the caller's history row, its factory, and stamps it on the child
  (`src/CcDirector.Gateway/Api/SpawnFactory.cs:136-169`). A seat is on the cron job and the run record
  (`CronJobDto.cs:71`, `CronRunRecord.cs:89`), never on a session.

Harm: step 2 downloads "the seat's kit from that version", but a child spawned by a seat (the review session goal
9 names) has a factory and no seat and no version, so the Director has nothing to download and the rules do not
reach it. "They inherit the definition version the same way" describes a path that does not exist; phase 5's QA
line "a child review session refused the same" cannot be built from what is written.

What must change: add seat and definition version to the start request, the session, the history row and the
settle step in `SpawnFactory`, stamped from the caller's credential exactly as the factory is; and decide what a
child gets: its parent's seat kit (brief, grants, files and all), or a factory-level kit with the rules only.
Say which.

### F3. "One source of truth" refuses commands that do not exist and leaves the real writers open (section 5.3, 5.9, goal 4)

Evidence:

- The schedule subcommands are list, get, runs, plan, load, create, link, unlink, run, enable, disable, delete,
  endpoint (`tools/cc-devthrottle/src/cli.py:3913-4111`). There is no `schedule edit`. `schedule load` shows the
  machines' load forecast (`cli.py:3962-3968`).
- The trigger subcommands are add, list, show, pause, resume, runs (`cli.py:4490-4538`). There is no `trigger edit`.
- The write surface is the Gateway: `POST /cron/jobs`, `PUT /cron/jobs/{id}`, `DELETE /cron/jobs/{id}`,
  `POST /cron/jobs/{id}/unlink` (and link); `POST /triggers`, `PUT /triggers/{id}`, `DELETE /triggers/{id}`;
  the registry's register route. The Cockpit's `ScheduleEditor.tsx` and the Factory Seats tab (commit `742e222a3`)
  write through them.

Harm: a refusal keyed to the named commands leaves `schedule create`, `schedule delete`, `schedule link` and
`unlink`, `trigger add`, `factory register`, and every `PUT` from the editor as second writers of a defined
factory's parts, which is the drift the section exists to remove.

What must change: name the routes, refuse there (rule 7: the Gateway rules; the command line and the Cockpit
render the sentence), and list what stays allowed: cron enable and disable, trigger pause and resume, run-now.
Decide `link` and `unlink`: a schedule's seat is part of how a seat starts, so on a defined factory it is a
definition edit.

### F4. The phase 4 publish-time secret check needs Director code that only the phase 5 release ships (section 5.5 step 5, A6, phase table)

Evidence:

- "At publish the Gateway asks the seat's machine" needs a request down a Director's stream with an answer. The
  channel exists (`src/CcDirector.Gateway.Contracts/DirectorCommandMessages.cs:1-12`: a verb the Gateway sends
  down the stream, answered over the same connection), and a `cc-secrets` runner exists in the Director
  (`src/CcDirector.ControlApi/CcSecretsRunner.cs`), but the verb that answers "do these names exist" is new
  Director code.
- Phase 4 is Gateway work "after 1"; Director code reaches a machine only through the release the plan puts after
  phase 5 (section 6, phase 5 row; question 3).

Harm: phase 4 cannot show its publish-time check against a real machine, and a Director that predates the verb
cannot answer it, so a publish either waits on a reply that never comes or treats silence as "confirmable". The
design is right that the start-time check is the one that binds; the sequencing is what is wrong.

What must change: put the Director half of the check in phase 5 and let phase 4's publish report "the machine
cannot answer yet" as the finding the person confirms past; and gate the verb on a hello capability so an older
Director is reported, not waited on (the pattern at `NewSessionRequest.cs:57-59`).

### F5. A seat with no repository cannot be scheduled or started as written (section 5.5 step 6, goal 10)

Evidence:

- A schedule is refused without a repository path: `if (job.Action is null ||
  string.IsNullOrWhiteSpace(job.Action.RepoPath))` (`src/CcDirector.Gateway/CronSchedule.cs:54`).
- `NewSessionRequest.RepoPath` is "Absolute path to the repository / working directory" (`NewSessionRequest.cs:9`).
- The kit's `work/` folder lives under `<storage>/factory-run/<session id>/` (5.5 step 2); the session id does not
  exist before the create, so no caller can put that path in a schedule or a request.
- The product already solved this once: `FleetManagerHome` is a flag the Director resolves to a folder of its own,
  ignoring `RepoPath`, sent only to a Director that said it understands it (`NewSessionRequest.cs:52-60`).

Harm: phase 4's publish cannot write a schedule for a no-repository seat without inventing a path, and phase 5
cannot start one; goal 10's second sentence has no mechanism.

What must change: say that a no-repository seat's schedules, triggers and start requests carry a flag the Director
resolves to the kit's `work/` folder, that validation accepts an empty path only when the flag is set, and that
the flag is capability-gated like `FleetManagerHome`.

### F6. Phase 0(a) proves the rule mechanism under the wrong launch line (section 6 phase 0, A4, 5.5 step 4)

Evidence:

- The Director already passes `--settings` to every Claude Code session, carrying the session-pointer hooks file
  (`src/CcDirector.Core/Sessions/SessionManager.cs:978-990`; `src/CcDirector.Core/Claude/ClaudeHookInstaller.cs:32-33`,
  which relies on `--settings` MERGING with the user's own hooks).
- The default launch adds the permission-bypass flag (`NewSessionRequest.cs:66-74`).
- Phase 0(a) as written proves one `--settings` file against the folder's `.claude/settings.json`. It does not
  prove two `--settings` flags on one line, and it does not say the proof runs with the bypass flag.

Harm: a proof under a different invocation certifies nothing about the one the Director uses. The kit's rules
could pass phase 0 and not bind in phase 5, which is the one failure the design says it cannot survive ("if (a)
fails, stop and report").

What must change: phase 0(a) runs the Director's real line - hooks settings file, kit settings file, bypass flag -
or the design merges the kit's rules into the one settings file the Director already passes and the proof covers
that merge. Say which.

### F7. The phone has no factory page, so "desktop and phone" in phase 6 is unsized (sections 5.7 and 6)

Evidence: the factory page with its tabs exists only in the Cockpit (`apps/cockpit/src/factory/FactoryView.tsx:28-29`:
Overview, Seats, Activity, Reports, Memory, Documents). Under `apps/mobile/src` there is no file that mentions
factories at all.

Harm: phase 6 as sized ("Definition and Records tabs") either hides building the phone's whole factory surface or
the goal line overstates what is delivered. Goal 1 says "the Cockpit"; 5.7 and phase 6 say "desktop and phone".

What must change: say desktop only, or make the phone's factory page its own phase with its own QA line.

### F8. Diagram 2 puts the records inside the definition (section 5.1, "After this mission")

Evidence: the "After" box reads "Gateway: the definition - everything in 1, 2, 3 and 6, one version number,
published as a whole, restorable". Box 1 is "registry, goal number, activity, run results, memory, schedules,
triggers, briefs". Activity, run results, memory and goal numbers are what the factory PRODUCES, and 5.2 says "Not
in a definition, ever: anything the factory produced".

Harm: the diagram contradicts the text on the one line that defines what a version is; a Developer reading the
picture versions the records.

What must change: "the definition: 2, 3, 6 and the definition-shaped part of 1 (registry, schedules, triggers,
briefs)"; the records box already says "everything in 4, beside activity and memory", so the activity and memory
of box 1 belong there in words too.

### F9. Diagram 1 and the text name the same things differently (section 5.1 against 5.2, 5.5, 5.6)

- The kit's rules file is `rules.json` on the diagram and "the seat's rules merged into one settings file" in
  5.5 step 3.
- Write-back is "cc-devthrottle factory journal | doc | memory" on the diagram and `factory journal`, `factory
  doc`, `factory attach` in 5.6 (memory is not a records write; attach is missing).
- `factory.yaml` holds "name" on the diagram and "id, title" in 5.2.

Harm: three names for the Delivery Lead to reconcile before the schema task starts. Pick one each.

### F10. The existing Documents tab is not mentioned (section 5.7)

Evidence: the factory page has a Documents tab whose only content is the sentence "A factory's documents - its
briefs, its goal file, its rules - live in its folder on its computer, not on the Gateway yet ... They will appear
here when the definitions move onto the Gateway" (`src/CcDirector.Gateway/Factory/FactoriesScreenFold.cs:94-96`;
`apps/cockpit/src/factory/FactoryView.tsx:138`). 5.7 adds Definition and Records and does not say what becomes of
Documents, which promises this mission's content.

What must change: one line - the Definition tab replaces Documents.

### F11. A model carried "in its arguments" replaces the Director's whole launch line (section 5.5 step 7, goal 11)

Evidence: when `Args` is supplied "that explicit line wins" and the bypass choice is ignored
(`NewSessionRequest.cs:65-74`); the model flag differs per agent and the composition of the default line lives on
the Director. A Gateway that writes `Args` from the definition must know every agent's flag syntax and remember the
bypass flag.

What must change: a `Model` field on the request beside `Agent`, composed by the Director per agent kind; `Args`
stays the person's override.

### F12. Wording: the agent is pinned, not defaulted (section 2, diagram 2 box 6)

Both starters hard-code the agent: `Agent = "ClaudeCode"` (`src/CcDirector.Gateway/Running/DirectorCronSessionStarter.cs:87`;
`src/CcDirector.Gateway/Factory/Triggers/TriggerService.cs:467`). The MODEL is the machine default (no `Args`).
The conclusion - neither is in any saved factory part - stands. No harm; say "the agent is always Claude Code and
the model is whatever the machine defaults to".

## Checked and found sound

- **`FactoryBriefVersionEntity`** is as described: append-only, (tenant, factory, seat, version) as the
  concurrency control across replicas, text never edited, the publish mark set once, restore adds a copy
  (`src/CcDirector.Gateway/Data/Entities/FactoryBriefVersionEntity.cs:9-20, 22-61`). The store refuses a stale
  write with the newest version attached (`FactoryBriefStore.cs:342-345`), refuses a session's publish with the
  held sentence (`FactoryBriefStore.cs:75-77, 207-210`), and bounds a session's waiting drafts (`:60, :182-183`).
  "The sentence briefs already use" in 5.3 exists.
- **`FactoryMemoryFiles` and the download-before-start path**: the Director downloads before the agent starts,
  writes under storage keyed by session id and never inside a checkout or pooled worktree, hands the path in an
  environment variable, ends the create when the download or the write fails, and deletes the folder when the
  session ends (`src/CcDirector.Core/Sessions/FactoryMemoryFiles.cs:9-24`; `SessionManager.cs:800-802, 823,
  1202-1237, 1191, 1658`). "Same rule and same code path" (5.5 step 2) is a fair description.
- **`InheritedSessionEnvironment`** strips `CC_FACTORY_MEMORY_DIR` so a session in no factory never inherits it
  (`src/CcDirector.Core/Sessions/InheritedSessionEnvironment.cs:12-21, 48-51`); adding `CC_FACTORY_KIT_DIR` to that
  list is the same shape. The test file is in `Core.Tests`, as section 7 says.
- **`NewSessionRequest.Factory`** is settled from the credential at the spawn doors and refused when a session names
  another factory (`NewSessionRequest.cs:177-203`; `SpawnFactory.cs:153-169`); children do inherit the FACTORY.
  **`.Agent`** exists with the supported list (`:45-50`); "an agent is one we support" (5.3) has something to
  check against.
- **`CcWorktreesPool`** runs the `cc-worktrees` tool and never falls back on a refusal
  (`src/CcDirector.Core/Git/CcWorktreesPool.cs:48-77`). The claim that a seat can get a fresh worktree off
  origin/main is true where the pool is on (F1 is about the switch, not the pool).
- **`CronJobEntity` and `TriggerEntity` hold no agent and no model.** Cron: `Target` is a machine only, `Action` is
  `RepoPath`, `Seed`, `WorkListName`, `AutoDismiss` (`src/CcDirector.Gateway.Contracts/CronJobDto.cs:231-270`).
  Trigger: `FactoryAgent` is the seat's name, not a tool (`TriggerEntity.cs:16`). Pausing exists on both:
  cron `Enabled` (`CronJobEntity.cs:22`), trigger `Paused` (`TriggerEntity.cs:22`), so "pausing stays allowed"
  has something to keep.
- **The registry** is `FactoryRegistryEntity` (factory, title, folder, computer, boss seat, goal, purpose, seats as
  JSON, registered by, archived) (`src/CcDirector.Gateway/Data/Entities/FactoryRegistryEntity.cs:19-67`); unknown
  manifest keys are refused in the command line (`tools/cc-devthrottle/src/factory_registry_ops.py:35, 58-59`)
  and again on the Gateway (`src/CcDirector.Gateway/Factory/Registry/FactoryRegistryStore.cs:320-324`). "As the
  registry manifest does today" (5.2) is true on both sides.
- **One transaction over registry, schedules and triggers is possible**: all three are sets on the one
  `GatewayDbContext` (`src/CcDirector.Gateway/Data/GatewayDbContext.cs:174, 288, 387`), with SQLite and PostgreSQL
  migration twins, and `CronJobStore` keeps no memory cache (`CronJobStore.cs:16`), so a write inside one
  transaction is what the engine reads. One build note the Delivery Lead should know: the three stores share one
  `GatewayDatabase` but each call opens and saves its own context (`FactoryRegistryStore.cs:85`, `TriggerStore.cs:44`,
  `CronJobStore.cs:117`), so the publish cannot be composed by calling the stores' existing methods; it writes the
  three tables through one context of its own, or the stores grow methods that take a context.
- **The rulings are not contradicted.** R7 (`DESIGN.md:178-186`): a person drives, a factory agent never writes a
  factory agent; 5.3's "a session saves a draft, only a person publishes" is the 5.2 reading of it
  (`DESIGN.md:437-442`). R9 (`:198-203`): one YAML file per factory agent; `seats/<seat>.yaml` is that. R13
  (`:231-236`): limits can only be added; A4 and 5.5 step 4 say the same. R20 (`:323-341`): draft until published,
  versions kept, roll back; 5.3 is that. Section 5.2 (`:397-477`): skills named never contained (A2 cites it
  correctly), the boss set at start and not in the file (5.2 of the mission keeps it out), "a running session is
  stamped with the version it started from" (5.5 step 1, see F2 for the part that is not built). Two silences, not
  contradictions: the approved design lets the body and the machine be overridden when a definition starts; the
  mission fixes them per seat and does not say whether a start may override. Worth one line either way.
- **Factory Seats P3 and P5 hold** (`factory-seats-2026-10-13/MISSION.md:92-93, 96-98`): 5.3's last bullet keeps
  the brief store for an undefined factory and routes a defined factory's brief commands into the draft. Phases 1,
  2a, 2b, 2c are merged on origin/main (`7d56ee52c`, `4ebd3e9c2`, `43da876c4`, `d6879848d`).
- **Skills reach any folder.** They are materialised once under the user's home and linked per agent, never into a
  repository (`src/CcDirector.Core/Skills/SkillInstallTargets.cs:27-39`), refreshed from the Gateway's skill store
  (`SkillStoreRefresh.cs`). A2 and phase 0(c) are consistent with the code; 0(c) is still worth running.
- **The Gateway can ask a Director and get an answer**: `DirectorCommandMessages.cs:1-12` is the down-channel with
  a correlated reply. The publish-time secret check is buildable (F4 is about when).
- **AUTONOMY.md says what section 2 says it says**: rules load from the start folder before the fast-forward, so a
  merged rule binds from the run after next (`devthrottle-factory/factory/AUTONOMY.md:76-78`), and Claude Code
  loads the rules file of the folder a run starts in, not its parents' (`:101-103`).
- **The measurements match**: `%LOCALAPPDATA%\cc-factory` holds 236 files, 22,909,671 bytes; 143 images,
  20,370,061 bytes, the largest 1,082,943 bytes; `devthrottle/defects/reads/rows.json` is 641,527 bytes.
- **Section 7's commands exist and run.** `scripts/test-local.ps1`; the three test projects; `typecheck` at the
  root `package.json:12`; the `@devthrottle/client-core` workspace with a `test` script
  (`packages/client-core/package.json:2, 41`); the Python line collects from the repository root (exit 0; 22 tests
  in `test_trigger_ops.py` among the selection; 15 test files name factory, schedule or trigger). The filters
  match 63 files in `Gateway.UnitTests` and 3 in `Gateway.Tests`. `Core.Tests` has
  `InheritedSessionEnvironmentTests` and no Factory test yet, which is fine for a filter that phase 5 fills. One
  note: `FactoryMemoryAtLaunchTests` and `CcWorktreesPoolTests` live in `Core.UnitTests`, which the default gate
  runs, so they are covered by the first line. `Gateway.Tests` is parked too (host-bound); its explicit line
  covers it.
- **Issue #3950** is open with the title the document implies.
