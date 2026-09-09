# Worker E - the Director window's close, re-pointed at the stop

You are a Worker on the "Stop a session" mission, issue #2633, Phase B. Your supervisor is the
Phase B Manager (session `578d8e29`). Report to it and to nobody else. **Do not narrate progress at
it** - a message interrupts the session that receives it. Finish, then say so once, in one line.

## Where you work

- Worktree `C:\ReposFred\devthrottle-stop-a-session`, branch `mission/stop-a-session`. **Work there
  and nowhere else.** Never in the shared checkout, never on `main`, and never `git checkout -b`.
- Commit as you go with clear messages. **Do not push and do not merge** - the Manager pushes, the
  Architect lands.
- **Nothing you write anywhere may name an assistant, a model or a vendor.** No `Co-authored-by`,
  no "Generated with", no robot emoji, in commits or code or comments.
- **Plain English, no abbreviations, anywhere.**
- **`CLAUDE.md` rule 0: never kill a running process to tidy up.** The owner runs several Directors
  on this machine. You are not launching or stopping any of them; you are writing code and running
  tests. If a build fails because a file is locked, say so and stop - do not reach for `taskkill`.

## Read these first, in full

1. `missions/stop-a-session.html` - the mission and its six rulings, and **Ruling 5 twice**: it names
   your exact change and states its cost out loud.
2. `missions/stop-a-session/handoff-phase-b.md` - the phase, including the part headed "ADDED AFTER
   PHASE A".
3. `CLAUDE.md` - rule 1 (responsive interface), rule 2 (log entry, exit and failure on every public
   method), rule 3 (no fallback programming), rule 4 (try/catch at entry points only), rule 7 (the
   client is dumb, the Gateway owns all ruling).
4. `docs/VisualStyle.md` - it governs the dialog you are about to draw.
5. `src/CcDirector.Gateway.Contracts/SessionStopDtos.cs` - the answer shape. Read the comments.

## THE RULE THAT GOVERNS EVERY LINE YOU WRITE

**No surface composes a sentence.** The Gateway folded the words. Your window renders `Headline`,
then each entry of `Details` in order, verbatim. You never write a sentence keyed off the verdict
word, and you never branch on the verdict to decide what it MEANS. There are four verdict words today
and a fifth could be added tomorrow in one place on the Gateway; your dialog must not care how many
there are.

## What is there today, and why it is wrong

`src/CcDirector.Avalonia/MainWindow.axaml.cs`:

- The context-menu entry `Close Session` (near line 2701) calls `CloseSessionAsync` (near line 2950).
- `CloseSessionAsync` removes the row from `_sessions`, then calls `_sessionManager.KillSessionAsync`
  and `_sessionManager.RemoveSession` directly, on this machine, in this process.

**That is a second implementation of the stop that records nothing.** No reason is asked for, none is
recorded, and the operator is told nothing about what happened. Ruling 5 says it is re-pointed at the
one route, and states the cost plainly: ending a session on your own machine now depends on the
Gateway. That dependency is deliberate. A local stop that quietly worked without the Gateway would be
a stop with no recorded reason, which is the one thing Ruling 4 makes mandatory.

**`CloseAllSessionsAsync` (near line 2469) is the Director shutting ITSELF down. Leave it exactly
alone.** It must not depend on the Gateway and this mission does not touch it. Do not "tidy" it, do
not share code with it, do not rename anything it uses.

## What you build

### 1. `StopSessionAsync` on the Director's Gateway client

`src/CcDirector.ControlApi/GatewayClient.cs` already holds the Director's authenticated client and
already talks to the Gateway constantly. **`RecordHoldAsync` (near line 649) is your model** - read
it first: it is the same shape, it fails loud with no fallback, and it explains itself in a comment.

Add a public method that posts `{ "reason": ... }` to `sessions/{sessionId}/stop` and returns the
parsed `SessionStopResponse` (the project already references `CcDirector.Gateway.Contracts`).

- **Fail loud, no fallback.** Throw when the Gateway is not configured, and throw when the call does
  not succeed - carrying the Gateway's own sentence where there is one, the way `RelayFailureAsync`
  already does for its neighbours. **Never fall back to a local kill.** Ruling 5 names that as the
  wrong fix in terms.
- Log entry, exit and failure in the house format: `FileLog.Write($"[GatewayClient] ...")`.
- Its comment says what it is, that this is the one stop every surface goes through, and why a local
  fallback would be wrong.

### 2. The window's close becomes a stop

- The menu entry becomes **`Stop Session`** with a tooltip that says what it does. Match the word the
  Cockpit and the phone use; another Worker is making those say `Stop` too.
- **It asks for the reason first**, in a dialog. The confirm control is disabled while the box is
  empty or only whitespace - the Gateway requires the reason, so the control must not offer a click
  that can only be refused. The dialog says, in plain words, that the reason is recorded and why.
- It calls the Gateway through the new method and then **shows the returned `Headline` and each
  `Details` line, verbatim, and stays up until the user dismisses it.** A control that accepts a
  click and says nothing is the exact defect this mission exists to remove.
- A failure shows the failure, in words, in the same dialog. The session is NOT removed from the rail
  when the stop failed - saying "it is gone" when it is not is the worse of the two mistakes.
- **The row is no longer pruned locally before the kill.** The Gateway sends the `kill` verb back
  down this Director's own tunnel, the session manager removes the session, and
  `OnExternalSessionRemoved` (near line 2097) already drops the rail row on the interface thread for
  exactly this case - a removal that came from outside `MainWindow`. Read it before you write
  anything; it also does the active-session teardown. Do not duplicate that teardown in a second
  place.
- **Responsive interface, `CLAUDE.md` rule 1.** The dialog appears at once; while the stop is in
  flight it says so; nothing blocks the interface thread; no synchronous input or output on it.
- Try/catch at the entry point only - the click handler and the dialog's lifecycle - never inside the
  helper or service methods you add.

## Testing

The local gate covers `Avalonia.Tests`, so run it, and know what it does not tell you:

```
.\scripts\test-local.ps1
```

Add tests for what is testable without a window: the Gateway client method (the route it calls, the
body it sends, the answer it parses, and that a failure throws rather than falling back), and any
pure helper you write. `src/CcDirector.Avalonia.Tests/` and the existing `GatewayClient` tests are
where to look for the pattern already in use.

**Say plainly what you could NOT test.** If the dialog itself cannot be driven headlessly in this
suite, write that down as a gap rather than dressing up a weaker test as coverage. A later seat
photographs the real window; your job is that there is something true to photograph.

**WATCH EVERY TEST FAIL ON PURPOSE BEFORE YOU BELIEVE IT.** Break the thing it claims to catch, run
it, see it go red, read what the red actually SAID, restore. Record each mutation and the exact red
message in `missions/stop-a-session/worker-e-notes.md` as a table.

## When you are finished

Write `missions/stop-a-session/worker-e-notes.md`: what you built, the mutation table, the test
numbers, anything you found that contradicts this brief or a ruling, and every gap you did NOT close
named as a gap. Then send the Manager (`578d8e29`) ONE line pointing at that file. Fleet messages are
cut off at the first line break, so the detail goes in the file, never in the message.
