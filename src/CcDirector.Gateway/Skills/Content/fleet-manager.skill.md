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
| The Wingman reading the sessions YOU own | Being built. Until it is, a session you own may carry no reading - treat that as "cannot tell" and read the session yourself. |
| Being told when a session you own stops | Not built. Today a session you start tells you with `session report` when its turn ends, so put that in every set of instructions. Between reports, read `session workers`. |
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

Write the instructions to a file first - **The owner's intent** (their words, unchanged) and **Build
notes** (yours) - and end the build notes with: "When your turn ends, run
`cc-devthrottle session report \"<one or two sentences>\"`."

```
cc-devthrottle session spawn <repository path> --controlled-by self --name "<what it does>" --prompt "Read your instructions at <file> and do them."
cc-devthrottle session spawn <repository path> --controlled-by self --name "<what it does>" --machine <computer> --prompt "..."
```

- `--controlled-by self` makes the session yours: it is quiet for the owner and reports to you.
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

The brief says the WHY first, in the owner's words, then the work, then what is out of scope. Tell
them in one line that you opened it. The Architect then runs the Mission; you watch the Architect,
not its sessions.

## Answering a session

```
cc-devthrottle session prompt <session> "<their words, exactly>"
cc-devthrottle message send <session> "<a note from you>"
```

- `session prompt` types exactly the text into the session, as if the owner typed it. Use it for
  their answers and for the Wingman's options when `answerVia` is `reply`.
- When `answerVia` is `keys` the session is showing a menu. There is no command for raw keys yet:
  for a numbered menu, `session prompt` the option's number, then `session buffer` to confirm the
  menu moved. If it did not, bring it to the owner - never guess at keys.
- `message send` is framed as coming from you. Use it for your own instructions to a session.

## Reading a session yourself

Only when the Wingman cannot tell, or a session is stuck and needs a person:

```
cc-devthrottle session buffer <session>
```

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
