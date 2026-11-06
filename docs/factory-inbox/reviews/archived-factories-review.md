# Review: the factory page offers a message only where the inbox takes one (pull request 4652)

**Verdict: APPROVE.**

Reviewed on 6 November 2026. One commit, `42eeb2415`, on top of base `00cbc58df`. Eleven files, 92 lines added, 6 removed.

## Scope

What was reviewed:

- The whole diff of the commit against its base: the new `FactoryInboxReach` class, its three callers (the Inbox list
  route, the wake's look, and the factory page fold), the new `LeaveMessageLabel` field on the page contract, the Cockpit
  factory page, the fixtures, and the three new tests.
- The send route's refusal (`RefuseOwnerSend` in `FactoryInboxEndpoints.cs`), which the change does not edit, read
  against the new rule.
- Every other place in the Gateway that reads `ArchivedAtUtc` or `BossSeat` for the inbox, to see whether a fourth
  copy of the rule was left behind.
- The phone shell, to see whether a second surface offers "Leave a message" and was missed.
- The branch against current `origin/main`, which has moved ten commits past the base.

What was run, on this machine:

| Run | Result |
|---|---|
| Gateway unit tests, every test under the Factory folders, on the branch | 83 passed, 0 failed |
| The two new Gateway tests by name, on the branch | 2 passed |
| Cockpit `FactoriesScreen.test.tsx`, on the branch | 93 passed |
| The two new Gateway tests with the fold, the send route and the wake reverted to the base | the fold test fails; the list-and-send test passes |
| The new Cockpit test with the factory page reverted to the base | fails: expected "Leave a message" to be "Leave a message (fixture)" |
| Trial merge onto `origin/main` at `d0d36d3c1` | clean, no conflicts |
| The Factory folder tests and the Cockpit test on the trial-merged tree | 3,123 Gateway unit tests passed, 0 failed; 93 Cockpit tests passed |

What was not done: no live Gateway, no phone, no full local gate. The tree was restored after every revert
(`git status` clean) and the trial merge was never pushed.

## The checks asked for

**One rule, one place, used by every caller.** `FactoryInboxReach.TakesMessages` is the rule, and it is the only
place the two facts are joined with "and". The Inbox list filters with it, the wake refuses with it, and the page fold
sets the label with it. The send route does not call it: it checks the same two facts one after the other so it can name
the reason in words ("archived, restore it first" against "names no Boss seat"). The class comment says so openly.
Whether that counts as "one place" is answered by the new test `List_AndSend_AgreeOnEveryFactory`, which sends to every
registered factory and asserts that the listed ones are accepted and the unlisted ones are refused in words, over a
fixture that holds a live factory, an archived factory with a Boss, and a Boss-less one. So the list and the send cannot
drift apart on either reason without that test going red. Note that this test also passes on the base, because the list
and the send already agreed there; it is a guard for the future, not a regression test for this defect. That is fine and
worth knowing.

No fourth copy of the rule was left behind. The stuck alert reads `BossSeat` to find the Boss's computer, the read scope
reads it to check who the Boss is, and the Talk button reads it to find the seat; none of those decides whether the
inbox takes a message.

**The client decides nothing (rule 7).** The Cockpit page renders the button when the Gateway gives a label and renders
the Gateway's text as the label. The only conditional in the view is "is there a label", which is layout, not meaning.
The composer panel is guarded by the same label, so an open panel cannot outlive the label when the owner archives the
factory from its page and the view reloads. The composer itself still prints the Gateway's refusal, so a race between
the page load and an archive elsewhere still ends in the Gateway's words, not a client guess.

**The phone.** The phone has no factory page, so there is no second "Leave a message" button to drift. The phone's
composer sits on the Inbox list, and the list is filtered by the same rule.

**Tests fail without the change.** Proved by running them, see the table. The fold test fails on the base code because
the label is never set. The Cockpit test fails on the base code because the button is hard-coded. The list-and-send test
is a guard and passes on the base, as said above.

**No fallback.** A factory that takes no messages gets a null label, never a default string. The contract field has no
default value, so an older Gateway cannot quietly offer the button; the Cockpit and the Gateway ship in one image in any
case.

## Observations, none blocking

1. **The send route reads the two facts itself rather than asking `TakesMessages` first.** Today the two are the
   same two facts and the new test binds them. If a third condition is ever added to `TakesMessages`, the send will
   not follow it unless the test fixture gains a factory that shows the difference. A one-line shape that keeps the
   reasons and still asks the rule first is possible, but it is a preference, and no harm follows from the code as it
   is.

2. **`TakesMessages` is true for a Boss seat name that is not among the registered seats.** Then the page offers a
   message but no Talk button, and the wake later reports it cannot start the Boss. That is the mission's own rule
   ("every factory with a Boss seat gets an inbox, defined or not") and was true before this change; it is noted only
   so nobody reads the two buttons disagreeing as a defect of this pull request.

3. **The branch is ten commits behind `origin/main`.** Main changed the send endpoint file and the endpoint test
   fixture after the base (the held spoken recordings, pull request 4630), in lines this change does not touch. The
   trial merge is clean. Rebase or merge before landing, as rule zero says.

## Verdict

APPROVE. The defect is fixed where it was caused: the rule the send enforces is now the rule the page reads, the
Gateway carries the verdict as a label, and the Cockpit only renders it. Nothing on the phone changes. The tests
prove the fold and the Cockpit view would regress without the change, and the list-and-send test keeps the two
Gateway surfaces agreeing from here on.
