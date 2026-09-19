# Phase 1 proof: scan and report

Written by the Developer for phase 1 of the Reclaim the Disk mission, 19 September 2026. It records
what was built, the exact numbers the tests hold it to, the gate's result, and - stated plainly -
what this proof does not cover.

## What phase 1 built

| Piece | What it is |
|---|---|
| `src/CcDirector.Reclaim` | The engine: the walk, the saved index, and the report. Platform neutral. No user interface. |
| `tools/cc-cleanup-storage` | A thin command line tool over the engine. Two commands: `scan` and `report`. |
| `src/CcDirector.Reclaim.Tests` | The tests, covering both, added to the default list in `scripts\test-local.ps1`. |

There is **no removal code of any kind** in this phase: no delete, no move, no holding folder, not
even unreachable. There is no rule contract, no classifier and no `recommend`. Those arrive in
phases 2 and 3.

## The fixture tree, and the exact numbers it is held to

Every test builds its own tree in a folder of its own and destroys it afterwards. No test points at
anything on the machine it runs on. The numbers are exact - not a range, and not "more than none".

The tree (`StandardFixture` in the test project):

```
<root>/alpha/a1.txt              100 bytes
<root>/alpha/a2.txt              200 bytes
<root>/alpha/nested/n1.txt        50 bytes
<root>/beta/b1.txt              1000 bytes
<root>/top.txt                    10 bytes
<root>/refused/r1.txt           4242 bytes   the folder then refuses this account a listing
<root>/cloud.bin                4096 bytes   marked offline: a cloud placeholder
<root>/link-to-alpha                         a junction to <root>/alpha
```

What the scan must report for it, to the byte:

| What | Expected | Why that number |
|---|---|---|
| Bytes seen | 1360 | The five ordinary files. The placeholder adds none, the link adds none, and the 4242 bytes behind the refusal are never seen. |
| Files seen | 5 | The placeholder is not an ordinary file and is counted on its own line. |
| Folders seen | 4 | alpha, alpha/nested, beta, refused. The junction is not a folder and the root is not counted. |
| Links found | 1 | The junction, counted and not followed. |
| Folders that refused | 1 | `refused`, named, with the reason "access denied". |
| Cloud placeholders | 1, holding 4096 bytes in a cloud store | Kept apart from the bytes on the disk, because none of them are on it. |
| Folder total for alpha | 350 bytes over 3 files | Its own two files and the one under nested. |
| Folder total for refused | 0 bytes | Nothing behind a refusal is ever counted. |

The three things that are easiest to get wrong are each built for real, and the fixture **proves it
built them** before any test runs:

- the junction is read back and the file system is asked whether it really is a link;
- the refused folder is listed once after permission is taken away, and the fixture fails if it can
  still be listed - which is what a test run by an account that can read anything would do;
- the cloud placeholder is read back and the offline mark checked.

A fixture that quietly did not build what it says it built would leave a test passing while proving
nothing. That is the failure this guards against.

## The gate

Run on this branch on 19 September 2026, on SOREN_NORTH:

```
.\scripts\test-local.ps1
```

```
CcDirector.Core.UnitTests                outcome=Completed    total=640    executed=640
CcDirector.Avalonia.Tests                outcome=Completed    total=550    executed=550
CcDirector.Engine.Tests                  outcome=Completed    total=63     executed=63
CcDirector.HostedAgent.Tests             outcome=Completed    total=88     executed=88
CcDirector.Launcher.Tests                outcome=Completed    total=197    executed=197
CcDirector.Terminal.Avalonia.Tests       outcome=Completed    total=25     executed=25
CcDirector.Reclaim.Tests                 outcome=Completed    total=116    executed=116
cc-director-setup.Tests                  outcome=Completed    total=25     executed=25
cc-director-setup-engine.Tests           outcome=Completed    total=614    executed=614

RESULT: all projects exited zero.
```

Nine suites, 2,318 tests, nothing failed and nothing skipped. The new suite is 116 tests in about
seven seconds, which leaves the gate's two-minute budget where it was.

```
dotnet test src\CcDirector.Reclaim.Tests
Passed! - Failed: 0, Passed: 116, Skipped: 0, Total: 116, Duration: 6 s
```

## A read-only run on the owner's machine

Not required by phase 1 - the mission asks for that at phase 2 - but run anyway, because it is the
only thing that can show the unseen-gap line doing its job on a real disk. It reads and writes
nothing but its own saved scan, which was pointed at a throwaway folder.

```
cc-cleanup-storage scan "C:\" --index-directory <throwaway> --top 12
```

```
verdict: ok
volume: C:\ is 999149268992 bytes (930.5 gigabytes), of which 819387944960 bytes (763.1 gigabytes)
        are used and 179761324032 bytes (167.4 gigabytes) are free
seen: 714651162363 bytes (665.6 gigabytes) in 3553286 files and 1160094 folders
unseen: 104736782597 bytes (97.5 gigabytes) that the volume counts as used and this scan did not see
refused: 245 folders refused a listing, and every one of them is named below
links: 7548 links and junctions were found, and none were followed
walked: 558.888 seconds
```

The largest folders it named, beside the numbers the Architect measured by hand during the design:

| Folder | This run | The design's measurement |
|---|---|---|
| `C:\Users\soren` | 381.6 gigabytes | 417 gigabytes |
| `C:\Windows\Installer` | 58.8 gigabytes | 58.8 gigabytes |
| `C:\ProgramData\mindzie` | 59.2 gigabytes | 59.2 gigabytes |
| Not visible without an administrator | 97.5 gigabytes | about 97 gigabytes |
| Folders that refused a listing | 245 | 244 |

Three of those five land on the design's number exactly, and the disk has been used for a day since.
Exit code 0, and the numbers were read out of the parsed answer rather than searched for in the
printed text.

## What this proof does NOT cover

Named here rather than left to be discovered.

- **The cloud placeholder line has no evidence from a real disk.** This run found none on C:, so the
  placeholder count is proven only by the fixture tree and by `EntryClassifier`, which covers every
  combination of attributes exactly and runs on every platform.
- **The end-to-end placeholder test is a Windows fact.** A cloud placeholder is marked by attributes
  only Windows keeps, so that one test cannot be built on macOS or Linux. The fixture says so out
  loud and fails with the reason rather than building an ordinary file and passing.
- **Nothing here runs on macOS or Linux.** The engine and the tool hold no platform specific code and
  target a platform neutral framework, and the continuous integration job for .NET runs on Windows
  only, so "it builds and passes elsewhere" is not claimed.
- **The three parked suites were not run.** `CcDirector.Core.Tests`, `CcDirector.Gateway.Tests` and
  `CcDirector.Gateway.UnitTests` are outside the default gate. This change adds new projects and
  touches no source file any of them reads: the only existing files changed are `cc-director.sln`,
  `scripts\test-local.ps1`, `scripts\build-all-tools.ps1` and `tools\registry.json`. The two guards
  that read `tools/registry.json` - `ShippedToolsManifestGuardTests` and `FleetToolsShipGuardTests` -
  look only at tools that are `"type": "python"` with `"ship": true`, and this entry is neither.
- **No number in this document was measured on a different commit.** The gate and the read-only run
  were both taken on this branch's work as it stands.
