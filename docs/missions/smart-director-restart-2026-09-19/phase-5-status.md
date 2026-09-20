# Phase 5 status - prove the behaviour without touching a screen

Written by the Tech Lead seat for phase 5 (session d6c0d235), branch `smart-restart/p5-headless`,
worktree `D:/ReposFred/devthrottle-smart-restart-p5b`, cut from `origin/main` at `2d4d90a38`.

The Delivery Lead polls this file. It is appended to as the phase runs; the newest entry is last.

---

## 20 September 2026 - what the Tech Lead found before opening anybody

### 1. The rig on disk is built from the WRONG COMMIT and must be rebuilt

`C:\Users\soren\AppData\Local\Temp\restart-qa-rig-builds\director\cc-director.exe` was published from
the stopped attempt's worktree, whose base is `d8fdaafed`. That is BEFORE all four of the merges this
phase exists to re-measure (3241, 3245, 3248 and the dev reports one). Every case run against that
binary would prove yesterday's behaviour. The rig is rebuilt from this tree before anything is run,
and the report says which commit the binary came from.

### 2. The command line door reaches LESS than the mandate assumes

The mandate says two cases need a real door pressed - the File menu and the window close - and
"everything else goes through the command line". That is not what the product has. There are exactly
three host verbs (`src/CcDirector.Gateway.Contracts/SmartRestartCommandDtos.cs`):
`smart-restart/start`, `smart-restart/progress`, `smart-restart/history`. There is no verb, and no
command, for any of these:

| Behaviour | Where it lives | Reachable from the command line |
|---|---|---|
| Smart shutdown and restart (the File menu's own action) | `ISmartShutdown.Start` | YES - `director smart-restart` |
| How it is going, and how it ended | `ISmartShutdownRun.Current` | YES - `director smart-restart-status` |
| Every record, newest first | `IDirectorWayUp.ReadHistoryAsync` | YES - `director restart-history` |
| Shut down now | `ISmartShutdownRun.ShutDownNow()` | NO |
| Cancel and keep working | `ISmartShutdownRun.CancelAndKeepWorking()` | NO |
| Shut down and ignore all sessions | `ISmartShutdown.ShutDownIgnoringAllAsync` | NO |
| What the dialog says before anything is asked (the session count, the question boxes, a refusal) | `ISmartShutdown.CheckAsync` | NO |
| Bring back / Not now / Don't ask again on the way up | `IDirectorWayUp.BringBackAsync`, `ClearFromStartUpOfferAsync` | NO |
| Reopen a saved conversation | `IDirectorWayUp.ReopenAsync` | NO |

So the honest split for this phase is wider than "the File menu and the X", and the report will say
so case by case rather than quietly leaving cases out. Where a behaviour is decided by the Director
against the record - the start-up presence check above all - it IS provable with no screen, because
`DirectorWayUp.FindOfferAsync` writes its own decision into the Director log
(`offering <id>, owed=N, rows=N` or `nothing waiting (N record(s) read)`). That log line, taken from
a real Director start against the rig Gateway, is the artifact for every offer-once case.

**Recommendation to the Delivery Lead, for the owner:** the argument that put the command line there
in the first place - "one broken window must never again leave a Director impossible to empty" -
applies just as hard to `ShutDownIgnoringAllAsync`. A Director whose window is broken and whose
sessions are all wedged cannot be emptied at all today. That is a follow-up issue, not phase 5 work.

### 3. "A session whose driver declares neither stop verb" cannot be produced by any shipping agent

The mandate asks to show it. Every driver in `src/CcDirector.Core/Drivers/` declares at least one of
Interrupt or Cancel: Claude Code, Codex and the generic driver declare both, Cursor and Copilot
declare Interrupt, Pi declares Cancel. `DrainStopVerb.None` is a defensive branch no real agent
reaches, so it is provable only by its unit tests, not on the rig. Named here so nobody later reads
its absence from the evidence as an omission.

### 4. The planted-password case FAILED on the first attempt and needs a different seat

`case-13-record.txt` from the stopped run: the Pi seat that was told to carry the credential into its
handover declined to, so the sweep swept a clean document and found nothing. Ten patterns were proved
and zero findings - which proves the sweep can fire, and proves nothing about it catching a real
planted credential. The second run puts the credential into a handover document by a route that
cannot decline.

