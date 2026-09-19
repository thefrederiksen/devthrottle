# Review of phase 1: scan and report

Written by the Reviewer for phase 1 of the Reclaim the Disk mission, 19 September 2026. The seat's
mandate is `review-brief-phase-1.md` beside this file. This review covers commit 9d8f08640 on
branch `reclaim/phase1-scan-and-report`, open as pull request #3122 against `main`.

## Scope

What I read: every file this branch adds or changes - the whole of `src/CcDirector.Reclaim`, the
whole of `tools/cc-cleanup-storage`, all nine test files, the solution and registry and gate
scripts changes, the tool's README, the mandate, the Developer's proof, the mission document, and
the parts of `docs/CodingStyle.md` and `docs/axi-standard.md` the code cites. I read the shipped
references it leans on (`FileLog`, `CcStorage.ToolLogs`, the shared list renderer in
`tools/cc_shared/axi_output.py`, the `cc-click` registry entry, the manifest guards) from
`origin/main`, not from the shared checkout.

What I ran: searches only, in a worktree cut from `origin/main`. No removal code execution, no
build, no test run, no run of the tool.

What I could not reach: the gate's result and the real-machine numbers are the Developer's own
account in `phase-1-proof.md`, read and cross-checked for internal consistency (the seen and
unseen numbers sum to the used number exactly), not reproduced by me; a reviewer who builds would
answer that differently. The continuous integration result of this pull request I did not read; by
the repository's own rule the local gate is the gate, and the Developer reports it green.

## The first question: does anything in this phase change the disk?

No. I searched the whole diff rather than trusting the pull request's account of itself. There is
no delete, no move, no holding folder, no purge, no apply flag, and no `recommend` anywhere in the
product code - the only matches for those words are comments and help text saying the tool never
does them. The unknown-command test proves `recommend` is refused by name. The one `Directory.Delete`
in the suite is the test fixture tearing down the tree that same test built, which the mission
allows. The only thing the product writes is its own saved scan, which the mandate requires. The
scanner itself opens no file at all, so it cannot even recall a cloud placeholder while measuring.

## The second question: does any check pass by an absence?

None found. Every empty state prints something definitive: `count: 0`, list headers with a nought in
them, and an empty or zero scan reporting `verdict: broken` with exit code 1 rather than "nothing
here". The one test whose fixture depends on the operating system agreeing to something (denying a
listing, making a junction) proves the fixture actually did it before the test runs, and fails the
whole run loudly if it did not - so the tests cannot pass on a tree that never held the thing they
prove. The negative assertions (bytes behind a refusal never counted, no folder total under a
junction) are the properties those tests exist to prove, and the exact-number assertions beside
them would catch a scanner that did count them.

## Findings

Two. Both are real; neither blocks the safety of the phase, which is the absence of removal code.

### 1. The saved scan is looked up by a case-sensitive key on a platform whose paths are not

`ScanIndexStore.PathFor` fingerprints the canonical root path with the letter case preserved, and
`report` resolves the saved scan through that same fingerprint. On Windows the file system does not
distinguish case, so `D:\Repos` and `d:\repos` are one folder - but they hash to two different
fingerprints and therefore two different index files.

The harm, on the mission's first platform:

- `cc-cleanup-storage scan "C:\Some\Folder"` followed by
  `cc-cleanup-storage report "c:\some\folder"` answers "There is no saved scan at ... Run:
  cc-cleanup-storage scan", with exit code 1. That answer is false - the saved scan exists - and
  the tool's own advice sends the caller to re-walk a disk the proof shows takes over nine minutes
  on this machine. An agent that lowercases a drive letter, which agents do constantly, hits this
  on its first `report`.
- After both spellings are scanned, the saved-scans listing shows the same folder twice.
- This file is explicitly the contract between the Launcher that writes it (phase 5) and the
  Director that resolves it by root path (phase 6). The fragile key is being inherited by the
  exact flow the index exists for.

The code comment defends the choice: "two spellings of one folder are therefore two files, which is
loud, rather than two folders sharing one file, which would be wrong." That trade is real on Linux,
where `Cache` and `cache` are genuinely two folders. On Windows it inverts: the two spellings are
one folder, so sharing one file would be right and two files is wrong. The same file already
solves the analogous problems - trailing separators and `.` segments are folded by
`DirectoryScanner.Canonical`, with a test proving it - and the case fold is the one that is
missing. It should be platform aware: fold case in the fingerprint on Windows (or resolve to the
casing the disk itself reports when the folder exists), and keep the exact bytes on platforms
where case distinguishes folders.

### 2. `--json` is read and then dropped by `--version` and `--help`

`cc-cleanup-storage --json --version` prints the version as a text line. The command line reader
accepts `--json`, sets the flag, and then the `--version` branch returns a request with the
machine-readable flag switched back off (and `--version --json` returns before the flag is read at
all). The mandate's own words describe this class of defect: "a flag that is read and dropped is
not a small untidiness but a wrong answer". The harm is small - a caller asking for the
machine-readable version of the tool gets a prose line, and the exit code is still 0 - but the
checklist item is unambiguous and the fix is one field in two places. There is no test covering the
combination, so nothing would notice it drifting further.

## What I checked and judged correct, so the next seat knows it was checked

- **Fallback programming.** Every `catch` in the engine either turns an expected failure into a
  named value or throws with the exact command that puts it right. A listing that fails part way
  is discarded whole and the folder is named as refused with a code - a conservative, honest answer
  rather than half a folder presented as a whole one. Nothing carries on with a degraded answer.
- **Logging.** Not every public method writes a log line: `Canonical`, `PathFor`, `SizeText`,
  `AxiOutput.Value` and `EntryClassifier.Classify` log nothing, and `DirectoryReadResult.Read`
  logs only its refusals. I read each and judge the deviation right: the classifier and the value
  renderers run per directory entry, and the proof's real scan covered 3.5 million files, so
  logging them would add noise and time with nothing diagnosable gained. Every method with real
  input and output logs its entry, exit and failure.
- **Try-catch placement.** Three boundaries in the engine hold try-catch inside service code. They
  cite the "Result Objects for Expected Failures" section of `docs/CodingStyle.md`; I verified that
  section exists and this is its intended use.
- **Hard links.** Two names for one file are each counted, because a hard link is not a reparse
  point. The mission accepts this by design - the component store's real size "is only known by
  asking Windows" and its rule arrives in phase 4 - and the unseen line can legitimately go
  negative, which `SizeText` renders with its sign rather than hiding.
- **The placeholder fixture on macOS and Linux.** `CloudPlaceholder` throws there, so the
  end-to-end placeholder test is red by design on those two platforms rather than skipped. I
  verified the continuous integration .NET job runs on Windows only, so no gate goes red, and the
  proof discloses it. I judge the loud failure the right side of the trade - a skipped test
  certifies nothing - but the next seat should know `dotnet test src\CcDirector.Reclaim.Tests` is
  red on macOS and Linux on purpose.
- **One comment that overstates.** The `MaximumFolderDepth` comment says the cap keeps a saved
  index a bounded size. The cap bounds depth, not size: a volume scan at depth ten still records a
  row for every folder within ten levels of the root. The default depth of two is what actually
  keeps an index small. A comment inaccuracy, not a code defect.
- **The list output shape.** I compared `AxiOutput` line by line against the shared Python module
  it reimplements (`tools/cc_shared/axi_output.py`): the plain-value rule, the quoting rule, the
  escape set, the empty-string case and the field-name restriction all match.
- **The proof's claim about the manifest guards.** The registry entry is type `dotnet`, and I
  read both guard tests on `origin/main`: they read only tools that are type `python` with
  `ship: true`, so the new entry cannot trip them.

## Verdict

Phase 1 contains no removal code of any kind, and I searched for it rather than taking the
description's word. No check passes by an absence, no failure is swallowed, and the proof names its
own limits instead of hiding them. The engine and the tool do what the mandate asked and nothing
beyond it.

Two findings above go back to the Developer to answer - accepted, or declined with the reason. The
first is the one I would not let reach phase 5 unanswered: the case-sensitive index key is a wrong
answer on Windows today and becomes the Launcher-to-Director contract later.
