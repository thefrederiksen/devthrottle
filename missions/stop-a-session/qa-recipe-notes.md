# The local stack recipe, repeated independently - what a second pair of hands found

**Who wrote this and why.** The QA seat was told to start from
`missions/stop-a-session/local-stack-recipe.md` and repeat it independently, and that if the recipe
did not work as written, saying so was itself a finding. It was then told to stop before capturing
any frames, because the inspection landed with defects that reach the frames. **So this file is only
about the recipe.** It is not the report, it does not accept the feature, and nothing here should be
read as a pass.

**Run on 9 September 2026, on machine SORENLAPTOP, against commit `91be52a6` of
`mission/stop-a-session`** - the branch tip at the time. Everything below was observed at that
commit; the accepted inspection findings will change some of the answers, and the ones most likely to
change are named at the end.

**Paths used, so the reader can substitute their own.** The recipe's two paths were
`C:\ReposFred\devthrottle-stack` and its `_stack`; this run used
`C:\ReposFred\devthrottle-qa-stack` and `C:\ReposFred\devthrottle-qa-stack\_stack`, a separate
worktree and a separate scratch area, and a **different named instance slug** -
`stop-a-session-report` rather than `stop-a-session-qa`. Why that mattered is finding 5.

---

## The short answer: the recipe works, and it is worth trusting

Every numbered step ran, in order, first time, with no step needing to be worked around. Nothing in
it was wrong. The things below are additions - traps it does not warn about and one step that cannot
work as written - not corrections to what it claims.

| Step | What happened here |
|---|---|
| 1. Cut an isolated worktree | Worked. |
| 2. Publish the Gateway in Release | Worked, about five minutes. **Both files the recipe tells you to check were there** - `wwwroot\c\index.html` and `wwwroot\mobile\index.html`. The Debug trap it warns about was not hit, because it warns about it. |
| 3. Run it on port 7997 with its own storage root | Worked. `GET /healthz` answered `2.0.7+91be52a642e65aaf7eeeb954f5361ea8483b5f70`, which is the branch tip, so the commit check the recipe insists on does what it says. An unauthenticated `GET /directors` answered **401**, so the authentication gate really is on. |
| 4. Register a named instance | Worked. See finding 5 and finding 6. |
| 5. Reserve a Director slot | Worked - the arbitration script handed out **slot 6** and registered `cc-director6-launch`. |
| 6. Build the Director into that slot | Worked, about two and a half minutes. |
| 7. Launch it through Task Scheduler with `--instance` | Worked. The instance registration appeared with the running process's own identifier, and the verification the recipe insists on passed: `GET /directors` listed exactly my Director and `healthz` said `"directors":1`. |
| 8. Stop a real session | Worked. Detail below. |
| 9. Take it back down | Worked, except step 9.5. See finding 8. |

### What step 8 actually produced at this commit

A throwaway session was opened in a throwaway repository, on my Director, on my Gateway. Its agent
process was a real `claude.exe`, process **1116**, found the way the recipe says to find it - as a
child of the Director process, because the session row carries no process identifier of its own.

    stopped 8ca4cb5a - process 1116 ended, row removed
    the worktree C:\ReposFred\devthrottle-qa-stack\_stack\scratch-repo was left untouched -
      it had no uncommitted changes
    reason: recipe check for the Stop a session QA seat

Process 1116 did not exist afterwards. Running the same stop a second time answered

    not on this fleet - nothing in this account carries the id 8ca4cb5a, so no machine was asked
    and no machine's processes were searched

and exited **zero**. A stop with no reason was refused, said a reason was what was missing, named the
flag that supplies it, and exited **non-zero**. That is the recipe's own account of itself,
reproduced by somebody who did not write it.

---

## Eight things the recipe does not say, that the next seat needs

### 1. THE DANGEROUS ONE: `cc-devthrottle` on the path answers from the OWNER'S REAL FLEET

This is the finding worth the whole file, and it nearly published client names.

The recipe's step 8b is safe as written, because it runs the branch's command line by absolute
interpreter path. The danger appears the moment you want a *screenshot*: a frame that reads
`python.exe -c "from cc_devthrottle.cli import app; app()" session stop ...` is not a picture of the
product, so the natural move is to stage a small `cc-devthrottle` shim on the path and photograph
that instead.

**If that path edit does not take, the same command name still works - and it answers from the hosted
Gateway.** The installed command line lives at
`%LOCALAPPDATA%\cc-director\instances\default\bin\cc-devthrottle.cmd`, it inherits
`CC_GATEWAY_URL=https://gateway.devthrottle.com` from any agent session's environment, and
`session list` is an old verb that it has. So it prints the owner's entire live fleet - every session
name, every repository name, across every machine - and it looks completely normal doing it.

That is exactly what happened here. A terminal was opened with
`cmd /c start "<title>" powershell -NoProfile -NoExit -Command "<a string that set the path>"`; the
quoted string was re-parsed by `cmd`, the path edit was silently dropped, and the first frame
captured the owner's real roster with client repository names in it. **The image was read before it
was committed, and it was deleted.** That is the only reason this is a note and not an incident.

For the next seat, three things follow:

- **Prove which command line is answering, in that same window, before every capture.**
  `(Get-Command cc-devthrottle).Source` and `$env:CC_GATEWAY_URL` are two lines and they settle it.
  A path edit you did not verify in the window you are photographing is not a path edit.
- **Consider giving the staged shim a name of its own** so the two can never be confused, and accept
  that the frames then show that name with a sentence explaining it. A frame that is honestly labelled
  beats a frame that is ambiguous and right by luck.
- **Read every image before committing it.** The Architect's ruling already says this. It is not
  belt-and-braces; it is the control that actually caught this.

### 2. The installed command line really does predate the mission

`cc-devthrottle session stop --help` on this machine answers `No such command 'stop'`. The recipe's
table is accurate at this tip. Do not install over it.

### 3. `python -c` makes the command's own help print the wrong name

Run the way step 8b describes, the command line believes it is called `-c`, so its usage line reads
`Usage: -c session stop [OPTIONS] {target}`. Harmless when you are only reading the answer; wrong in
a photograph. A two-line runner file fixes it:

    from cc_devthrottle.cli import app
    app(prog_name="cc-devthrottle")

### 4. The session row carries no process identifier, so a frame needs the operating system beside it

Confirmed rather than assumed: `session list --json` has no process field at all. The agent process
is a child of the Director process, so this is what pairs them up in one frame:

    Get-CimInstance Win32_Process -Filter "Name='claude.exe' AND ParentProcessId=<the Director's pid>"

### 5. Do not reuse Phase B's instance slug - its data home is still on this machine

`%LOCALAPPDATA%\cc-director\instances\stop-a-session-qa` **still existed** when this run started.
Step 9 of the recipe says that directory "can be deleted too", and it was not. Reusing the slug would
have started a supposedly independent run on top of somebody else's leftover data. Pick your own slug.
This run used `stop-a-session-report` and deleted its data home at the end.

### 6. Writing the named-instance registry from Windows PowerShell adds a byte-order mark

`ConvertTo-Json | Out-File -Encoding utf8` in Windows PowerShell 5.1 writes a byte-order mark at the
front of the file. This run rewrote the file without one rather than find out whether the Director
tolerates it. **So this is a caution, not a fact: it is not established that a byte-order mark breaks
anything.** It is established that the recipe's most dangerous step fails silently when it goes wrong
- an unknown slug falls back to the owner's instance - which is reason enough not to hand it a file
shape nobody has tested.

### 7. A terminal you can photograph has to be started a particular way

Relevant to any seat that needs a picture of a command running, on this machine, from inside a coding
agent session:

- `Start-Process powershell` produces a process with **no window at all** (`MainWindowHandle` is 0).
  There is nothing to photograph.
- `cmd /c start "<title>" powershell ...` produces a real Windows Terminal window that can be
  foregrounded, resized and captured.
- And per finding 1, whatever you pass through `cmd`'s `start` may not arrive intact. Set up the
  window by typing into it, not by passing it a startup string.

### 8. Step 9.5 cannot work as written, because step 3 put the scratch area inside the worktree

`git worktree remove --force` refuses with `Directory not empty`, because the recipe's own step 3
puts `_stack` - the published Gateway, the staged command line, the scratch repositories - inside the
build worktree, and none of it is tracked. Remove the directory first, then prune:

    Remove-Item <the worktree> -Recurse -Force
    git worktree prune

### The rest of the teardown is sound, and it was verified rather than assumed

The Director was **signalled, not killed** -
`Local\cc-director-shutdown-977a2ca3-7576-4bb6-ab8d-27a3d6d12bd6` - and it exited on its own. The
scheduled task was unregistered, so slot 6 is free again. The Gateway on port 7997 stopped and the
port no longer answers. The named-instance registry was restored from the backup step 4 takes, and
the restored file is byte-for-byte the two-instance-free original. **The owner's Director is process
1328 and his installed Gateway is process 2692, and those are the same two process identifiers at
the end of this run as at the start of it.** No `taskkill`, no `Stop-Process -Name cc-director*`, and
`scripts\redeploy-gateway.ps1` was never run. The only process force-stopped was the Gateway this run
published, whose image path was confirmed to be under `C:\ReposFred\devthrottle-qa-stack\`.

---

## One thing found by reading, not by running: how a browser gets in

The report needs a Cockpit frame, and the recipe correctly says it has not driven a browser. Reading
the code rather than guessing: the local Gateway keeps a break-glass wall at `GET /login`
(`src/CcDirector.Gateway/Api/GatewayLoginEndpoint.cs`) which takes the shared machine token, returns
it as the `cc-gateway-token` cookie, and honours a `?next=` path. That is the self-hosted owner's own
way in, so `http://127.0.0.1:7997/login?next=/c` is the door. It is bind-broken on a hosted Gateway
and reachable only on a self-hosted one, which is what this stack is.

**This was not driven and no browser was opened.** It is a pointer for the next seat, not a result.
The machine has a Director-owned browser profile called `agent-browser` with no account attached,
which is the right one to use: it carries none of the owner's tabs or sign-ins.

---

## What was NOT done, said plainly

- **No frames were captured and no report was written.** The seat was stopped after the inspection
  landed. The one frame that was taken contained the owner's live roster and was deleted unread into
  the repository.
- **No interface control was touched.** The Cockpit, the phone and the Director window were never
  opened. Everything above is the command line and the operating system.
- **This is not acceptance of the feature.** The answers quoted here are a record of what commit
  `91be52a6` said, taken before the inspection's findings were fixed. At least two of them sit
  directly on top of accepted findings - the verdict a stop gives when a process cannot be read, and
  what the Cockpit does with the answer afterwards - so **do not carry the wording above into the
  report.** Re-run it against the fixed tip and quote what that says.
