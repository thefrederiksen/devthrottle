# Missions First-Class - final proof

The goal's last screenshots: this Mission's own button, in a real Director, opening this Mission's own document.

## What was run

- A slot-5 test Director built from b3e7ead62 (the phase 3 merge, which contains phase 1 154ba1f43 and phase 2
  f24a46a51, both checked as ancestors before building), in a new detached worktree, with
  `scripts\local-build-avalonia.ps1 -Slot 5 -NoRuleHook` (this machine has no C++ linker for the rule hook, which
  plays no part here). Its window footer reads `v2.36.1 (b3e7ead)`.
- Run as instance `slot-5`, connected to the live hosted Gateway `https://gateway.devthrottle.com`. The
  `cc-director-launch` scheduled task failed to start it with 0xC0000142 (the known desktop failure on this machine
  under load), so it was started through WMI `Win32_Process Create`, which is also outside the agent's process tree.
- Two PowerShell sessions were opened in it: session 106, attached to the real Mission 0c91e2c5 "Missions
  First-Class - every session shows its mission and goal", whose document is report c46de9ec; and session 109, in no
  Mission.
- Afterwards the test Director was stopped with its named shutdown signal, which ended both sessions; slot 5's
  configuration was put back as it was. The Mission 0c91e2c5 itself was not changed - it is still active, under the
  same name - and its document was not republished.

## The screenshots, and what each proves

| Screenshot | What it proves |
|---|---|
| `final-screenshots/01-mission-with-document.png` | Session 106 selected: the tab bar Mission button beside the Cockpit button, and the toolbar corner button beside the toolbar Cockpit button, both indigo filled, both showing the Mission's name (cut with an ellipsis at the button's set width). |
| `final-screenshots/02-tab-bar-tooltip-name-and-goal.png` | Hovering the tab bar button shows "Mission: Missions First-Class - every session shows its mission and goal" and the Mission's goal, read from section 3 of the mission document, in full. |
| `final-screenshots/03-corner-tooltip-name-and-goal.png` | The corner button shows the same name and goal for the same selected session. |
| `final-screenshots/04-tab-bar-click-opens-the-mission-document.png` | After clicking the tab bar button: the browser on `gateway.devthrottle.com/report/c46de9ec-3240-470c-9af4-7c4be47eb3ce` (where `/r/<id>` sends a desktop browser), showing this Mission's document. The browser's tab row is cropped off because it named an unrelated signed-in account; the address bar is kept. |
| `final-screenshots/05-no-mission.png` | Session 109 selected: yellow "No mission" on the tab bar button and in the corner, which followed the selection. The session works normally. |

## The clicks, from the Director's log

Quoted exactly from `%LOCALAPPDATA%\cc-director\instances\slot-5\logs\director\director-2026-11-06-59676.log`. Session
`f2ed774d...` is session 106 (in the Mission); `21b499ed...` is session 109 (no Mission). These are the only
`MissionButton_Click` lines in that log.

```
2026-11-06 19:56:50.689 [MainWindow] MissionButton_Click: session=f2ed774d-1173-4994-b25f-6b236f16a7c2, address=/r/c46de9ec-3240-470c-9af4-7c4be47eb3ce
2026-11-06 19:56:50.689 [MainWindow] MissionButton_Click: opening https://gateway.devthrottle.com/r/c46de9ec-3240-470c-9af4-7c4be47eb3ce
2026-11-06 19:57:52.907 [MainWindow] MissionButton_Click: session=f2ed774d-1173-4994-b25f-6b236f16a7c2, address=/r/c46de9ec-3240-470c-9af4-7c4be47eb3ce
2026-11-06 19:57:52.908 [MainWindow] MissionButton_Click: opening https://gateway.devthrottle.com/r/c46de9ec-3240-470c-9af4-7c4be47eb3ce
2026-11-06 19:58:07.272 [MainWindow] MissionButton_Click: session=21b499ed-829e-4985-b7b0-cf4e5508645b, address=(none)
2026-11-06 19:58:07.273 [MainWindow] MissionButton_Click: the Gateway gave no document address, opening nothing
2026-11-06 19:58:12.406 [MainWindow] MissionButton_Click: session=21b499ed-829e-4985-b7b0-cf4e5508645b, address=(none)
2026-11-06 19:58:12.406 [MainWindow] MissionButton_Click: the Gateway gave no document address, opening nothing
```

In order: 19:56:50 is the tab bar button on session 106 (screenshot 04 is what it opened); 19:57:52 is the corner
button on the same session, opening the same address; 19:58:07 is the tab bar "No mission" on session 109 and
19:58:12 the corner "No mission", each opening nothing. No screenshot is offered for "opened nothing": a click that
opens nothing leaves the same frame as no click, so the log is the evidence.

## What else is in this folder

- `mission.html` - the mission document as it stood when copied, on 6 November 2026.
- `phase1-review.md`, `phase2-review.md`, `phase3-review.md` - the separate review sessions' reviews of each phase,
  copied unchanged (all four files were already plain ASCII).
- `phase1-proof.md`, `phase2-proof.md`, `phase3-proof.md` and `phase2-screenshots/` - each phase's own proof, merged
  with that phase.
