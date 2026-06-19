# Issue 551 - Proof Note

## Problem

The main branch continuous integration check "Build and Test (.NET)" was red on every
recent commit. The cause was the test
`SessionAskRunnerTests.AskAsync_ClaudeCode_RealDriverWithFixtureTranscript_ReturnsParsedAnswer`
in `src/CcDirector.Core.Tests/Drivers/SessionAskRunnerTests.cs`. That test drives the real
`ClaudeDriver`, which resolves and requires `claude.exe` on the PATH.
`ClaudeDriver.ResolveExecutable` (in `src/CcDirector.Core/Drivers/ClaudeDriver.cs`, around
line 131) throws a `FileNotFoundException` when `claude.exe` is not on the PATH. GitHub
continuous integration runners have no Claude Code install, so the test always threw there
and failed the merge gate for the whole repository.

## Fix

Marked that single integration smoke test with a static skip attribute, matching the
existing convention already used on the main branch:

    [Fact(Skip = "Requires claude.exe on PATH; not available on CI runners")]

The same static skip convention is used by `NulFileWatcherTests` (line 35) and
`SessionEdgeCaseTests` (line 114).

### Why static skip and not a runtime skip

The test project uses xUnit version 2 (`xunit` package version `2.*` in
`src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj`). xUnit version 2 has no clean
runtime skip mechanism such as `Assert.Skip` or `Xunit.SkipException`; that was introduced
in xUnit version 3. The issue explicitly instructed falling back to the static
`[Fact(Skip = "...")]` convention when no clean dynamic-skip mechanism exists, so that is
what was used.

The deterministic fake-backend tests in the same file (which already cover the ask and
parse logic) were left untouched and keep running everywhere.

No change was made to `ClaudeDriver` production code. The only source change is the one test
attribute plus an explanatory comment.

## Verification

Build of the full solution from the worktree root:

    dotnet build cc-director.sln -p:WarningsNotAsErrors=NU1903
    Build succeeded.
        0 Warning(s)
        0 Error(s)

(The WarningsNotAsErrors=NU1903 flag suppresses a local-only SQLite NuGet online-advisory
restore warning that is unrelated to this change. No project file change was committed for
it.)

Test run:

    dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj --filter "FullyQualifiedName~SessionAskRunnerTests"

    [xUnit.net]  SessionAskRunnerTests.AskAsync_ClaudeCode_RealDriverWithFixtureTranscript_ReturnsParsedAnswer [SKIP]
      Skipped ...AskAsync_ClaudeCode_RealDriverWithFixtureTranscript_ReturnsParsedAnswer [1 ms]

    Passed!  - Failed: 0, Passed: 17, Skipped: 1, Total: 18

The real-driver test is skipped (not failed). The other 17 SessionAskRunner tests still run
and pass. This developer machine happens to have claude.exe on the PATH, yet the static
skip applies regardless, which is exactly the behavior the continuous integration runner
(no claude.exe) needs: skip, never fail.

## Proof Target

The real proof is the green "Build and Test (.NET)" check on the pull request branch, plus
the local test output above showing the real-driver test skipped and the fake-backend tests
passing.
