# A restart index and a workspace are the same object

Issue 2722, phase 3 of the restart epic (issue 2719).

## The insight

A **workspace** is a named set of **seats**. A seat is a repository plus the agent, model, role, mission,
controller and opening prompt that make one session.

There are two ways to get one:

- **authored** by hand - "my morning fleet";
- **captured** from a running Director, which is exactly what a drain produces.

And two ways to use one:

- started **cold**, which is the regular set-of-sessions case and falls out for free;
- started with each seat **seeded by its own handover**, which is the restart case.

Those are the same object. The restart index written by hand during the first real drain, on 2026-09-06,
already was a workspace with extra fields on it; a workspace is a restart index with the drain's judgments
left blank. So one thing is stored, not two.

## Where it lives, and why

**On the Gateway.** The one moment a workspace is worth having is the moment the machine it describes has
been restarted out from under its fleet - so it has to be readable when that machine is down, and editable
from another computer.

That is the whole reason this **replaces** the Director-local workspace files rather than sitting beside
them. Those lived under each Director's own configuration directory: readable only while that machine was
up, editable only at that keyboard.

## What happened to the old feature

- `CcDirector.Core/Sessions/WorkspaceDefinition.cs` and `WorkspaceStore.cs` are **deleted**, with their
  tests.
- The desktop's **Save Workspace** and **Load Workspace** still exist, in the same place on the File menu.
  They now read and write the Gateway through the Director's own connection. With no Gateway configured
  they say so - "workspaces are stored on the Gateway, and this Director is not connected to one" - rather
  than showing an empty list, which would read as the upgrade having deleted the user's saved work.
- Nothing on disk is lost. The first time either dialog is opened, `LegacyWorkspaceImport` pushes every
  `*.workspace.json` this machine already had up to the Gateway and renames each file aside to
  `.imported`. A file whose id is already on the Gateway leaves the Gateway's copy alone - that is the one
  people have been editing since - and a file that cannot be parsed keeps its own name so the bytes stay
  findable.
- `CcStorage.Workspaces()` survives for exactly that one reader. Delete it when no machine can still be
  carrying a legacy file.

## The schema

Taken from the hand-written `index.json`, not designed fresh. Four things it has to keep, and why:

| Field | Why it cannot be dropped |
|---|---|
| `drainState`, all five values | `covered` - the seat reported UP and its senior's document accounts for it - is a real state, not a gap. No file is ever invented for such a seat. The other four are `drained`, `blocked`, `unreachable`, `declined`. |
| per-seat `restore` decision, `why`, `command` | The decision is made once, by whoever read the handover. A seat marked for restore with no command is a document that looks complete and cannot be acted on. |
| `restoreAfterRestart` | The short ordered list a stranger acts on. On 2026-09-06 a session with no transcript and no knowledge of the drain restored the whole fleet from that one array. Everything else in the document explains; this one instructs. |
| `restoredSessionId`, `restoredSeedFile`, `restartPerformed` | Written back AFTER the restart. An index that stops at the drain cannot say later whether the restart worked. |

The document is stored as JSON in one column, with a few head columns projected from it for the list. That
is deliberate: a workspace is a RECORD, its shape grew three whole blocks during the first real run - the
launcher update, how the restart was actually asked for, what came back - and it will grow again. Columns
would make each of those a migration.

## What happened to the Director, and what happened to the seats

These are TWO independent facts and the record keeps them apart. The hand-written index had one coarse
word for both - `outcome`, with four values - and two of them welded the pair into a single token:
`restored` meant *the Director was restarted AND the seats came back*. So the first combination nobody
happened to weld had no word at all, and that combination is not exotic: a drain that BLOCKS has already
closed, leaf-first, every seat that handed over cleanly, so the expected result of never forcing is a
half-gone fleet with no restart whose seats must then be brought back WITHOUT one.

Three fixes were tried, and the first two each looked finished:

1. **A fifth value.** It hid the same defect one case further out - the next case (restarted, restore only
   half complete) needs a sixth.
2. **Deriving the word from the pair.** That stopped it CONTRADICTING them but still welded them: a
   refused restart and a session that would not stop both derive `blocked`, which are the two most
   confusable results a run can have. The fix for that was a warning telling every future reader never to
   read the field alone, and a rule that must be obeyed forever by people who were not in the conversation
   is the weakest kind of fix there is.
3. **Deleting it**, which is what shipped.

So the record carries:

| Field | Values |
|---|---|
| `directorOutcome` | `not-restarted`, `restarted`, `restart-refused` |
| `seatOutcome` | `restoredCount`, `notRestoredCount`, `notRestoredWhy`, and a `scope` DERIVED from the two counts: `nothing-to-restore`, `none`, `some`, `all` |

`scope` has four values and not three for the same reason the pair exists at all: "nothing was owed" and
"everything owed is missing" are a success and a total failure, and one word for both is the
absence-shaped hazard in miniature - an empty restore list reading as success is how a run that restored
nothing certifies itself. `notRestoredWhy` is REQUIRED whenever `notRestoredCount` is above zero, because
a missing seat with no reason beside it is indistinguishable from one nobody noticed.

A document written elsewhere that still carries `outcome` keeps it verbatim in the unknown-field bag: not
modelled, not obeyed, and not lost.

## The surface

```
GET    /gateway/workspaces        every workspace, newest first (summaries)
GET    /gateway/workspaces/{id}   the whole document
POST   /gateway/workspaces        CAPTURE: fold a Director's live sessions into a new one
PUT    /gateway/workspaces/{id}   store one (authored, or a captured one with judgments written on)
DELETE /gateway/workspaces/{id}   remove one
```

Capture is a verb rather than something the caller assembles, because the facts on a seat - the agent, the
resolved role, the controller, the model, the transcript path - are ones the Gateway holds firsthand, and
this document is read after those sessions are gone, when nobody can check. It **creates and never
replaces**: overwriting would destroy a drain somebody is halfway through, silently.

The capture makes **no judgments**. Drain state, handover path, restore decision and the restore list are
all left at their honest empty values, so a document nobody has judged yet cannot be mistaken for one that
has been.

A session key may call all five. A session drives a drain, so a guard that let an agent read workspaces but
never write one would leave the record to a human at the keyboard of the machine being restarted - the one
person the exercise exists to spare. Restarting the Director stays refused; that is the admission surface.

## The proof, and what it does not cover

`tools/harnesses/workspace-capture-diff` runs the real fold over a fleet roster and diffs it against the
hand-written index. It is a harness rather than a test because both inputs are private to the machine.

```
dotnet run --project tools/harnesses/workspace-capture-diff -- \
    --roster <cc-devthrottle session list --json output> \
    --index  <the hand-written index.json> \
    [--director <id>] [--director-name <name>] [--director-version <v>] [--out <report>]
```

It reports two separate facts per field, and the distinction matters: whether the schema **has a home** for
it (a "no" is a fact that would be LOST), and whether the capture **fills** it (a "no" is usually correct -
a judgment nobody has made yet - but is always named).

Run on 2026-09-06 against this machine's fleet and the hand-written index: **zero fields with no home**,
**zero replay differences over all eighteen hand-written seats**.

Three things the report deliberately says out loud:

- **The value comparison proves nothing.** Not one session in the live roster is in the hand-written index,
  because the restart it records destroyed every one of them. A section that can only ever report "no
  overlap" is not a check.
- **The replay cannot be failed by mutating the index.** Both sides are built from the same hand-written
  seat, so a changed value moves both. It fails when the harness and the fold disagree about WHERE a fact
  comes from - watched, by moving the fold to read the wrong properties and seeing thirty-six differences
  printed across the eighteen seats.
- **`openingPrompt` is never captured.** A live session does not carry the prompt it was started with, so a
  captured seat has none. That is a real gap in what a captured workspace can be started COLD from; the
  restart case does not need it, because each seat is seeded from its own handover instead.
