# Review: pull request 4586 - move to .NET SDK 10.0.403 for the patched Native AOT compiler

Reviewer: a separate review session (Factory Inbox - Reviewer - SDK 10.0.403 advisory fix), 5 November 2026.
Reviewed commit: e01b02bdb on branch fix/ilcompiler-advisory, read in the worktree D:\ReposFred\devthrottle-fi-4586
(detached at that commit) and diffed against origin/main (b9fe38b3e). The worktree was not edited; the only
writes were build outputs under bin and obj and test results in the session scratch folder.

## Verdict

Approve. The change does what it says and leaves no pin behind in anything that builds: the single
setup-dotnet pin in ci.yml, all ten in release.yml and global.json now name 10.0.403; no other workflow
installs an SDK; the Dockerfile's floating sdk:10.0 tag resolves today to the same image as sdk:10.0.403, so the
container build satisfies the new global.json. NuGetAudit is not turned off and the advisory is not suppressed
anywhere. The rollForward setting is right for the purpose. One documentation sentence is now false
(CONTRIBUTING.md still says global.json pins 10.0.301); that is the only finding, and its harm is small. Nothing in
this change can cause the Avalonia.Tests problem the author reports, and I could not reproduce that problem on
this commit with the new toolchain: the whole suite run alone passes 1215 of 1215 in 44 seconds.

## Scope

Checked:
- The diff itself (3 files, 12 lines).
- Every workflow under .github/workflows and the composite action under .github/actions for setup-dotnet,
  global-json-file, dotnet-version, dotnet images and quality channels.
- Dockerfile, .dockerignore, the deploy workflow's image build step, and what the MCR tags resolve to today.
- global.json (the only one in the tree), its rollForward and allowPrerelease.
- Every csproj, props, targets and config file for NuGetAudit, NuGetAuditSuppress, NoWarn on NU19xx.
- Scripts (ps1, sh, py) and docs for a named SDK version.
- The advisory itself (GitHub advisory database) and the compiler version each installed SDK bundles.
- The gate records and run folders of the author's test-local runs today, and a reproduction of the Avalonia
  suite on this commit.

Not checked: that 10.0.402 bundles compiler 10.0.13 (the pull request says so; 10.0.402 is not installed here,
and it is below 10.0.14 either way); the macOS and Linux release jobs actually running on a runner.

## What was verified, with the evidence

### The advisory and the compiler version

- GHSA-p3jp-9v83-76w9 is Microsoft Security Advisory CVE-2026-111497, .NET Brotli decoder out-of-bounds write,
  severity high, published 2026-11-03T17:33:48Z. For the nuget package Microsoft.DotNet.ILCompiler it lists
  >= 10.0.0, < 10.0.14, first patched 10.0.14 (and the same range for the NativeAOT runtime packs win-x64 and
  linux-x64). Read with `gh api advisories/GHSA-p3jp-9v83-76w9`.
- SDK 10.0.403 bundles ILCompilerPackVersion 10.0.14 and BundledNETCoreAppPackageVersion 10.0.14
  (C:\Program Files\dotnet\sdk\10.0.403\Microsoft.NETCoreSdk.BundledVersions.props, lines 72 to 80 and 22).
- SDK 10.0.303 bundles ILCompilerPackVersion 10.0.11, which is inside the vulnerable range. So the old pins
  restored a vulnerable compiler and the new one does not. The compiler version comes from the SDK under
  PublishAot (CcDirector.RuleHook is the only PublishAot project, and it pins no ILCompiler package), so the
  SDK is the right place to fix it.

### No pin left behind

- `grep -rn dotnet-version .github/` finds 11 lines, all "10.0.403": ci.yml line 281, release.yml lines 99,
  152, 230, 296, 490, 541, 583, 660, 711, 785.
- No other workflow (deploy-hosted-gateway, docs-drift, install-mac-smoke, provision-hosted-gateway-slots,
  rollback-hosted-gateway, set-gateway-staff-emails) and no composite action installs an SDK or names one.
- Exactly one global.json in the tree (repo root). No Directory.Packages.props, no nuget.config.
- Scripts: `grep -rnE "10\.0\.[0-9]{3}"` over scripts and tools finds nothing. local-build-linux.sh and
  local-build-mac.sh print a dotnet-install hint with `--channel 10.0`, which installs the newest 10.0 SDK,
  so it satisfies the new global.json without a change.
- A grep for any three-digit 10.0.x version across the tree finds only historical records: mission review
  and proof files that state which SDK a past run used. Those are records and must stay as written.

### The container image build

- Dockerfile builds with `FROM mcr.microsoft.com/dotnet/sdk:10.0` and copies the whole repository, so the
  publish inside the container is governed by the new global.json.
- MCR manifest digests today: sdk:10.0 and sdk:10.0.403 both resolve to
  sha256:de4fe90a98845d75910b9769662465c84de1a934665c431279d1382b2bddc02b; sdk:10.0.402 is a different digest.
  So the image build gets 10.0.403 and global.json is satisfied. If MCR moves ahead, latestMinor accepts the
  newer SDK; if a future global.json ever outruns the floating tag, the build fails loud with "A compatible
  .NET SDK was not found", never silently with an older one.
- The container's build graph is CcDirector.Gateway.Host -> CcDirector.Gateway and
  CcDirector.Gateway.Migrations.Postgres. CcDirector.Gateway links one source file from RuleHook
  (HookWireLimits.cs) and does not reference the project, so the image build restores no ILCompiler and was
  never affected by the advisory. The runtime image aspnet:10.0 serves 10.0.14 (tags 10.0.10 to 10.0.14 exist).

### rollForward

- `latestMinor` with version 10.0.403 means: any installed 10.x SDK at or above 10.0.403, highest wins;
  anything below is refused. That is the behaviour wanted here: an SDK below 10.0.403 would restore a
  vulnerable compiler, and the refusal is explicit. allowPrerelease stays false.
- Checked on this machine: `dotnet --version` in the worktree answers 10.0.403. In a scratch folder holding
  origin/main's global.json (10.0.301, latestMinor) it ALSO answers 10.0.403, because 10.0.403 is installed and
  latestMinor already picks the highest. So on any machine that has 10.0.403, this pin changes nothing; its
  only effect is to refuse machines still on an older SDK. Machines known to be on 10.0.301 (devlinux at
  ~/.dotnet, the Mac mini) will need the SDK before they can build this; the error names the version.
- On the GitHub runners setup-dotnet installs exactly 10.0.403; if a runner image carries a newer 10.x,
  latestMinor would select it, which still carries a compiler at or above 10.0.14. No harm.

### NuGetAudit

- No NuGetAudit, NuGetAuditSuppress, NuGetAuditLevel or NoWarn NU19xx in any csproj, props, targets or config
  under src or tools, and none in Directory.Build.props (it holds only the version and the test guard links).
- The three NU1903 mentions in CcDirector.Core.csproj, CcDirector.Engine.csproj and CcDirector.Gateway.csproj
  are older comments about SQLitePCLRaw and SixLabors pins, not suppressions.
- The two mission documents that mention NuGetAudit=false describe a local run from October; nothing in the
  tree sets it.

## Finding

### F1 (low) CONTRIBUTING.md names the old pin

CONTRIBUTING.md line 13: "You need the .NET 10 SDK (`global.json` pins 10.0.301)". After this change that
sentence is false. Harm: a contributor who installs 10.0.301 as instructed gets "A compatible .NET SDK was not
found" on the first build. It is a one-word edit (10.0.301 to 10.0.403), and the review brief asked for docs
that name an SDK version, which is why it is recorded; it does not block the merge.

## The Avalonia.Tests question

The author reports CcDirector.Avalonia.Tests stopped at the 120-second ceiling in test-local and did not finish
in 30 minutes when run alone, cause not established. Can anything in this change plausibly cause that?

No, and here is the proof rather than the argument:

1. The change cannot alter which SDK builds the tests on the author's machine. Under origin/main's global.json
   (10.0.301, latestMinor) this machine already selects 10.0.403 (shown above). The runtime the tests run on is
   chosen from the installed runtimes by the test host's roll-forward, not by global.json, and runtime 10.0.14
   was installed at 12:03 local today together with the SDK (folder times under C:\Program Files\dotnet). So main
   and this branch build and run identically here.

2. What the gate recorded is not a slow suite but five failed tests under the concurrent run. The gate record
   (%LOCALAPPDATA%\cc-director\test-gate\runs.jsonl, run started 2026-11-05T18:42:49Z, commit e01b02bdb, run
   folder cc-test-local-17121007) shows CcDirector.Avalonia.Tests with outcome NO-TRX, exit 1, 0 executed. Its
   stderr log names the failures, at 36 to 38 seconds into the run, long before the ceiling:
   RepositoryDetailRenderTests.DetailHeader_OffersCopyReport_AndNoHandOffDialog,
   DirectorTeamScreensTests.Capture_D1_D2_D3_DrawRealPictures, TryAnswer_EmptyName_RefusedWithAReason,
   TryAnswer_GivesTheSelectedTeamAndTheTypedName and
   TeamChoiceDialog_SelectionChanges_NameFollowsUntilThePersonTypes. The test host then wrote no results file.
   The last gate run before the SDK install (18:42Z is after; 10:18Z on commit 330350482, run folder
   cc-test-local-9e4b0916) has the same suite Completed: 1215 passed in 45 seconds.

3. Reproduction on this commit, this machine, SDK 10.0.403, runtime 10.0.14 (the xunit banner prints
   "64-bit .NET 10.0.14"), run in the review worktree with a hang detector armed:
   - The two classes named above, alone: 25 of 25 passed, 13 seconds.
   - The whole suite alone (`--filter "Gate!=Quarantine" --blame-hang --blame-hang-timeout 5m`): Passed,
     Failed 0, Passed 1215, Skipped 0, Duration 44 seconds, wall clock 71 seconds. The hang detector never fired.
     Results in the session scratch folder, av-results\five.trx and av-results\full.trx.
   That is the same count and the same time as the pre-install gate run.

4. No leftover test host or dotnet process from the author's solo run exists on the machine now, and that
   solo run left no gate record (it was not run through the script), so I cannot read what it did.

So the Avalonia suite is healthy on the new toolchain at this commit, and the five failures the gate saw are
load-sensitive, not SDK-sensitive: they passed alone and pass in a solo full run. Whatever stopped the author's
30-minute run was in that worktree or that moment on the machine, not in this diff. If it recurs, run the
suite through the gate again and read the suite's .log.err in the run folder; the test names are there within
40 seconds.

## Checks the brief asked for, answered in one line each

- No pin left behind: correct. 11 workflow pins and global.json all 10.0.403; nothing else names an SDK.
- rollForward right: yes, latestMinor; refuses below 10.0.403 loudly, accepts the floating Docker tag.
- NuGetAudit on, advisory not suppressed: correct, nothing in the tree turns it off or suppresses NU1903.
- Container image gets a matching SDK: yes, sdk:10.0 equals sdk:10.0.403 by digest today, and that build never
  restores ILCompiler anyway.
- Release workflow: all ten publish jobs pin 10.0.403; the tag-guard job installs no SDK.
- Avalonia.Tests: not caused by this change; not reproducible on this commit with the new toolchain.
