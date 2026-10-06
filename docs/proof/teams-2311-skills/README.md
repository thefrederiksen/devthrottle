# Two Directors on one computer never remove each other's skills (devthrottle_internal#2311, live proof F7)

## The problem

The skill folders belong to the USER, not to the Director: every Director on a computer writes the same
`~/.agents/skills` (the one real copy) and `~/.claude/skills` (one link per skill). Each folder carried a marker that
said "DevThrottle installed this", and nothing about WHICH Director's library. `ReconcileCopies` and
`ReconcileLinks` deleted every marked folder their own library did not hold. So in the #2311 live proof (finding F7),
two test Directors on a Gateway serving a different skill set removed six of the person's skills, and the person's
own Directors put them back about half an hour later.

## Release order - the hosted Gateway FIRST

**The hosted Gateway must be deployed with this change before any Director release carries it.** A Director with this
change places skills only when the Gateway's skills register names the library (`source`, below). Against a Gateway
without it, every Director stops placing skills - it fails closed, changes nothing, and reports
`SourceUnknown` ("the Gateway must be updated"). Skills already on disk stay where they are; nothing is removed. A
self-hosted Gateway needs the same update before its Directors take the new release.

## The rule now

The marker records the **source** that installed the folder: the serving Gateway's stable id and the tenant on it -
the person's own personal account, or one team (`SkillSource`, new file `src/CcDirector.Core/Skills/SkillSource.cs`).
Three lines are added after the existing three (id, version, content hash), so nothing that reads the first three
changes:

```
dev-throttle
1
3f1c...
gateway-id=5b0d...        (this Gateway's own id, created once in its database)
tenant=7a41...            (the tenant the key is bound to)
account=personal          (or account=team:<team id>)
```

**Where the source comes from (review findings SK-F2, SK-F3).** The Gateway's skills register now answers, beside the
list, `source = { gatewayId, tenantId, teamId }` for the caller's own key: this Gateway's stable id, the tenant the
store answered for, and the team when that tenant is one (a team's id IS its tenant id). The store refresh records
that answer WITH the store it materialized (`.library-source.json`), and placement stamps exactly that. So the identity
on disk is the identity that fetched the library, never a local record that can be missing, and never the address the
Director used.

**Why the Gateway's id plus the tenant, and not the Director or the address.** One person's two Directors on the same
account are served the same library and must SHARE their folders; a Director id would make them fight. The tenant alone
is not enough: every self-hosted Gateway's tenant is the same constant, `local`. And the address is not an identity: a
move to TLS, a new domain or a changed address is the same Gateway, and an address-keyed stamp would strand the
person's skills there (SK-F3).

**The Gateway id** is one row in the existing `tenant_settings` table, under the reserved `system` tenant, key
`gateway_instance_id` - no schema change (`GatewayInstanceIdentity`). It is created the first time it is asked for and
never regenerated; when two processes create it at the same moment, the primary key lets one row in and the other
reads it (`TenantSettingsStore.GetOrAdd`). **A restore from backup** keeps the id - it is the same Gateway with the
same tenants. **A second deployment started from a copy of one database** carries the same id and the same tenant ids,
so Directors would see the two as one library; that is right only if the copy replaces the original. A copy meant to
run beside it must have that row deleted before it starts, and then makes its own.

**Placement fails closed (SK-F2).** With no source recorded (a Gateway older than this), or a recorded source that
disagrees with the team this Director is set up for (`gateway-team.json`; no file = the personal account), placement
touches nothing and records `SourceUnknown` or `SourceMismatch`. The second is the partial enrollment the review
found: the team-bound key and the Gateway address saved, the team file not. Before this, that Director read itself as
personal and could remove the person's own skills.

**One critical section per shared folder (SK-F1).** The whole reconciliation - every ownership read, every delete,
every replace, every link - runs under one machine-wide named mutex per folder, its name a hash of the folder's full
path (`SharedSkillFolderLock`; `Global\` on Windows, because a Director started by the Task Scheduler runs in another
logon session). Ownership is read inside the lock immediately before the change it licenses. A Director that cannot
have the lock within ten seconds changes nothing and records `FolderBusy`. A lock left by a process that died holding it
is taken over and logged; the reconciliation that follows rebuilds what it owns.

The decision, one function (`SkillDirectoryInstaller.Decide`):

| The folder at that name | A personal source | A team source |
|---|---|---|
| no marker (made by hand) | never touched, recorded as Shadowed (unchanged) | same |
| stamped by this source (same Gateway id and tenant) | refreshed; removed when withdrawn | same |
| stamped by a team | **takes it over** (re-stamps it); never removes it | the first team keeps it; this one yields |
| stamped by another personal source (another Gateway, or another person) | the first keeps it; this one yields | yields |
| old marker, or a stamp naming the Gateway by address (this change's earlier head) | counts as personal: takes it over; **never removes it** | yields; never removes it |

A yield is recorded as a placement problem, the new fault `HeldByAnotherSource`, the same way Shadowed is recorded
today. The Director's warning and the Gateway's placement page both say what happened, for all four new faults
(`SkillPlacement.Describe`, `SkillPlacementStore`).

**Links** follow the same rule. A link in `~/.claude/skills` to a shared copy is removed only when that copy is this
source's, or when the copy is already gone (a link to nothing reads as nothing to every agent, and whoever still holds
the skill makes the link again with the copy). A full copy left there by the scheme before links is judged by its own
marker.

**An old marker is never removed by anyone.** Which personal library wrote it is not recorded, so a withdrawal would
be deleting the person's skill on a guess. The cost: a skill withdrawn at the same moment as this upgrade can stay on
disk. A stale extra skill is recoverable; a deleted one is not.

**The one-time reclaim is once per folder, not once per Director (SK-F4).** The record that the retired installer's
leftovers were moved aside now lives BESIDE the folder (`~/.claude/skills.devthrottle-reclaimed`), where every Director
reads it. A Director that recorded it the old way, in its own storage, carries that over without moving anything. Not
covered: a computer whose first upgraded Director never ran the migration at all still runs it once, as before.

## Tests

First round. `src/CcDirector.Core.Tests/Skills/SkillSourceOwnershipTests.cs`, the real installer run twice over ONE pair
of folders, once per library (two of its tests were replaced in review round 1, below):

- `Two_sources_never_remove_each_others_skills_in_any_order_and_repeatedly` - the main one. Personal and team, three
  orders (personal first, team first, team twice then personal twice), after EVERY launch every skill either library
  has installed so far is still in both folders, and the shared name holds the personal copy.
- `The_personal_account_wins_a_name_even_when_the_team_installed_it_first`
- `A_withdrawal_removes_only_the_withdrawing_sources_own_skills` - a withdrawal by each.
- `Between_two_teams_the_first_installed_keeps_the_name_until_it_withdraws_it`
- `A_marker_written_before_sources_were_recorded_belongs_to_the_personal_account` - the upgrade: a team neither takes
  nor removes it; the personal account does not remove it; the personal account that serves it takes it over.
- `A_folder_with_no_marker_is_never_touched_by_any_source` - today's behaviour, still pinned, in both folders.
- `The_personal_account_on_another_Gateway_is_another_source` - the F7 rig's own shape.
- `A_stamp_reads_back_and_one_without_a_Gateway_id_reads_as_unrecorded` (replaced the address-based stamp test)
- `A_stamp_naming_the_Gateway_by_address_is_taken_over_by_the_personal_account_and_never_removed` (replaced the address
  alias test: the upgrade from this change's earlier head)

The existing `SkillDirectoryInstallerTests` now pass an explicit source, so no test reads the Gateway configuration
of whoever runs it. Gateway: `A_skill_kept_by_another_Directors_library_says_so_and_not_could_not_be_linked`.

### The red check - [red-check.txt](red-check.txt)

Today's rule restored by one line at the top of `Decide` (`SkillDirectoryInstaller.cs`, the line after
`return Claim.NotOurs;`): `return Claim.Mine;` - any marker is ours, which is exactly what the old
`ReconcileCopies` (`if (!File.Exists(marker)) continue; if (!wanted.Contains(name)) delete`) and the old
`IsOurs` in `ReconcileLinks` did. Committed first, then mutated, then a full build: **6 red of 48**, including the
main one, whose message is the F7 symptom word for word:

```
'mine-one' is missing from the shared folder after team team-a on https://gateway.test installed
```

The three that stay green under the old rule are the ones that never involve two sources (no marker, address
recognition, stamp parsing). Restored with `git checkout`, rebuilt in full, 48 of 48 green.

### Review round 1 - the tests for SK-F1 to SK-F4

| Finding | Tests |
|---|---|
| SK-F1 two at the same instant | `SkillFolderConcurrencyTests`: `A_withdrawal_racing_a_takeover_never_deletes_or_overwrites_the_other_sources_folder` - 150 rounds, a team withdrawal and a personal takeover of the same folder released together on two threads by a barrier; after every round both folders exist, stamped and filled as the person's, and no installer threw. `A_Director_that_cannot_have_the_folder_in_time_changes_nothing_and_says_so`. `The_lock_has_one_name_per_folder_whatever_the_spelling_and_a_different_one_for_another_folder`. |
| SK-F2 the source that fetched the library | `SkillSourceEstablishmentTests`, through the REAL `SkillStoreRefresh` against a hermetic Gateway: `The_refresh_records_the_source_the_Gateway_named_for_the_key`; `A_team_key_saved_without_its_team_record_removes_and_overwrites_nothing` (the partial enrollment: nothing removed, nothing overwritten, `SourceMismatch`; with the team file it places as the team); `A_Gateway_that_does_not_name_the_source_gets_nothing_placed_and_nothing_removed`. |
| SK-F3 the account, not the address | `The_same_account_keeps_owning_its_skills_when_its_Gateway_address_changes` - three cases: http to https, a custom domain to the default, a moved Gateway; each time a changed skill is refreshed and a withdrawn one removed, with no yield. `Two_Directors_of_one_account_at_two_front_doors_share_its_skills_and_another_account_does_not`. Gateway: `GatewayInstanceIdentityTests` (created once, read back unchanged by a new process over the same database, never replaced, never visible to an account) and `SkillEndpointsTests.The_register_names_the_library_source_and_it_does_not_change` (over real HTTP). |
| SK-F4 once per folder | `SkillDirectoryInstallerTests`: `A_second_Director_never_moves_the_persons_own_folder_after_the_first_one_migrated` (with `move-session`, a historical name) and `A_migration_recorded_the_old_way_is_carried_over_and_moves_nothing` (with `fleet-comms`). |

### Review round 1 - the red checks - [red-check-review1.txt](red-check-review1.txt)

Committed first, then one line mutated per finding, a full build, the matching tests, restored with `git checkout`:

| Finding | The old behaviour put back | Red |
|---|---|---|
| SK-F1 | `TryAcquire` hands back an empty lock without waiting | 2 of 3 concurrency tests. The race: **271 failures in 150 rounds**, first message `'both' is gone - the team's withdrawal deleted the person's folder`, plus installers throwing mid-copy. |
| SK-F2 | no agreement check: the recorded source is used whatever the team file says | the partial-enrollment test: the team library is placed by a Director that believes it is personal. |
| SK-F3 | the refresh records the ADDRESS it used as the Gateway's id | all three address changes, the two-front-doors test, and the record test - 5 red, each with `HeldByAnotherSource` where none belongs. |
| SK-F4 | the migration record is this Director's own file again | the second-Director test: the second Director TRIES TO MOVE the person's `move-session` (the move fails only because the first move's backup has the same second-resolution name). The scripted run of this check produced no result line, so it was rerun by hand; the file says so. Its first rerun failed on the record assertion, not the harm, so the assertion was moved to the end of the test and the check run again. |

### Runs

| What | Result | File |
|---|---|---|
| `CcDirector.Core.Tests` skill tests (full build, clean source) | 48 passed, 0 failed | [test-runs.txt](test-runs.txt) |
| `.\scripts\test-local.ps1` (default) | all 10 suites `outcome=Completed`, every project exited zero | [gate-default.txt](gate-default.txt) |
| `CcDirector.Gateway.UnitTests`, whole suite (I changed Gateway code) | 9409 passed, 0 failed, 14 skipped | [gateway-unit-suite.txt](gateway-unit-suite.txt) |
| **Review round 1:** `CcDirector.Core.Tests` skill tests (full build, clean source) | 60 passed, 0 failed | [test-runs-review1.txt](test-runs-review1.txt) |
| **Review round 1:** `.\scripts	est-local.ps1` (default) | all 10 suites `outcome=Completed`, every project exited zero | [gate-default-review1.txt](gate-default-review1.txt) |
| **Review round 1:** `CcDirector.Gateway.UnitTests`, whole suite | 9412 passed, 0 failed, 14 skipped | [test-runs-review1.txt](test-runs-review1.txt) |
| **Review round 1:** `CcDirector.Gateway.Tests`, the classes covering `/gateway/skills` (filtered, as ruled) | 36 passed, 0 failed | [test-runs-review1.txt](test-runs-review1.txt) |

The default gate's first run failed one test, `RetiredMessagingWordsTests`, because I was writing the gate's own
output into this folder and the repository-wide scan could not open the locked file. Rerun with the output outside the
tree: green. `CcDirector.Core.Tests` is a parked suite; only its `Skills` tests were run, not the whole suite.
`CcDirector.Gateway.Tests` was run only for the four classes that cover `/gateway/skills`, not in full. The filtered
Gateway.Tests run waited about ten minutes for the suite's machine-wide lock, held by another session's run; past the
tool's ten-minute foreground limit it was moved to the background by the tool, not by choice, and was read when it
finished.

## Live proof

Both Directors share ONE rig user profile. Full output, every listing: [evidence/live-run.txt](evidence/live-run.txt).
The rig: [rig/run-live-proof.ps1](rig/run-live-proof.ps1) and [rig/SkillsRig/Program.cs](rig/SkillsRig/Program.cs).

### What ran, and how it matches the Director

No Director window was started (Tech Lead's ruling). Each "Director" is a real storage home - its own
`CC_DIRECTOR_ROOT` holding its own `config\config.json` and team file - driven by `SkillsRig`, which runs the Director's
two skill calls:

| The Director | The rig |
|---|---|
| `ControlApiHost.cs:457` `await new SkillStoreRefresh().RefreshAsync()` | the same call, no arguments: it reads the Gateway address and key from this home's `config.json` |
| `SessionManager.cs:1056` `SkillDirectoryInstaller.InstallFor(agent.Kind)` | `InstallFor(AgentKind.ClaudeCode, pathsOverride: <rig home>\.agents\skills, <rig home>\.claude\skills)` |

**The one argument that differs, and why.** On Windows, `SkillInstallTargets.For` finds the home folder through
`Environment.GetFolderPath(UserProfile)`, which asks the system for the signed-in user's profile folder and ignores
`USERPROFILE` and `HOME`. The rig's first run found this: its environment check refused before reading or writing
anything, because the folder would have been the owner's `C:\Users\soren`
([evidence/refused-run-home-folder.txt](evidence/refused-run-home-folder.txt)). So the rig passes the same two
folders that `SkillInstallTargets.For` builds for Claude Code (`<home>\.agents\skills`, `<home>\.claude\skills`),
rooted in the rig's home. The store and the source are NOT overridden: both come from the Director's own storage home,
as they do in the Director. The path table itself is covered by the existing unit test
`Every_agent_kind_writes_to_the_one_shared_directory`.

**Two stub Gateways.** The personal Director's config names `http://localhost:7811` and the team Director's names
`http://localhost:7812`. Each run serves its own port with an `HttpListener` for as long as the run lasts (checked free
first, by the script with `Get-NetTCPConnection` and by the run itself; localhost only, refused otherwise). The
personal library serves `dev-throttle`, `fleet-comms`, `browsers`, `demo-mode`; the team library serves `dev-throttle`,
`fleet-comms`, `team-runbook`, `team-style`. Two names clash, and each body says which library it came from. Each
stub names its library on the register as the real Gateway now does: `rig-gateway-7811` / `tenant-person` / no team,
and `rig-gateway-7812` / `team-a` / `team-a`.

**Checked before anything is touched, every run, printed and written as the first lines of that Director's log**:
`CC_DIRECTOR_ROOT`, `USERPROFILE`, `HOME`, `LOCALAPPDATA`, `APPDATA`, the storage root, config and log folders, and the
two skill folders, each asserted to be inside the rig; the only `CC_*` variable present is `CC_DIRECTOR_ROOT`;
`CC_VAULT_PATH` absent. Any failure refuses the run before anything is read.

**Seeded first**, as a person's folders look today: `my-own-skill` written by hand (no marker) in both folders, and
`old-personal` as the current release leaves it (the old three-line marker, a junction into the shared copy).

### What it showed

Scenario 1, personal first: personal, team, personal, team, then the team's Gateway withdraws `team-style`, then the
personal Gateway withdraws `demo-mode`. Scenario 2, team first: team, personal, team, personal.

| Step | Result |
|---|---|
| 1.1 personal | 4 of 4 placed. |
| 1.2 team | `team-runbook` and `team-style` placed; `dev-throttle` and `fleet-comms` yielded to the personal copies, reported as `HeldByAnotherSource`. **None of the personal skills removed.** |
| 1.3 personal, 1.4 team | The same; nothing removed by either. |
| 1.5 team withdraws `team-style` | `team-style` gone from both folders; every personal skill still there. |
| 1.6 personal withdraws `demo-mode` | `demo-mode` gone from both folders; `team-runbook` still there. |
| 1.7 team, its team file moved aside (the partial enrollment, SK-F2) | 0 of 3 placed, every one `SourceMismatch`; the listing after 1.7 is identical to the one after 1.6, line for line. |
| 2.1 team first | All 4 team skills placed, including the two clashing names. |
| 2.2 personal | Takes `dev-throttle` and `fleet-comms` over (re-stamped `account=personal`, personal bodies); `team-runbook` and `team-style` still there. |
| 2.3 team, 2.4 personal | Team yields the two names; nothing removed by either. |

In all 13 listings of both scenarios, `my-own-skill` stays `no marker / written by hand`, and `old-personal` stays
`old marker, no source / installed by the current release`: neither Director touched or removed either. After the last
step the rig is removed. Every process the script started was a foreground run that exited; afterwards nothing listened
on 7811 or 7812 and no `SkillsRig` process was running.

One run is not in the evidence. A first attempt of the full script was cut off after step 1.1 by my own console
command (`Select-Object -First 80` ends the pipeline it reads from, which ended the script mid-step). It was rerun in
full; the file above is that rerun.

### What this does not prove

- **The Director window's own call path.** No Director window was started. The installer and the store refresh are the
  real code, called as the Director calls them, but `SessionManager` launching a session, the Control API's timer, and
  the agent that would read the skills were not run.
- **The real folder table on Windows.** The skill folders were passed explicitly (above). Under a real Director, the
  folders are the signed-in Windows user's, whatever the environment says.
- **The NUL file watcher.** A Director window on Windows starts a watcher over every fixed drive (`C:\` and `D:\` here)
  that deletes files named `nul`, unconditionally (`App.axaml.cs`, the `NulFileWatcher.Start()` block). No environment
  setting turns it off. That is why no Director window was started for this proof.
- **Two Directors at the same instant, as two processes.** The live runs were one after another. The lock is proved by
  `SkillFolderConcurrencyTests` with two THREADS contending for the same named operating-system mutex, which is the
  object two processes would contend for; two separate processes were not run against it. A Director of an OLDER
  release takes no lock at all, so it can still race one of this release until it is updated.
- **Real Gateways.** The Gateways were stubs serving the two skill routes, not the Gateway.

## Finding for the record

**On Windows no environment setting isolates a test Director from the person's skill folders.**
`SkillInstallTargets.For` resolves the user's profile through the system's known-folder lookup, which ignores
`USERPROFILE` and `HOME`. Any live rig that starts a real Director, window or not, writes the signed-in person's
`~/.agents/skills` and `~/.claude/skills`. This is very likely how the earlier F7 run reached the owner's folders
despite its rig. A live run with real Directors needs a separate Windows user. The product is not changed for this in
this pull request.

## To run it again

From the worktree root, with the rig built:

```
dotnet build docs\proof\teams-2311-skills\rig\SkillsRig\SkillsRig.csproj
powershell -NoProfile -File docs\proof\teams-2311-skills\rig\run-live-proof.ps1
```

It creates `.skills-rig` under the worktree, refuses to start if 7811 or 7812 is in use, and removes the rig at the end.
