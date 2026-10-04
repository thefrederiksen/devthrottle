# Review: Teams 6, the team's shared skills and workflows, Gateway part

Pull request thefrederiksen/devthrottle#3532, head 577dbf919, for devthrottle_internal#2304.
Reviewed by a separate review session on 4 October 2026, in `D:\ReposFred\devthrottle-teams-2304-review`
(detached at 577dbf919; origin/main fetched, merge base cdaab4f2b).

## Scope

**Read in full:** the production diff (`git diff origin/main...HEAD -- src/CcDirector.Gateway`):
`Api/TeamLibraryEndpoints.cs`, `Api/SkillEndpoints.cs`, `Api/WorkflowEndpoints.cs`, `GatewayHost.cs`,
`Teams/TeamEndpointGate.cs`, `Teams/TeamEndpointRules.cs`. All five test files in the diff. The proof file
`docs/proof/teams-2304/README.md` and `test-runs.txt`. Issue #2304, the role table in #2098, the Developer's brief,
and screen S5 of the mockups.

**Read to check the diff against what lies beneath it (not changed by this pull request):**
`Tenancy/HostedTenantBoundary.cs`, `Core/Tenancy/AsyncLocalTenantContext.cs`, `Core/Tenancy/TenantId.cs`,
`Teams/TeamAccess.cs`, `Teams/TeamRegistry.cs` (`IsTeam`, `RoleOf`), `Api/TeamEndpoints.cs` (`ResolveCaller`),
`Skills/SkillStore.cs` (every read and every write path), the request pipeline in `GatewayHost.cs` lines 3775 to
3943 (authentication, the personal tenant scope, routing, the team gate), the key definitions for the skill and
workflow tables in `Data/GatewayDbContext.cs`, and the session key guard's entries for skills and workflows.
`Workflows/WorkflowStore.cs` was read for its log lines and shared state only, not line by line; it has the same
shape as the skill store.

**Ran:** `dotnet test src\CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Teams"` in the review worktree:
398 passed, 0 failed, 0 skipped, which matches section 4 of `test-runs.txt`. No file in the worktree was edited.

**Did not run, and could not reach:**
- `CcDirector.Gateway.Tests` was NOT run (the machine-wide lock belongs to the Tech Lead's gate runs and the
  machine is short of memory). So every end-to-end claim in `HostedTeamLibraryTests` and `HostedTeamsDarkTests` -
  the three tests of the issue among them - is taken from reading the test code and the committed output, not from
  a run of mine. The three revert checks were likewise read, not repeated.
- The whole Gateway unit suite was not run. `test-runs.txt` section 5 records one failure in its first full run
  whose name was cut off and which did not recur; I cannot say what it was.
- No PostgreSQL run. Nothing here depends on the database engine as far as I can see (no schema change, exact
  string equality on the team id on both engines), but that is reading, not a run.
- The Cockpit page is not in this pull request and was not reviewed.

## Verdict

**No blocker. Two should-fix findings and two notes.** The tenant switch is sound: I could not find a path that
runs a skill or workflow route in a team's tenant without the gate having allowed that request in that team.

What I checked on the dangerous part, so the Tech Lead can see what the verdict rests on:

- Every route under `/teams/{teamId}` in the new group carries the group's filter, and the filter enters the team
  only when the route's `teamId` equals, exactly, the team the gate recorded on this request
  (`TeamLibraryEndpoints.TeamToEnter`). The gate records a team only on `Allowed`, and for these rules the team it
  decides is the same route value the filter reads. There is no second source for the team.
- A route under `/teams/` with no rule is refused by the gate itself (`NamesATeam`), so a route mounted without a
  rule cannot be served. The rule prefixes match the mounted patterns (`/teams/{teamId}/skills/...`,
  `/teams/{teamId}/workflows/...`, and `/teams/{teamId}/library` exact).
- Every write verb in both endpoint files is a non-GET method, so it falls under the "change" rule; every GET is a
  read with no side effect.
- The caller is the person behind the request's own personal account. A key bound to a team's tenant, the shared
  machine token and a session key are all refused before any store is touched.
- The store resolves the tenant from the ambient scope at each call; built-ins come from the fixed library
  partition; a clone can read only the library or the ambient tenant. So a request in team A cannot read or write
  team B's rows or the caller's personal rows. The skill and workflow tables are keyed by tenant and id, so two
  teams may hold the same id without colliding.
- The scope is restored: `AsyncLocalTenantContext.Enter` restores the previous tenant on dispose, the filter is an
  async method so its change cannot flow back to the caller, and nothing in either store keeps state per tenant in
  memory. The response body is built from data already read inside the scope.
- Built-ins: every authoring write asks `IsLibraryBuiltIn` first, inside a team as anywhere.
- Teams dark: `TeamLibraryEndpoints.Map` is inside the same `if (TeamsReleased)` as the other team routes; the
  default roots of the two existing `Map` methods are unchanged, and no literal route string was left behind.

## Findings

### F1 - should-fix - a member's email is written to the Gateway log on every add, clone and switch

**Where:** `src/CcDirector.Gateway/Skills/SkillStore.cs` lines 441, 632, 698, 712;
`src/CcDirector.Gateway/Workflows/WorkflowStore.cs` lines 312, 521, 828, 844 (all write `authoredBy=` or `by=` to
the log); `src/CcDirector.Gateway/GatewayHost.cs` line 3842 with line 2416 (the access log writes the query string
as sent, so `?by=...` on enable, disable and clone is logged a second time). Made reachable for a person's identity
by this pull request at `src/CcDirector.Gateway.Tests/Teams/HostedTeamLibraryTests.cs` lines 132 and 142
(`authoredBy = "manager@example.com"`), `docs/proof/teams-2304/README.md` line 60
(`"changedBy": "manager@example.com"`), and `src/CcDirector.Gateway/Api/TeamLibraryEndpoints.cs` lines 303 to 311,
which serve that field as the page's "changed by".

**The harm:** the store's log lines are not new, and in a personal account the value is whatever an agent calls
itself. This pull request is what turns the field into "which person changed the team's skill" - screen S5 shows
"Priya, 2 Oct" - and its own fixture and its own documented response fill it with an email address. A Cockpit page
built to this contract sends the member's email as `authoredBy` and as `?by=`, and the Gateway then writes that
email into its log on every create, clone, enable and disable. An email in the log is forbidden on this mission.
The tests here already do it: `ManagerAddsSkill` causes `[SkillStore] CreateDraft: ... authoredBy=manager@example.com`.

**Why it must change before the page is built, not after:** the page will copy the fixture. Either the Gateway
stamps the name itself from what it already knows about the caller (and logs nothing personal), or the contract
says in writing that the field carries a display name and never an email, and the fixture and the proof sample are
changed to match. Either way the choice belongs in this part.

Developer answer: **Accepted and fixed - the Gateway now stamps the author itself, and nothing personal is stored or logged.** Every team library route records the author from the member the server identified, and ignores the client's `authoredBy` and `?by=` (`ServerStampedAuthor`, set by the team route group's filter; the two endpoint files read it through `ServerStampedAuthor.Resolve`, so a route that stamps nothing - every personal account - behaves exactly as before). What is stored, and so what the stores write to the log, is an opaque member reference: `team-member:` plus 16 hex characters of SHA-256 over the team id and the account subject (`TeamLibraryEndpoints.MemberReference`). It carries no email and no subject, and it differs per team for the same person. The page's read turns it back into a name from the team's own member list. The fixture no longer uses an email as the author, and the proof's sample was changed. One part I did not do as suggested: **a display name**. The Gateway holds no display name for a person (the `tenants` row has only an email), and the Team page's member list (#2300) already shows the email as the member's name to every member. So "changed by" uses that same rule, now one function (`TeamEndpoints.MemberName`), so the two pages cannot disagree; when a display name exists, both change in one place. The email is shown only to members of the team, who can already read it on the Team page, and it never reaches the log or the stored row. The page will not send `?by=` on team routes, so the access log line has nothing personal in it either. Tests: `ChangedBy_IsTheMemberTheServerIdentified_StoredAsAReferenceWithNoEmailOrSubject` (create by a Manager, and a clone by the Owner with a typed `?by=`, both recorded as the caller's reference, no `@`, no subject), `ChangedBy_APersonalAccountsAuthor_IsStillWhatTheClientTyped`, and unit tests for `MemberReference`, `ChangedBy`, `ServerStampedAuthor` and `MemberName`.

### F2 - should-fix - two sentences say a team session's own pull works today; the gate refuses it today

**Where:** `src/CcDirector.Gateway/Api/TeamLibraryEndpoints.cs` lines 28 to 29 of the file (the class summary:
"A session on a Director that belongs to the team needs none of this: its key is bound to the team's tenant, so the
ordinary `/gateway/skills` and `/gateway/workflows` already answer with the team's library");
`docs/proof/teams-2304/README.md` line 20, in the table headed "Already on main" ("A session whose key is bound to
the team's tenant is allowed to read the team's library"); and the test name
`Issue2304Test1_TheSessionSide_ATeamSessionsOwnPull_IsAllowedAndServedTheTeamSkill_WaitingOnlyOn2311ToAuthenticate`
(`HostedTeamLibraryTests.cs` line 193).

**The harm:** on this commit that request is refused, twice over. The device registry does not accept a key bound
to a team's tenant, and if it did, `TeamEndpointGate.RunAsync` answers "caller unknown" for it
(`TeamEndpointGate.cs` lines 190 to 193 and 232 to 237: a team's tenant has no subject). The session-side test does
not exercise that path: it calls `Check` directly and hands it the Developer's subject and `TeamOwnership.Callers`
itself (lines 203, 223, 225), which is exactly what the real middleware cannot yet supply. So that test proves two
true things - the role table allows a Developer to read and refuses a change, and the row lives in the team's
partition and no other - and it would stay green if no request from a team session were ever allowed. It is not
evidence that a team session is served.

The proof file's own section "The session side, and what waits on #2311" states all of this correctly, including
the session-key seam, and I find that section truthful. The fault is that the three places above contradict it, in
the present tense, and two of them are where the next reader looks first. "Waiting only on #2311 to authenticate"
is also too narrow by the proof file's own account: authentication, the gate naming the person behind a team
device key, and the gate naming the person behind a team SESSION key are three things, and the third is not yet in
anybody's brief.

For the Tech Lead's ledger: the issue's test 1, as written ("available to a Developer's next session on that
team"), is NOT met by this pull request. What is met is "available to a Developer on that team, from their own
account, and to no other team and no personal account", over real HTTP. That is a fair place to stop for the
Gateway part, provided the three sentences say so.

Developer answer: **Accepted and fixed.** All three now say what is proven today and what is not. The class summary of `TeamLibraryEndpoints` says what is proven is a member reaching the team's library from their own account, and that a team session's own pull is refused today on three counts (the device registry, the person behind a team device key, the person behind a team session key), all waiting on #2311's one resolver in `seam-director-key.md`. The proof's "Already on main" row now describes only the role table side and says the request is still refused. The test is renamed `Issue2304Test1_TheSessionSide_RoleTableAndStoreOnly_CallerSuppliedByTheTest_TheRealPullWaitsOn2311`, and its class comment and the proof say it proves the role table and the tenant partition only, and would stay green if no team session were ever served. The test table now states plainly: **#2304 test 1 as written ("a Developer's next session") is not met yet; it waits on #2311.** The third gap - the person behind a team session key - is now in #2311's scope (seam 2 of `seam-director-key.md`, accepted 4 October).

### F3 - note - "changed by" on the team's page is whatever the client typed, not the member the server identified

**Where:** `src/CcDirector.Gateway/Api/TeamLibraryEndpoints.cs` lines 302 to 304 and 309 to 311
(`published?.AuthoredBy ?? ""`), fed by the `authoredBy` field of the request body.

**The harm:** the gate knows exactly which member made the request, and discards it; the page then shows a name the
request supplied. An Owner or Manager - the only roles that can write - can record a change under any name,
another member's included, and every member reading S5 sees it as fact. Inside a personal account this never
mattered, because there was one person. In a team it is the only record of who changed a shared skill. Low harm
today (two trusted roles), and it shares its remedy with F1, which is why it is a note and not a separate demand.
The `?? ""` also means a published row with no version row shows a blank author rather than failing; I could not
construct that state, so I only mention it.

Developer answer: **Accepted - fixed by the same change as F1.** The author is now the member the gate identified, never what the request typed, so an Owner or Manager cannot record a change under another member's name. On the `?? ""`: the page no longer shows raw text in any case. `ChangedBy` answers the member's name for a reference to a current member, "A former member of the team" for a reference to someone who has left, and "Not recorded" for anything the server did not stamp, including a missing version row - it never shows a client-typed value as fact. I could not construct a published head with no published version row either; the store throws on that state elsewhere (`GetPublished`), so I left it as "Not recorded" rather than adding a second check.

### F4 - note - one unnamed failure in the full unit suite is recorded and left unexplained

**Where:** `docs/proof/teams-2304/test-runs.txt` section 5; `docs/proof/teams-2304/README.md` lines 127 to 129.

**The harm:** the first full run of `CcDirector.Gateway.UnitTests` had one failure out of 8,363, the name was lost,
and the second run used `--no-build` on the same binary and passed. It is disclosed honestly, which is why this is
a note. But a failure nobody can name is not shown to be unrelated to this change, and this change touches shared
static seams (a new item on every allowed request, a process-wide hosted-mode switch in the tests beside it). If it
appears again in the Tech Lead's parked run, capture the name from the result file rather than the console summary
before deciding it is someone else's.

Developer answer: **Accepted - rerun, and this time named.** The full `CcDirector.Gateway.UnitTests` was run again on the rebased branch with the review fixes (head bf4fd8127), with a result file, not the console summary: 8,371 passed, 8 skipped, 1 failed. The failure is `CcDirector.Gateway.Tests.Factory.Memory.FactoryMembershipThroughTheCallersTests.A_GRANDCHILD_IS_BORN_INTO_THE_FACTORY_THROUGH_TWO_REAL_HOPS`, with `SQLite Error 19: 'UNIQUE constraint failed: device_import_markers.SourcePath'`. The class then ran alone three times: 19 of 19 passed each time. This branch changes no file in that test's area (`git diff --name-only origin/main...HEAD` lists only Teams, the skill and workflow endpoints, `GatewayHost.cs` and the proof). The test builds `new DeviceRegistry()` with no arguments (line 170), and the clash is on the import marker's source path, so the likeliest cause is two tests in the parallel run sharing one default device-file path. That cause is my reading, not observed. Whether the first run's unnamed failure was this same test I cannot say; it was not captured. Recorded in `test-runs.txt` section 5. If it shows up in your `-Parked` run, it is the same name to look for.
