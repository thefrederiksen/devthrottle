# Review: the .NET half of the mission check, made green on macOS

**Reviewed:** branch `mission/one-repo-list-green-dotnet`, one commit (`64e7bb8bd`), against `origin/main`
(merge base `74485174f`; none of the files this branch touches changed in the seven commits between the
base and today's `origin/main`, verified by diff).
**Reviewer seat:** Worker on the mission workflow, running a different agent family from the seat that
wrote the code. Reviewer never built and never changed a line of the work; every experiment below ran in a
disposable copy, and this branch's tree was left untouched until this review file was committed to it.
**Date:** 20 September 2026.

---

## Scope

### What I read

The mission document, the author's record (`proofs/green-check-dotnet/README.md`), the repository rules, and
the coding style guide's sections on error handling, logging and testing. Every one of the fifteen changed
files in full: both Gateway rule classes, `RepositoryPaths`, `MicAudioCapture`, `LegacyWorkspaceImport`,
and all nine test files. The `SpeakDialog` startup sequence (`OnDialogOpenedAsync`,
`StartNewServiceAsync`, `ResolveSavedMic`, `EnumerateMics`) to understand where device enumeration sits
relative to the pre-flight and the recorder factory. The author's list of thirteen unfixed
`Path.GetFileName` sites, each checked against `origin/main` — all thirteen exist as listed.

### What I ran, and its result

1. **The mission check's three commands, on this branch, on this Mac** (this machine's only platform):

   | Suite | Result |
   |---|---|
   | `CcDirector.Core.Tests` | Failed: 0, Passed: 4461, Skipped: 18, Total: 4479 |
   | `CcDirector.Avalonia.Tests` | Failed: 0, Passed: 550, Skipped: 0, Total: 550 |
   | `CcDirector.Gateway.UnitTests` | Failed: 0, Passed: 6355, Skipped: 8, Total: 6363 |

   Identical to the author's "after" table, including the skip counts and the pre-existing reasons for them.

2. **The same suites' failing tests on unmodified `origin/main`**, in a scratch worktree, filtered to the
   affected classes. Witnessed with my own eyes: exactly the seven Gateway failures and exactly the seven
   Avalonia failures the author's "before" table names — no more, no fewer. `CcDirector.Core.Tests` was not
   re-run on `origin/main` (the author reports it already green; nothing in this review depends on that).

3. **A standalone probe of the operating-system behaviour finding 1 rests on.** `Path.GetTempPath()` here is
   `/var/folders/...` and `/var` is a link to `/private/var`; `Directory.ResolveLinkTarget(alias,
   returnFinalTarget: true)` on a link whose target is written through a linked ancestor returns that
   target with **both** the `/var` ancestor and the mid-path link still exactly as written. I then ran the
   OLD resolver's algorithm (lifted verbatim from `origin/main`'s text) against that shape: a file plainly
   inside the repository resolved to a string that does not start with the resolved root — **the defect is
   real** — and a link inside the repository that leads out to `/etc` still resolved outside the root —
   **it fails closed**. Both halves witnessed, neither taken on trust.

4. **The author's "watched it fail with the fix reverted" claims, repeated by me.** In the scratch worktree
   at `origin/main` (old product) with the branch's test files copied in: three tests red —
   `IsPathInside_follows_a_link_that_stays_inside_the_root`,
   `IsPathInside_follows_a_link_whose_target_is_written_through_a_linked_ancestor`, and the strengthened
   `A_rule_scoped_to_this_sessions_repository_is_a_candidate` — and the other forty-two tests of the two
   rule classes green. Exactly what the author reported.

5. **The false-green claim, proved by breaking the product in the copy.** I deleted BOTH close guards from
   `SpeakDialog` in the scratch tree — the post-preflight `_closed` check and the
   `StartNewServiceAsync` refusal — a product with its entire close-during-startup protection gone. The
   OLD (unpinned) `ClosingDuringThePreflight_ConstructsNoRecorderAtAll` still **passed** on this machine.
   The branch's pinned version of the same test, against that same broken product, **failed**. So: before
   the change the test certified nothing on macOS (a product with the guards deleted satisfied it), and the
   pinning is what restored its power. The author's claim is right on both halves, and it is the most
   valuable single thing in this change — a test that passes for the wrong reason is worse than one that
   fails.

6. **`RepositoryPaths.FolderName` on the edge cases**, by running the real method from the branch's built
   `CcDirector.Core`: Windows paths, forward-slash Windows paths, trailing separators, `\\server\share`,
   `\\server\share\devthrottle\`, POSIX paths, `/`, `//server/share`, a bare name, padded blanks, null and
   empty. All platform-independent and all sensible. Details under finding 3 below.

### What I could NOT reach

- **Windows.** This machine is a Mac; no Windows host was available to me either. The half of finding 2
  that fails on Windows (a POSIX path compared case-insensitively by the old code) and the Windows arm of
  the `BlockedRename` fixture are argued from the old code's text, which I checked and find sound
  (`OperatingSystem.IsWindows()` selecting `OrdinalIgnoreCase` for every path is right there in the
  reverted file) — but **neither I nor the author has watched them go red on a Windows machine**. The
  change is proven on macOS; on Windows it is proven only by reading.
- The npm half of the mission check (out of this review's subject).
- The PostgreSQL-backed proofs remain skipped (no database rig on this machine), pre-existing and named in
  the author's record. I did not run `-Parked`.

---

## The four product defects, checked one by one

### 1. `RulePrimitives.IsPathInside` and a link target's own ancestors — REAL, and it FAILS CLOSED

Both halves are now witnessed, not read out of the author's record:

- **The defect is real.** Verified three independent ways: the standalone probe (item 3 above); the
  original test failing on unmodified `origin/main` (item 2); and the new hand-built test failing against
  the old product while passing on this branch (item 4). The new test does not depend on where the
  temporary directory happens to sit — it builds the linked-ancestor shape deliberately — and its
  instrumented link assertions mean it cannot silently stop proving anything if link creation fails.
- **It fails closed, not open.** The probe ran the OLD algorithm against an escape link
  (`repo/escape -> /etc`) and the file reached through it: still reported outside the root. The
  under-resolution leaves the TARGET path with its links un-walked, which can only make a genuinely-inside
  path *fail to match* the resolved root — the opposite direction (an outside path being judged inside)
  would require the half-resolved string to acquire the root's prefix, and I could not construct one.
  Nothing about the fix loosens the segment walk, so nothing that answered false before answers true now
  except files that really are inside.
- **The fix is a root fix, not a fallback.** The old `ResolveFinalPath` promised in its own contract that
  a link target's ancestors are resolved; the code simply did not do it on macOS and Linux. The change
  makes the code do what the contract said, with a depth limit that throws (as the contract also already
  promised) rather than guessing.

One pre-existing note, NOT a defect of this change and NOT counted against it: `IsPathInside` decides its
case comparison from `OperatingSystem.IsWindows()` — the pattern the author rightly condemned in
`RuleCandidateFilter`. It is defensible here only because this primitive probes the **local** filesystem
(`File.Exists`, `Directory.ResolveLinkTarget`), so it can only ever answer for paths that live on the
machine running it, and for local paths the host's rules are the machine's rules. But that is also a
boundary the mission should know as it builds a catalogue of paths from several machines: an
`is_path_inside` check evaluated on the hosted Gateway for a repository path pushed up from a Windows
Director mangles under `Path.GetFullPath` on Linux and answers false, fail closed, always. Rules using it
against other machines' paths silently never fire — the same failure shape as this pull request's findings
1 and 2. It is untouched by this branch, older than it, and reported here so the seat that owns the
mission decides where it lands.

### 2. `RuleCandidateFilter` compared paths by the HOST's operating system — REAL, and the fix is sound

The old code's text is unambiguous (`OperatingSystem.IsWindows()` choosing `OrdinalIgnoreCase` for every
path), the macOS half was witnessed red on `origin/main` and green on the branch, and the strengthened
test fails against the reverted product (item 4). The fix genuinely lets the path's own shape decide:

- A drive letter (`written[1] == ':'` with an ASCII letter before it) or a `\\server\share` prefix routes to
  the Windows comparison — case-insensitive, separators unified, trailing separator dropped. Correct for
  how Windows matches its own paths.
- Anything else compares exactly, with only a trailing `/` dropped. Correct for Linux, and the decision
  to leave backslashes alone in POSIX paths is right (a backslash is a legal character there; the old
  `Normalize` only appeared not to mangle them because on Unix both separator characters are `/`).
- **Ambiguous shapes.** I looked for a wrong answer in both directions and could not produce a false
  match: a POSIX path can never acquire a drive prefix through `AsWindows`, and a Windows path compared
  POSIX-style cannot arise (if either side is Windows-shaped, both go through the Windows comparison,
  which normalizes both). Every ambiguous case I could construct — `//server/share` (a share written with
  forward slashes, which the shape test reads as POSIX), a relative path, a POSIX name containing a colon
  behind a leading `/` — fails **closed**: the rule does not fire. That is the right failure direction for
  a rule filter. Two boundary behaviours worth stating plainly so the fix is not oversold:
  - `//server/share` compared case-**sensitively** while `\\server\share` is not. Windows APIs do not emit
    the forward-slash spelling, so this is unlikely to be met; it fails closed if it is.
  - The path's shape cannot express the **case-insensitivity of a Mac's default volume**. A rule scoped
    `/users/dev/repos/scratch` and a session in `/Users/dev/ReposFred/scratch` on a default
    case-insensitive APFS volume are the same directory to the Mac and different strings to this
    comparison. Fail closed again, and no shape test can fix it — only the machine could say. Worth the
    owner knowing as the fix's honest boundary, not a reason to change it.

The mission-relevant judgement is confirmed: the fix removes the wrong machine from the decision, which
is this mission's whole problem in miniature, and does not swap in a new one.

### 3. `RepositoryPaths.FolderName` — correct for the cases that matter

Probed directly (item 6): trailing separators on both spellings, forward-slash Windows paths, a bare name,
padded input, null and empty all answer what the caller needs, identically on every platform. Two edge
answers worth recording, neither a defect: a bare drive root `D:\` answers `D:` — which is exactly what the
old code's `Path.GetFileName("D:".TrimEnd(...))` answered on Windows, so no regression, and a drive root is
not a repository; `\\server\share` answers `share`, matching `Path.GetFileName` on Windows. The helper is
used at the one site the failing test pointed at, and the thirteen remaining `Path.GetFileName` sites are
correctly reported rather than swept — that restraint is right for a Developer seat and the list checks out
line by line.

### 4. `MicAudioCapture.Dispose` — the fix is right and hides nothing

`Stop()` cannot throw (it catches internally and logs — verified in the source), so removing the old
`catch { }` swallow is safe; the only throwing member is `_waveIn.Dispose()`, now caught and logged in the
repository's format. The two construction tests dispose through `using`, so this machine's green Avalonia
run IS the regression proof for the macOS failure (they were red on `origin/main`, green on the branch).
Catching in `Dispose` is not fallback programming — it is the standard disposal contract (a throw from a
cleanup path replaces the real problem with a wrong one), and the failure still surfaces loudly at
`Start()`, where a caller can act. The wider macOS Speak-dialog defect is correctly left as a reported
product decision.

---

## The ten test faults: was anything made green by weakening it?

**No.** Checked against the actual diff, assertion by assertion:

- Nothing was deleted, skipped or loosened: every suite's test count went **up** or stayed flat
  (Gateway 6360→6363, Core 4463→4479, Avalonia 550→550), no assertion was edited except to be made
  stronger (the repository-candidate test's scope gained mixed separators, casing AND a trailing
  separator), and the skip counts are unchanged with the same pre-existing reasons.
- **The unreadable-process test is a real proof, not a dressed-up skip.** It asserts four things in order,
  all against real system calls on this machine: non-superuser premise (fails loudly otherwise), `kill(1,
  0)` refused with EPERM (the operating system positively denying rights over the initial process),
  that same denied process still readable and alive, and a genuinely ended process throwing
  `ArgumentException`. It ran green here. Its failure mode is the one the original author wanted and did
  not get: on a future host where the Unreadable state becomes reachable (a hardened Linux `procfs`, for
  instance), step three goes red and names what changed. The old `Assert.Fail` made a false claim — it
  reported an unproven state as a product failure when the state cannot exist on the platform at all; the
  replacement proves the property that makes the absence correct.
- **The recovery tests now prove their fixture.** `BlockedRename` attempts a real rename and throws if
  the host allowed it, undoing the probe first and naming the superuser as the likely cause — the exact
  opposite of the quiet false-state those tests ran in before, and the mechanism is the product's own
  genuine failure on each platform (no delete-sharing on Windows, a non-writable containing directory on
  Unix). The assertions of both tests are untouched.
- **The false green next door is real and is fixed.** Item 5 above proves both halves on a deliberately
  broken product: unpinned, the test passed with the entire close-guard mechanism deleted; pinned, it
  fails. This also means the file's own header claim ("each test fails when its own guard is removed")
  is now true on this machine, where before it was false.
- The `cmd.exe` → `/bin/sleep` change alters the fixture only; every liveness assertion is unchanged and
  now runs against a real live and a real dead child on this machine.

## Findings

**None against the change.** Every product claim checked out; every test-fault fix strengthened or
preserved its test; nothing in the record was overstated in any particular I could reach. The three
observations above — the pre-existing `is_path_inside` local-filesystem boundary (the one I would most
encourage the mission owner to read), the two fail-closed boundaries of the shape-based comparison, and
the `D:\` edge answer — are recorded for the seats that decide, and none of them asks this pull request to
change.

## Verdict

**Approve.** The four product defects are real (two of them witnessed red on unmodified `origin/main`),
both halves of the containment defect — real, and fail-closed — were verified by probe rather than taken
from the record, the shape-based path comparison is a genuine root fix with only fail-closed ambiguities
at its edges, and the ten test faults were fixed without weakening a single assertion. The record is
accurate. What remains unwitnessed, on Windows, is stated in the scope section and should be read before
the Windows half of finding 2 is repeated to the owner as observed fact.
