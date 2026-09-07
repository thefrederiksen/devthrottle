# The "could not determine" class sweep - 2026-09-07

A sweep of the whole codebase for ONE defect class, run because it bit the
"Restart a Director without losing the fleet" mission five times in a single day and every
instance was found by accident.

**The class:** a binary that should have been a trinary, with the missing third state being
"I DO NOT KNOW", silently folded into whichever of the two is PERMISSIVE.

**The rule being applied:** when a result describes the WORLD rather than a DECISION, "I could
not find out" is a real answer and it needs its own name. If it has no name it is already inside
one of the others, and it is inside the permissive one. A result about a decision ("may I stop
this?") may safely fold unknown into decline; a result about the world may not, because two
callers will read the same value in opposite directions.

**A fold is only dangerous if a consumer reads the permissive value as permission.** Everything
below is therefore ranked by consumer, not by count.

---

## WHAT THIS DOCUMENT DOES NOT KNOW - read this before using any number in it

This section is first because a reader who stops after the counts will otherwise take them for a
total. They are not. **An inventory that says what it does not yet know is usable; one that reads
as complete is dangerous, because a wrong exoneration marks something FOUND AND SAFE and stops
anyone looking again.**

**1. Every count here is a FLOOR, not a total.** The sweep found what its instrument was shaped to
find - `catch` blocks in C#. It did not mechanise guard clauses, it did not sweep TypeScript, and
its PowerShell matcher was not trustworthy (see the coverage section at the end). More exist.

**2. THE EXONERATION METHOD HAD THE DEFECT THIS DOCUMENT IS ABOUT.** This is a defect in the
method, not a caveat on a row.

Defects were ranked by looking at ALL of a member's consumers. Exonerations were granted after
looking at ONE. So a single safe consumer was enough to clear a member - the clearing path had a
permissive branch, and the unknown landed in it. That is precisely the shape being hunted, inside
the instrument built to hunt it, which makes a wrong exoneration structurally likely rather than
bad luck.

It produced a real one. `VoiceUploadStore.ReadRecordFile` was cleared here on the strength of
`ExpireStalePending`, which genuinely does positively admit. Its OTHER consumers do not: an
unreadable terminal record fails the `Delivered or Abandoned` test, falls through to `Register`
plus `MarkPending`, and re-opens a delivered upload - duplicate injection of the operator's own
speech into a live session. Filed as issue 2745. Found by a reviewer, not by this document.

**3. The nine exonerations HAVE since been re-triaged, and the result is recorded below.** Two were
refuted (`VoiceUploadStore`, `DictationLockReader`), one was right for the wrong reason
(`TranscriptionMode.IsValid`), two had their stated reason narrowed to display-only, and four
stand.

**The bigger limit is one layer up, and it now has a NUMBER rather than a caveat.** The shortlist
this document's sections A to C were drawn from was built by matching caller NAMES against a
pattern of destructive-sounding verbs, so a consumer that writes, deletes or launches under a name
that pattern did not match was never shortlisted and was never consumer-checked at all.

Re-run with a caller-BODY heuristic instead - does the consumer actually write, delete, start or
persist, whatever it happens to be called - the numbers are:

| Measure | Count |
|---|---|
| Folds with any production caller at all | 69 |
| Shortlisted by the original caller-NAME heuristic | 22 |
| Shortlisted by the caller-BODY heuristic | 65 |
| **Never consumer-checked by this document** | **47** |

So the unexamined set is **more than double** the set that was examined. Stated as a number because
"some remain" understates it: a reader deciding how far to trust these rows needs 47 of 69, not an
adjective.

**Why the 47 is credible rather than alarming.** The body heuristic was checked against the known
instance before its output was believed, and it found `DirectorInstanceLocator.ReadRegistrations`
and `TryGetLiveProcess` - the issue 2730 root cause - by a route the name heuristic could not take,
because their consumer is called `Resolve` and `Resolve` is not a destructive-sounding verb. The
original shortlist only ever reached 2730 because `Resolve`'s OWN consumer happened to be named
`Start`. It missed the known defect on the first hop and caught it on the second by luck.

**The four newly-shortlisted folds with genuinely destructive consumers were checked, and all four
are correct** - the worktree reaper folds toward KEEP, the crash journal catches only the two
exceptions that are proof of not-running and lets anything else propagate, and the orphan-file
sweep is the model described below. The 47 is a measure of what was not looked at, not a count of
defects.

**4. TWO SPECIFIC CORRECTIONS TO EARLIER ROWS.**

- **A source comment was cited as a live compensating control, and it had been DELETED.**
  `DictationLockReader.IsSessionLocked` was classified deliberate-and-compensated on the strength
  of its own comment, which names the fail-closed `SendSource.UserInput` default as the
  compensation. That default does not refuse anything: `Session.cs` states "sends are never refused
  by source" and that "the old dictation-lock rejection here was removed deliberately", and
  `SessionCommandExecutor` says the same. **A comment standing in for a check is worse than a
  comment describing one wrongly** - this one was used as EVIDENCE.
- **A named consumer was a name-collision artifact.** `TranscriptionMode.IsValid` was recorded with
  the consumer `SetTimeZone`. It has no such consumer; it has no production consumer at all. The
  row's verdict was right by accident and its stated reason was wrong.

**5. A NAME COLLISION IS AN INSTRUMENT RETURNING A TRUE HIT ABOUT THE WRONG THING, and the fix is
to OPEN the hit rather than count it.** This is the single most useful thing the sweep produced and
it applies to every number in this document.

A count of "156 test files naming `StopAsync`" was used here to argue that a safe consumer was
well covered. Opened, it was an unrelated `StopAsync` on dozens of types; the real figure for the
type in question is ONE behavioural test file, exercising `AccountUrl`, a pure string builder -
the opposite conclusion. The same shape bit a second search on this mission within the hour, where
a field-name match on `origin/main` turned out to be an unrelated voice type; reporting it
unopened would have told the owner a false thing about his own data leaving his machine.

**Therefore: every count in this document came from a pattern match unless an opened file is cited
beside it.** The rows in sections A and B name files and line numbers because those were opened.
The aggregate counts in the table below were not.

---

## The instrument, and the proof it can fire

Two searches, both written for this sweep.

**C# (`sweep.py`).** Blank out comments and string literals while preserving byte offsets,
brace-match every `catch` block, walk outward to the innermost enclosing member, and classify what
the catch body resolves to. A catch that rethrows is not the shape and is dropped; a catch that
returns `null` / `false` / `true` / `0` / an empty collection, or `continue`s, is. A second pass
(`consumers2.py`) finds each fold's production callers - test projects excluded, and a bare name
match rejected unless the calling file also names the declaring type, because `Parse` and `Read`
exist on dozens of unrelated types.

**Proof the C# instrument fires.** Pointed at `DirectorInstanceLocator.Resolve` - the known
instance, issue 2730 - before it was trusted anywhere else. It returns seven hits on that file
alone and names the actual root cause: the `catch` in `ReadRegistrations` at line 474 that
`continue`s, and the swallow at line 493, which together turn "this registration could not be
read" into "this registration does not exist". The consumer pass then re-derived the dangerous
pair on its own, without being told it: `DirectorInstanceLocator.Resolve <- DirectorSupervisor.Start`.

It also found a fault in ITSELF during calibration: the first version could not parse a tuple
return type, so both `ReadRegistrations` catches - the actual bug - had no enclosing member and
were dropped silently. A catch whose enclosing member cannot be determined is now REPORTED rather
than dropped, because dropping it would make this instrument fail open, which is the defect class
it exists to find.

**Scripts (`sweep_scripts.py`).** The same hunt in Python and PowerShell, because two of the five
mission witnesses were verdict-making scripts, not product code.

---

## Counts

| Measure | Count |
|---|---|
| C# catch sites classified across `src/` and `tools/` | 2691 |
| ... of those, in production (non-test) code | 2001 |
| ... in a world-shaped member returning a boolean, a nullable or a collection | 359 |
| ... folding to an EXPLICIT permissive value (`null`, `false`, `true`, `0`, empty, `continue`) | 202 sites |
| Distinct (type, member) folds behind those sites | 174 |
| **... with at least one production caller that starts, writes, deletes or authorises** | **24** |
| Python folds found | 37 (6 in a verdict-making function, all in throwaway harnesses) |
| PowerShell folds found | 0 - see the caveat below |

All 24 were read by hand. They classify as: **10 defects, 3 deliberate and documented,
6 correct as they stand, 1 owned by another seat**, and 4 that are display-only once read.

---

## A. Defects - the ranked list

### A1. `ClaudeConfigDialog.SaveSettingsJson` destroys the user's Claude settings file - HIGH

`src/CcDirector.Avalonia/ClaudeConfigDialog.axaml.cs:389` (`ReadJsonFile`), consumed at
`SaveSettingsJson`.

- **The two values:** the settings file parsed / the settings file is not there.
- **The missing third:** it is there and could not be read or parsed.
- **Which way it folds:** permissive. `ReadJsonFile` returns `null` for a missing file AND for a
  read or parse failure. `SaveSettingsJson` then does
  `var obj = root as JsonObject ?? new JsonObject();` and ends with an unconditional
  `WriteJsonFile(_settingsJsonPath, obj)`.
- **The consumer, and why it is the worst one here:** it WRITES. A settings file that is briefly
  locked, half-written by another process, or hand-edited into invalid JSON is replaced by an
  object containing only the handful of fields this dialog knows about. Everything else is gone -
  including `hooks`, which is what carries this fleet's own SessionStart preamble, and which the
  comment on the very first line of the method says the read exists to preserve
  ("Read existing file to preserve fields we don't edit (hooks, schema, etc.)").
- **The tell that this is the class and not a design choice:** the sibling method 14 lines below,
  `SaveClaudeJson`, faces the identical situation and gets it right -
  `if (root is not JsonObject obj) return;`. Two adjacent methods disagree about what `null` means,
  which is exactly how this class hides.
- **Shape of the fix:** `ReadJsonFile` must distinguish absent from unreadable. Absent may create;
  unreadable must abort the save and tell the user, because there is nothing safe to merge into.

### A2. `AutomationBrowserService.IsUpAsync` folds a timeout into "not running" - MEDIUM-HIGH

`src/CcDirector.Core/Browsers/AutomationBrowserService.cs:85`, consumed by `LaunchAsync` (same
file) and by `StatusAsync`.

- **The two values:** the debug port answered / the port refused.
- **The missing third:** the port did not answer IN TIME, or the caller cancelled.
- **Which way it folds:** permissive. The catch filter is
  `HttpRequestException or TaskCanceledException or OperationCanceledException`, so both the HTTP
  timeout and the caller's own cancellation return `false` - "the browser is not running".
- **The consumers:** `LaunchAsync` reads `false` as permission to `StartProcess`, so a browser that
  is up but slow gets a SECOND process started on the same profile directory and the same port -
  structurally the same consequence as 2730. `StatusAsync` folds it to
  `AutomationBrowserStatus.Stopped` and every client renders that verbatim, so a running browser is
  reported stopped.
- **Note:** the doc comment argues the fold is legitimate, and for a refused connection it is. It
  does not address the timeout or the cancellation, which are the two that are not answers about
  the world.

### A3. `ConnectionsView.IsDaemonReachableAsync` - same shape, but a BARE catch - MEDIUM

`src/CcDirector.Avalonia/Controls/ConnectionsView.axaml.cs:400`, consumed by
`EnsureDaemonRunningAsync` at line 347 and again in its retry loop at 389.

Worse than A2 in one respect: the catch has no exception filter and no log line at all -

```csharp
catch
{
    return false;
}
```

so a timeout, a cancellation, a malformed port and an outright bug in `_http` all become
"the daemon is not running", invisibly. `EnsureDaemonRunningAsync` reads that as permission to
`Process.Start` a second `cc-browser connections status`. The retry loop then polls the same
broken probe twenty times and throws `TimeoutException("cc-browser daemon did not start within 5
seconds")` - a message that names the wrong cause, because the daemon may have been up the whole
time.

### A4. A failed hook install launches the session anyway, silently - MEDIUM

`src/CcDirector.Core/Claude/ClaudeHookInstaller.cs:144` (returns `null`) and
`src/CcDirector.Core/Codex/CodexHookInstaller.cs:136` (returns `false`). Both consumed by
`SessionManager.CreateSession` at lines 756 and 770.

- **The two values:** the hooks are installed, here is the settings path / no hooks.
- **The missing third:** the install FAILED.
- **Which way it folds:** permissive. `CreateSession` tests `if (!string.IsNullOrEmpty(hookSettings))`
  and `if (CodexHookInstaller.EnsureInstalled())`, so a failure simply skips `--settings` and
  `--dangerously-bypass-hook-trust`.
- **Why it matters here specifically:** the Claude hooks are how the Director learns the session id
  across `/clear` and auto-compaction, and the Codex hook is what re-injects the fleet preamble -
  the preamble that carries the standing laws. A session that silently launches without either is
  mis-configured in a way nobody can see: `_log?.Invoke` fires only on SUCCESS, so on failure the
  user is told nothing at all and only `FileLog` records it.
- **Shape of the fix:** this one should probably still launch - refusing to start a session because
  a hook did not install is worse. But the third state needs a name and a line in the session log.

### A5. An unreadable history entry is overwritten with a fresh one - MEDIUM

`src/CcDirector.Core/Sessions/SessionHistoryStore.cs:68`, and the same shape in
`WorkspaceStore.cs:110` and `NamedSessionStore.cs:112`.

`Load` returns `null` for "not found" and for "found but unreadable" alike. In
`MainWindow.axaml.cs:6186` (`UpdateSessionHistory`) a `null` leads straight to
`SaveSessionToHistory(vm)`, which writes a fresh record; the existing `FirstPromptSnippet`,
`CustomName` and `CustomColor` are lost. `UpdateAllSessionHistoryTimestamps` at 6212 takes the
gentler `continue`, so the two callers of the same value already disagree - the signature of the
class.

### A6. `SkillPlacementLog.ReadAll` reports "nothing placed" when it means "cannot read" - MEDIUM-LOW

`src/CcDirector.Core/Skills/SkillPlacementLog.cs:106` returns `Array.Empty` on any read failure -
its own doc comment states the fold outright ("Empty when nothing has been placed OR the record
cannot be read"). The consumer, `ControlApiHost.PushSkillPlacementAsync`, does
`if (records.Count == 0) return;` and sends nothing.

Nothing is destroyed, but the Gateway cannot distinguish a machine with nothing to report from a
machine whose record it cannot read - so the fleet's skill-placement view goes stale silently
instead of saying it does not know. This is the diagnostic form of the class: a pass condition
that is an absence.

### A7. `VoiceTurnLog.FindPendingInbound` - a bare `catch { continue; }` - LOW-MEDIUM

`src/CcDirector.Core/Voice/VoiceTurnLog.cs:130` - `catch { continue; }`, no exception filter and no
log line, so a corrupt `inbound.json` is invisible. The consumer is
`FindPendingInbound(SessionKey(sessionId)) ?? CreateStandalone(sessionId)`: an inbound record that
cannot be deserialised is read as "there is no pending inbound", and the outbound half of the turn
is written into a BRAND NEW standalone directory instead of being paired with the inbound that is
sitting right there. The turn brief that results has an agent reply with no prompt, and the real
inbound stays forever unpaired.

### A8. `ClaudeProcess.ReadSessionIdFromStream` folds cancellation into end-of-stream - LOW-MEDIUM

`src/CcDirector.Core/Claude/ClaudeProcess.cs:168` - `catch (OperationCanceledException) { break; }`,
so `StartAndGetSessionIdAsync` cannot tell "the stream ended without an id" from "we were
cancelled while waiting for it".

### A9. `ClaudeConfigDialog.SaveClaudeJson` is silently a no-op - LOW

Same file. The fold here is SAFE (it declines to write), but the user pressed Save and is told
nothing, which reads as the button being broken.

### A10. `ExecutableResolver.Resolve` gives the wrong reason - LOW

`src/CcDirector.Core/Utilities/ExecutableResolver.cs:58` - `catch { continue; }` on a malformed or
unreadable PATH entry. It fails LOUDLY (`SessionManager.CreateSession` throws a clear
`InvalidOperationException`), so it is not a permissive fold; but the message it throws says the
command "was not found on this Director's PATH" when the truth may be that a PATH directory could
not be read. A wrong reason sends the next person to the wrong place.

---

## B. Deliberate, documented, and compensated - leave alone, but know they are there

- **`DictationLockReader.IsSessionLocked`** (`src/CcDirector.Core/Sessions/DictationLockReader.cs:58`)
  returns `false` on any read error. **THIS ROW WAS WRONG - see limit 4.** It was classified
  deliberate-and-compensated because the class comment names the fail-closed
  `SendSource.UserInput` default as the compensation. **That compensation does not exist.** It was
  removed deliberately: `Session.cs` says "sends are never refused by source" and that "the old
  dictation-lock rejection here was removed deliberately", and `SessionCommandExecutor` states the
  same rule. The fold may now belong in the display-only group, since the product deliberately
  permits the send and the value feeds the roster paint - but it is NOT compensated, and this
  document repeated a stale safety sentence instead of checking the code beneath it.
- **`BrowserHarnessInstaller.ReadVersion`** - logged "non-fatal"; an unreadable version causes a
  reinstall, which is wasteful rather than dangerous.
- **`ClaudeHookEventParser.Parse`** - `null` on `JsonException`; a malformed hook event is genuinely
  unparseable.

## C. Correct as they stand - the fold goes the RESTRICTIVE way

Listed so nobody re-opens them, and because several are good models for fixing the ones above.

- **`LauncherDiscovery.IsRunning`** (reason narrowed: two of its three consumers are restrictive,
  the third, `CatalogReadExecutor`, is display-only and will report a running-but-unreadable
  launcher as stopped) - unknown folds to "not healthy". `Launcher/Program.cs`'s
  `isHealthy` reads false as ROLLBACK, and `UpdateStatusFold` reads it as "do not offer the
  install". The doc comment states the rule: "identity that cannot be checked must not pass for
  health". `UpdateStatusFold` even carries an explicit `HeldBecauseUnknown` state - this class
  already has a named third value in that one place.
- **`VoiceUploadStore.ReadRecordFile` / `ExpireStalePending`** - **THIS ROW WAS WRONG. It is a
  LIVE DEFECT, filed as issue 2745, and it is the worked example behind limit 2.** `ExpireStalePending`
  genuinely does positively admit ("No marker, unreadable, or any state but PENDING: not ours") and
  it remains the best model in the codebase - but it is ONE consumer of `ReadRecord`, and clearing
  the member on it was the single-consumer error. `GatewayDictationEndpoint` tests
  `existing is { State: Delivered or Abandoned }`, which is a correct POSITIVE test that an
  unreadable record fails, so it falls through to `Register` plus `MarkPending` and RE-OPENS a
  delivered upload, defeating the durable de-duplication the comment four lines above promises.
- **`GatewayApp/Program.TryReadGatewayToken`** - a null token aborts the swap with
  "cannot ask the Gateway to exit, so the swap will abort" rather than posting a request known to
  401.
- **`ControlEndpoints.ResolveSessionFile`** - "nothing here is safe to serve".
- **`TranscriptionMode.IsValid`** - narrow `catch (ArgumentException)`; an unparseable enum value
  genuinely is invalid. **Right verdict, wrong reason - see limit 4.** The consumer recorded here
  originally (`SetTimeZone`) was a name-collision artifact. This member has NO production consumer
  at all, so it is unreachable rather than correct.
- **`PythonRuntimeProbe.CanImportStdlib`** - unknown folds to "cannot", which blocks rather than
  permits.

### The correct implementation of 2730's fix already exists in this repository

Found by the caller-body re-triage, and recorded here because it is directly actionable.

**`DirectorRegistry.IsProcessDead`** (Gateway) and **`DirectorInstanceLocator.TryGetLiveProcess`**
(Core) ask the SAME question about the SAME thing - is this Director's process id alive - and answer
it with opposite discipline.

The Gateway one treats `ArgumentException` alone as proof of death and returns "not dead" for
everything else, with its reasoning written on it:

> A permission or other unexpected error returns false (do not assume dead) so we never delete on
> uncertainty.

The Core one folds EVERY exception into `null`, which its caller reads as not-running. That is what
makes 2730 dangerous.

So the fix for 2730 is not a design problem: the correct version is one assembly away, in code that
already ships, with the rationale attached. Copy that discipline rather than inventing one - and
note which way each fold leans, because the Gateway's caller DELETES and the Core caller STARTS, so
the two are protecting against opposite mistakes with the same rule.

## D. Owned by another seat

- **`DirectorInstanceLocator.Resolve`** - issue 2730, being fixed on
  `fix/2730-locator-unreadable-registration`. Counted here, not touched.

---

## What this sweep does NOT cover

Stated plainly so the next reader judges the instrument rather than the result.

- **Guard clauses were not swept mechanically.** The mandate names two tells - a `catch`, and a
  guard clause that returns the permissive value. Only the catch tell was mechanised. A guard
  clause such as `if (!Directory.Exists(dir)) return false;` carries the same fold and would need a
  different instrument; every guard clause reached here was read as part of reading its member, not
  found by a search.
- **PowerShell returned zero, and zero is not yet trustworthy.** 47 `catch` blocks exist across 78
  `.ps1` files, so the corpus is real, but the PowerShell brace-matcher produced garbage bodies for
  several of them because it does not strip comments or here-strings. The gate-producing scripts
  (`test-local.ps1`, `verify-gateway.ps1`, `validate-test-selection.ps1`, `test-qualification.ps1`,
  `check-tree-freshness.ps1`) were therefore read BY HAND instead, and are clean:
  `test-local.ps1`'s selector catch says so out loud ("A selector that cannot run must not fail the
  gate, but it must not be silent either") and prints the note. **The remaining PowerShell was not
  swept.**
- **`verify-gateway.ps1` decides PASS by the absence of failures** (`$failed.Count -eq 0`) rather
  than by counting the checks that ran. It is not a live defect - all five `Add-Result` calls are
  straight-line - but it is one refactor away from certifying a run that performed no checks. A
  count assertion would close it.
- **The 407 TypeScript and TSX files were not swept.** By the "client is dumb, Gateway rules" law
  the clients only render verdicts the Gateway folds, so by the rank-by-consumer rule they are the
  lowest-value region - but that is a reason to sweep them LAST, not a reason to call them clean.
- **The 4 display-only folds** among the 24 (thumbnail loading and similar) were read and dismissed;
  they are not listed individually.

## Reproducing this

The three scripts are throwaway instruments, not shipped code. They live in the session scratchpad
and are described above in enough detail to rewrite. The load-bearing parts are: blank literals
before brace-matching; report - never drop - a catch whose enclosing member will not parse; exclude
test projects from the CALLER side; and reject a bare name match unless the calling file also names
the declaring type.
