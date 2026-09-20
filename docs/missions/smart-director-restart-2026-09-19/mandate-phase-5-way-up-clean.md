# Mandate - Smart Director Restart - Developer: the restart screen makes sense, and records stop piling up

You are a Developer on the Smart Director Restart mission. You were opened by the Delivery Lead
(session number 150) and you report to it. You have one task with two halves, both about the way up:
what it OFFERS, and how it READS. You have no transcript; this file and the files it names are your
history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-wayup`, branch `smart-restart/way-up-clean`,
cut from `origin/main` at `e188d3048`.

## Why this exists - the owner's own words, 20 September 2026

He ran the feature, photographed the restart offer, and could not read it:

> I don't understand because when the restart comes back, why are there multiple sessions, were that
> from multiple old restarts? If that's the case, we're totally missing a date and time when that was
> saved. And there should be a way to remove old [ones]. Once you restart a session, it shouldn't be
> there anymore, I think, or they should timeout. It's really confusing with all of those on the
> screen.

Then: "clean up the restart screen so it makes more sense."

What he saw: the headline said "One session is waiting to be brought back" and the list below it held
seven rows - one he could act on and six greyed "ended without a handover". The date and time WERE on
the screen, second line, but the list drowned them. So the window contradicted itself.

## Finish before you stop

On this Director a session that has ended its turn is never woken (product issue 3186). Do EVERYTHING
below before your turn ends. Never `run_in_background`; keep any one command under nine minutes (split
a long test run by filter). Never run `rm` on a path built from a variable: a safety hook stops it and
asks the owner, which hangs you for good. Literal paths only. The account's weekly limit is nearly
spent; if your screen warns it is close, write where you are into
`docs/missions/smart-director-restart-2026-09-19/phase-5-status.md`, commit and push at once.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - "If you are a Developer", and laws 1, 4, 14, 17.
2. `docs/missions/smart-director-restart-2026-09-19/mission.md` - section 5.3 items 10 and 11, and
   rulings 10.2, 10.3, 10.5. It wins over this file.
3. `docs/missions/smart-director-restart-2026-09-19/ruling-way-up-presence-check.md` - the Delivery
   Lead's ruling that a record whose sessions ALL ended without a handover is still offered. It stands;
   do not undo it.
4. The code: `src/CcDirector.ControlApi/SmartRestart/DirectorWayUp.cs` and `WayUpWords.cs`,
   `src/CcDirector.Avalonia/SmartRestart/WayUpOfferWindow.axaml(.cs)`, `WayUpOfferViewModel.cs`,
   `WayUpRowViewModel.cs`, `WayUpStartUpAsk.cs`, `RestartHistoryWindow.*`. Read before you change.
5. `docs/VisualStyle.md`.

## Half one - the screen reads correctly

- **The main list shows only what can be brought back.** The sessions that ended without a handover
  come off it, behind ONE line that says how many there are and opens them when asked - for example
  "six sessions ended without a handover", with the rows and their "Reopen its saved conversation"
  buttons revealed on demand. Ruling 10.3 still holds: those sessions are still listed, still
  unticked, still reopenable. They are moved, not dropped.
- **The headline and the list agree.** After the change, a person reading the top line and counting the
  rows below gets the same number.
- **When the record holds NOTHING that can be brought back** - the case the ruling added - the window
  must still read sensibly rather than showing an empty main list under a headline about none. Decide
  what it says, and say why in your proof.
- **The date and time stay and read clearly.** They are already there ("Shut down on 20 September 2026
  at 12:36.") and he missed them; make them part of what the eye lands on.
- `WayUpWords.ReasonLabel` prints "No reason was given." when there is no reason. That is noise on a
  window he called confusing: say nothing when there is nothing to say.
- Follow `docs/VisualStyle.md`. Do not redesign the window beyond what is above.

## Half two - records stop piling up

Today a record is written and kept for ever, and bringing a session back does not mark it, so the same
record and the same dead sessions are offered again after the next restart. That is product issue
**#3230** and it is the other half of what confused him. Three parts:

1. **A seat is marked when it is dealt with** - brought back, or its conversation reopened - so it is
   never offered twice. This closes #3230, which today is guarded only by a set held in memory that a
   restart empties (`DirectorWayUp.Reopened`).
2. **A record stops being offered once every seat in it is dealt with.**
3. **A record stops being offered once it is older than seven days.** Seven is the Delivery Lead's
   number, not the owner's; if you find a reason it is wrong, say so in your proof rather than change
   it.

All three stay READABLE in Restart history. Nothing is deleted: he asked for them to stop appearing,
not to be destroyed, and no seat on this mission destroys anything.

**A warning you must design around.** Marking a seat needs a new mark the record can carry, and the
hosted Gateway REFUSES a mark it does not know - its workspace validation checks a closed list. So a
new mark works on the rig and on a local Gateway at once, and on the hosted one only after it is
deployed, which is the owner's separate decision. Build it so that an older Gateway that refuses the
mark FAILS LOUDLY AND SAFELY - the offer says what it could not record rather than quietly offering
the same session for ever - and write in your proof exactly what happens against a Gateway that does
not know the mark. Do not add a fallback that pretends it worked.

## The check

Run these yourself, before and after, and put the counts in your proof. Read the COUNT, never the
colour:

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Drain|FullyQualifiedName~Restart"
    dotnet test src/CcDirector.Avalonia.Tests --filter "FullyQualifiedName~SmartRestart"

The Delivery Lead's own runs on `origin/main`: 637 and 147, 0 failed. Tests are always written, and
prove each can fail. The window is proved with PICTURES from the headless tests, committed under
`docs/missions/smart-director-restart-2026-09-19/attachments/way-up/`: at least the offer with one
session to bring back and six that ended without one (the owner's own case), the collapsed line opened,
a record with nothing to bring back, a record already dealt with not being offered at all, and one
older than seven days not being offered. The owner reviews those pictures, so draw the states he will
ask about.

## What you owe

`proof-way-up-clean.md` in `docs/missions/smart-director-restart-2026-09-19/`, committed beside the
code: what you changed and why for each half, what the window now says in each state, the counts before
and after, what each new test proves in plain words, the revert proofs, what happens against a Gateway
that does not know the new mark, and what you could not reach. Then push, open the pull request against
`main` naming issues #3167 and #3230, and squash merge it with the branch deleted. Do not wait for the
hosted checks. If the merge is refused, write why into `phase-5-status.md`, push, and stop - force
nothing. Then `cc-devthrottle session report "<one paragraph>"`.

Never sign anything: no agent or vendor name, no "Co-authored-by", no "Generated with", in any commit,
pull request, comment or file. ASCII only. Never deploy. Never run the feature against a real Director.
Never restart, drain, stop or message a session that is not yours.
