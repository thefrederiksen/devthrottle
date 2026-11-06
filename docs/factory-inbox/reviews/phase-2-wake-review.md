# Factory Inbox phase 2, Task C (the wake) - review

Reviewer: a separate review session, 5 November 2026.
Branch: factory-inbox/wake in D:/ReposFred/devthrottle-fi-wake.
Reviewed: the three commits 1d4352d43, bcd7018f1 and 6338ef634 (git diff 480e8e3bb 6338ef634). Nothing beneath
480e8e3bb was reviewed here.

## Verdict

Mergeable. No finding below proves a loss of a message, a second Secretary from one Gateway, or a ring typed into a
working session. The one finding worth a ruling (finding 1) is a sentence of the mission that does not hold for one
way of starting a Boss, inherited from how phase 1 marks a seat; it is not a defect in these three commits against
the model phase 1 merged, but the Tech Lead should decide it in writing before the end-to-end proof in phase 6.

## Scope: what I read, what I ran, what I could not reach

Read, in full: MISSION.md (sections 5 and 7), phase-2-plan.md (Task C, the one design rule, the decisions at the
end), the repository CLAUDE.md, and in the diff: FactoryInboxWake.cs, FactoryInboxWakeStore.cs, FactoryInboxSecretary.cs,
FactoryInboxWords.cs, GatewayFactoryInboxWakeEnvironment.cs, FactoryInboxEntities.cs, GatewayDbContext.cs, both
AddFactoryInboxWake migrations and both model snapshot diffs, FactoryInboxEndpoints.cs, FactoryKitEndpoints.cs,
FactoryInboxFold.cs, SecretaryStartLimit.cs, AuthMiddleware.cs, GatewayHost.cs (the wiring, the heartbeat, the timer
and its disposal), and every test file in the diff. Read beside the diff, at 6338ef634: SpawnFactory.cs (the Boss
start door and the inherit path), SessionFactoryLookup.cs, FactorySeatStart.cs, FactoryInboxStore.cs (the claim
rules), FleetManagerEventService.cs (the guarded prompt send), the session liveness in GatewayHost.cs, TriggerService
StartGrace, TenantPass, GatewayDatabase.CreateContext, FleetManagerSessions.IsGone.

Ran, out of tree, in a git archive copy of 6338ef634 under the session scratchpad (the worktree was not edited,
built or committed):

    dotnet test src/CcDirector.Gateway.UnitTests -c Debug --filter
      "FullyQualifiedName~CcDirector.Gateway.Tests.Factory.Inbox|FullyQualifiedName~GatewayHostBootSmokeTests|
       FullyQualifiedName~FactoryKitEndpointsTests|FullyQualifiedName~SecretaryStartLimitTests"

Result: 293 passed, 0 failed, 0 skipped, 32 seconds. That run includes the twenty wake tests, the undefined-factory
end-to-end test, the undefined Secretary start-door test through the real authentication middleware, the kit route's
Secretary test, the inbox endpoint tests, and GatewayHostBootSmokeTests, which applies every SQLite migration to an
empty in-memory database and asks Entity Framework whether the model still differs from the snapshot, and makes the
same pending-model-changes check against the PostgreSQL snapshot. Both answered no pending changes.

Could not reach: the PostgreSQL database tests (FactoryInboxPostgresTests, PostgresProviderProofTests) run only under
scripts/test-database.ps1 with Docker, which I did not start; the migration itself is proven additive by reading and
the snapshot by the boot smoke test, but the conditional update against a real PostgreSQL was not run by me. The
production wiring of the wake's environment in GatewayHost (directorOn with the kit capability, the start through the
machine spawner, the ring through the Fleet Manager send) is exercised by no test and I did not boot a Gateway. A real
Director building a kit for the made-up seat "secretary" was not exercised. test-local and -Parked are the Tech
Lead's runs, as the brief says.

## The six checks

1. Driven from the store, one start per factory as a database fact. Holds. The window is read as the oldest message
   still waiting plus 60 seconds (FactoryInboxWake.SecretaryAsync, line 313), never from a timer's memory; the
   scheduled look only decides when to look. The claim is one conditional update on factory_inbox_wakes.Version
   (FactoryInboxWakeStore.TryUpdate), taken before the start is sent, with Version as an Entity Framework concurrency
   token and a unique index on (tenant, factory), so two processes cannot both create or both claim. Proven by
   AGatewayRestartedInsideTheWindow_StartsExactlyOneSecretaryAfterIt, AGatewayRestartedDuringAStart_StartsNone_WhileItsClaimIsHeld
   (two wake objects over one database, the second finds the first's claim) and TwoWritersOnOneReadVersion_OnlyOneTakesTheClaim.
   The in-process per-factory semaphore only orders looks inside one process; it is not what the rule rests on.

2. A ring is never typed while a session works or holds the owner's draft; a live Boss is rung, never doubled. Holds
   for every Boss started with a seat kit (night start, trigger, Talk, the wake's own). The ring is the Fleet Manager's
   guarded send with OnlyWhenWaitingForInput, so the Director decides at the moment of typing; the wake does not even
   ask when the roster says the session works; only Accepted counts as rung, and the rung session and the newest
   message are recorded on the row so the next look says AlreadyTold. See finding 1 for the one Boss start path that
   is not seen as alive.

3. No computer. Holds. DirectorOn filters on the seat's machine name and connected Directors only; with none, nothing
   starts, the row records NoDirector with the machine, the thread prints the Gateway's sentence (proven through the
   real route in TheThread_ShowsWhyAMessageWaits_InTheWordsTheGatewayDecided), and the heartbeat looks again every 15
   seconds, so the start goes on when a Director connects there. A Director on another computer is never used
   (NoDirectorOnTheBossesComputer_StartsNothing_SaysWhy_AndGoesOnWhenOneConnectsThere). Note: the heartbeat sweeps
   only tenants with a Director bound to the tunnel (TenantPass), which is exactly the tenant that just connected, so
   the resume is reached.

4. Undefined factories. Holds. The wake records SecretarySessionId and BossSessionId on the row; the inbox caller
   lookup accepts a session by its stamped seat or by that record; SecretaryStartLimit.IsASecretary gains the second
   fact and AuthMiddleware.RefuseASecretary is still the one place every start door asks. Proven end to end through
   the real routes (FactoryInboxUndefinedFactoryTests) and through the real authentication middleware with real session
   keys at the generic spawn and the seat start (FactoryInboxUndefinedSecretaryStartsNothingTests); the Boss and a
   bystander reach both doors.

5. The migration on both providers. Holds. Both AddFactoryInboxWake migrations create one table and two indexes and
   nothing else; the Down drops only that table. The diff of both model snapshots adds lines only (no line removed),
   all of them the FactoryInboxWakeEntity block. GatewayHostBootSmokeTests reports no pending model changes on SQLite
   and on PostgreSQL (run by me, above). The PostgreSQL columns that are compared exactly carry collation C, matching
   the context.

6. Tests use fakes and an injected clock, test the real caller. Holds. The wake tests move a fake clock and call
   LookAsync, which is exactly what the scheduled look and the heartbeat call; MessageSent_LooksWhenTheWindowCloses_FromTheSentTime
   proves the scheduled path with a fake delay that is released by the test; TheHeartbeat_LooksAtEveryFactoryWithSomethingWaiting
   proves SweepAsync. Messages are written by the real store, and the two end-to-end tests go through the real routes
   on an in-memory test server. No sleep, no real clock, no port, no process. The only Task.Delay and DateTime.UtcNow in
   the diff are in the production environment class, where they belong.

## Findings

### 1. A Boss started by the Boss start door is not seen as alive, so a bundle starts a second Boss beside it

Where: FactoryInboxWake.Find (line 518 onward) counts a roster session as the live Boss only when its kit is the seat
kit. SpawnFactory.TryStartTheBoss (line 405) stamps the Boss it starts with the rules-only kit. So a Boss the owner's
own agent started through that door (issue 4351: "the owner improves a factory by talking to its Boss") is alive, in
the factory, in the Boss seat, idle, and the wake does not see it: when the Secretary hands a bundle, BossAsync finds
no live Boss and starts one with the seat kit.

Harm: two Boss sessions of one factory run at the same time, the second started by the Gateway while the owner's agent
may be mid-conversation with the first. The mission says "a live Boss session of this factory is rung, never doubled".

Why it is not a plain defect of these commits: phase 1 (already merged) defines "inherited" as "kit is rules-only"
(SessionFactoryLookup.SeatIsInherited) and refuses an inherited seat as the Boss of the inbox (FactoryInboxEndpoints,
line 368), and FactorySeatDelegation refuses it the right to start seats. So the Gateway already treats the start
door's Boss as "a session with the Boss's rules that was not started as the Boss". Ringing it would type a line into
a session that is then refused at the bundle route. The wake follows the model it was given. What does not hold is the
comment in SessionFactoryLookup ("every inheritance writes the rules-only kit, and nothing else does"), which issue 4351
made false, and the mission's sentence for that one path.

Asked of the Tech Lead: rule it in writing. Either (a) the start door's Boss is not the Boss for the inbox, so the wake
is right to start one, and the plan and the SessionFactoryLookup comment say so; or (b) it is the Boss, in which case
the start door needs a marker that tells it from a child (a child has a parent session of the same seat; the start
door's Boss does not), the inbox routes accept it, and the wake rings it. Either way the end-to-end proof in phase 6
should include "the owner's agent has the Boss open, a message arrives" so the answer is observed, not assumed.

### 2. Undefined factory: a Gateway restart between the start answering and the row write leaves a session nobody can recognise

Where: ClaimAndStartAsync takes the claim, sends the start, and only then Started writes the returned session id
(WriteOwned). A defined factory is safe across that gap: the started session carries the seat and the seat kit, so the
roster finds it. An undefined factory's session carries no seat (the Director stamps a seat only for a published
definition), so if the Gateway stops after the Director created the session and before the row holds its id, the new
Gateway reads a claim with no session id, waits the five-minute grace, and starts a second Secretary (or Boss).

Harm: one extra Secretary or Boss in an undefined factory, in a window of milliseconds during a deploy. Low. Worth a
line in the plan's decisions so it is a known limit and not a surprise; a cheap narrowing would be to match an
undefined factory's start by the exact session name the wake gave it, since the name is set before the start is sent.

### 3. The "no Director" sentence also covers a Director that is there but cannot build a kit

Where: ClaimAndStartAsync asks DirectorOn(machine, buildsKit) and records NoDirector with the machine when it answers
null. For a defined factory buildsKit is true, so a Director that is running on the Boss's computer but is too old to
build a seat kit is reported to the owner as "Waiting - no Director is running on the Boss's computer, SOREN_NORTH".
The owner looks at a running Director and is told there is none.

Harm: a misleading sentence in exactly the case where the owner needs the real reason (update the Director). Low.
One more waiting word, or the detail saying "the Director there cannot build a seat kit", fixes it.

### 4. The ring line counts every sent message, not the untold ones

Where: SecretaryAsync, line 299, RingLine(sent.Count); untold is computed on line 295. A Secretary told of one message
by its opening and rung for a second is rung with "2 new factory inbox messages". Harmless, because the Secretary runs
"factory inbox new" and files what it finds; a nit, untold.Count is the honest number.

## Things I checked and found sound, so nobody checks them twice

- A Secretary that ends holding received messages: the wake's HasEnded and the inbox's IsEnded are the same function
  (DevReportSessionLiveness: roster Exited or Failed, else a history ending), so the next Secretary the wake starts
  finds those claims lapsed and files them; no endless start loop.
- A session whose Director is only disconnected is Alive and cannot be rung; nothing else starts until its ending is
  recorded. Task D's stuck alert is the backstop if that never comes.
- The Secretary's seat is composed on the Gateway only; the kit route refuses a session key (existing test), the
  definition check reserves the name "secretary", and the kit holds the Boss seat's rules and the Secretary's brief and
  nothing else (Kit_TheSecretary_IsMadeUpFromTheBossSeat_TheFactorysRulesAndItsBriefAndNothingElse).
- FactorySeatStart.Build gives the Secretary no "Boss never does the work" paragraph (it is not the Boss seat and has
  the role Secretary), and gives the wake's Boss the full seat kit, never rules-only.
- The heartbeat runs one pass at a time, only for accounts with the factory agents switch on, is disposed on shutdown,
  and its first look is 60 seconds after boot so connected Directors have pushed before anything is started.
- Every failed start releases the claim and says why; an unknown outcome holds the claim for the grace; both proven.
- The two non-ASCII hits in the diff are the byte order marks dotnet ef writes at the top of generated migration
  files, as on every earlier migration.

## Answers (the Developer of Task C, 5 November 2026)

1. Ruled by the Tech Lead, option (a), and done as ruled. A Boss started through the Boss start door carries the
   rules-only kit and is not the inbox's Boss, so the wake starts one seat-kit Boss beside it and types nothing into the
   owner's conversation with it. The `SessionFactoryLookup` comment now says the start door writes the rules-only kit too,
   and `FactoryInboxWake.Find` says why a rules-only session in the seat is not counted. New test:
   `FactoryInboxWakeTests.ABossTheOwnersAgentStartedThroughTheBossStartDoor_IsNotTheInboxsBoss_SoOneSeatKitBossStartsBesideIt`
   (it passes with or without this commit's code change, because the behaviour was already as ruled; it now holds it).

   **OVERTURNED BY THE OWNER, 6 November 2026, for a recorded Boss.** Soren, in chat: "Yes, I also want to allow those."
   Issue #4598 gave the Gateway a record of every Boss it starts for a session in no factory (`factory_boss_starts`).
   A Boss with that record is the inbox's Boss: it decides bundles, starts seats, and the wake rings it instead of
   starting a seat-kit Boss beside it - option (b) above, with the record as the marker. A rules-only session in the
   Boss seat with NO record (a Boss's child, a restored or reopened child, a start-door Boss from before the record
   existed) is still not the Boss, as ruled. Tests:
   `FactoryInboxWakeTests.ABossTheGatewayRecordedAsStartedByASessionInNoFactory_IsTheInboxsBoss_AndIsRungNotDoubled`,
   `FactoryInboxWakeTests.ABossTheOwnersAgentStartedThroughTheBossStartDoor_WithNoRecord_IsNotTheInboxsBoss_SoOneSeatKitBossStartsBesideIt`,
   `FactoryInboxEndpointTests.Decide_ByTheBossASessionInNoFactoryStarted_IsAccepted`,
   `FactorySeatStartByTheBossTests.Start_ByTheBossASessionInNoFactoryStarted_IsAllowed_though_its_kit_is_rules_only`.
   Known limit, the same one a schedule-started Boss has: a recorded Boss the wake has never rung is not on the wake's
   row, so if its Director is disconnected when bundles wait, the roster lacks it and one seat-kit Boss starts beside it.
2. Declined, kept as a known limit. Matching an undefined factory's start by its session name would be a second way of
   recognising a seat, beside the row, for a gap of milliseconds during a deploy. And the harm is smaller than the review
   states: a session started in that gap is not on the wake's row, so the inbox routes refuse it as the Secretary or the
   Boss (`not_the_secretary`). It can file nothing and decide nothing, so it cannot double any work, and the stuck alert
   is still there if the next session does not start. The cost is one idle session the owner closes. Recorded here as
   the known limit.
3. Accepted. A new waiting reason, `director-cannot-build-kit`: when no Director on the seat's computer can build a seat
   kit but one runs there, the thread says "Waiting - the Director on the Boss's computer, X, cannot build a seat kit; it
   goes on when that Director is updated". "No Director is running" is now said only when none runs there. Test:
   `ADirectorOnTheBossesComputerThatCannotBuildAKit_StartsNothing_AndSaysToUpdateIt`. It is red without the change.
4. Accepted. The Secretary's ring line counts the untold messages, and the Boss's ring line likewise counts the untold
   bundles, which had the same nit. Test: `ARungSecretary_IsToldOnlyTheMessagesItWasNotToldOf`. It is red without the change.
