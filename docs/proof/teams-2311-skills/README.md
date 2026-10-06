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

**A visible skill folder is never without its marker (review finding SK-F5).** The copy used to be rebuilt in place:
delete the folder, create it empty, copy the files, stamp it. A Director killed anywhere in that left a folder at the
skill's name with no marker or no stamp, which every Director after it read as the owner's own skill - `Shadowed`,
never replaced, never removed. Now each copy is built and stamped in a staging folder, and only a complete folder is
renamed to the skill's name; an old copy is first renamed aside, and deleted after (`SkillDirectoryInstaller.SwapIn`).
A withdrawal renames the folder aside before deleting it (`Withdraw`), and so does the removal of a full copy left in
`~/.claude/skills` by the scheme before links. A rename within one volume is one step, so the name always holds the
old complete copy, nothing, or the new complete copy. Leftovers are recognised by their names
(`<skill>.<random>.staging|old|withdrawn`) and removed under the folder lock at the start of the next reconcile; any
other name found there is left alone.

The staging folder is a SIBLING of each skills folder - `~/.agents/skills.devthrottle-staging` and
`~/.claude/skills.devthrottle-staging` - not a folder inside it. The ruling said "beside the destination"; inside the
skills folder a staging copy holds a `SKILL.md` in a folder every agent scans, and would be read as a skill under a
mangled name for as long as it existed, which is exactly what the superseded folders once did. A sibling is on the
same volume, so the swap is still a rename and never a copy, and it sits under the same lock.

**The source travels with each skill's bytes (review finding SK-F6).** The store's marker now carries the same three
source lines as a placed copy, written last with the bytes (`Materialize(store, bundle, source)`). Three things follow:

- **The store refresh** treats a skill as already present only when it was fetched for the library the register names
  now (`IsAlreadyMaterialized`), so the same version served by two libraries is fetched again for the second. When a
  version cannot be read, a kept copy fetched for ANOTHER library leaves the store instead of being kept, and the log
  says whose it was; a copy fetched for this library is kept, as before.
- **Placement** reads each skill's own recorded source. A skill whose source is not the library's is not placed and is
  recorded as `SourceMismatch`; whatever is in the folder under its name is left alone. Each placed copy is stamped
  with its OWN recorded source, never relabelled.
- **On upgrade**, every store marker lacks the source lines, so the first refresh fetches each skill once more. A
  skill whose fetch fails then leaves the store and arrives on the next cycle that can read it.

**Refresh and placement share one lock on the store (review finding SK-F7).** Both layers above held in sequence, but
a refresh could rebuild a skill directory between placement reading its source and copying its bytes, so the new
library's bytes went out under the old library's stamp. The store now has its own machine-wide named lock, the same
kind as the folder lock. The refresh fetches with no lock held (only it writes the store), then takes the store lock
for every write: materialise, drop, delete what is no longer served, record the source. Placement takes the
shared-folder lock FIRST and the store lock SECOND, and holds both from reading the store's record and each skill's
source to the last copy. The refresh takes only the store lock, so no two of them can wait on each other in a circle.
A refresh that cannot have the lock within 60 seconds changes nothing; a placement that cannot have both within its
10 seconds records `FolderBusy`, as before.

**Cleanup deletes only what it can prove it made (review finding SK-F8).** Every folder the installer makes in a
staging root - a copy being built, an old copy moved aside, a withdrawn one - is created and given the marker
`.devthrottle-staging` BEFORE anything goes in, and the skill itself goes into a subfolder, so the marker never
travels into the skills folder. Cleanup deletes a folder only when it is a real folder, has a name of the right shape,
AND its marker's first line is this installer's signature. Anything else is left alone and logged.

**Copies are built on the volume the skills folder really lives on (review finding SK-F9).** When a skills root is a
link - a junction or a symbolic link, both followed the same way (`DirectoryInfo.ResolveLinkTarget`) - the staging root
goes beside the link's final target, not beside the link's own spelling. If the target does not exist, or is the top
of a drive, nothing is changed and every skill is reported as the new fault `FolderLinkUnresolved`, with its own
sentence on the Director and on the Gateway's placement page.

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

### Review round 2 - the tests for SK-F5 and SK-F6

| Finding | Tests |
|---|---|
| SK-F5 never without its marker | `SkillSwapTests`, with real junctions under `~/.claude/skills`. `A_reconcile_killed_at_any_step_of_replacing_a_skill_is_repaired_by_the_next_one` - five steps (`copying`, `copied`, `staged`, `moved-aside`, `swapped`), each for a personal and a team source. `A_reconcile_killed_while_placing_a_new_skill_is_repaired_by_the_next_one` - four steps. Each kill throws from inside the reconcile; at EVERY step passed on the way, and after the kill, the folder at the skill's name is either absent or has its marker, its source stamp and its `SKILL.md`. The next reconcile reports no problem at all - no `Shadowed` - places the new version, and leaves the staging folder empty. `A_reconcile_killed_while_withdrawing_a_skill_leaves_no_half_deleted_folder_and_the_next_one_finishes`. `Nothing_is_ever_built_inside_a_skills_folder_and_a_staging_folder_name_this_code_did_not_make_is_kept`. |
| SK-F6 the source per skill | `SkillSourceEstablishmentTests`, through the real store refresh. `A_Director_moved_from_a_team_to_the_personal_account_never_places_the_team_bytes_as_personal` - the ruling's case: team A, then the personal key, the same skill id, the personal version unreadable; the team's bytes stay labelled as the team's, and once readable the person's skill takes the name. `A_kept_skill_fetched_for_another_library_leaves_the_store_and_one_fetched_for_this_library_stays`. `The_same_version_of_a_skill_served_by_two_libraries_is_fetched_again_for_the_second`. `A_skill_in_the_store_recorded_for_another_library_is_never_placed` (also a skill with no recorded source). |

### Review round 2 - the red checks - [red-check-review2.txt](red-check-review2.txt)

Each puts the old behaviour back, builds, runs the tests, and restores the saved source; the diff after all six was
compared byte for byte with the diff before them.

| Finding | The old behaviour put back | Red |
|---|---|---|
| SK-F5 | the copy rebuilt in place at the skill's own name, nothing moved aside | all 9 kill tests. The kill never lands: the check at an earlier step fails first, with `'...\.agents\skills\new-one' is visible without its SKILL.md`. |
| SK-F5 | leftovers in the staging folder never cleared | all 10 kill tests, on the empty-staging assertion. |
| SK-F6, store | a failed read keeps whatever the store holds | the store test: the team's bytes are still in the store. |
| SK-F6, placement | every held skill placed and stamped with the library's source | the placement test: nothing is refused. |
| SK-F6, both | the two above together | those two, and the ruling's test: `the team's bytes are stamped as the person's own`. |
| SK-F6, already present | a skill at the served version counts as present whoever it was fetched for | the same-version test: the person's own skill is refused. |

With one layer put back, the ruling's own test stays green, because the other layer still holds: the two layers are
tested separately for that reason.

### Review round 3 - the tests for SK-F7, SK-F8 and SK-F9

| Finding | Tests |
|---|---|
| SK-F7 refresh racing placement | `SkillSourceEstablishmentTests`, the real refresh against the hermetic Gateway. `A_refresh_that_lands_while_placement_is_copying_never_gets_its_bytes_stamped_with_the_old_source` - 20 rounds alternating the account; on every round placement pauses right after reading a skill's source and starts a refresh to the OTHER account, giving it 300 ms to land before the copy. `Refresh_and_placement_racing_freely_for_many_rounds_never_relabel_a_librarys_bytes` - 60 rounds, three skills, a refresh and a placement released together. After every round, every placed copy carries the stamp of the library its bytes came from. |
| SK-F8 only what it made | `SkillSwapTests`. `A_folder_in_the_staging_folder_without_this_installers_marker_is_never_deleted` - four folders, three with names of exactly the right shape (the review's `backup.<32 hex>.old` among them), each with a marker file of the right NAME but not the installer's content, and somebody's file inside: all survive. `Every_folder_the_installer_makes_in_the_staging_folder_carries_its_marker_before_anything_else` - checked at every step, for a copy being built, an old copy and a withdrawn one. The kill tests now also check that the staging marker never reaches the skills folder. |
| SK-F9 the real volume | `SkillSwapTests`, with a real junction on a temporary folder (a symbolic link on other systems). `A_skills_folder_that_is_a_link_has_its_copies_built_beside_the_folder_it_points_at` - placed, refreshed and withdrawn through the link; staging beside the target, none beside the link. `A_skills_folder_linked_to_nowhere_changes_nothing_and_says_why` - one `FolderLinkUnresolved`, the agent's folder never touched. Gateway: `SkillPlacementStoreTests.A_skills_folder_linked_to_nowhere_says_so_and_not_could_not_be_linked`. |

### Review round 3 - the red checks - [red-check-review3.txt](red-check-review3.txt)

| Finding | The old behaviour put back | Red |
|---|---|---|
| SK-F7 | placement takes no store lock | both race tests: `round 0: the team's bytes in '...\skills\both' are stamped 'personal'` (the forced one) and the same at round 2 of the free race. |
| SK-F8 | cleanup trusts the name alone | the three right-shaped foreign folders: their contents are gone. The fourth, with a name of the wrong shape, survives either way. |
| SK-F9 | the link is not followed | both link tests: the staging root is beside the link, and a link to nowhere is not refused. Both folders in the test are on one volume, so the red shows the WHERE, not a cross-volume move failing. |

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
| **Review round 2:** `CcDirector.Core.Tests` skill tests (full build, clean source) | 75 passed, 0 failed | [test-runs-review2.txt](test-runs-review2.txt) |
| **Review round 2:** `.\scripts\test-local.ps1` (default) | all 10 suites `outcome=Completed`, every project exited zero | [gate-default-review2.txt](gate-default-review2.txt) |
| **Review round 2:** the Gateway suites | not run: round 2 changes no Gateway code (`git diff 60f0e64c1 --stat` touches `src/CcDirector.Core` and its tests only) | - |
| **Review round 3:** `CcDirector.Core.Tests` skill tests (full build, clean source) | 84 passed, 0 failed | [test-runs-review3.txt](test-runs-review3.txt) |
| **Review round 3:** `.\scripts\test-local.ps1` (default) | all 10 suites `outcome=Completed`, every project exited zero | [gate-default-review3.txt](gate-default-review3.txt) |
| **Review round 3:** `CcDirector.Gateway.UnitTests`, whole suite (the placement message changed) | first run 9412 passed, **1 failed**: `DirectorHubTests.Hello_WithNothingStoredForThisDirector_SaysSo_RatherThanStayingSilent`, `SQLite Error 14: unable to open database file` at a temporary path named for another test (`ccd-catalog-name-...`). Its class alone: 30 of 30, three times. Second whole run: 9413 passed, 0 failed. | [test-runs-review3.txt](test-runs-review3.txt) |
| **Review round 3:** `CcDirector.Gateway.Tests`, filtered as before | **NOT RUN**: the suite's machine-wide lock was held by another session's run for over an hour; my run waited about five minutes and was stopped. Round 3 changes no Gateway route; its Gateway change is one sentence, covered by the unit test above. | [test-runs-review3.txt](test-runs-review3.txt) |

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

**Not rerun for review rounds 2 and 3.** Round 3 adds locks, the staging marker and link resolution; none of
it changes what ends up in the two folders, and the rig's folders are not links.

**Round 2 note.** Round 2 changes how a copy is written and removed, not what ends up in the two
folders: every listing above would be the same. The staging folders are siblings of the two folders, outside what the
rig lists.

### What this does not prove

- **A kill by the operating system (review round 2).** The swap tests throw from inside the reconcile at each named
  step; no process was killed. Between the steps the code relies on a rename within one volume being a single step.
- **Two Director PROCESSES racing over the store (review round 3).** The store-lock race tests run the refresh and
  placement on two threads of one process, contending for the same named operating-system mutex two processes would.
- **A real cross-volume link (review round 3).** The link test's folders are on one volume; it proves where staging
  goes, not that a move across volumes fails without it.
- **A rename refused because an agent holds a file open (review round 2).** On Windows a folder with an open file in it
  may refuse to be renamed. That case was not tested; it fails the reconcile with an exception, as a refused delete did
  before.

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
