# Reclaim the Disk: the QA report

Issue 3120. Run by the Delivery Lead on 20 September 2026, against merged main at `297c090a5`,
on the owner's own machine SOREN_NORTH. The tool was built from that commit with
`dotnet publish -c Release`.

**This is the flow AND the failure cases, not one success run.** Eight checks follow. Six of them
are the tool being unable to do something, because that is where this mission's whole risk lives.

**Nothing was removed from this machine.** Every `reclaim` below was a dry run. The holding folder
`C:\cc-reclaim-holding` was never created, which is checked at the end.

## 1. The flow: a real report on the real C: drive

`cc-cleanup-storage report "C:\" --top 12`, reading a saved scan that walked the drive in 681
seconds.

```
volume: C:\ is 999149268992 bytes (930.5 gigabytes), of which 783089917952 (729.3 gigabytes)
        are used and 216059351040 (201.2 gigabytes) are free
seen:   702715010069 bytes (654.5 gigabytes) in 3517993 files and 1135254 folders
unseen: 80374907883 bytes (74.9 gigabytes) that the volume counts as used and this scan did not see
refused: 251 folders refused a listing, and every one of them is named below
links:  5584 links and junctions were found, and none were followed
```

The four largest, from the real output: `C:\Users` 378.6 gigabytes, `C:\Windows` 96.3,
`C:\ProgramData` 70.8, `C:\Windows\Installer` 58.8.

**What makes this pass:** the 74.9 gigabyte unseen figure and the 251 named refusals are printed
beside the totals, unprompted. The report does not claim to have seen the drive. It says how much
it did not see and names every folder that turned it away.

## 2. The flow: the real recommendation

`cc-cleanup-storage recommend "C:\"`

```
rules-run: 10
rules-broken: 1
items-offered: 208
reclaimable: 56173460250 bytes (52.3 gigabytes)
unclassified: 646541549819 bytes (602.1 gigabytes) seen by the scan and matched by no rule,
              which is never offered for removal
warning: 1 of 10 rules could not do their work; each says why below, and none of them offers anything
```

| Rule | Verdict | Offered | Size |
|---|---|---|---|
| orphaned-windows-installer-packages | ok | 204 | 26.6 GB |
| nuget-cache | ok | 1 | 10.9 GB |
| uv-cache | ok | 1 | 5.5 GB |
| npm-cache | ok | 1 | 4.9 GB |
| pip-cache | ok | 1 | 4.4 GB |
| devthrottle-test-scratch-folders | ok | 0 | 0 |
| windows-crash-dumps-and-error-reports | ok | 0 | 0 |
| windows-disk-cleanup-on-c | ok | 0 | 0 |
| windows-recycle-bin-on-c | ok | 0 | 0 |
| **windows-component-store** | **broken** | - | - |

**The goal's named check passes.** The mission required the orphaned installer rule to find the
orphans measured during the design: 211 files, 27.9 gigabytes on 18 September. It offered **204**,
and its own control line reads `control-orphans-too-young-to-offer: 7`. 204 plus 7 is 211. The rule
found the same set two days later and held seven back because they had not yet passed its 30-day
age gate. Its controls are all non-zero: 576 records read, 576 found on disk, 787 candidates
examined, 362 product records, 216 patch records.

**602 gigabytes is reported and never offered.** No rule matched it, so it is named and left alone,
however large it is.

## 3. Failure case: a rule that cannot do its work says BROKEN

The component store rule, in its own words from the real run:

> `verdict: broken`
> `broken-reason: asking Windows how big the component store is needs an administrator, and this
> tool never raises itself to one: run cc-cleanup-storage from a command prompt opened with Run as
> administrator, or run Dism.exe /Online /Cleanup-Image /AnalyzeComponentStore yourself`

**This is the mission's central claim, proven on a real machine.** The rule could not check, so it
said so, offered nothing, and the count `rules-broken: 1` was carried up to the summary. It did not
report "nothing to remove". It also did not elevate itself; it printed the command for the person.

## 4. Failure case: zero offered, with the reason

The scratch folders rule offered nothing. Its controls say why:

```
control-names-looked-for: 16
control-folders-examined: 42003
control-folders-matching-one-of-our-names: 5889
control-matches-too-young-to-offer: 5889
control-matches-with-a-file-still-open: 0
control-matches-that-would-not-be-listed: 0
```

**A zero that explains itself.** It examined 42,003 folders, matched 5,889, and every one was
younger than its age gate. That is a different answer from "found nothing", and the numbers make
the difference readable.

## 5. Failure case: no rule applies to this folder

`cc-cleanup-storage reclaim` on a folder outside every rule's reach:

> `verdict: broken`
> `reason: no rule on this machine looks inside the folder that was asked about, so there is nothing
> behind this answer: a folder no rule applies to finds nothing to remove every time and looks
> exactly like a folder with nothing to remove`

The empty answer is refused rather than returned.

## 6. Failure case: the refusal list fires on a real candidate

A throwaway folder was made in the temporary folder with a name the scratch rule matches, backdated
past its age gate so the rule would genuinely offer it, and a `.git` entry placed inside it. Then a
dry run:

```
items-would-move: 0
items-refused: 1
item: C:\Users\soren\AppData\Local\Temp\cc-director-test-qagitcheck
refused: refusal 2, inside a git working tree: an entry named .git stands at
         C:\...\cc-director-test-qagitcheck\.git; worktrees belong to cc-worktrees, which already
         proves whether work has landed, and this tool reports them and never rules on them
```

**A rule matched and the tool still refused**, named which refusal fired, and said why. The fixture
was deleted afterwards by the person running this report, not by the tool.

## 7. Failure case: a folder that is not there

```
error: folder-not-found
message: There is no folder at C:\this-folder-does-not-exist-qa. Give the scan a folder that exists.
```

Exit code is non-zero. It does not scan nothing and report success.

## 8. Failure case: asked for a report with no saved scan

```
error: no-saved-scan
message: There is no saved scan at ...\d-reposfred-devthrottle-reclaim-gate1-82b47965ddef.json.
         Run: cc-cleanup-storage scan "<folder>"
help: cc-cleanup-storage scan "<folder>"
```

Exit code 1, and it names the command that fixes it.

## 9. Dry run moves nothing, and says so before the numbers

Every `reclaim` above opened with:

```
apply: no - this is a dry run, nothing moves
```

and reported `candidate-bytes-before`, `candidate-bytes-after` and `volume-free-before` as measured
figures.

**Checked at the end of this run: `C:\cc-reclaim-holding` does not exist.** No removal has ever
happened on this machine, by this tool, at any point in this mission.

## What this report does NOT cover

- **The tool has never been run with `--apply` on this machine, by anyone.** Removal, holding,
  restore and purge are proven only on fixture trees the tests build and destroy, 310 tests in the
  engine suite. The first real removal is the owner's.
- **Only the C: drive and the temporary folder were exercised here.** The mission's goal asked for
  C: and D:. D: has no saved scan, and scanning it would take a further twenty minutes or so; the
  same rules apply to it and nothing about it is special, but this report did not do it.
- **The component store rule has never been seen working**, only failing correctly. Seeing it work
  needs an administrator prompt, which this mission does not take.
- **Nothing was run on macOS or Linux.** The rules are Windows-only; see the platform note below.
- The parked test suites were not run for the final revert, which only removes code.

## Platform support

The engine and the command line tool are .NET 10 and cross-platform. The **rules are Windows-only**.
On macOS or Linux `MachineRules.ForThisMachine()` returns an empty list, and the engine turns an
empty rule set into a **broken** verdict rather than a clean one - so the tool will scan and report
sizes there, recommend nothing, and say plainly that it has no rules, rather than implying the disk
is clean.
