# Phase 1, the Windows proof, the Linux rig and the merge up to main - answers to the review

Written 2026-10-19 by "Session Rules - Developer - phase 1 rig and record fixes", on
`mission/session-rules-hooks-p1-fixes`, answering `reviews/phase-1-rest-review.md` and the Tech Lead's
calls at its end. The original Developers are gone; this is the fresh Developer the calls name. Every
finding and note: accepted or declined, with the reason and the proof.

## F1 - the copied sign-in's protection was a convention, and `down` could overstate it. ACCEPTED, fixed.

All four parts of call 1 are in `scripts/session-rules-rig.sh`, and the header's sign-in paragraph and
`live-rig.md` section 2 item 4 now say exactly what the script does:

- **`down` says the sign-in is gone only once nothing holds it.** It deletes the copy's file, then lists
  every process whose HOME is the rig's (read from `/proc/<pid>/environ`, the user's own processes only).
  This is wider than "the Director exited": the Director's Claude Code sessions and their children
  inherit the rig's HOME, so an orphaned session is caught too. If any remain, `down` names each one and
  says the file is deleted but those processes may hold the sign-in in memory and refresh it, and the
  sign-in is NOT gone until they exit. Only when none remain does it say the copy is deleted and nothing
  holds it.
- **`spawn-session` records the expiry** of the sign-in the rig will use, in `<rig>/state/signin-expires-at`.
- **`status` prints the minutes left** on the rig's copy, called the run's deadline, and says the copy
  has REFRESHED, and the owner's own sign-in may have been rotated out, when the copy's expiry no longer
  matches what `spawn-session` recorded.
- **A spawn under the margin is refused**, measured on the sign-in the rig would actually use (the
  owner's file, or the rig's own copy when that is kept). The margin cannot be lowered:
  `RIG_MIN_SIGNIN_MINUTES` under 120 is refused when the script starts.
- **A second `spawn-session` never overwrites a newer copy.** When the rig's copy expires later than the
  owner's file, it has refreshed; the script keeps it, says why, and says to end the run.

Proof, on devlinux, 2026-10-19 (the owner's sign-in had 441 minutes left, so nothing was overridden):

- Live: `build` from this worktree, `up`, `spawn-session` (Claude Code, repo A: "copied ... 438 minutes
  left"), `status` ("438 minutes left - the run's deadline"), a shell session, a second Claude Code
  `spawn-session` (repo B: owner file and copy equal, so copied again, 437 minutes), `status`, the rig
  tool listing all three sessions ready, then `down`: "Director process 73877 exited on its signal",
  "Gateway process 73113 exited", "deleted the rig's copy of the Claude Code sign-in; no process runs
  under the rig's HOME, so nothing holds it", "rig is down". The owner's `~/.local/bin` listing was
  identical before and after, and the owner's `~/.claude/.credentials.json` hash was unchanged.
- The paths a live run must not provoke, run against a scratch HOME holding fake sign-in files (never the
  owner's): `RIG_MIN_SIGNIN_MINUTES=30` refused at start; a rig copy expiring later than the owner's was
  kept with the warning and not overwritten; `status` said REFRESHED after the copy changed; an owner
  sign-in with 60 minutes left was refused and nothing was copied; `down` with a process still under the
  rig's HOME named it and said the sign-in is NOT gone, and kept the recorded expiry.
- `bash -n` on the script after every change.

What this does not do: it cannot stop a session that is already running from refreshing. The run is
bounded by what `status` shows and by the operator acting on it; nothing ends a run for them.

## F2 - two switches turned nothing off. ACCEPTED, fixed.

Deleted `CC_AUTOUPDATE=0` from the environment allow-list and the top-level `"autoUpdate"` from the
Director's `config.json`. In their place, a comment above the allow-list says why the rig needs no
switch: a slot build's `UpdateService` is inert unless the build passes `-p:UpdaterEnabled=true`, which
only release builds do (`src/CcDirector.Avalonia/CcDirector.Avalonia.csproj`), and the cc-* tool sync
that does run writes under the rig's HOME and root. The live run above stood up and joined with the
switches gone; its `config.json` holds no `autoUpdate`.

## F3 - two Windows proof command lines could not be run as written. ACCEPTED, fixed.

Both lines in `phase-1-windows-proof.md` now read `.\scripts\test-local.ps1`; the file holds no tab
character.

## N1 - the token sentence was inexact. ACCEPTED, sentence fixed.

The comment above `gw` now says a Gateway call never puts the token in the process list, and names
`write_director_config` as the one place it is passed to `python3` as an argument, visible for that
moment. The behaviour was left as it is, as the call says: it is the throwaway rig's token on the
owner's machine.

## N2 - "nothing anywhere else" was inexact. ACCEPTED, sentence fixed.

The header now says the one write outside the rig folder is `build`'s: the worktree's own
`scripts/local-build-linux.sh` writes `<worktree>/local_builds/linux/cc-director-linux<N>` (ignored by
git), `build` copies it into the rig, and `reset` leaves it.

## N3 - the premise about `~/.local/bin` was out of date. ACCEPTED, sentences fixed.

The script header now says a Director on a root other than the default never writes the user's global
`~/.local/bin` (main's change #4211, commit `3306b29f1`, in this branch through the merge), and the rig's
HOME is a second guard. `live-rig.md` section 1 keeps the 2026-10-18 proof as history and says the same.

## N4 - the stale comment in `ClaudeHookInstaller`. ACCEPTED, fixed.

`src/CcDirector.Core/Claude/ClaudeHookInstaller.cs` `BuildSettings` now says Claude Code runs the
command string through sh on Linux and macOS and through Git Bash on Windows (as the phase 1 Windows
proof showed), so the Windows form must survive bash's quoting. A comment only.
`~/.dotnet/dotnet test src/CcDirector.Core.UnitTests --filter FullyQualifiedName~Hook`: 82 passed,
0 failed.

## N5 - kept, as the call says. No change.
