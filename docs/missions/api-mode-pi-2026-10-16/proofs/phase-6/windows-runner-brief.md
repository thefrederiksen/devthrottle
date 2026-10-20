# Brief: the Windows proof run on SOREN_NORTH (mission #4070, phase 6)

You are a runner seat on SOREN_NORTH, opened by the Delivery Lead (session 130), for one task: run the API mode proof
on Windows, commit its evidence, and report to the Delivery Lead. Report only to the Delivery Lead, with
`cc-devthrottle session handback`. Never guess: if something here does not match what you find, ask and carry on with
the rest.

Read first: `CLAUDE.md` rules 0 (never kill a process without permission), 0b (a Director you start is launched by Task
Scheduler, slot 5 or higher, and stopped with its named signal) and 00 (work from origin/main, in a worktree). The
script does the launching and the stopping; your part is to not do either by hand.

## What the run does

`scripts/prove-api-mode-pi.ps1` builds a Gateway (with the Cockpit and the phone app) and a slot-5 test Director from
the tree it runs in, starts them on a throwaway folder, and drives a real Pi through that Gateway:

1. Pi started in API mode; "Reply with exactly the word PONG" gets a Delivered receipt and PONG.
2. A prompt that runs a bash tool gets a tool card that finished.
3. A long prompt is stopped; the turn ends cancelled.
4. Take the wheel, "Remember the code word OTTER-58" typed into Pi's terminal, hand back, the code word asked in API
   mode comes back.
5. A prompt sent while holding the wheel is queued, then delivered on hand back.

It also shows three failure cases (take the wheel twice, hand back mid-turn, Pi's executable missing), photographs and
dumps the accessibility tree of the Cockpit and phone screens at each moment, and on Windows dumps the desktop
Director's New Session dialog and Terminal tab through UI Automation. It writes `summary.txt`, `results.json`,
`wire.jsonl`, `director-pi.log`, `spend.txt` and `screens\`, scrubbed of paths, the user name and the token.

**The Windows branches of the script have never run.** Expect the first run to find something. A problem in the
SCRIPT you may fix (in the script only, committed on its own, then run again). A problem in the PRODUCT you report as
it is, with the run's evidence; you do not fix the product.

## Before you start

1. **The tree.** The Delivery Lead tells you which ref to run: origin/main once the API mode pull requests are merged,
   or a named branch. Cut a worktree from it (rule 00): `git fetch origin` then
   `git worktree add ..\devthrottle-apiproof-windows -b apiproof-windows <ref>`. Run everything from that worktree.
   It must be clean when the run starts: the summary's `Tree:` line says "clean", or the run is not evidence.
2. **The tools.** PowerShell 7.4 or later (`pwsh -v`), the .NET 10 SDK (`dotnet --list-sdks`), Node and npm, Google
   Chrome at `C:\Program Files\Google\Chrome\Application\chrome.exe` (or pass `-Chrome <path>`), and Pi 1.2.0 on the
   PATH (`pi --version`). The cc-* tools' own Python at `%LOCALAPPDATA%\cc-director\pyenv\Scripts\python.exe` (or pass
   `-Python <path>`). If any is missing, stop and tell the Delivery Lead; do not install anything machine-wide.
3. **Slot 5 is free.** `Get-Process | Where-Object { $_.Path -like '*cc-director5*' } | Select-Object Id, Path` shows
   nothing, and `Get-ScheduledTask -TaskName cc-director5-launch -ErrorAction SilentlyContinue` shows nothing (the
   script refuses to replace a task it did not make). If either is in use, stop and ask.
4. **Port 7995 is free** (`Get-NetTCPConnection -LocalPort 7995 -ErrorAction SilentlyContinue` shows nothing), or pick
   another and pass `-GatewayPort <port>`.
5. **The rig folder.** Pictures cannot be scrubbed, and the screens show the rig's work folder. The default rig folder
   is under `%TEMP%`, which contains the user's name, so pass a short folder: `-RigRoot C:\apiproof`. Create it if it
   does not exist; the script makes its own folder inside it and removes it at the end.
6. **Disk.** At least 12 GB free on the drive of the worktree and of the rig folder.

## The run

From the worktree root:

```powershell
pwsh -NoProfile -File scripts\prove-api-mode-pi.ps1 -RigRoot C:\apiproof
```

It takes about ten minutes (most of it the two builds) and prints PASS or FAIL per check. Pi's spend is about 0.15
dollars. Do not run it again for a failure in the PRODUCT: one run is the evidence. Run it again only after fixing the
SCRIPT, and say in your report how many runs you made.

If the script says the test Director did not stop on its named signal, it has NOT been killed and is still running:
tell the Delivery Lead and stop. Do not kill it.

## After the run

1. Read `summary.txt` in the new folder `docs\missions\api-mode-pi-2026-10-16\proofs\phase-6\windows-<time>\`. Look at
   every picture in `screens\` before you commit it: if any shows the user's name, a token, or anything private,
   delete that picture, say so in your report, and do not commit it.
2. Add the run's row from `spend.txt` to `docs\missions\api-mode-pi-2026-10-16\spend.md` (and the totals), one row per
   real run.
3. Commit the folder and the spend row, with no assistant name anywhere in the message, as
   `-c user.name="thefrederiksen" -c user.email="soren@duksrevo.com"`; push the branch.
4. Report to the Delivery Lead: the branch and commit, the summary's PASS and FAIL lines, anything you deleted from the
   screens, any script fix you made, and the spend.

The QA report is built from your folder and the Linux one by `scripts\build-api-mode-qa-report.ps1`; the Delivery Lead
does that, not you.
