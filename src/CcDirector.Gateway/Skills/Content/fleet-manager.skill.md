# The Fleet Manager's commands

How the Fleet Manager does each thing its conduct asks for, with the command line that does it.
The conduct itself - the rules, the three kinds of news, the plain-language table - is the
`fleet-manager` workflow: `cc-devthrottle workflow instructions fleet-manager`. Read that first.
This skill is only the HOW.

Every command below is `cc-devthrottle`, already on your path, with your Gateway and your session
key already in your environment. Where a session is named, you can pass its id, an id prefix, its
number, or its exact name.

## What is built, and what is not yet

Say the gaps plainly; never act as if a missing piece exists.

| Piece | Today |
| --- | --- |
| The Wingman's reading of each stop | Built. On every session row, in `session list --json`. |
| The Wingman reading the sessions YOU own | Built, for the sessions you start directly, once the account marks you as its Fleet Manager: `cc-devthrottle fleet-manager show` prints the mark, `cc-devthrottle fleet-manager set [<session>]` sets it (no session given marks the session running the command), and `cc-devthrottle fleet-manager clear` removes it. Without the mark, a session you own carries no reading - treat that as "cannot tell". |
| Being told when a session you own stops | Being built: the Gateway will tell you at the end of each of their turns. Until it is, use the TEMPORARY path in "Checking your sessions" below: a report from each session at each handoff (finished, or blocked on a decision), and a check of your sessions at the start of every one of your turns. |
| Outcome records (Ready, Finding, Decision) that stay open until answered | Not built. Keep your open items in a file (below) and read it at every start. |
| One digest command for the start of a conversation | Not built. Use the routine below. |
| Handing an existing session over to you | Not built. Say so when they ask. |

## The start-of-conversation routine

```
cc-devthrottle session whoami
cc-devthrottle session workers
cc-devthrottle session list --json
cc-devthrottle mission list
```

- `session workers` is the sessions you own and which of them have their hand up.
- `session list --json` has one row per session. For each one you own (its `controllerSessionId`
  is your id), read `turnVerdict` - that is the Wingman's reading of its latest stop - and
  `verdictLabel`, `activityState`, `uncommittedCount`, `missionName`.
- Your open items and their standing preferences live in ONE file, kept in your own working folder:
  `fleet-manager-record.md`, with three headings - **Open**, **Preferences**, **Answered** (newest
  first, trimmed to the last few days). Read it first; write to it the moment anything opens,
  closes, or is decided. It is the only thing that survives your own restart until the Gateway
  keeps these records.

## Reading the Wingman's reading

In each row's `turnVerdict`:

| Field | Meaning |
| --- | --- |
| `verdict` | One of `needed-you`, `finished`, `continues-alone`, `stuck-recoverable`, `stuck-needs-person`, `cannot-tell`. |
| `finishedKind` | On `finished` only: `done` (the work is complete) or `report` (it is only telling). |
| `evidence` | The session's own decisive words, copied exactly. Pass it on unchanged. |
| `label`, `summary` | The short label and summary the owner sees. |
| `options`, `agentRecommends`, `answerVia`, `menu` | The answers on offer, the session's own pick, and whether they are typed words (`reply`) or menu keys (`keys`). |
| `risk` | `none`, `irreversible`, `standing-grant` or `spends-money`. Anything other than `none` is theirs to answer. |
| `spoken` | The version for reading aloud. |
| `failed`, `failureReason` | The Wingman could not read this stop. Treat it as "cannot tell". |
| `judgedAtUtc` | When it read. A reading older than the session's last activity is about an earlier stop. |

## Starting a session you own

Give the session its WHOLE task at the start, so it never has to be told anything more. Write the
instructions to a file first - **The owner's intent** (their words, unchanged) and **Build notes**
(yours: the repository, the files that matter, what done means, how to prove it, and where to write
its report file if the work is a report).

**Temporary, until the Gateway delivers end-of-turn events to you:** end the Build notes with this
line, word for word, and ask for nothing else:

> Run `cc-devthrottle session report "<one or two sentences>"` at each handoff, and only then:
> when your task is finished, and each time you are blocked on a decision you cannot make. If you
> were blocked, got your answer and then finish, report again when you finish. Never run it for
> progress.

Once end-of-turn events are live, this line leaves the instructions: the events and the Wingman's
reading replace it.

```
cc-devthrottle session spawn <repository path> --controlled-by self --name "<what it does>" --prompt "Read your instructions at <file> and do them."
cc-devthrottle session spawn <repository path> --controlled-by self --name "<what it does>" --machine <computer> --prompt "..."
```

- `--controlled-by self` makes the session yours: it is quiet for the owner, and its stops come to you.
  Never start their work as `--standalone`; that hands it back to them.
- `--machine` starts it on another computer; if no Director runs there, one is started. A computer
  that is off fails loudly - tell them, never retry somewhere else.
- `--agent` picks the tool (the default is Claude Code). Use the one they asked for, or the default.
- Name it for the work, in plain words. Never a bare repository name.

## Opening a Mission for big work

```
cc-devthrottle mission create "<Mission name> - <why, in a few words>"
cc-devthrottle session spawn <repository path> --controlled-by self --mission <mission id> --role Architect --name "<Mission name> - Architect" --prompt "Read your brief at <file>. Conduct: cc-devthrottle workflow instructions mission."
```

What the brief must hold is set by the mission conduct (`cc-devthrottle workflow instructions
mission`), not here; put the owner's words in it unchanged. Tell them in one line that you opened it. The Architect then runs the Mission; you watch the Architect,
not its sessions.

## Messages are rare

Every word sent into a session interrupts it. The owner has ruled that this must be rare.

- The whole task goes in the spawn prompt. Nothing routine is sent afterwards.
- You never ask a session what it did. You read it (below).
- Once the Gateway delivers end-of-turn events to you, no session reports to you: those events and
  the Wingman's reading tell you what happened. **Until then (temporary)**, the report line in
  "Starting a session you own" is the only report you ask for - at each handoff (finished, or
  blocked on a decision), never for progress.
- You never use `message send` or `message ask` for routine coordination, and you never send to
  `all`.
- You send words into a session only in two cases: it is idle and waiting for exactly that input
  (the Wingman read `needed-you`), or the owner asked for their words to be passed on.

## Checking your sessions

**Temporary, until the Gateway delivers end-of-turn events to you.** Once those are live, you wait
for them instead, and this section goes.

Today, at the start of EVERY one of your own turns - whatever woke you - run:

```
cc-devthrottle session workers
cc-devthrottle session list --json
```

- `session workers` lists the sessions you own and their state.
- For each one that has stopped (idle, waiting, or gone) and that you have not yet dealt with, read
  the Wingman's reading of that stop first: its `turnVerdict` in `session list --json` (see
  "Reading the Wingman's reading"). Then act on it as the conduct says. This catches a stop whose
  report never arrived.
- Open its screen (`session buffer`) only when the Wingman cannot tell, there is no reading for
  that stop, or the session is stuck and needs a person.
- Between your turns, do not poll and do not ask.

## Answering a session

```
cc-devthrottle session prompt <session> "<their words, exactly>"
```

- `session prompt` types exactly the text into the session, as if the owner typed it. Use it only in
  the two cases above: the owner's answer, or the Wingman's option when `answerVia` is `reply`.
- When `answerVia` is `keys` the session is showing a menu. There is no command for raw keys yet:
  for a numbered menu, `session prompt` the option's number, then `session buffer` to confirm the
  menu moved. If it did not, bring it to the owner - never guess at keys.

## Reading a session yourself

This is how you learn what a session did - never by asking it.

```
cc-devthrottle session workers
cc-devthrottle session buffer <session>
git -C <its copy of the repository> log --oneline <its base>..HEAD
```

- `session workers` and `session list --json` say what state each one is in.
- Its commits, its pull request and its report file say what it produced.
- Read its screen (`session buffer`) only when the Wingman cannot tell, there is no reading for that
  stop, or the session is stuck and needs a person.

## Snooze, close, stop

```
cc-devthrottle session hold <session> --minutes <n>
cc-devthrottle session hold <session> --release
cc-devthrottle session done <session> --reason "<why>"
cc-devthrottle session stop <session> --reason "<why>"
```

- `session hold` is the snooze (the command still carries the older name).
- `session done` is the polite close: the session finishes its turn first. Check `uncommittedCount`
  and that its work has landed BEFORE you close it. A session with unlanded work is never closed.
- `session stop` ends a session now. Use it only when the owner has asked for that session to be
  stopped, and confirmed.

## Merging

Merge only with their word, or where they have allowed that repository to merge on green, and never
when the checks are red. Merge the way that repository's own rules say to (for most, a squash merge
of the pull request, then delete the branch). Confirm to them in one line with the link.

## Computers, Directors and schedules

```
cc-devthrottle machine list
cc-devthrottle director list
cc-devthrottle schedule list
```
