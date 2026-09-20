# Answers - phase 3 review 1 on the way up ENGINE

Written by the Developer seat opened by the phase 3 Tech Lead (session 38f41a97), 20 September 2026.

- Branch: `smart-restart/p3-way-up-engine`, pull request **#3202** (already open, not merged, no second one).
- Worktree: `D:/ReposFred/devthrottle-smart-restart-p3-engine`.
- Reviewed commit: `0cda45788`. My commit: `f9443652e`.
- All four findings were accepted by the Tech Lead and all four are answered here.

---

## READ THIS FIRST - what changed on the published surface

Another Developer is building the windows against `IDirectorWayUp` and `WayUpWords` on this branch head.
Three things moved. Nothing was removed and nothing was renamed.

1. **`WayUpHistorySeat` gained a sixth part, `WayUpReopenOffer? Reopen`** (finding 3). It is the same
   `WayUpReopenOffer` the rows already carry. It is non-null exactly for a seat that ended without a
   handover, and null for every other seat. Anything building a `WayUpHistorySeat` positionally must pass
   it; nothing in a window reads a history seat today.
2. **`IWayUpGateway` gained a fourth method, `Task<RestoreRoster> GetRosterAsync(CancellationToken ct)`**
   (finding 1). It matters only to whoever fakes that seam - the real one, `GatewayClientWayUp`, already
   implements it. No window touches it.
3. **`AgentPluginLaunchMetadata` gained a third, REQUIRED part, `bool CanResumeSavedConversation`**
   (finding 2). Every agent plugin now has to state it. This is in `CcDirector.Core`, which the Tech Lead's
   ruling allows; nothing else widened.

`IDirectorWayUp`'s four methods, `WayUpOffer`, `WayUpRecord`, `WayUpRow`, `WayUpRowSeat`,
`WayUpReopenOffer`, `WayUpHistory`, `WayUpHistoryEntry`, the two requests and the three results are all
unchanged in name and in shape. `WayUpWords`' method names are unchanged. Two of its SENTENCES changed, and
both are finding 2: a Copilot, Cursor, Gemini, Grok or opencode seat is now worded from that agent's own
plugin, so Copilot reads "GitHub Copilot" rather than "Copilot" and is told its conversation comes back.

---

## The counts

Both checks were measured on the UNTOUCHED tree first, at commit `0cda45788`, so the after-number means
something. Every run in this document is a full build; there is no `--no-build` run anywhere in it.

| Check | Before | After |
|---|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain\|FullyQualifiedName~Restart"` | 567 total, 567 passed, 0 failed | **580 total, 580 passed, 0 failed** |
| `dotnet test src/CcDirector.Core.Tests --filter "FullyQualifiedName~AgentPlugin\|FullyQualifiedName~Agent"` | 238 total, 238 passed, 0 failed | **247 total, 247 passed, 0 failed** |

The mission's check rose by 13 and the agent plugin check by 9 (one walk test plus its eight named cases).
No new failure in either. The 567 before-figure is the same one the Tech Lead and the Reviewer each
measured on this commit. The intermittent `SessionStateEventEmitterTests` red the Tech Lead saw did not
appear in any of my eight runs; it is not this phase's and I did not chase it.

---

## Finding 1 - the reopen could start a session that was still running, and could reopen the same saved conversation twice

### Part one - a seat that may still be running is refused

`ReopenAsync` now asks the roster and refuses the seat with **the product's own rule**,
`DirectorRestore.StillRunning(seat, roster)`, which is `internal static` and hands back the reason in plain
words. Nothing about that rule is restated in the way up: both of its halves come for free - a seat running
on a Director the Gateway can reach, and a seat still LISTED under a Director nobody can reach that the
drain never recorded closed.

The roster reaches the engine through a new fourth method on the Gateway seam,
`IWayUpGateway.GetRosterAsync`, shaped exactly as `IRestoreGateway.GetRosterAsync` is: it returns the
envelope WITH per-Director reachability and never the plain list, because "not on the list" is the fact
being acted on. `GatewayClientWayUp.GetRosterAsync` is the same four lines
`GatewayClientRestoreGateway.GetRosterAsync` is, over `ListFleetSessionsWithReachabilityAsync`.

**The start-up check still never asks what is running.** Until now that was held by the seam having no such
question at all, which the Reviewer's section 5 called out as the structural proof. The seam now has one,
so the rule is held by a count instead: `FakeWayUpGateway.RosterAsked`, asserted to be zero in
`A_record_with_one_owed_seat_is_offered_and_nothing_is_asked_about_running_sessions`. The roster is asked
only on the reopen path, immediately before a session would be started.

Tests that hold it, both new:

- `DirectorWayUpHistoryTests.Reopening_a_seat_that_is_still_running_is_refused_and_starts_nothing`
- `DirectorWayUpHistoryTests.Reopening_a_seat_listed_under_a_director_nobody_can_reach_is_refused`

### Part two - one reopen per seat while this Director is up

`DirectorWayUp` holds a `HashSet<string>` keyed on the workspace id and the seat's captured session id, and
`ClaimReopen` takes the claim under a lock. A second attempt is refused with a sentence that is **a word of
the engine, not of a window**: "'A busy worker' has already been reopened from this record since this
Director started, so it is not opened again - a second agent in the same saved conversation would
interleave its turns with the first one's. Find it in the session list."

The claim is taken BEFORE the start leaves, so two clicks or two screens racing cannot both pass it. **It is
not given back when the start fails**, and that is deliberate rather than an oversight: a start whose answer
never came back may have happened anyway, which is exactly what `DirectorRestore.TimedOut` says of the
restore's own token. Giving the claim back on a failure would hand the owner a retry that is sometimes the
second agent this guard exists to prevent. The cost is that a genuinely failed reopen cannot be tried again
until this Director restarts; the refusal tells the owner to look in the session list.

Test: `DirectorWayUpHistoryTests.Reopening_the_same_seat_twice_starts_only_one_session`.

### WHAT IS STILL OPEN, and it is not hidden

**Across a Director restart the same seat can still be reopened twice, because nothing is recorded.** The
in-process guard covers one run of one Director and no more. Marking a seat as reopened needs the restore
lease and a workspace write, and a new mark kind would change `CcDirector.Gateway.Contracts` and need a
Gateway deploy - which the Tech Lead ruled is the Delivery Lead's decision and not mine. This is written in
three places so it cannot be lost: the comment on the `_reopened` field, the `ReopenAsync` documentation on
`IDirectorWayUp`, and here. It is the Delivery Lead's to close.

---

## Finding 2 - Copilot and Cursor really do resume, and were being told they would come back blank

The cause was fixed, not the list. `WayUpWords.Resumes` no longer holds any agent names at all.

**The fact now lives where the agent is defined.** `AgentPluginLaunchMetadata` gained
`CanResumeSavedConversation`, and it is a REQUIRED positional part of the record, so every plugin has to
state it and a new agent cannot get one by silence - a plugin that leaves it out does not compile. The test
double in `AgentPluginLoaderTests` had to be updated for exactly that reason, which is the guarantee
working.

Each value was read off the driver, not guessed:

| Agent | What its own launch path does with the id | Flag |
|---|---|---|
| Claude Code (`ClaudeAgent`) | appends `--resume <id>` | true |
| Pi (`PiAgent`) | appends `--session-id <id>`; its comment records pi 0.80.10 recalling the first launch's conversation from the same id | true |
| Copilot (`CopilotAgent`) | appends `--resume <id>` | true |
| Cursor (`CursorAgent`) | appends `--resume="<id>"` | true |
| Codex (`CodexDriver`) | LOGS that it is ignoring the id | false |
| Gemini (`GeminiAgent`) | LOGS that it is ignoring the id | false |
| Grok (`GrokAgent`) | LOGS that it is ignoring the id | false |
| opencode (`OpenCodeAgent`) | LOGS that it is ignoring the id | false |

`WayUpWords.Resumes` reads that flag through `AgentPluginRegistry`. A record holds the agent as a STRING, so
the spelling is matched against the three names the plugin itself publishes - its kind, its id and its
display name - under the normalisation that was already there, which keeps "ClaudeCode", "claude" and
"Claude Code" one answer without a table of spellings living in the words file. **An agent the registry does
not know at all still answers false and falls to the fresh-session wording**, which the Reviewer confirmed
is right; that is kept, and its test with an invented agent still passes.

`WayUpWords.AgentName` reads the same registry for the display name, so the second hand-kept list in that
method is gone too. An agent the registry does not know is still named as the record spells it.

**The test that stops it drifting again**:
`AgentPluginLaunchMetadataTests.Every_registered_agent_resume_flag_matches_what_its_launch_spec_really_builds`
walks EVERY registered agent - `AgentPluginRegistry.All`, built-ins and any external plugin alike - builds
each one's launch spec with a known conversation id, and asserts the flag equals whether that id really
appears in the arguments the agent built. It is a presence check over the real drivers, and it fails the day
an agent gains or loses resume without its flag moving. It asserts first that the walk found at least eight
agents, so a registry that answered with nothing is a broken instrument rather than a clean run.

Two things guard the walk against passing vacuously. `Each_built_in_agent_declares_the_resume_support_its_driver_has`
names all eight expected values, so a world where every agent declared false and no agent built the id would
fail. And three of the wording tests in `DirectorWayUpOfferTests` now assert the fresh-session side
(Gemini, Grok, opencode) beside the two that assert the resuming side.

Tests that hold it, all new:

- `AgentPluginLaunchMetadataTests.Every_registered_agent_resume_flag_matches_what_its_launch_spec_really_builds`
- `AgentPluginLaunchMetadataTests.Each_built_in_agent_declares_the_resume_support_its_driver_has` (eight cases)
- `DirectorWayUpOfferTests.A_copilot_seat_is_offered_its_saved_conversation`
- `DirectorWayUpOfferTests.A_cursor_seat_is_offered_its_saved_conversation`
- `DirectorWayUpOfferTests.An_agent_whose_driver_ignores_the_id_is_offered_a_fresh_session` (three cases)

---

## Finding 3 - the history said a conversation could be reopened and gave no way to do it

Every seat that ended without a handover now carries its reopen offer **wherever it is shown**:
`WayUpHistorySeat.Reopen` holds the same `WayUpReopenOffer` the start-up rows hold, computed by the same one
method, and it is there whether or not the record still owes a seat that can come back. A record that owes
nothing but holds saved conversations offers those conversations in the history, with the words saying which
agent it is and what will really arrive - so no window has to invent the sentence.

Only a seat that ended without a handover gets one. A seat that handed over and is waiting, or that has
already come back, carries null: a button beside it would start a second copy of a session the bring back is
going to restore.

**The start-up presence check was NOT changed.** A record with no seat decided restore is still not OFFERED
at start-up - the mission says so in section 5.3 item 10 - it is simply readable, with working buttons, in
the history. The test asserts both halves in one place, and then actually reopens from such a record to show
the button is not decorative.

Tests that hold it, both new:

- `DirectorWayUpHistoryTests.A_record_whose_every_seat_ended_at_the_limit_offers_its_conversations_in_the_history`
- `DirectorWayUpHistoryTests.A_seat_that_handed_over_or_came_back_carries_no_reopen_offer_in_the_history`

---

## Finding 4 - the bring back and the reopen accepted any workspace id

One rule, in one place, used by both. `DirectorWayUp.NotThisDirectors(doc)` answers with a plain sentence or
null, and both `BringBackAsync` and `ReopenAsync` call it the moment the record has been read - before the
bring back chooses a seat, and before the reopen looks for one. It checks the two things the two READ paths
already require: the same machine, and the same Director display name, matched the same way
(`OrdinalIgnoreCase`). Each refusal names what it found, so "it belongs to Director 'DevThrottle_2'" reads
as a fact rather than a shrug.

I did NOT add the record's origin to this guard, although the read paths filter on it too. The ruling named
the machine and the Director name, and `DirectorRestore.SelectTargets` already refuses a workspace that is
not captured, so a bring back is covered. A reopen of a hand-authored workspace is therefore not refused by
origin - but such a workspace carries no captured machine or Director name, so it is refused by this guard
anyway, on the first of the two checks.

Tests that hold it, all new:

- `DirectorWayUpBringBackTests.Bringing_back_another_directors_record_is_refused_by_name_and_the_restore_is_never_called`
- `DirectorWayUpBringBackTests.Bringing_back_a_record_from_another_machine_is_refused_by_name`
- `DirectorWayUpHistoryTests.Reopening_from_another_directors_record_is_refused_by_name`

---

## The revert proof - three of my own tests shown able to fail

The work was **committed first** (`f9443652e`), so `git checkout --` could only ever restore the committed
file and never eat the change. Each mutation is one line, on code I added rather than code that was already
there. Every run below is a full build.

### Mutation one - defeat the still-running guard (finding 1, part one)

In `DirectorWayUp.ReopenAsync`:

```
-  var roster = await _gateway.GetRosterAsync(ct).ConfigureAwait(false);
+  var roster = new RestoreRoster(Array.Empty<SessionDto>(), Array.Empty<DirectorReachabilityDto>());
```

    Failed ...DirectorWayUpHistoryTests.Reopening_a_seat_that_is_still_running_is_refused_and_starts_nothing
    Failed ...DirectorWayUpHistoryTests.Reopening_a_seat_listed_under_a_director_nobody_can_reach_is_refused
    Failed!  - Failed:     2, Passed:   578, Skipped:     0, Total:   580

**2 failed, 578 passed, 580 total.** Both halves of the rule are held, and only those two tests noticed - so
those two, and only those two, are what hold it.

### Mutation two - defeat the once-only guard (finding 1, part two)

In `DirectorWayUp.ClaimReopen`:

```
-  return _reopened.Add(workspaceId + "\n" + seatSessionId);
+  return true;
```

    Failed ...DirectorWayUpHistoryTests.Reopening_the_same_seat_twice_starts_only_one_session
    Failed!  - Failed:     1, Passed:   579, Skipped:     0, Total:   580

**1 failed, 579 passed, 580 total.** Exactly one test holds this rule; if it is ever deleted the rule is
unguarded.

### Mutation three - tell Copilot it cannot resume (finding 2)

In `CopilotAgentPlugin`:

```
-  CanResumeSavedConversation: true);
+  CanResumeSavedConversation: false);
```

The agent plugin check:

    Failed ...AgentPluginLaunchMetadataTests.Each_built_in_agent_declares_the_resume_support_its_driver_has(kind: Copilot, canResume: True)
    Failed ...AgentPluginLaunchMetadataTests.Every_registered_agent_resume_flag_matches_what_its_launch_spec_really_builds
    Failed!  - Failed:     2, Passed:   245, Skipped:     0, Total:   247

**2 failed, 245 passed, 247 total.** The walk caught it from the driver's real arguments, which is the whole
point of it.

The mission's check, with the same one line broken:

    Failed ...DirectorWayUpOfferTests.A_copilot_seat_is_offered_its_saved_conversation
    Failed!  - Failed:     1, Passed:   579, Skipped:     0, Total:   580

**1 failed, 579 passed, 580 total.** One line in `CcDirector.Core` moved a sentence in
`CcDirector.ControlApi`, which is the proof that the wording really is read from the agent and is not a
second copy of the fact.

### Restored, and green again on a full build

After the last `git checkout --`, `git status --short` and `git diff HEAD --stat` both printed nothing, so
the source is byte for byte the committed source. The three touched files were then touched again to force a
recompile, and both checks were run. Each printed its assemblies rebuilt - `CcDirector.Core`,
`CcDirector.ControlApi` and `CcDirector.Gateway.UnitTests` in the first,  `CcDirector.Core` and
`CcDirector.Core.Tests` in the second - so the green is the restored SOURCE and not a leftover binary:

    Passed!  - Failed:     0, Passed:   580, Skipped:     0, Total:   580
    Passed!  - Failed:     0, Passed:   247, Skipped:     0, Total:   247

---

## What I did NOT do, and what this does not cover

- **The forward risk in finding 3 about the operating-system shutdown record** (mission ruling 10.5). Not
  built and not designed for, as the Tech Lead directed; it is with the Delivery Lead.
- **The stale comment on `NewSessionRequest.ResumeSessionId`** saying resume is ignored by Pi. True, and
  left alone as directed.
- **The intermittent `SessionStateEventEmitterTests` red.** Not mine and not seen.
- **No change to the Gateway, to `WorkspaceStore`, to `WorkspaceValidation` or to any contract in
  `CcDirector.Gateway.Contracts`.** The only widening beyond the engine is `AgentPluginMetadata` in
  `CcDirector.Core`, which finding 2 allows.
- **No window, no XAML, nothing in `src/CcDirector.Avalonia`.**
- **Nothing was run against a real Gateway, Director or session.** Every test here is against fakes of the
  two seams, exactly as the first Developer's were. What is proved is the RULES. That a real Gateway answers
  `ListFleetSessionsWithReachabilityAsync` as the fake roster does is not proved here - although the
  restore has been reading it that way in production since inspection 7.
- **The parked suites, the web tests and the Python tests were not run**, and neither was the full local
  gate. I ran the two checks the mandate names. Nothing in the diff touches the browser shells or the Python
  toolbelt. The whole solution does build: `dotnet build cc-director.sln` succeeded after every change.
- **The hosted checks on pull request #3202 were not waited for.**
- **One decision worth naming**, because it trades one risk for another: a reopen whose start FAILS keeps its
  once-only claim, so it cannot be retried until this Director restarts. The reasoning is under finding 1,
  part two. If the Delivery Lead would rather have the retry than the certainty, that is one line in
  `ReopenAsync`.
