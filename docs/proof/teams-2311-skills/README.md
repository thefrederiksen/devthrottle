# Two Directors on one computer never remove each other's skills (devthrottle_internal#2311, live proof F7)

## The problem

The skill folders belong to the USER, not to the Director: every Director on a computer writes the same
`~/.agents/skills` (the one real copy) and `~/.claude/skills` (one link per skill). Each folder carried a marker that
said "DevThrottle installed this", and nothing about WHICH Director's library. `ReconcileCopies` and
`ReconcileLinks` deleted every marked folder their own library did not hold. So in the #2311 live proof (finding F7),
two test Directors on a Gateway serving a different skill set removed six of the person's skills, and the person's
own Directors put them back about half an hour later.

## The rule now

The marker records the **source** that installed the folder: the Gateway address and the account on it, the
person's own personal account or one team (`SkillSource`, new file `src/CcDirector.Core/Skills/SkillSource.cs`).
Two lines are added after the existing three (id, version, content hash), so nothing that reads the first three
changes:

```
dev-throttle
1
3f1c...
gateway=https://devthrottle.com
account=personal          (or account=team:<team id>)
```

**Why the Gateway plus the account, and not the Director.** One person's two Directors on the same personal account
are served the same library and must SHARE their folders; a Director id would make them fight exactly as before. The
account alone is not enough either: the personal account on a test Gateway is a different library from the personal
account on the hosted one, and that is precisely the F7 case. A self-hosted Gateway has several addresses (#1233), so
a Director recognises its own stamp under any address in its configuration and stamps the active one.

The decision, one function (`SkillDirectoryInstaller.Decide`):

| The folder at that name | A personal source | A team source |
|---|---|---|
| no marker (made by hand) | never touched, recorded as Shadowed (unchanged) | same |
| stamped by this source | refreshed; removed when withdrawn | same |
| stamped by a team | **takes it over** (re-stamps it); never removes it | the first team keeps it; this one yields |
| stamped by another personal source (another Gateway) | the first keeps it; this one yields | yields |
| old marker, no source (today's code) | counts as personal: takes it over; **never removes it** | yields; never removes it |

A yield is recorded as a placement problem, the new fault `HeldByAnotherSource`, the same way Shadowed is recorded
today. The Director's warning and the Gateway's placement page both say "kept by another Director's library under the
same name" rather than "could not be linked" (`SkillPlacement.Describe`, `SkillPlacementStore`).

**Links** follow the same rule. A link in `~/.claude/skills` to a shared copy is removed only when that copy is this
source's, or when the copy is already gone (a link to nothing reads as nothing to every agent, and whoever still holds
the skill makes the link again with the copy). A full copy left there by the scheme before links is judged by its own
marker.

**An old marker is never removed by anyone.** Which personal library wrote it is not recorded, so a withdrawal would
be deleting the person's skill on a guess. The cost: a skill withdrawn at the same moment as this upgrade can stay on
disk. A stale extra skill is recoverable; a deleted one is not.

**A Director with no Gateway configured** installs nothing and removes nothing (there is no library to install from).

## Tests

New: `src/CcDirector.Core.Tests/Skills/SkillSourceOwnershipTests.cs`, nine tests, the real installer run twice over
ONE pair of folders, once per library:

- `Two_sources_never_remove_each_others_skills_in_any_order_and_repeatedly` - the main one. Personal and team, three
  orders (personal first, team first, team twice then personal twice), after EVERY launch every skill either library
  has installed so far is still in both folders, and the shared name holds the personal copy.
- `The_personal_account_wins_a_name_even_when_the_team_installed_it_first`
- `A_withdrawal_removes_only_the_withdrawing_sources_own_skills` - a withdrawal by each.
- `Between_two_teams_the_first_installed_keeps_the_name_until_it_withdraws_it`
- `A_marker_written_before_sources_were_recorded_belongs_to_the_personal_account` - the upgrade: a team neither takes
  nor removes it; the personal account does not remove it; the personal account that serves it takes it over.
- `A_folder_with_no_marker_is_never_touched_by_any_source` - today's behaviour, still pinned, in both folders.
- `A_source_recognises_its_own_stamp_under_any_address_it_knows_its_Gateway_by`
- `The_personal_account_on_another_Gateway_is_another_source` - the F7 rig's own shape.
- `A_stamp_with_no_source_lines_reads_as_unrecorded_and_a_team_stamp_reads_back`

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

### Runs

| What | Result | File |
|---|---|---|
| `CcDirector.Core.Tests` skill tests (full build, clean source) | 48 passed, 0 failed | [test-runs.txt](test-runs.txt) |
| `.\scripts\test-local.ps1` (default) | all 10 suites `outcome=Completed`, every project exited zero | [gate-default.txt](gate-default.txt) |
| `CcDirector.Gateway.UnitTests`, whole suite (I changed Gateway code) | 9409 passed, 0 failed, 14 skipped | [gateway-unit-suite.txt](gateway-unit-suite.txt) |

The default gate's first run failed one test, `RetiredMessagingWordsTests`, because I was writing the gate's own
output into this folder and the repository-wide scan could not open the locked file. Rerun with the output outside the
tree: green. `CcDirector.Core.Tests` is a parked suite; only its `Skills` tests were run, not the whole suite.
`CcDirector.Gateway.Tests` was not run.

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
`fleet-comms`, `team-runbook`, `team-style`. Two names clash, and each body says which library it came from.

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
| 2.1 team first | All 4 team skills placed, including the two clashing names. |
| 2.2 personal | Takes `dev-throttle` and `fleet-comms` over (re-stamped `account=personal`, personal bodies); `team-runbook` and `team-style` still there. |
| 2.3 team, 2.4 personal | Team yields the two names; nothing removed by either. |

In every listing of both scenarios, `my-own-skill` stays `no marker / written by hand`, and `old-personal` stays
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
- **Two Directors at the same instant.** The runs were one after another. Two Directors reconciling the same folder in
  the same moment are not locked against each other; that was true before this change and is unchanged by it.
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
