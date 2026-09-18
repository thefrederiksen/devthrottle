# PR 3048 adversarial inspection

**Verdict: FAIL — 1 finding.** The changed registry test retains its intended assertion. A nearby Gateway test still has a reproducible timer/sleep scheduling flake.

## 1. Does polling weaken the registry test?

No. In Release, the unmodified `Register_WhenStreamUpNeverArrives_TearsTheSinkDownAfterTheTimeout` ran and passed (1/1). I then temporarily removed the sole `Teardown(streamId, "stream did not start")` call from `GatewayStreamRegistry.TimeoutUnclaimed`, built and ran the same filtered test in Release, and observed **1/1 fail** after 10 seconds with `Timed out after 10 seconds waiting until the open timeout tore the sink down.` I restored the source and reran; it passed again. This mutation establishes that the poll does not pass when timeout teardown is absent. The test separately asserts the returned token is cancelled and `LiveStreamCount == 0` after sink completion. It does not prove teardown occurred precisely at 100 ms; that is not its stated claim.

## 2. Can the ten second cap flake?

It remains a finite scheduling bound. The mutant above demonstrates that the test fails at ten seconds if the timer callback and test continuation have not observed completion. No finite cap can be proven above *every* plausible runner stall. Here it allows roughly 9.9 seconds beyond the 100 ms open timeout, versus the old 300 ms margin. The local unmodified test completed in 114 ms on its first run and passed again after restoration. I found no observed ten second stall in this inspection, so this is a residual risk rather than a finding that the new cap is itself a demonstrated flake. The helper uses `DateTime.UtcNow`, so a forward wall-clock jump could also shorten the real-time wait; a monotonic clock would avoid that separate edge case.

## 3. Does `WaitUntil` busy-wait, leak, or swallow condition exceptions?

No on all three. Its unsuccessful iteration awaits `Task.Delay(10)`, so it yields instead of spinning. It creates no owned stream, timer, token source, or registration; each awaited delay completes before the next iteration. I temporarily added and ran two Release test probes: a condition returning true on call three returned after exactly three calls, and a condition throwing an `InvalidOperationException` on call three propagated the **same exception object** after exactly three calls. Both probes passed (2/2) and were removed. The timeout mutant also exercised the failure path. This verifies the helper's control flow, not memory usage under an indefinitely suspended test runner.

## 4. Did v2.6.0 touch these files?

`git log --date=short --format='%h %ad %s' -- src/CcDirector.Gateway/Streaming/GatewayStreamRegistry.cs` shows only `120cf565` (2026-07-21) and `7fe9d383` (2026-07-12). The same command for `src/CcDirector.Gateway.UnitTests/GatewayStreamRegistryTests.cs` shows `a084c422` (2026-08-02) and this PR's `9b3f01ee` (2026-09-17). Thus the release period introduced no registry change and no pre-PR test change. This verifies the two named paths in local Git history; it does not independently verify the cited release run or all other registry callers.

## 5. Other same-shape tests

**Finding 1 — `WingmanVoiceServiceTests.ASpeechProvidersRetryAfter_IsHonoured_WhenItAsksForLongerThanTheRung`** (`src/CcDirector.Gateway.UnitTests/WingmanVoiceServiceTests.cs:2421`). It books an 800 ms retry, awaits a fixed 300 ms delay, then requires `handler.Calls == 1`. If that delay's continuation runs after the retry callback, correct production behavior makes the assertion fail. I reproduced the scheduling shape by temporarily changing only this test's delay from 300 to 1000 ms: its Release run failed with `Expected: 1; Actual: 2`. After restoring it, a Release run of this test together with the registry test passed (2/2). The mutation models a delayed continuation; it does not show a spontaneous loaded-runner failure in this environment. This is an early-before-timeout assertion, the symmetric clock-race to the PR's late-after-timeout assertion.

I searched every `Task.Delay` and `Thread.Sleep` in Gateway unit tests and inspected the surrounding assertions. Other sleeps in `GatewayStreamRegistryTests` gate backpressure or frame flow and do not assert a timer callback has completed after a fixed margin. Several `WingmanVoiceServiceTests` use fixed sleeps for retry-absence checks or timer-window checks, but the test named above is the one whose false failure I reproduced. I did not count general time-based tests as confirmed instances of this defect.

## Commands and restoration

All runs used `/Users/soren/.dotnet/dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj -c Release --filter … --no-restore`. The mutated production and test files were restored byte-for-byte from backups after each probe; `git status --short` showed no tracked changes before this report was written. No product or test fix was made.
