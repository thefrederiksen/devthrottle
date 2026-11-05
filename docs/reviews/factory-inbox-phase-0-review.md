# Factory Inbox phase 0 review - the words, and the Boss only delegates

Reviewed 4 November 2026 by a separate review session. Nothing in either worktree was edited; the
scratch work below ran outside both repositories.

## What was reviewed

- thefrederiksen/devthrottle pull request 4573, branch `factory-inbox/p0-words-and-rules`, one
  commit `8c4fa3aaf` on top of origin/main `31db15d92`. Six files, 308 added, 18 removed.
- thefrederiksen/devthrottle_internal pull request 2677, branch `factory-inbox/p0-boss-only-delegates`,
  one file: `.claude/skills/factory-interviewer/SKILL.md`, one new bullet under "Rules that do not bend".
- The mission document `D:\ReposFred\_briefs\factory-inbox-2026-11-04\MISSION.md`, sections 4, 5 and the
  phase 0 row of section 6.

## Verdict

Both pull requests do what the phase 0 row asks, and every test I ran is green. One finding stands, a
false notice from the new definition check on a live Boss brief, which the author should decide on
before merging; it does not refuse any factory, because the check is a notice and not an error.

## The checks the brief asked for

**The Boss-only-delegates rule is in both openings.** The rule lives once, in the constant
`FactorySeatOpening.BossNeverDoesTheWork`. The owner's talk reaches it through `FactoryTalkSeed.Compose`
(line 185 of `FactoryTalkSeed.cs`), and the night start through `ScheduledRunOpening.PromptFor`
(`Running/ScheduledRunOpening.cs`, lines 53 and 63: both the seat-with-a-brief path and the Boss with no
published brief). The same constant also reaches Handle the issue, the Boss start door
(`Api/SpawnFactory.cs` line 395), a seat start (`FactorySeatStart.cs` line 92) and the kit's brief
(`FactoryKitComposer.cs` line 168), so all six surfaces changed together. The new wording says the two ways
on (a to-do by `cc-devthrottle factory memory set todo-<short name> "<the whole job>"`, or a session), that
the Boss never does it itself, that it judges good and bad, and that it pushes back on the owner and does
the thing once the owner has heard it and still says so. The new test
`TheTalkAndTheNightStart_BothTellTheBossItOnlyDelegates_AndPushesBackOnTheOwner` composes both openings
and asserts the seven sentences in each. The memory command's shape matches the command line
(`factory memory set <name> <text>`, `tools/cc-devthrottle/src/cli.py` line 1320).

**The definition check flags a Boss brief that tells the Boss to do the work itself.** It is a notice on
the brief's key line, for the seat `factory.yaml` names as the boss and for any seat whose role is Boss
(the same two `FactorySeatOpening.IsABoss` covers). `HasErrors` stays false, so a save or publish is not
refused. Revert proof, out of tree: I added a detached scratch worktree of the pull request's commit in
the session scratchpad, deleted the five lines that call the check (the comment and the `if ... Notice`),
built, and ran `FactoryDefinitionCheckBossDelegatesTests`. Three tests failed and twenty passed:

    Check_ABossBriefThatTellsTheBossToDoTheWorkItself_GetsTheNoticeOnTheBriefLine_AndNoError  FAIL
    Check_ASeatWithTheRoleBoss_ThatFactoryYamlDoesNotName_IsCheckedToo                       FAIL
    Check_APhraseWrappedAcrossLines_IsStillFound                                              FAIL

So the tests do watch the check and not only the phrase function. The scratch worktree was removed
afterwards; `git worktree list` no longer shows it.

**Terminology entries.** The shipped `terminology.skill.md` gains "The inboxes": Factory inbox (with the
Sent, Received, With the Boss trail, and the names it must not be called), Your inbox (Unread, Read,
Archived; "email me" and "tell me" mean Your inbox), Secretary (a seat, not a role; never judges, never
decides, never starts work sessions; closes itself when the inbox is empty), and the six decision words
with which three also write to Your inbox. All match sections 4 and 5 of the mission word for word where
the mission is specific. The Boss entry gains the delegator and judging sentences.

**Built-in skills edited in one place.** Only `src/CcDirector.Gateway/Skills/Content/terminology.skill.md`
carries content; `.claude/skills/terminology/SKILL.md` is the same body under the frontmatter. I compared
the two bodies with `diff --strip-trailing-cr` (identical; both files are CRLF on disk) and ran the
guards: `BuiltInSkillsHaveOneSourceTests`, `ShippedSkillsTeachOwnershipTests` and the terminology tests in
`CcDirector.Core.UnitTests`, 6 passed, 0 failed. The frontmatter description was extended to name the new
words, which is the Claude Code part of the copy and not the body.

**The factory-interviewer skill (internal pull request 2677).** The new bullet quotes the owner's words
from section 4, says what the Boss hands on and what its own work is, applies it to the Marketing Factory
seat the skill runs as, and tells a brief author to say what gets done and let the Boss hand it on. It
reads the same as the Gateway opening and the terminology entry. Two notes, neither a defect: the bullet
says the definition check flags such a brief, which is true only once pull request 4573 is merged, so
4573 should land first or at the same time; and the factory-interviewer skill is a repository skill, not
one of the fleet skills (`cc-devthrottle skill list` does not list it), so this change reaches people
through the internal repository checkout and nothing needs deploying for it.

## What I ran

- `dotnet test CcDirector.Gateway.UnitTests` filtered to `FactoryDefinitionCheck`, `BossNeverDoesTheWork`,
  `FactorySeatOpening`, `ScheduledRunOpening`, `FactoryKit`, `MailDesk`: 916 passed, 0 failed, 1 minute 25.
- `dotnet test CcDirector.Core.UnitTests` filtered to the skill guards and terminology: 6 passed, 0 failed.
- The revert proof above, in a scratch worktree outside this one.
- A scratch console program in the session scratchpad, referencing the Gateway project, that calls
  `FactoryDefinitionCheck.BossDoesTheWorkPhrase` on every Boss seat brief I could find and on probe
  sentences. The briefs: the three in this repository (the Mail Desk template, the two QA definitions) and
  six from devthrottle_internal origin/main (`handover-files/seats/boss.yaml`, `erik/definition-v14`,
  `website-business/definition`, `website-business/definition-new`, `factory/marketing/seats/marketing-lead.yaml`,
  and the unmerged `factory/devthrottle-course/seats/boss.yaml` from the working tree).

## Finding 1 - the check flags a sentence that FORBIDS the Boss from doing something

Severity: should be decided before merge. Not a refusal; a wrong notice on a correct brief.

The phrase list matches "<verb> ... yourself" and "do ... it yourself" without reading what comes before
it, so a sentence that forbids an action reads to the check as an order to do the work. Proof on a live
brief: the DevThrottle Marketing factory's Boss seat, `factory/marketing/seats/marketing-lead.yaml` on
devthrottle_internal origin/main (role Boss, last changed 28 October 2026, commit `e933f4e0`), says at
line 49 and again at line 181:

    cc-secrets. Never send or request it yourself.

That line tells the Boss NOT to send a secret. With this pull request the check answers:

    NOTICE: "send or request it yourself"

and the notice sentence shown to the person reads "This brief tells the Boss to do the work itself
("send or request it yourself"). A Boss only delegates ...". That is false on this brief, and it will be
printed on every check and save of the one published Boss brief that is long enough to carry a limit like
that. The other eight briefs get no notice.

The same gap shows on a brief that restates the rule itself. From the probe:

    NOTICE "do it yourself"        <- Never do it yourself; delegate.
    NOTICE "do the work yourself"  <- You do not do the work yourself - you start a session.
    NOTICE "do the work yourself"  <- You never do the work yourself.

The Mail Desk template escapes only because its wording is "do the seats' work yourself", and the word
"seats'" is not in the list. The terminology entry and the factory-interviewer skill both teach authors the
sentence "never does the work itself", so a brief that copies it in the second person is the next one to be
flagged.

The design comment in `FactoryDefinitionCheck.cs` says the words are matched, not understood, and that is
why it is a notice. I am not asking for understanding. The harm is that a prohibition is reported as its
opposite; a negation guard on the match (do not, don't, never, must not, without ... within a few words
before the phrase) would silence these three and the live brief, and the ten positive theory cases would
still match. That is the author's call; the mission asked for a flag, not for a flag that is never wrong.

**Author's answer: ACCEPTED, fixed in this pull request.** A prohibition reported as its opposite is
worse than no notice, and the live Marketing Boss brief would have carried it on every save. The three
order phrases (doing the work yourself, a work verb with "yourself", "you do the work") now stand down when
a negation (not, don't, never, no, without, mustn't, cannot, can't, nor) is among the last four words before
them in the same clause - a clause ends at a full stop, semicolon, colon, exclamation or question mark. The
two phrases that are already prohibitions of delegating ("never delegate", "do not start sessions") are not
guarded. New tests in `FactoryDefinitionCheckBossDelegatesTests` use the review's exact sentences: "cc-secrets.
Never send or request it yourself.", "Never do it yourself; delegate.", "You do not do the work yourself - you
start a session." and "You never do the work yourself." each give no phrase; the Marketing limit inside a Boss
brief gives no notice on a whole check; and an order in the next clause after a prohibition ("... Then send
the report yourself.", "Ask nobody; do it yourself.") is still flagged. The ten positive cases still match.
The misses listed under Observations are left as they are: the check is a notice, the opening binds the
Boss whatever the brief says, and widening the list widens the false notices this finding is about.

## Observations, not findings

- The phrase list misses common ways of telling the Boss to do work: from the probe, "Make the videos
  yourself", "Run the campaign yourself", "Do everything yourself", "Answer every email personally",
  "Reply to each prospect yourself" and "Handle every message in this session" all return no notice. The
  Boss opening says it holds whatever the brief says, so a missed brief does not make a Boss do the work;
  it only goes unflagged. Worth knowing when someone reports "the check did not catch my brief".
- The terminology says the Secretary "notes Good or Bad for the Boss" and also "never judges". Section 5
  of the mission says the same two things in the same words, so the entry is faithful; the tension is the
  mission's, and the phase 1 and 2 seats will have to say what a Good or Bad note is if not a judgement.
- The opening paragraph is now about 1,900 characters and is given on six surfaces. No defect; it is the
  design.

## What I could not reach

- I did not run the whole gate (`scripts\test-local.ps1`); the brief asked for a review, and the changed
  files are covered by the filtered runs above plus the parked-suite selection the gate makes on its own.
- I did not start a Gateway or a Boss session to read the opening on a live screen; the composed strings
  were checked by the tests, not by a running seat.
- The `factory/devthrottle-course` Boss brief is not on origin/main of devthrottle_internal, so I read it
  from the working tree and mark it as such.
