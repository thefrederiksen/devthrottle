# Review: Teams 6, Gateway part - the change made after the red continuous integration run

Pull request thefrederiksen/devthrottle#3532, head def562ec2 (rebased onto origin/main f2c5c29a9), for
devthrottle_internal#2304. Reviewed by a separate review session on 4 October 2026, in
`D:\ReposFred\devthrottle-teams-2304-delta-review` (detached at def562ec2, origin fetched, tree clean before and
after). This review covers ONLY what changed since the reviewed head c3b6de244; the full review is `review-2304.md`.

## Scope

**How the change was isolated:** `git range-diff d3c225be2..c3b6de244 f2c5c29a9..def562ec2`. The first four commits
are the reviewed ones carried across the rebase: three are identical, and the first differs only in the context
lines around its hunks in `GatewayHost.cs`, `TeamEndpointRules.cs` and `HostedTeamsDarkTests.cs` (main's
invitations work arriving beside it). Four commits are new: 3877ebefe, 333a8e9f2, d9f8850ad, def562ec2.

**Read in full:** `git diff fb4583bda def562ec2` - four files, 78 lines added and 35 removed:
`src/CcDirector.Gateway/GatewayHost.cs`, `src/CcDirector.Gateway.Tests/Teams/HostedTeamsDarkTests.cs` (the whole
file as it now stands), `src/CcDirector.Gateway.Tests/ContextLessRouteCensusTests.cs` (the whole file, its rules
included), and `docs/proof/teams-2304/test-runs.txt` section 9. Also the `GatewayHost.cs` diff of the pull request
at three points: the old head against its base, the rebased fourth commit against the new base, and the new head
against the new base.

**Read to check the change against what lies beneath it (not changed by this delta):**
`Cockpit/CockpitReactApp.cs` (the fallback that answers every unmapped path, and the browser-page middleware);
`GatewayHost.cs` at the Teams switch (lines 4687 to 4704), at the team gate's place in the pipeline (lines 3971 to
3980) and at the finalised route table (lines 5176 to 5205); `Tenancy/HostedRefusalRouteSpace.SelectFinalisedEndpoints`;
`TeamEndpointRules.Normalize`; `Api/ServerStampedAuthor.cs`; every `Map` call in `Api/SkillEndpoints.cs` and
`Api/WorkflowEndpoints.cs`; and the teardown of `CockpitReactAppServingTests`, which deletes `wwwroot/c`.

**Ran:** `dotnet test src\CcDirector.Gateway.Tests --filter "FullyQualifiedName~HostedTeamsDarkTests|FullyQualifiedName~ContextLessRouteCensusTests"`
with `MSBUILDDISABLENODEREUSE=1`, a fresh build of this worktree, with a result file. 5 passed, 0 failed, 0 skipped:
the three dark tests and the two census tests, each named in the result file. It did not wait on the machine-wide
lock. There was no `wwwroot/c` in the test output, so this was the "no Cockpit" condition only.

**Did not run, and could not reach:**
- The dark tests WITH a Cockpit page in the test output - the condition that failed in continuous integration. To
  make it I would have had to put a file into the build output, and I was told to edit nothing. That those tests
  pass there is from reading the code and from the Developer's section 9, not from a run of mine.
- None of the three red checks in section 9 was repeated, for the same reason. I judged them by reading (finding F1
  and the Verdict).
- Continuous integration run 37233259321 was not read. Nothing else in `CcDirector.Gateway.Tests` and no other
  suite was run.

## Verdict

**No blocker and nothing to fix before merge. Two notes.** The dark guarantee is stronger than it was at the
reviewed head, not weaker; every team route of this pull request is mapped inside the switch; the six census rows
left for the right reason and nothing else left with them; the invitations dark test kept everything it proved.

What the verdict rests on:

- **Mapped only inside the switch.** `TeamLibraryEndpoints.Map` is called once in the whole Gateway, at
  `GatewayHost.cs` line 4698, inside `if (TeamsReleased) { ... }` beside `TeamEndpoints.Map` and
  `TeamInvitationEndpoints.Map`. Against the new base the pull request's change to that block is four added lines
  and nothing else. No other file maps a path under `/teams`.
- **The dark tests cannot pass with a team route mapped.** Each of the three now reads the finalised route table -
  the same table production validates before it binds - and fails if any pattern starts with `/teams` (the
  invitations test also `/team-invitations`). That is a statement about what is mapped, taken from the mapping
  itself, and it does not depend on what a request happens to be answered with. It catches the exact leak the
  brief names: with the library mapped outside the switch the table holds `/teams/{teamId}/skills` and all three
  tests fail on that line, which is what section 9 reports. Before this change only the library test read the
  table; the other two would have stayed green with the library leaked, because they never requested a library
  route. So two of the three tests are strictly stronger.
- **The request half is not weaker than the pinned 404.** With no Cockpit in the output the control path answers
  404, so "answers like the control path" IS "answers 404" - the old assertion. With a Cockpit the control GET is
  the 200 shell as `text/html`, which no team handler and no gate refusal produces, so a mapped route still differs.
  A POST is pinned to 404 as well as compared, because the fallback refuses every verb but GET and HEAD
  (`CockpitReactApp.ServeAsync`, lines 145 to 149) whether or not the Cockpit is built. I checked that the control
  path is treated like a team path on the way in: `teams` is not one of the four browser-page roots, the requests
  send no `Accept: text/html`, and the team gate lets a request that reached no endpoint through.
- **The table read is not an empty one.** The library test still asserts `/gateway/skills` and `/gateway/workflows`
  ARE in the same table. The other two tests assert only the absence; they share one helper and one host
  configuration with the test that asserts the presence, so a table that came back empty would redden that test.
  I record this as an observation, not a finding.
- **The census.** The census is computed, not kept: it reflects each handler's parameters off the real route
  table. `clone`, `enable` and `disable` on both `/gateway/skills` and `/gateway/workflows` now take `HttpContext`
  (`SkillEndpoints.cs` lines 153, 184, 189; `WorkflowEndpoints.cs` lines 137, 181, 186), so they are not
  context-less and the six rows had to go; `publish` on both takes none and is still listed. Exactly six rows were
  removed and none added. The written verdict follows the file's own precedent for a route that leaves (the
  schedule delete, the rule promotion) and what it says is true: `ServerStampedAuthor.Resolve` only reads an item
  the team route group's filter put on the request, and the stores still take the tenant from the ambient scope.
  The request context is used for the author and for nothing else. Both census tests pass in my run.
- **The invitations dark test.** Same eight routes, same two callers, same bodies on the POSTs, the same seeded
  team and waiting invitation, and the same four assertions on what was written (one invitation, still `sent`, same
  link hash, nobody joined). The only changes are the route table assertion added before the loop and the loop
  body replaced by the shared helper.
- **Nothing else changed.** Four files. No production code beyond the four moved lines in `GatewayHost.cs`.

## Findings

### F1 - note - the rebased branch did not compile; the record says the rebase silently un-darkened the library

**Where:** `docs/proof/teams-2304/test-runs.txt` section 9 ("The rebase also put TeamLibraryEndpoints.Map outside
the Teams switch (a merge, not a conflict); moved back inside", and the red check "TeamLibraryEndpoints mapped
outside the switch ... all 3 dark tests FAILED"); the message of commit d9f8850ad; `GatewayHost.cs` at commit
fb4583bda, lines 4698 to 4706.

**The harm:** at fb4583bda the rebase left the library's `Map` call and the closing brace of the old block AFTER
the un-braced `if (TeamSeatConvergence is { } convergence)` statement, with no opening brace to match. That file
has one more closing brace than opening braces (main f2c5c29a9: 686 and 686; fb4583bda: 687 and 688, the extra
opening one being the `{teamId}` in a comment; def562ec2: 687 and 687). It is a syntax error, so commits
805e90110 through 333a8e9f2 cannot be built, and a Gateway with the library mapped outside the switch could never
have come out of the rebase as it stood. The compiler stopped it, not the dark tests.

So two things in the record are not what happened. The cause is described as a silent leak when it was a build
break; and the second and third red checks were run on a state the Developer made by hand to see the tests fail,
not on the state the rebase produced. As a check that the new assertions can fail, a hand-made leak is the right
thing to run, and by reading I agree it fails all three tests. But the record should say that is what it was. A
later reader who takes section 9 at its word will believe a merge can quietly put a team route outside the switch
and that these tests caught a real instance; neither is shown. The red checks are also recorded as one line each,
with no failing assertion text, so they cannot be told apart from a failure for some other reason.

No change to code is needed. The pull request is squashed on merge, so the unbuildable commits do not reach main.

Developer answer: Accepted; the record was wrong and is corrected. The first build after the rebase stopped with CS1519 in GatewayHost.cs - I saw the compiler errors and then described the cause as a leak, which it was not: the rebased commits 805e90110 to 333a8e9f2 do not compile, and no Gateway with the library outside the switch could have come out of them. Section 9 of `docs/proof/teams-2304/test-runs.txt` now says that, names the unbuildable commits, and says the squash keeps them off main. The second and third red checks are now labelled as a leak made by hand (the call moved before `AdminTrialEndpoint.Map`, in a file that compiles), not the rebase's state. I kept only the failing test names from those runs, not the assertion text, and the record now says so; by reading, the first assertion to fail is the route-table one, which runs before any request. The message of d9f8850ad is pushed history and is not rewritten; the proof file is where the correction lives.

### F2 - note - the dark tests are proven in the condition that failed only by the Developer's own run

**Where:** `docs/proof/teams-2304/test-runs.txt` section 9, "Dark tests with a Cockpit index.html in the test
output (the CI condition): Passed 3 of 3"; `src/CcDirector.Gateway.Tests/Teams/HostedTeamsDarkTests.cs` lines 169
to 179.

**The harm:** the whole reason for this change is that the tests' result depended on whether `wwwroot/c` existed
when they ran. The local gate builds Debug, where it never exists, so every local run - mine included - exercises
only the branch that already passed. The other branch (the 200 shell) is reached only when the Cockpit is built in
or another test has staged it, and nothing in the test class makes that happen: the class does not stage a page
and run its assertions a second time. Today that branch is covered by one hand-made run recorded in one line, and
from now on by whichever order the continuous integration runner picks. If the run 37233259321 happened to order
`CockpitReactAppServingTests` first again, it is green without the fixed branch having run at all, and it would be
read as confirming the fix.

This is a note because the reading holds - I could find no way for a mapped team route to answer a GET with the
shell - and because the route table assertion, which does the real work, does not depend on the Cockpit at all.
What I would ask of the Tech Lead is only this: when reading run 37233259321, do not take a green as proof that
the Cockpit-present branch ran. If that matters, the lasting answer is a dark test that stages a page itself, in
the collection that already serialises these hosts; that is a later change, not this one.

Developer answer: Accepted as written. The page-present branch has run exactly once, by hand, and section 9 now says that, and that a green CI run does not show it ran. I agree the lasting answer is a dark test that stages its own page; it is not a small change, because `CockpitReactAppServingTests` deletes `wwwroot/c` and sits in no collection, so a dark test that stages a page would race it unless both are serialised together (and `SpaFallbackNonGetTests`, in the `DirectorRoot` collection, writes there too). I have not built it here, per "nothing to fix"; it should be its own issue against those three classes, and I recommend the Tech Lead files it.
