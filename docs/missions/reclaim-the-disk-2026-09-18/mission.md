# Reclaim the Disk

Mission document. Written by the Architect on 18 September 2026 from the design interview with the
owner. It follows the ten sections of the DevThrottle Method. Where this document and the method
disagree, this document wins.

## 1. The mission

Build a DevThrottle feature that tells the owner what is filling a disk and recommends what is safe
to remove, and that removes only what it can prove is disposable, in a way that can be undone.

## 2. The why

On 18 September 2026 the C: drive of the owner's main machine reached zero bytes free of 931 GB and
broke running commands in the middle of a session. A hand cleanup recovered 130 GB. It was a
one-off: nothing on the machine prunes anything, so it all comes back, at a measured 1.2 GB a day
from DevThrottle's own data alone.

In the owner's words: "currently my disks keep filling up with shit". He runs DevThrottle and one
coding agent on that drive and nothing else he cares about, "and I'm using 900... gigabytes."

A machine that runs a fleet of agents fills its disk faster than a person notices, and a full disk
stops the fleet. Every DevThrottle user will meet this. The existing tools in the world either show
a map and leave the judgement to the person, or delete by path patterns that "look like cache".
Neither is safe to hand to an agent. This one is built so that it can be.

## 3. The goal

At the end, all of this is true, and the QA report shows it:

- On the owner's machine, one command produces a report for C: and for D: that names the large
  things, says what each one is, and says how much of the drive the scan could not see.
- The same command lists recommendations. Every recommendation names the rule, what it removes, the
  proof that it is safe, what is lost, and how to get it back.
- On the owner's machine the orphaned Windows installer rule finds the orphans measured during the
  design (211 files, 27.9 GB on 18 September; the number will have moved, the rule's own controls
  must be shown non-zero).
- On a fixture tree built by the tests, removal moves exactly the proven items to a holding folder,
  reports measured bytes before and after, restores them on request, and refuses everything in the
  refusal list in section 5. Every refusal has a test, and every refusal test has been shown to fail
  when the refusal is taken out.
- The background scan runs once per machine, and the Director shows its saved result without
  scanning anything itself.

## 4. Decisions

Every answer the owner gave, in his words. The interview session is thrown away; what is not here is
lost.

- **Speed is not a constraint.** "fast doesn't really matter because we can do this in the
  background, offline, and slowly". This REPLACES the original brief, which called performance a
  design constraint. Do not build a fast path that reads the file table.
- **The output is recommendations.** "It should be more of just here's what we're recommending."
- **The hard part is the product.** "The biggest thing is going to be with this Reclaim the Disk is
  figuring out which of the directories we can deletion. How many of the packages can we delete
  under the Windows installer?"
- **Knowledge of what is safe must not be baked in.** "There has to be an online component of...
  What's good and bad in Windows and how do we safely delete stuff?" The Architect's reading of this
  is in section 10.
- **It belongs in the product.** "We can build it into the director. There's lots of options we can
  build it into the CC launcher. So it's a massive feature".
- **Not Python.** "could we write this in Go or C#? Anything that's cross-platform".
- **Go.** "So why are you stopping? We didn't build it yet."

From the owner's written brief, unchanged:

- Windows first, then macOS, then Linux. The engine is separate from the platform rules from day
  one. D: matters as much as C:.
- It never touches the credential vault or anything cc-secrets owns, git history, working trees with
  uncommitted work, source repositories, the owner's documents, or anything it cannot positively
  classify.
- Dry run is the default. Removing requires an explicit flag.
- Every rule states what it removes, why it is safe, and what is lost. "Probably cache" is not a
  policy.
- It reports measured before and after, never an estimate presented as a fact.
- An empty or zero result is a broken instrument until proven otherwise.
- The command line tool is named `cc-cleanup-storage`.

## 5. Design

### Measured baseline (C:, not elevated, 18 September 2026)

The two scripts that produced these are in `evidence/`. They are throwaway measurement scripts, not
product code, and they only read.

| What | Size |
|---|---|
| Volume used | 800 GB |
| Seen by an ordinary scan (4.6 million entries, 181 seconds, 244 folders refused access) | 703 GB |
| Not visible without an administrator | about 97 GB, content unknown |
| `C:\Users\soren` | 417 GB |
| `C:\Windows\Installer` | 58.8 GB |
| of which still pointed at by Windows (576 files) | 29.5 GB |
| of which orphaned (211 files) | 27.9 GB |
| `C:\ProgramData\mindzie` | 59.2 GB |
| DevThrottle's own data | 47.1 GB |
| AgentEyes recordings | 45 GB or more |
| `AppData\Local\Packages` | 34.1 GB |
| `AppData\Local\Temp` | 31.9 GB |
| Package caches (Hugging Face, Playwright, uv, npm, pip, Gradle) | about 31 GB |

A plain directory walk managed 20,000 to 60,000 entries a second. `D:\ReposFred` alone holds more
than 3.7 million entries. That is acceptable for a background job and is why no faster method is
built.

### The one idea

**A rule may recommend removal only when it holds a proof, and there are exactly three kinds of
proof.** Anything matched by no rule is reported as unclassified and is never offered for removal.
This is an allow-list: the tool enumerates what to remove, never what to skip.

1. **A record says nothing needs it.** The system keeps its own record of what it still needs, and
   the item is not in it. Model: Windows records, for every installed product and patch, the cached
   package it needs to repair or uninstall it; a package nothing points at is an orphan.
2. **The owner has its own cleanup command.** The tool that created the data ships a command that
   clears it, and we run that command instead of deleting files. Models: `npm cache clean`,
   `pip cache purge`, `uv cache clean`, `dotnet nuget locals`, and for the Windows component store
   `Dism.exe`. We never delete inside the component store ourselves: most of its files are hard
   links and its real size is only known by asking Windows.
3. **We made it, by an exact name, and it is old and closed.** A folder whose name matches a pattern
   DevThrottle's own code creates, older than the rule's age gate, with no file in it open. Model:
   the test scratch folders leaked into Temp.

A path pattern from somebody else's product is not a proof. The public rule collections (BleachBit,
Winapp2) were read during the design; Winapp2 has no licence and both reason from path patterns.
They are reading material only. Nothing is imported from them.

### Every rule fails closed

A rule that compares two lists reports its controls with its answer: how many records it read, how
many of those exist on disk, how many candidates it examined. If either side of the comparison is
empty the rule reports BROKEN, never "nothing to remove". The installer measurement script in
`evidence/` shows the shape.

### The report tells the truth about its own reach

Every report states bytes seen against bytes the volume says are used, and shows the difference as
a number with the folders that refused access. Links and junctions are never followed and are
counted. Cloud placeholder files are counted separately because they occupy no disk.

### Removal is a move, and it is late

- Removal moves items into a holding folder on the same volume and records where each came from.
  `holding list`, `holding restore` and `holding purge` manage it. Space is truly freed only when
  holding is purged, and the report says so plainly instead of claiming the space early.
- Default holding period 30 days. Purging is its own explicit command.
- An age gate on every rule. One of the 211 measured orphans had been written two days earlier;
  the installer rule's gate is 30 days.
- Items cleared by an owner's own command are not held, because the owner's command does not allow
  it and the data is re-downloadable by definition. The rule's "what is lost" says how long the
  re-download takes to the extent that can be known, and otherwise says it is unknown.
- The check that an item is still disposable is made again at the moment of the move, not only at
  the moment of the recommendation.
- The tool never raises itself to administrator. A rule that needs one says so, and the report
  prints the exact command for the owner to run.

### The refusal list (each one is a numbered test)

The tool refuses, even when a rule matched:

- anything under the credential vault, the secrets store, or any path the storage resolver in
  `CcDirector.Core` calls credentials or vault. The list is read from the resolver, never typed;
- anything inside a git working tree. Worktrees belong to `cc-worktrees`, which already proves
  whether work has landed; this tool reports them and points there;
- anything under the user's Documents, Pictures, Videos, Desktop or OneDrive folders;
- anything reached through a link or junction;
- anything younger than its rule's age gate;
- anything with an open file in it;
- anything when the rule's controls are empty;
- anything when the item changed between recommendation and removal;
- any removal without the explicit apply flag;
- any path that is not canonical after resolution.

`tools/cc-worktrees` on main is the house model: 42 tests, nearly all named for something the tool
declines to do. Follow it.

### The pieces

- `src/CcDirector.Reclaim` - the engine library: scanner, saved index, rule contract, classifier,
  report, holding folder. No user interface. Nothing platform specific in the engine.
- `src/CcDirector.Reclaim.Windows` - the Windows rules. macOS and Linux arrive later as sibling
  rule sets, not as a rewrite.
- `src/CcDirector.Reclaim.Tests` - added to the default list in `scripts\test-local.ps1`.
- `tools/cc-cleanup-storage` - a thin C# command line tool over the engine, registered in
  `tools/registry.json` as type `dotnet` the way `cc-click` is. Commands: `scan`, `report`,
  `recommend`, `reclaim` (dry run unless `--apply`), `holding`. Every command has `--json`.
- The Launcher hosts the background scan, because there is one Launcher per machine and several
  Directors. The Director and the Cockpit render the saved report and scan nothing. The engine
  produces the finished sentences; the screens do not decide what anything means (critical rule 7
  in `CLAUDE.md`).
- Rules are data with a version. They ship inside the product and are refreshed from the Gateway
  the way built-in skills are, so a new rule reaches every machine without a release. A rule file
  can only select among the three proof kinds the engine implements; it cannot introduce a new way
  to delete.

### First Windows rules, in order of measured value

| Rule | Proof kind | Needs administrator | Measured here |
|---|---|---|---|
| Orphaned Windows installer packages | a record | yes | 27.9 GB |
| Package caches: npm, pip, uv, NuGet, Gradle | the owner's command | no | about 16 GB |
| DevThrottle test scratch folders in Temp | we made it | no | 90,479 folders before the hand cleanup |
| Windows component store | the owner's command (`Dism.exe`) | yes | not yet asked |
| What Windows' own Disk Cleanup offers (the `VolumeCaches` registry list) | the owner's command | some | not yet asked |
| Crash dumps, error reports, recycle bin | a record | some | 2.6 GB recycle bin |

Reported, sized and dated, never offered for removal in this mission: DevThrottle's own session
logs and history, recordings, application data such as `ProgramData\mindzie`, Hugging Face models,
Playwright browsers, Docker and virtual machine disks, Android emulators, browser profiles.

### Out

Reading the file table for speed. Any removal that runs unattended. Finding duplicate files.
Cleaning the registry. macOS and Linux rules. Anything inside a repository.

### Style guides

`docs/CodingStyle.md` for the C#. `docs/VisualStyle.md` and `docs/CockpitVisualStyle.md` for phase 6.
Logging on every public method as `CLAUDE.md` rule 2 requires.

## 6. Phases

Each phase is one or more merged pull requests and leaves main working.

1. **Scan and report.** Engine project, scanner, saved index, report, the `scan` and `report`
   commands. Proof: the fixture tree reports exact expected numbers, including a link that is not
   followed, a folder that refuses access, and the unseen-gap line. Developer only.
2. **Rules and recommendations.** The rule contract, the classifier, the `recommend` command, and
   the first three rules. No removal code exists yet, so this phase cannot delete anything. Proof:
   controls shown, the BROKEN case shown for an empty record set, a read-only run on the owner's
   machine that finds the installer orphans. Developer only.
3. **Removal with holding.** `reclaim`, the holding folder, the whole refusal list. This is the
   dangerous phase. **Seat a Tech Lead.** Every refusal test is proven to fail with the refusal
   removed, and the commit order in the memory notes on revert proofs is followed. The Reviewer
   reads this phase before it merges, not after.
4. **The remaining Windows rules.** May run in parallel with phase 3 once phase 2 is merged, because
   it adds rules and touches no removal code.
5. **Background scan and rules as data.** The Launcher hosts the scan; rule files are versioned and
   refreshed from the Gateway. Needs a Gateway deploy through the `deploy-hosted-gateway` skill,
   which is the owner's decision, so this phase ends at merged.
6. **The screen.** The Director and Cockpit page that renders the saved report. After phase 5.

## 7. The check

An agent can run all of this alone:

    .\scripts\test-local.ps1

must be green with `CcDirector.Reclaim.Tests` in its default list, and

    dotnet test src\CcDirector.Reclaim.Tests

must show every numbered refusal test by name. Then the read-only run on the real machine:

    cc-cleanup-storage recommend C:\ --json

must exit 0, must show the installer rule with all three controls greater than zero, and must show
the unseen-gap line. Exit code and parsed JSON decide, never a search of the printed text.

**No removal is ever run on the owner's machine by this mission.** Removal is proven on fixture
trees the tests build and destroy. The method's law 18 forbids a seat to destroy on its own, and the
owner has granted building, not deleting. The first real removal is his, from the finished tool,
after the report.

## 8. Merge plan

Merge on local green plus a review by a different agent family, per `CLAUDE.md` rule 5a. Small
pull requests, at least one per phase, more where a phase splits cleanly. One worktree per
workstream, cut from origin/main. Safe because phases 1 and 2 contain no removal code at all, and
phase 3 is removal on fixtures only.

## 9. Where it ends

Merged to main. Not released: the release is a separate decision and the owner's. Phase 5's Gateway
deploy is also his.

## 10. Questions

The owner asked not to be stopped again, so these are answered with the Architect's recommendation
and recorded as rulings he may reverse. None of them blocks phase 1 or 2.

- **Language.** C#. The repository holds 73 C# projects, 35 Python and no Go; the Director, the
  Gateway, the Launcher and the installer are C# and already run on all three platforms; the
  registry and the installer records are first-class there.
- **Where the scan runs.** The Launcher, because it is the one process per machine. The Director
  shows the result.
- **"An online component".** Read as: the rules are data that reach every machine through the
  Gateway without a release. It was ALSO read as an instruction to research what is known about
  safe Windows cleanup, which was done; the sources are below.
- **Holding period and age gates.** 30 days holding. 30 days for installer orphans, 7 days for test
  scratch folders.
- **Removal on the owner's machine during the mission.** None. See section 7.

## Raised separately, not part of this mission

- DevThrottle's own data has no retention policy: session logs, session history and Director logs
  grew to 59 GB in seven and a half weeks. The tool reports it; the fix is in the product.
- The test suites create scratch folders in Temp and never delete them, under many different name
  patterns. The fix is one known parent folder that the suites clean up, which would also turn this
  tool's rule from a typed list of names into a single derived presence.
- The owner's global `disk-report` skill points every session at a `cc-disk` command that no longer
  exists anywhere. Retire it when this tool exists.

## Sources read during the design

- https://www.winhelponline.com/blog/windows-installer-folder-safe-cleanup-free-disk-space/
- https://learn.microsoft.com/en-us/answers/questions/5678943/can-i-cleanup-manually-msp-files-under-installer-f
- https://www.elevenforum.com/t/analyze-and-clean-up-component-store-winsxs-folder-in-windows-11.7597/
- https://ss64.com/nt/cleanmgr-registry.html
- https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/automating-disk-cleanup-tool
- https://github.com/MoscaDotTo/Winapp2
- https://docs.bleachbit.org/cml/cleanerml.html
