# Review of Item A - a factory's boss can read its own Factories screen (issue 3685)

Reviewer: a separate review session (session 2c26afbe), 8 October 2026.
Verdict: nothing found. No finding proves harm. Four notes follow the findings section; none blocks the merge.

## Scope

What I read:

- The whole uncommitted diff in `D:/ReposFred/devthrottle-fdi` (ten modified files) and the two untracked files
  `tools/cc-devthrottle/src/factory_status_ops.py` and `tools/cc-devthrottle/tests/test_factory_status_ops.py`.
- The uncommitted diff of `ideas/website-factory/factory/.claude/skills/website-ceo/SKILL.md` in
  `D:/ReposFred/cc-consult-fdi`.
- Item A of `docs/missions/factory-design-improvements-2026-10-08/DESIGN-report.html`.
- Around the change, on origin/main: the whole of `SessionKeyGuard.Check` and `IsAllowed` (the three-part rule sits
  inside the `GET`/`HEAD` branch); every route mapped under `/gateway/factories` (`FactoriesScreenEndpoints`,
  `FactoryOwnerActionEndpoints`, `FactoryTalkEndpoints`); how `GatewayHost` wires `resolveTenant` for these routes
  (`GatewayEndpoints.ResolveReadTenant`, the same binding the activity record and registry routes use);
  `FactoryAgentsGate` (a switch-off answers a bare 404 with no body); the answer shapes in
  `FactoriesScreenDtos.cs` and `FactoryAgentsViewDtos.cs`; the clearing rule in `FactoriesScreenFold.IsOver`; the
  activity record's handling of `corrects` (no restriction on which row a correction names).
- The one commit this worktree is behind origin/main (3f07647c3, the Factories list sort order).

What I ran, all in the foreground:

- `dotnet test src/CcDirector.Gateway.UnitTests --no-build --filter "FullyQualifiedName~SessionKeyGuardTests|FullyQualifiedName~FactoryTalkSeedTests"`:
  358 passed, 0 failed. The test binary (built 15:21) is newer than every edited source (15:13 to 15:15), so the run
  certifies this source and not an older build.
- `python -I -m pytest tools/cc-devthrottle/tests/test_factory_status_ops.py -p no:cacheprovider` on the click 8.2.1
  interpreter: 11 passed.
- `python -I -m pytest tools/cc-devthrottle/tests/test_axi_step_6c_help_and_errors.py` (the actions catalogue pin,
  also modified): 508 passed, 3 skipped.
- A scan of every added line in both diffs and both new files: zero characters outside ASCII; no mention of an
  assistant, a vendor, or an attribution trailer.

What I could not reach:

- `src/CcDirector.Gateway.Tests/FactoryRegistryRouteTests.cs` was not run (the suite queues on a machine-wide lock
  another session holds). It is the only test that exercises `OwnerOrSession` at the endpoint. I read it instead:
  it now asserts 200 for a session key on the list and the page, the same status word the owner got, 404 for an
  unregistered factory, and 403 on the Seats tab. The version on origin/main asserts 403 on those two reads, so the
  new assertions are red on the old endpoint code by construction. The Delivery Lead should see it pass once on the
  rebased branch before merging.
- No live Gateway was driven; every proof above is a test.

## Findings

Nothing found. Each of the four things I was asked to look for, with what I checked:

1. A session key reaching a write or another account's data. Not found. The new three-part rule in
   `SessionKeyGuard` is inside the `GET`/`HEAD` branch, so no verb that writes passes it. Every write mapped under
   `/gateway/factories` is four or five path parts (`failures/{id}/handled`, `waiting/handled-older`, `archive`,
   `restore`, `seats/{seat}/talk`) and the Seats tab is four, and the new refusal test names each of them. The only
   three-part `GET` mapped today is the page. The tenant on both routes comes from `ResolveReadTenant`, the same
   binding the activity record and registry routes already use for a session key, so the answer is the key's own
   account. The answers do carry the owner's action descriptors (archive, restore, bulk handled labels), session ids
   and labels, archived rows and the account's schedules outside any factory - all account data a session key can
   already read elsewhere (`cron/jobs`, `sessions`), and the actions themselves stay refused.

2. A word or verdict the command decides itself. Not found. The status word, the reason, the items, their row ids,
   `by`, `subject`, `what`, `link`, the waiting word (asked or escalated) and the truncation note are all printed from
   the Gateway's answer, and the `--json` flag prints that answer unchanged (two tests compare it equal to the
   server's body). An answer with no rows, or a page with no status word, is an error and is proven never to print
   RUNNING. The four words the Talk seed, the skill and the reference name (FAILING, NEEDS YOU, PAUSED, RUNNING) are
   exactly the four constants in `FactoriesScreenFold`. The one sentence of the command's own is the switch-off
   explanation for a 404, which the other factory commands also supply because the gate answers with no body.

3. A test that would stay green if the production change were reverted. Not found. Origin/main's guard has zero
   mentions of `gateway/factories`, so the two new allowed cases in `SessionKeyGuardTests` return Refuse on the old
   code. The new seed test asserts sentences absent from the old seed ("all three", "factory status"). The command
   line tests invoke the real application through the real gateway module against a local web server and assert the
   path it requested; on the old code `factory status` is an unknown command. The route test asserted 403 before and
   asserts 200 now.

4. ASCII, abbreviations, attribution. Clean. Zero non-ASCII characters on any added line; the only hit for the
   attribution scan is the directory name `.claude/skills` in the skill's path, which is where that agent reads
   skills from and not an attribution. Prose uses the product's own words (row id, session key); I found no
   shortened form that is not already the product's vocabulary.

## Notes (not findings; nothing here blocks the merge)

- The worktree is one commit behind origin/main. That commit (3f07647c3) adds `statusRank` and `waitingCount` to the
  list rows and touches `FactoriesScreenFold` and `FactoriesScreenFoldTests`. The addition is purely additive, the
  command reads fields by name, and the diff touches none of those files, so I expect a clean rebase - but the
  branch must be rebased and the route test run on the rebased base before the merge.
- With `--factory`, the lines `waiting on the owner: N` and `failing[N]` are counts of the items the Gateway
  returned, not a number the Gateway stamped (the page has no such field; the list's `waitingCount` is new on
  origin/main). A count of the Gateway's own rows is not a verdict, and the truncation note is printed beneath, so I
  do not call it a defect. If the owner wants no number computed on the client, the page could stamp the count.
- In `SKILL.md`, step 12, the record command reads `--outcome done     --corrects` (five spaces where a line
  continuation looks to have been dropped). It runs as written. Its evidence prefix is "Handled by the boss:" while
  the Talk seed and the reference say "Handled:"; harmless, both are free text, but one wording would read better
  on the screen.
- The activity record accepts a correction of any row from a session key, with no check on the corrected row's
  outcome. That is what makes the boss's "mark it handled" work without a new write route, and it was already so
  before this change; the seed and the skill both say "only if it is resolved", which is the right guard for it.
