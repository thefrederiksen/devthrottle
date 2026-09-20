# Mandate - Smart Director Restart, phase 3, Developer: the two MEASUREMENTS on the rig

You are a Developer on the Smart Director Restart mission, opened by the phase 3 Tech Lead
(session 38f41a97). You report to it and to nobody else. You have no transcript; this file is your
whole history.

Your worktree is `D:/ReposFred/devthrottle-smart-restart-p3-rig`, branch `smart-restart/p3-rig`,
cut from `origin/main` = `ab2770c4a`. Work only there. Never work in `D:/ReposFred/devthrottle`.

**You write NO product code.** Your whole output is measurements and two documents. If you find a
defect, write it down; do not fix it.

## TWO RULES THAT KEEP YOU ALIVE - read twice

1. **Nobody can wake you.** On this Director a session that has ended its turn is never woken again
   (product issue 3186). FINISH INSIDE ONE TURN and write your documents BEFORE your turn ends. If
   you stop half way to ask something, nothing will ever answer you. If you cannot finish, write how
   far you got into your proof file, commit, push, and then stop.
2. **Never run `rm` on a path built from a variable.** A safety hook stops any such command and asks
   the owner, which hangs your session for good. Literal paths only, always.

Never run anything in the background. Keep any one shell command under nine minutes - the rig build
is longer than that if you let it be, so read the note about that below.

## NEVER TOUCH A REAL DIRECTOR

Everything you do runs against the isolated rig (`scripts/restart-qa-rig.ps1`) or against an agent
binary on its own. You never start, stop, drain, interrupt, message or end any session that is not
one you created inside the rig, and you never point anything at the machine real Gateway
(port 7878) or at the installed Director. Read the script header before you run it - it explains what
it isolates and why.

## The two questions you answer

### QUESTION ONE - does reopening a saved conversation actually work on this build?

The mission says this is untested and that nothing may be built on it until it is measured (mission
document 10.3, and the "Not verified by the Architect" list). Another Developer is building the way
up engine beside you against what the CODE says; your job is to find out what the BUILD does.

What the code says, already read by the Tech Lead, so you do not have to find it:

- Claude Code: `src/CcDirector.Core/Drivers/ClaudeDriver.cs` `BuildLaunchSpec` appends
  `--resume <id>`;
- Pi: `src/CcDirector.Core/Agents/PiAgent.cs` `BuildLaunchSpec` appends `--session-id <id>` and its
  comment claims a second launch with the same id recalled the first conversation, verified against
  pi 0.80.10;
- Codex: `src/CcDirector.Core/Drivers/CodexDriver.cs` `BuildLaunchSpec` LOGS that it is ignoring the
  resume id. Codex is "noted only" in this mission; you confirm that it ignores it and stop there.

Answer the question in three layers, and in your document say exactly what each layer covers and,
just as plainly, WHAT IT DOES NOT COVER:

1. **The launch spec.** A test or a direct call showing the argument each agent really builds for a
   given conversation id. This proves the Director asks. It does NOT prove the agent obeys.
2. **The agent on its own.** Take a REAL saved conversation - one this session or a scratch session
   produced, in a scratch folder - end it, then start the agent binary yourself with the flag the
   driver would have passed, and see whether the conversation comes back. Claude Code first, then Pi.
   This is the layer that actually answers the question, and it needs no Director and no Gateway.
   Record the exact command and the exact evidence you saw that the history was there (not "it looked
   right" - quote something from the earlier conversation that the resumed one knew).
3. **Through the product, on the rig.** Stand the rig up and have its Director create a session with
   a resume id, through the product own path, and see the same thing. This is the layer that proves
   the whole chain. If you cannot reach it inside your turn, SAY SO - layer 2 answered the question
   and layer 3 is the confirmation. Do not fake it and do not skip straight to it.

For each agent the answer is one of: it works (with the evidence), it does not work (with what
happened instead), or it was not reached (with why). "If reopening does not work for an agent, that
agent is noted only too, and you say so; you do not build a second way round it."

### QUESTION TWO - how long does an interrupted session need to write a handover?

The mission gives sessions ten minutes and interrupts whatever is still mid-turn at two thirds of
that, because the Architect GUESSED that three minutes is enough to write a handover after an
interrupt (mission document 4.5). Nobody has measured it. If the guess is wrong the feature ends
sessions that were about to hand over.

On the rig, several runs - at least five, and say how many you actually got:

- a session genuinely mid-turn (give it real work that takes a while, not a sleep);
- interrupt it and send it the handover request the product really sends
  (`src/CcDirector.ControlApi/Drain/DrainMessages.cs` - use the product own words, not your own);
- measure from the moment the interrupt is sent to the moment a handover file exists on disk that
  the drain would ACCEPT. The drain ignores a document under 500 bytes
  (`DirectorDrain`), so the clock stops when the file is over that, not when it first appears.

Report the numbers: every run, the fastest, the slowest, and the spread. Then say plainly whether
three minutes looks safe, tight or wrong, and what you would move it to. **You do not change the
two-thirds point** - the Delivery Lead decides that from your numbers. Also record anything you
learned about how a session BEHAVES when interrupted mid-turn, which the mission lists as unverified
too.

Vary the agents if you can afford it, and if you cannot, measure Claude Code and say so.

## The rig, practically

    powershell -File scripts/restart-qa-rig.ps1 build
    powershell -File scripts/restart-qa-rig.ps1 up
    powershell -File scripts/restart-qa-rig.ps1 status
    powershell -File scripts/restart-qa-rig.ps1 down

`build` publishes a Gateway, a launcher and a Director, and by default it also builds the two web
shells from the tree with npm, which is minutes on its own. You do not need the web shells for either
question, so pass `-WebShells installed` and say in your document that you did and why. Keep each
command under nine minutes; if `build` will not fit, run its parts and say so.

**Take the rig down when you are finished.** Leave nothing of it running.

## What you owe

Two documents on your branch, plus a proof file:

- `docs/missions/smart-director-restart-2026-09-19/attachments/phase-3/reopen-test.md` - question one:
  every command you ran, verbatim, what you saw, and the verdict per agent with its layer.
- `docs/missions/smart-director-restart-2026-09-19/attachments/phase-3/handover-timing.md` - question
  two: the runs, the numbers, the conditions, and your recommendation.
- `docs/missions/smart-director-restart-2026-09-19/proof-phase-3-rig.md` - a short covering file: what
  you ran, what you reached, what you did NOT reach and why, and anything you found that somebody
  should know about. Name the pull request number.

Then commit (plain English, no abbreviations, **sign nothing** - no "Co-authored-by", no "Generated
with", no agent or vendor name; ASCII only, no Unicode, no emoji, no tick marks), push, and open a
pull request against `main` naming the mission issue #3167. Do not merge it yourself and do not wait
for the hosted checks. Then your turn may end.

Write down what you measured even if the answer is inconvenient. An honest "it does not work" is
worth far more here than a hopeful yes - the whole point of ordering this test before the building is
that the feature must not be built on a guess.
