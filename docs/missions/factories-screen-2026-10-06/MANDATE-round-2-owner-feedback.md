# Mandate - round 2: the owner's feedback on the live list (2026-10-06, 23:39)

From the Factory Manager (708e71ec), which started you. The owner looked at the live list
(Website Business FAILING with "63 decisions", Machine Care NEEDS YOU with "1 decision", Tallyhand PAUSED)
and said, in his words:

> "I don't know why the first one has failed. And the second one needs you. And I don't know how to clear it
> or how to fix it. There's no clear way to say. Go fix this. And why is the tally hand paused? And how do we
> actually just remove the tally [hand] if we no longer need that factory?"

Pull origin/main into a NEW worktree off origin/main for this round (the factories-screen branch is merged).
Same rules as MISSION.md: separate review per pull request (Codex, else Fable), merge to main, deploy only with
the deploy-hosted-gateway skill, no attribution, foreground, QA on the live site.

## What the Factory Manager found (verify, do not trust)

- **Website Business FAILING** comes from four Sender rows at 2026-10-06 12:02 UTC: "keep.page (failed):
  https://centerconsulting.com/websites/keep/<slug> answers 404 after 20 min" (alltypes-fence,
  sooner-excavation, reynolds-septic, alderson-tree). All four answer 200 now. So the status says FAILING for
  something that has since fixed itself, and nothing on the screen says what failed.
- **The "63 decisions"** are escalations since 21 September never marked handled; most are stale. Machine
  Care's "1 decision" looks like its 2026-09-23 escalation ("C has 55.3 GB free, below the 60 GB line"),
  long resolved.
- **Tallyhand is closed.** `cc-consult/ideas/tallyhand/MOVED.md`: on 2026-10-04 the owner moved it into
  mindzie as the mindzie AI Reports factory. It was registered by mistake (the Factory Manager's review listed
  it). That is also why both showed the CEO "Max Ridley".
- One real item hiding in the noise: Sender, 2026-10-06, needs-you: "Gmail asks soren@centerconsulting.com
  to sign in again; 4 planned for 2026-10-07". The Factory Manager tells the owner this directly; your job is
  that the screen would have shown it at the top instead of burying it under 62 stale rows.

## What to build

1. **Every status word explains itself.** Under FAILING / NEEDS YOU / PAUSED on the list, one short line:
   what it is (e.g. "Sender: 4 keep pages answered 404, 12:02"). Clicking the status or the count opens the
   items on the factory's page.
2. **FAILING clears when the failure is over.** A failed row stops counting when a later successful row for
   the same seat and subject exists, or when the owner marks it handled. Write the rule into the decisions
   file and test both clearing paths.
3. **Waiting on you is actionable.** On the factory page: each item with its text, when, which seat, a link
   to its evidence, and "Handled". Plus one owner action to clear old items in bulk ("Mark everything older
   than 7 days as handled"), with a confirm that says how many, recorded in the activity record as the owner's
   act. Newest and most important first. Never hide or expire an item silently.
4. **Remove a factory.** An owner-only "Archive factory" action on the factory page: a confirm that lists
   exactly what it does (removes it from the list, disables the named Gateway schedules it owns if any,
   keeps all history), recorded in the activity record. A "Show archived" view to restore one. Then
   **archive Tallyhand** on the live Gateway as its first use - the owner asked for it removed, and archiving
   can be undone. Say so in the report.
5. **PAUSED says why.** A factory with no schedules at all says "Nothing scheduled" under the word, not the
   same thing as a factory whose schedules were switched off.
6. **Center Consulting's head is its CFO, Ruth Calder** - the list should offer "Talk to Ruth Calder", not
   "No CEO". The factory's head may have any title.

## Done means

Merged, deployed, and a short QA addendum (a new version of your QA report, same file) with live screenshots:
the list where every non-RUNNING row says why, a failure that cleared itself, the bulk clear used on Website
Business and Machine Care's stale items (owner's act - do it only if the owner says so in the report; until
then show the control and the count), Tallyhand archived and restorable, and Center Consulting with Talk to
Ruth Calder. Then handback to the Factory Manager.
