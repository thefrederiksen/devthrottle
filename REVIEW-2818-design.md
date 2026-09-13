# Design review: issue #2818

**Verdict: CHANGES NEEDED**

Reviewed `DESIGN-2818.md` against `origin/main` at `678b8ec4d6a940dd294cdd28500b3ff9ac061c0c`. At the time of the source review, the working checkout and the freshly fetched main branch pointed to that same commit.

The reviewed design has SHA-256 `86361514D54C17938E4BDFFB570F2CA2B1145B9BE48BD8FC21ACC1197B4CD892`. Its content was checked again before this review was written to disk and matches that version.

## 1. Prompt recovery needs a complete safety contract

**Design reference:** lines 65-80, especially the instruction that an inconclusive attempt "waits again instead of clearing."

Preserving text when the machine is slow is the right objective, but the design does not specify the extended deadlines, the number of additional waits, cancellation, or the outcome when evidence remains inconclusive. Those decisions affect whether a send eventually reports a result and whether later sends can corrupt the retained prompt.

The existing `ScreenShowsText` in `src/CcDirector.Core/Drivers/TerminalSubmit.cs` returns `false` both when no screen provider exists and when the supplied rows do not match the text. The code also acknowledges that rows can be captured during a repaint. A negative result therefore does not establish that the composer is empty or that typed input cannot arrive later.

The existing attempt loop writes the text at the start of each attempt. Reusing that loop for an additional wait would write another copy unless observation is explicitly separated from typing. At the session boundary, `Session.SendTextAsync` records an exception as a failed delivery and rethrows it. That method does not serialize terminal sends or retain an unresolved-composer state. Merely throwing while preserving the text therefore leaves the next send able to append to it.

**Required design changes:**

- Define present, positively absent, and unknown evidence, including what makes a screen observation sufficiently current to justify clearing or retyping.
- Give the Tight and Critical deadlines concrete values and specify a finite observation budget and cancellation behavior. Additional observations must not write the prompt again.
- Define what happens when the budget expires with unknown evidence, how the retained prompt is reported, and how subsequent sends avoid appending to it.
- Clarify whether the positive-evidence requirement applies under Normal and Unknown readings as well as under measured pressure, and how explicit timeout overrides interact with that requirement.
- Require behavioral tests for delayed echo, unavailable or incomplete screen evidence, eventual timeout, cancellation, and a subsequent send. Assert the actual text writes, Escape writes, Enter writes, and delivery outcome.

## 2. The threshold predicate contradicts its stated purpose

**Design reference:** lines 39-43.

The design says the absolute threshold prevents a large machine from being called Critical while it still has many gigabytes available. Its proposed rule is Critical when available memory is under 7 percent **or** under 800 megabytes.

For a machine with 128 gigabytes total and 8 gigabytes available, the available fraction is 6.25 percent. The proposed rule calls that Critical even though 8 gigabytes remain. The absolute branch of an OR expression cannot prevent the percentage branch from classifying the machine as Critical. The Tight rule has the same structural issue.

**Required design changes:** settle the intended relationship between the percentage and absolute thresholds and write the exact predicate. Pin its behavior with examples covering both large machines with several gigabytes available and small machines near exhaustion, in addition to boundary tests. Whether to use a conjunction, a cap, or another rule is a design decision; the current prose and predicate cannot both be implemented faithfully.

## 3. The thread-pool proposal does not establish recovery under memory pressure

**Design reference:** lines 49-61.

The proposed startup adjustment supplies neither the new minimums nor a sizing rule. The Windows implementation has two blocking tasks per session in `src/CcDirector.Core/ConPty/ProcessHost.cs`: `StartDrainLoop` performs synchronous output reads inside `Task.Run`, and `StartExitMonitor` waits indefinitely for process exit inside another `Task.Run`. Eighteen sessions can therefore occupy 36 workers before other work is considered.

Raising the worker minimum may address worker starvation, but the design's original symptom also includes the operating system delaying execution while paging. The design contains no measurement showing that changing the pool minimum resolves that case. Increasing the minimum can also increase memory consumption, resource contention, and scheduling delays, as documented by [Microsoft's ThreadPool.SetMinThreads reference](https://learn.microsoft.com/en-us/dotnet/api/system.threading.threadpool.setminthreads?view=net-10.0#remarks).

**Required design changes:** specify the chosen minimums and supported session load, accounting for both blocking tasks per session and capacity for other work. Justify changing the completion-port minimum separately from the worker minimum. Define how a refused adjustment is detected and reported. Require measurements of heartbeat timing and resource use under worker starvation and paging; pure deadline-selection tests do not cover either behavior. State the limits of the mitigation if the process still cannot run before the peer's silence deadline.

## 4. The proposed logging misses delayed timer execution

**Design reference:** lines 82-85.

Adding memory readings to existing skip and slow-push logs does not make scheduling delay observable. In `src/CcDirector.ControlApi/GatewayStreamClient.cs`, `RePushAsync` records its start time when it begins executing and measures elapsed time from there. `RePushTick` reports a skip when disconnected or when a previous push is still running.

A callback can begin after a long scheduling delay, find a connected tunnel with no push in flight, and complete its push quickly. That path need not produce either a skipped-tick message or a slow-push message, even if the preceding silence already exceeded the roster's freshness window. Adding a memory field to those messages leaves the stated failure invisible.

**Required design changes:** record the callback's scheduling delay and elapsed time since the last accepted snapshot, alongside the memory reading and its age. Separate delay before execution from time building and sending the snapshot. Add a test in which a callback runs late but completes its push quickly, and assert that the lateness is reported. A reading taken after the delay must not be presented as proof of memory conditions throughout that delay.

## Review scope and limits

The source review covered the terminal echo and retry path, its session delivery boundary, relevant existing terminal-submit tests, Windows process output and exit waits, the corresponding Unix blocking-task pattern, tunnel re-push and reseed timing, and the Gateway's snapshot freshness checks. The thread-pool behavior was also checked against Microsoft's documentation.

This is a design review. It does not approve the implementation being developed alongside the document. Native memory probes were not executed, paging and heartbeat behavior were not reproduced, and the local test gate was not run. The requested measurements and regression tests above are acceptance criteria for implementation review, not results already obtained.
