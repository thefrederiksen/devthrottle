# Review: Mail Desk phase 7, the Gateway half (the channel fold's words and tones)

Reviewer seat, Mail Desk mission (issue #4213), phase 7. Reported to the phase 7 Tech Lead. 19 October 2026.

## Scope

**Read**

- The diff `origin/mail-desk-phase-7...origin/mail-desk-p7-fold`, commit `4389faaf9`. This worktree's HEAD was
  confirmed equal to `origin/mail-desk-p7-fold` after a fetch. The diff covers `MailDeskChannelFold.cs`,
  `MailDeskWords.cs`, `MailDeskEndpoints.cs`, the new `MailDeskChannelFoldWordsTests.cs` and
  `MailDeskEndpointTests.PageWords.cs`, and the one-word `partial` change to `MailDeskEndpointTests.cs`.
- `docs/qa/mail-desk/phase-7/CONTRACT.md` sections 1 and 2, `docs/qa/mail-desk/phase-1/CONTRACT.md` (the
  `machines` column and section 5), and mission sections 5.9 (backup machines) and 5.10.
- `TriggerRunOutcome` (`CcDirector.Gateway.Contracts/TriggerDtos.cs`), `MailDeskDecisionCounts`,
  `MailDeskChannelStore.Machines` and `WhyNotMachines`.
- `tools/cc-devthrottle/src/maildesk_ops.py` and the `maildesk` entries in `cli.py`. These show that `--json` prints
  the Gateway's answer as it arrives and that the plain output picks named fields. The command and flags in the
  empty sentence (`maildesk channel add <address> --provider outlook --machine <machine> --why`) match the command
  as it exists.
- Phase 3 (`origin/mail-desk-phase-3`, `87d723b74`): its changes to `MailDeskChannelFold.cs` and `MailDeskWords.cs`,
  and `MailDeskMemory.HandedTo` and `MailDeskService` in the trial merge (below).

**Ran**

- `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.MailDesk"`
  on this branch: **291 passed, 0 failed**.
- A trial merge of `origin/mail-desk-phase-3` and `origin/mail-desk-p7-fold` (`git merge-tree`). It merged with no
  conflict. The same filter on the merged tree in a scratch worktree: **532 passed, 0 failed**. The additions
  survive phase 3's rewrite of `signedIn`. `MailDeskSignedIn.Tone` reads the state phase 3 now decides from each
  mailbox's own checks, with no change needed.

**Could not reach**

- The Python tests for `cc-devthrottle maildesk`: pytest is not installed in this machine's Python. The commit does
  not touch the Python, and the command passes `--json` through as the Gateway sends it. The new fields are all
  additions, and no existing field's name or value changed. So by reading, the command's `--json` shape holds, but
  this is not proven by a run.
- The page half (client-core) is not part of this diff.

## Verdict

**Changes needed: one finding (F1) puts a sentence on the page that the system does not do. F2 is smaller.**

Everything else holds. Every field is additive and keeps the existing names and meanings. Every word and tone is
decided in the fold. A provider with no label, a mode that is not a mode, a sign-in state that is not a state, an
empty machine list and a decision missing from the counts all throw instead of showing something plausible. The
contract's test list is covered. The reworded decision labels ("Route to a seat", "Archive", "Keep for you") read
correctly beside a count in observe mode.

## Findings

### F1. `machinesLabel` promises a failover order that nothing follows

`MachinesLabelOf` turns the channel's machine list into "devlinux, then north-box if devlinux is not answering". The
fold claims this as the order in which the mailbox is read. No code reads the channel's list in order:

- On this branch, nothing reads a channel's `machines` to decide who reads the mailbox. The Mail Desk start's
  trigger runs on its own machine.
- After phase 3, the only reader is `MailDeskMemory.HandedTo`. It hands a channel to **any** machine that holds the
  Mail Desk start's claim **and** appears anywhere in the channel's list (`.Any(...)`). The order is never used.
  Which machine holds the claim, and which takes over when one goes quiet, is set by the **start's** machine list.
  Mission section 5.9 says: "A start may name several machines in order of preference ... the next machine on the
  list takes the claim". That list is set separately from the channel's list.

**Harm.** Suppose a channel lists `[devlinux, north-box]` and the start lists `north-box` first. The page says
devlinux reads the mailbox, with north-box only as a backup, but north-box reads it every time. Now suppose a channel
lists only `devlinux` and the start fails over to `north-box`. The page shows plain "devlinux", while during that
time the mailbox is not read at all, because `HandedTo` gives north-box nothing for it. Repository rule 7 exists so
the Gateway says what is **true**. This sentence explains how the system works, and the system does not work that
way. An owner who believes it will look for the problem on the wrong machine.

**Why it must change.** The contract (section 2) set this wording on the premise "the order is the order of
preference phase 1 stores". Phase 1 does store an order, but nothing uses it. That makes this a contract question for
the Tech Lead as well as a code change. One fix is to word only what is true of the channel's list, for example "Read
on devlinux or north-box, whichever holds the Mail Desk start". The other is to have the fold read the start's
machine order, which is what really decides it. The current tests
(`MachinesLabelOf_TwoMachines_NamesTheBackupAndWhenItReads` and the three-machine case) test the sentence, not the
behaviour it describes, so they would keep passing while the page says something false.

### F2. Any outcome other than `failed` is green, including "paused, so no mail was screened" and outcomes nobody has added yet

`LastCheckToneOf` is `utc is null ? neutral : outcome == failed ? bad : good`.
`LastCheckTone_AnyOtherOutcome_IsGood` locks in `"an-outcome-added-later"` as **good**.

**Harm.**

- After phase 3, a paused start reads "Checked ...: the start is paused, so no mail was screened" with a **good**
  tone. A skipped run reads "the previous screening is still running, so this check was not screened", also
  **good**. Green beside a sentence saying no mail was screened is a page contradicting itself, and the client may
  not override it.
- Phase 3 has just added a new outcome (`screened`). Outcomes get added. The next one is green automatically, even if
  it means something went wrong. Every other tone in this diff (`ModeToneOf`, `SignedInToneOf`) throws on a value it
  does not know, so this one is inconsistent. It is the fallback repository rule 3 forbids: an unknown value gets a
  plausible answer instead of a loud failure.

**Why it must change.** Name each `TriggerRunOutcome` in a switch and throw on anything else, the way `ModeToneOf`
does. Give `paused`, and the screening-still-running case of `skipped-running`, the tone `neutral`: nothing failed,
but nothing was screened either. The contract's rule "any other outcome is good" was written before phase 3 reworded
`paused`, so the Tech Lead should rule on the change. Replace the `"an-outcome-added-later"` test case with one
that asserts it throws.
