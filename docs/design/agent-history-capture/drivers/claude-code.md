# Driver: Claude Code (history capture)

How cc-director captures the full conversation history of a Claude Code CLI session
and turns it into our canonical model. This is the first of a per-driver set; each
supported agent gets its own document in this folder.

---

## 1. Why Claude needs a transcript driver (not the terminal)

Claude Code runs as a **full-screen terminal application** (it switches the terminal
into the alternate screen buffer, `ESC[?1049h`). While that is active the terminal's
local scrollback is empty by design - the application owns the screen and repaints it.
So our usual trick of scraping the terminal byte stream does **not** work for Claude:
there is no linear scrollback to read, only a repainting grid.

The good news: Claude writes its **own** complete conversation log to disk, as JSON
lines (one JSON object per line):

```
~/.claude/projects/<encoded-repo-path>/<claude-session-id>.jsonl
```

That file is the authoritative source of truth - user prompts, assistant replies,
tool calls, and tool results, in order. Our job is therefore two separate problems:

1. **The pointer problem** - reliably know *which* `.jsonl` file is the live
   conversation right now (it changes on `/clear` and on auto-compaction).
2. **The reader problem** - read that file and normalize it into our canonical model.

These map to the two halves of the driver: the **hooks** (pointer) and the
**transcript reader** (content). They are deliberately decoupled.

---

## 2. The big picture

```
 +=====================================  CC DIRECTOR  =====================================+
 |                                                                                         |
 |  SessionManager.CreateSession(agent = ClaudeCode)                                       |
 |                                                                                         |
 |   1) ClaudeHookInstaller.EnsureInstalled()  - writes once, shared by all sessions:      |
 |        %LOCALAPPDATA%\cc-director\claude-hooks\report-session.ps1   (the hook script)   |
 |        %LOCALAPPDATA%\cc-director\claude-hooks\hooks-settings.json  (registers hooks)    |
 |                                                                                         |
 |   2) launch inside a ConPTY:                                                            |
 |        claude --session-id <uuid> --settings "<hooks-settings.json>"                    |
 |        env injected into the child:  CC_SESSION_ID,  CC_DIRECTOR_API                     |
 |                                          |                                              |
 +==========================================|==============================================+
                                            |  spawns
                                            v
                                     +--------------+
                                     |  claude.exe  |   full-screen TUI - terminal
                                     +--------------+   scrollback is empty / useless
                                       |          |
              writes its own           |          |   fires a hook on every SessionStart
              conversation log         |          |   (sources: startup, resume,
                                       v          v    clear, compact)
     ~/.claude/projects/<repo>/<id>.jsonl     powershell -File report-session.ps1
       user / assistant / tool_use /              |  reads STDIN json:
       tool_result  (one JSON per line)           |    { session_id, transcript_path, source }
              ^                                    |  reads ENV:  CC_DIRECTOR_API, CC_SESSION_ID
              |                                    v
              |  (3) read on demand,    POST {CC_DIRECTOR_API}/sessions/{CC_SESSION_ID}/claude-hook
              |  the file named by the          body: { claudeSessionId, transcriptPath, source }
              |  pointer                         |
              |                                  v
              |             +-----------------------------------------------------+
              |             |  Control API:  POST /sessions/{id}/claude-hook      |
              |             |    Session.UpdateClaudeSessionPointer(...)          |
              |             |      Session.ClaudeSessionId      <- current id     |
              |             |      Session.ClaudeTranscriptPath <- current file   |  <== THE POINTER
              |             +-----------------------------------------------------+
              |                                  |
              +----------------------------------+
              |   ClaudeTranscriptReader.Read( Session.ClaudeTranscriptPath )
              v
       ConversationHistory   (canonical, agent-agnostic)
         Messages[] = Role(User | Assistant) + Parts[ Text | Thinking | ToolUse | ToolResult ]
              |
              v
       consumers:   Wingman briefing,   session save,   ...
```

**Two paths, decoupled:**

- **Pointer path (the hooks)** - tiny, event-driven. Answers only "*which* file is the
  live conversation right now?". This is the part that survives `/clear` and compaction.
- **Content path (the reader)** - reads the file the pointer names and produces the
  canonical history. Answers "*what* was said?".

The key idea: **the hooks do not carry the conversation.** They carry only the
*pointer* to where the conversation lives. The conversation itself stays in Claude's
own `.jsonl`, which we read directly. That separation is what makes this robust - we
never try to stuff a whole transcript through a hook.

---

## 3. What a "hook" is, and the problem it solves

A Claude Code **hook** is a command Claude runs itself when a lifecycle event happens.
You register hooks in a settings file. We pass ours with `--settings`, which **merges**
with the user's own hooks rather than replacing them (Claude Code issue #11392), so the
user's hooks keep working.

The event we care about is **SessionStart**, which fires with a `source`:

| source    | when it fires                                  |
| --------- | ---------------------------------------------- |
| `startup` | a fresh session begins                         |
| `resume`  | a session is resumed (`--resume` / `/resume`)  |
| `clear`   | the user runs `/clear`                         |
| `compact` | the context auto-compacts (or `/compact`)      |

### The pointer problem (why we need this at all)

When cc-director launches a new Claude session it **preassigns** the id with
`--session-id <uuid>`, so it already knows the *first* id and file. The trouble is:

> On `/clear` and on auto-compaction, Claude starts a **new** session id and writes a
> **new** `.jsonl` file.

Without a signal, cc-director would keep reading the *old* (now frozen) file and miss
everything after the clear. The `SessionStart` hook fires at exactly those switch
moments and hands us the new id and path. That is the whole reason the hook exists.

### What the hook command receives and does

Claude runs the hook command and pipes it a JSON object on **stdin**:

```json
{
  "session_id": "bfaa74c9-3e87-4d16-8c23-3dbfc572e9c3",
  "transcript_path": "C:/Users/soren/.claude/projects/.../bfaa74c9-....jsonl",
  "hook_event_name": "SessionStart",
  "source": "clear",
  "cwd": "D:/ReposFred/devthrottle"
}
```

Our hook script (`report-session.ps1`) does only this:

1. read that JSON from stdin;
2. read `CC_DIRECTOR_API` and `CC_SESSION_ID` from the environment cc-director already
   injects into the session;
3. `POST` `{ claudeSessionId, transcriptPath, source }` to
   `{CC_DIRECTOR_API}/sessions/{CC_SESSION_ID}/claude-hook`;
4. swallow every error and exit 0 - a hook must never block or break the session.

The endpoint calls `Session.UpdateClaudeSessionPointer(...)`, which sets
`Session.ClaudeSessionId` and `Session.ClaudeTranscriptPath`. Those two fields **are**
the pointer.

---

## 4. Sequence: a session, then a /clear

```
  time   claude / user                              cc-director
  ----   --------------------------------------     -----------------------------------------
   t0    (session requested)                        preassign id A; launch claude
                                                       with --settings <hooks>; inject env
   t1    claude boots, writes <A>.jsonl
   t2    SessionStart(source=startup) fires  ---->  POST claude-hook { id=A, path=<A>.jsonl }
                                                       pointer := <A>.jsonl
   t3    turns happen; claude appends to <A>.jsonl
                                                     (on demand) read <A>.jsonl -> history
   t4    user runs /clear
   t5    claude starts NEW id B, writes <B>.jsonl
   t6    SessionStart(source=clear) fires    ---->  POST claude-hook { id=B, path=<B>.jsonl }
                                                       pointer := <B>.jsonl   (followed switch)
   t7    more turns; claude appends to <B>.jsonl
                                                     (on demand) read <B>.jsonl -> history
```

Without the t6 hook, cc-director would still be reading `<A>.jsonl` at t7 and would be
permanently stale after the clear. This was verified live: a real `/clear` moved the
pointer from `5af19963...` to `bfaa74c9...` (both the id and the transcript path).

---

## 5. The reader: from .jsonl to canonical history

`ClaudeTranscriptReader.Read(path)` opens the file (with `FileShare.ReadWrite`, because
Claude is appending to it live) and maps each line into the canonical model:

- transcript line `type=user`  -> a `ConversationMessage(User, ...)`
- transcript line `type=assistant` -> a `ConversationMessage(Assistant, ...)`
- each content item becomes a `ConversationPart`:
  - `text`        -> `Text`
  - `thinking`    -> `Thinking`
  - `tool_use`    -> `ToolUse`     (carries tool name + id + raw input JSON)
  - `tool_result` -> `ToolResult`  (carries the id of the call it answers)
- bookkeeping lines (mode, permission-mode, snapshots, titles) are skipped;
- subagent sidechains (`isSidechain: true`) are skipped (nested Task-tool threads);
- a truncated final line (Claude mid-write) is tolerated and skipped.

The output `ConversationHistory` is **agent-agnostic** - every other driver maps its
own store into the same shape, so consumers (Wingman, session save) never see anything
Claude-specific.

---

## 6. Capabilities and limitations of this driver

| Captured | Notes |
| --- | --- |
| User prompts | full text |
| Assistant replies | full text |
| Tool calls | tool name + the input JSON, paired by id to their results |
| Tool results | full result text, linked to the call by id |
| Timestamps | per message, when present |
| Across `/clear` and compaction | the pointer follows the new file |

| Limitation | Why |
| --- | --- |
| Thinking text is usually empty | Claude stores thinking blocks signature-only (no plain text) in the transcript, so there is nothing to surface; we skip empty thinking parts |
| Subagent sidechains skipped | they are nested Task-tool conversations, not the main thread; could be included later if wanted |
| Claude-specific | this driver only reads Claude's format; each other agent has its own driver |
| Turn-level freshness | today the pointer updates only at session boundaries (SessionStart). A future Stop hook would let the Director know a turn finished so it can re-read immediately, rather than re-reading on demand |

---

## 7. Where each piece lives (code map)

| Piece | Location |
| --- | --- |
| Writes the hook script + settings | `src/CcDirector.Core/Claude/ClaudeHookInstaller.cs` |
| Generated hook script | `%LOCALAPPDATA%\cc-director\claude-hooks\report-session.ps1` |
| Generated settings (passed via `--settings`) | `%LOCALAPPDATA%\cc-director\claude-hooks\hooks-settings.json` |
| Injects `--settings` at launch | `src/CcDirector.Core/Sessions/SessionManager.cs` (ClaudeCode branch) |
| Env injection (`CC_SESSION_ID`, `CC_DIRECTOR_API`) | `src/CcDirector.Core/Sessions/SessionManager.cs` |
| Hook receiver endpoint | `POST /sessions/{id}/claude-hook` in `src/CcDirector.ControlApi/ControlEndpoints.cs` |
| The pointer | `Session.ClaudeSessionId`, `Session.ClaudeTranscriptPath`, `Session.UpdateClaudeSessionPointer(...)` in `src/CcDirector.Core/Sessions/Session.cs` |
| Pointer exposed on the API | `SessionDto.ClaudeSessionId`, `SessionDto.ClaudeTranscriptPath` |
| Transcript reader | `src/CcDirector.Core/Claude/ClaudeTranscriptReader.cs` |
| Canonical model | `src/CcDirector.Core/History/ConversationHistory.cs` |
| Path resolution helper | `ClaudeSessionReader.GetJsonlPath(...)` |
