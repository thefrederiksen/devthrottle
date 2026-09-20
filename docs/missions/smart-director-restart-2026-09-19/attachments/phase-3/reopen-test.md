# Question one: does reopening a saved conversation actually work on this build?

Measured on 2026-09-20 on SOREN_NORTH, against the tree at `origin/main` = `ab2770c4a`, in the
worktree `D:/ReposFred/devthrottle-smart-restart-p3-rig` (branch `smart-restart/p3-rig`).

Versions under test: Claude Code 2.1.278, pi 0.85.1, codex-cli 0.155.1. The rig was built from this
tree: Gateway, launcher and Director all `2.8.1+ab2770c4af29740d13a5483a032f1e94b873dbfa`.

## The verdict, per agent

| Agent | Verdict | Deepest layer reached |
|---|---|---|
| Claude Code | It works | Layer 3 - through the product, on the rig |
| Pi | It works | Layer 3 - through the product, on the rig |
| Codex | It ignores the reopen id, as the code says it does | Layer 1 only, which is all this mission asked for |

Read the three layers below before acting on that table. Each layer says plainly what it covers and
what it does NOT cover, because the layers are not interchangeable: layer 1 proves the Director
asks, layer 2 proves the agent obeys, and only layer 3 proves the whole chain.

## Layer 1 - the launch spec

**What this layer covers:** the argument the Director really builds for a given conversation id.
**What it does NOT cover:** whether the agent binary does anything with that argument. Nothing in
this layer touches an agent, a Director or a Gateway.

Eight tests already in the tree assert exactly this. All eight pass on this commit.

```
dotnet test src/CcDirector.HostedAgent.Tests/CcDirector.HostedAgent.Tests.csproj \
  --filter "FullyQualifiedName~ClaudeDriverTests.BuildLaunchSpec"
Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 289 ms

dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj \
  --filter "FullyQualifiedName~PiAgentTests.BuildLaunchSpec|FullyQualifiedName~CodexDriverTests.BuildLaunchSpec"
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 279 ms
```

What they hold:

- Claude Code (`ClaudeDriverTests.BuildLaunchSpec_Resume_UsesResumeFlagWithoutPreassignedSession`):
  the arguments contain `--resume abc-123` and `PreassignedSessionId` is null.
- Pi (`PiAgentTests.BuildLaunchSpec_Resume_PassesTheSameIdAsSessionId`): the arguments are
  `--thinking high --session-id 8be79bf8-7db0-46c2-b19e-73857c9a7159` and `PreassignedSessionId` is
  that same id.
- Codex (`CodexDriverTests.BuildLaunchSpec_UsesBaseArgsWithoutPreassignedSession`): given the reopen
  id `old-session`, the arguments come back as `--sandbox danger-full-access` with no trace of it,
  and `PreassignedSessionId` is null. The id is dropped, and `CodexDriver` writes a log line saying
  so.

The same construction was seen for real in the rig Director's own log, which is the launch spec as
the product actually built it rather than as a test built it:

```
[ClaudeAgent] BuildLaunchSpec: userArgs=--dangerously-skip-permissions --model sonnet, resume=(null), studio=False
[CcDirector] Launching ClaudeCode: exe=C:\Users\soren\.local\bin\claude.EXE,
  args=--dangerously-skip-permissions --model sonnet --session-id fcd96039-cb7d-4dbd-b941-d146791f0b7c --settings "..."
```

## Layer 2 - the agent on its own

**What this layer covers:** whether the agent binary, started by hand with the flag the driver would
have passed, brings the earlier conversation back. No Director, no Gateway, no product code.
**What it does NOT cover:** the product's launch path - the working directory it chooses, the
`--settings` file it adds, the session preamble its SessionStart hook injects, and the fact that the
product runs the agent in an interactive terminal while this layer used the agent's own
non-interactive print mode. Layer 3 covers those.

The marker is the string `CRIMSON-OTTER-4417` and the name `Delphine`. Neither appears in the second
command, so a second process that says them can only have got them from the first conversation.

### Claude Code 2.1.278 - it works

Working directory: a scratch folder, `...\scratchpad\reopen-claude`.

```
claude --print --model haiku --session-id 11111111-2222-3333-4444-555555555502 --permission-mode auto
  "Remember this exactly: the rig passphrase is CRIMSON-OTTER-4417 and the rig owner is Delphine.
   Reply with only the word ACKNOWLEDGED."
ACKNOWLEDGED
```

The process ended. Then, with the flag `ClaudeDriver.BuildLaunchSpec` builds:

```
claude --print --model haiku --permission-mode auto --resume 11111111-2222-3333-4444-555555555502
  "Without using any tool, tell me the rig passphrase and the rig owner name I gave you earlier.
   If you do not know them, reply exactly NO HISTORY."
Rig passphrase: CRIMSON-OTTER-4417
Rig owner: Delphine
```

On disk, the reopened conversation APPENDED to the file it already had rather than forking a new
one - 37 lines, and exactly one session id throughout:

```
grep -o '"sessionId":"[^"]*"' 11111111-2222-3333-4444-555555555502.jsonl | sort -u
"sessionId":"11111111-2222-3333-4444-555555555502"
```

That matters beyond this test. `BuildLaunchSpec` returns `PreassignedSessionId: null` for a reopen,
so on the face of it the Director would not know the reopened conversation's transcript path. It
does not need to - the id does not change.

### Pi 0.85.1 - it works

`PiAgent`'s comment claims this was verified against pi 0.80.10. The pi on this machine is 0.85.1,
so it was measured again rather than taken on trust. Working directory `...\scratchpad\reopen-pi`.

```
pi --print --provider openai --no-tools --session-id 11111111-2222-3333-4444-555555555601
  "Remember this exactly: ... CRIMSON-OTTER-4417 ... Delphine. Reply with only the word ACKNOWLEDGED."
Warning: No project session found with id '11111111-2222-3333-4444-555555555601'; creating a new session with that id.
ACKNOWLEDGED
```

Second launch, same id, which is the whole of what `PiAgent.BuildLaunchSpec` does for a reopen:

```
pi --print --provider openai --no-tools --session-id 11111111-2222-3333-4444-555555555601
  "Without using any tool, tell me the rig passphrase and the rig owner name I gave you earlier.
   If you do not know them, reply exactly NO HISTORY."
The rig passphrase is CRIMSON-OTTER-4417 and the rig owner is Delphine.
```

Two pieces of evidence, not one. The answer quotes the marker; and the "No project session found"
warning that pi printed on the first launch did NOT print on the second, which is pi saying it found
the session. One file on disk, 7 lines, two occurrences of the marker:

```
C:\Users\soren\.pi\agent\sessions\--C--...-scratchpad-reopen-pi--\2026-09-20T11-01-42-973Z_11111111-2222-3333-4444-555555555601.jsonl
```

### Codex 0.155.1 - noted only, and it does ignore the id

Not launched. The mission asks only that the drop be confirmed, and layer 1 confirms it at the point
where the id would have been used. For the record, codex does have a reopen surface, but it is a
SUBCOMMAND and not a flag - `codex resume` (a picker by default, `--last` to continue the last one) -
so the Director could not reach it by appending an argument to the launch line the way it does for
the other two. Nothing was built on this.

## Layer 3 - through the product, on the rig

**What this layer covers:** the whole chain - a session created through the Gateway's own spawn door
with `resumeSessionId` set, handed to the rig Director, launched by the Director into a real
interactive terminal with the product's own arguments, settings file and injected preamble.
**What it does NOT cover:** the desktop's Resume Session tab, which is the human's way into this
door; these creates came in over `POST /directors/{id}/sessions`. It also does not cover a reopen
after a real Director restart - the conversations here ended because their agent process ended, not
because a Director was restarted.

The rig was `scripts/restart-qa-rig.ps1` with `-WebShells installed`, because neither question
touches the Cockpit or the mobile app and building the two shells from the tree costs minutes of
npm. The rig Gateway ran on loopback 7911 under its own root
`%LOCALAPPDATA%\cc-director-restart-qa-rig`; the machine's real Gateway on 7878 and the installed
Director were never contacted.

### Claude Code - it works, and it nearly reported the opposite

The conversation was seeded by hand, in the folder the rig sessions use:

```
cd ...\scratchpad\rigrepo
claude --print --model sonnet --dangerously-skip-permissions --session-id 22222222-3333-4444-5555-666666666610
  "Remember this exactly and use no tool: the rig passphrase is CRIMSON-OTTER-4417 and the rig owner
   is Delphine. Reply with only the word ACKNOWLEDGED."
ACKNOWLEDGED
```

A print-mode reopen of that transcript recalled it correctly, which makes it a controlled starting
point rather than an assumption:

```
claude --print --model sonnet --dangerously-skip-permissions --resume 22222222-3333-4444-5555-666666666610
  "Use no tool. Tell me the rig passphrase and the rig owner name from earlier in this conversation.
   If you do not know them, reply exactly NO HISTORY."
ACKNOWLEDGED - wait, you asked me to state them: the rig passphrase is CRIMSON-OTTER-4417 and the rig owner is Delphine.
```

Then through the product:

```
POST http://127.0.0.1:7911/directors/9f46482f-8eb6-4d2f-b2ae-f7b54def0b39/sessions
{ "repoPath": "...\\scratchpad\\rigrepo", "agent": "ClaudeCode", "name": "rig reopen claude 610",
  "args": "--dangerously-skip-permissions --model sonnet",
  "resumeSessionId": "22222222-3333-4444-5555-666666666610" }
-> sessionId 312ac29e-4186-495c-a55d-665e63bfcd5a, claudeSessionId 22222222-3333-4444-5555-666666666610
```

Two things were true immediately. The session's `claudeSessionId` came back as the reopen id, so the
product tracks the reopened conversation by the id it had. And the terminal showed the earlier
exchange drawn on screen - the model banner read `Sonnet 5`, and the buffer contained
`CRIMSON-OTTER-4417`, `Delphine` and `ACKNOWLEDGED`.

**And then the first question put to it answered NO HISTORY.** So did a second. On a transcript that
print mode had just recalled correctly. Taken at face value that is a verdict of "reopening does not
work through the product", and it would have been wrong.

Two pieces of evidence say the conversation WAS in the model's context.

First, the token counts recorded in the transcript itself. Each turn's cached input grows, which it
cannot do if the earlier turns are not being sent:

```
assistant in=2 cache_read=63101 cache_new=0     | ACKNOWLEDGED                  (the seed)
assistant in=2 cache_read=63114 cache_new=56    | ACKNOWLEDGED - wait, ...      (print reopen, recalled)
assistant in=2 cache_read=63170 cache_new=4046  | NO HISTORY                    (the product reopen)
```

The product's turn read MORE cached context than the turn that recalled correctly, plus 4046 fresh
tokens, which is the Director's injected session preamble.

Second, the same session was asked again, without the words "earlier in this conversation" that let
a model treat the question as being about a session it had just been told was new:

```
"Use no tool. Scan everything in your context window. Does the exact string CRIMSON-OTTER-4417
 appear anywhere in it? Answer YES followed by the sentence it appears in, or answer NO."

YES - it appears in my own earlier reply: "ACKNOWLEDGED - wait, you asked me to state them:
the rig passphrase is CRIMSON-OTTER-4417 and the rig owner is Delphine."
```

**Verdict: reopening works for Claude Code through the product.** The reopened session has the
conversation, in the terminal and in the model's context.

The trap is worth carrying forward, because the way up engine will be judged the same way. A
reopened session is handed a Director preamble telling it what it now is, and a question phrased as
"do you remember" can be answered against that preamble instead of against the transcript. Ask a
reopened session for a STRING, not for a memory.

### Pi - it works

Seeded in the same folder:

```
pi --print --provider openai --no-tools --session-id 33333333-4444-5555-6666-777777777701
  "Remember this exactly: ... CRIMSON-OTTER-4417 ... Delphine. Reply with only the word ACKNOWLEDGED."
```

Then through the product, `agent: "Pi"`, `args: "--provider openai"`, `resumeSessionId` set to that
id. Session `0cf5116f-8818-467c-8bb2-77d69e2864dd` came up with the earlier exchange drawn on
screen, and answered:

```
YES - it appears in your first message: "Remember this exactly: the rig passphrase is
CRIMSON-OTTER-4417 and the rig owner is Delphine."
```

**Verdict: reopening works for Pi through the product.**

## Things found on the way that somebody should know

1. **`NewSessionRequest.ResumeSessionId`'s own comment is wrong about Pi.** It reads "Ignored by
   agents that don't support resume (e.g. Pi)". Pi is the agent in this mission for which reopening
   is most straightforward - one flag, verified at all three layers. Somebody planning the way up
   engine off that comment would exclude the wrong agent. Not fixed here; this mandate writes
   defects down rather than fixing them.

2. **Claude Code's folder-trust dialog eats a new session's first prompt, and can end the session.**
   The first rig session was created in a folder Claude Code had never been trusted in. It came up
   on the "Quick safety check - Is this a project you created or one you trust" modal. The Director
   typed its prompt into that modal, the echo check failed twice, and the process exited 0 part way
   through:

   ```
   [ClaudeCode] EchoVerifiedSubmit: composer echo not seen on attempt 1 ... screenTail="...
     QuicksafetycheckIsthisaprojectyoucreatedoroneyoutrust ... NoexitYesItrustthisfolder..."
   [Session] ProcessExited: session=72a0f0a2-..., exitCode=0, pid=29252, uptime=30.4s
   [CcDirector] Session 72a0f0a2-... exited cleanly; reaping in 3000ms.
   ```

   `--dangerously-skip-permissions` was on the command line and did NOT suppress the dialog. This
   bears on the restore path directly: a Director restoring seats into a folder nobody has opened
   Claude Code in before will lose them exactly this way, and its log will say "exited cleanly". The
   measurements here were only possible after the scratch folder was marked trusted by hand in
   `~/.claude.json`; that entry was removed afterwards.

3. **Claude Code's interactive path ignores the `--model` argument the Director passes.** Two rig
   sessions launched with `--model sonnet` and with `--model claude-sonnet-5` both ran the model
   named in the user's `settings.json`, and both answered with that model's spend-limit banner. The
   identical argument line in print mode selected the requested model correctly:

   ```
   claude --print --dangerously-skip-permissions --model sonnet --settings "<the Director's hooks file>"
     "reply with only the exact model id you are running"
   claude-sonnet-5
   ```

   A reopened session is the exception, and it is the reason this phase could measure anything at
   all: a session created with `resumeSessionId` runs on the model recorded in the transcript it
   reopened. That is how every timing run in the companion document got off the spend-limited
   default.

## What none of this covers

- The desktop's Resume Session tab was not exercised; every create came in over the Gateway's
  `POST /directors/{id}/sessions` door.
- No conversation was reopened after an actual Director restart.
- Codex was confirmed only at layer 1.
- Claude Code layer 2 used print mode; the product uses an interactive terminal. Layer 3 closes that
  gap for the product's own path but not for a hand-run interactive terminal.
- Nothing here says a reopened session is USEFUL - only that it has its conversation. Whether a
  reopened seat picks its work back up is the way up engine's question, not this one.
