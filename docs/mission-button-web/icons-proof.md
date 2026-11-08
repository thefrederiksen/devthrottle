# Mission button - smaller, with icons - Developer proof

Mission: "Mission Button on the Web". The owner approved the mockup on 7 November 2026
(`D:\ReposFred\_briefs\mission-button-web\button-mockup.html`): "as mocked", and "all three surfaces look the same".
Branch `mission-button-icons`, cut from origin/main and rebased onto 303de6ad8.

## What changed

**The Gateway decides it** (repository rule 7).

- `src/CcDirector.Gateway.Contracts/MissionButtonDisplay.cs` - a new `IconKey` field and three keys:
  `document`, `none`, `warning`.
- `src/CcDirector.Gateway/Missions/MissionButtonFold.cs` - the label is "Mission" in every state (the mockup says
  "Mission" for no Mission too). The icon key is `document` with a document, `none` with no document yet, `warning`
  with no Mission. The Mission's name and goal stay in the tooltip; the tooltip of a Mission with a document now ends
  "Click to open the mission document.", as the mockup's hover box does.

**The web** - one shared component, so the Cockpit and the phone cannot drift.

- `packages/client-core/src/api/client.ts` - `iconKey` on the hand-written `MissionButtonDisplay`.
- `packages/client-core/src/sessions/MissionButton.tsx` - draws the icon the key names as inline SVG (the mockup's
  own paths; no characters, no icon font) before the word. An unknown icon key, or none at all, is a broken contract:
  reported centrally and shown as the visible problem note, exactly like an unknown colour key. The colour key and
  the icon key are mapped separately; neither is inferred from the other or from the verdict.
- The long-press bubble stays inside the screen: after it is drawn it is measured and slid sideways if it would cross
  either edge, keeping an 8 pixel gap (`bubbleShift`). A bubble wider than the screen is pinned to the left gap.
- `packages/client-core/src/sessions/missionButton.css` - 22 pixels high, 12 pixel text, 13 pixel icon, 5 pixel gap,
  6 pixel corners. The looks are the mockup's: filled #4338CA with border #6366F1 and white text; outline #6366F1 with
  text #C7D2FE; no Mission a transparent button with border #CA8A04 and text #FDE68A, the icon in #EAB308.
- `apps/mobile/src/styles.css` - the phone's own size override for the button (bigger padding and font, a 46 percent
  width cap) is removed, so the button is the same size in every header.

**The desktop Director.**

- `src/CcDirector.Avalonia/MissionButtonPresenter.cs` - `Icon` (a `MissionButtonIcon`: None, Document, Warning) from
  the icon key; an unknown or missing icon key is a problem, logged, and no button is drawn, the note shown instead.
- `src/CcDirector.Avalonia/MainWindow.axaml` - both buttons (the tab bar's and the toolbar corner's) hold a
  document path icon and a warning path icon before the word; the window shows the one the presenter
  names. The style is 22 pixels high, padding 9, font 12, corners 6. `App.axaml` holds the mockup's colours.

**Compatibility.** An older Director against this Gateway ignores `iconKey` and shows "Mission" in its old looks
(the no-Mission one still yellow-filled) until it updates. A Director from this branch against a Gateway that does
not send `iconKey` shows the problem note in place of the button, by design: an absent key is not guessed.

## Tests

New or changed:

- `MissionButtonFoldTests` (Gateway.UnitTests) - each state's label "Mission", colour key, icon key and tooltip; the
  icon key arrives on the row through the real `StampFleetRolesAndFold`. 11 tests, all pass.
- `MissionButtonPresenterTests` (Avalonia.Tests) - each icon key to its one icon; unknown, wrong-case, empty and
  absent icon keys are no icon; an unknown or empty icon key shows the note and no button; each state's icon and
  look. 42 tests, all pass.
- `MissionButton.test.tsx` (client-core) - each icon key draws its icon first and the word after it; "none" draws
  the word alone; the icon follows the icon key, not the colour; unknown and absent icon keys show the note and are
  reported; `bubbleShift` for fits, past the left edge, past the right edge, wider than the screen; a long-press
  bubble drawn past the left edge is slid back on screen. 27 tests, all pass.
- The Cockpit and phone mount tests use the new fixtures.

Results, 7 November 2026:

| What | Result |
|---|---|
| `.\scripts\test-local.ps1 -Parked` | GREEN. All 18 suites `outcome=Completed`, 0 failed: Core.UnitTests 3639, Avalonia.Tests 1281, Engine 85, HostedAgent 85, Launcher 234, Terminal.Avalonia 111, Reclaim 307, DeliveryQualification 151, RuleHook 70, setup 43, setup-engine 1023 of 1029 (6 skipped), setup-cli 78, Gateway.Tests 426 + 482 + 439 of 442 (3 skipped) + 489, Core.Tests 4643 of 4661 (18 skipped), Gateway.UnitTests 17443. |
| `packages/client-core` | `tsc --noEmit` clean; 193 files, 2507 passed, 0 failed |
| `apps/cockpit` | `tsc --noEmit` clean; 138 files, 1504 passed, 0 failed |
| `apps/mobile` | `tsc --noEmit` clean; 41 files, 214 passed, 0 failed |
| `eslint` on every changed web file | clean |

## Desktop screenshots - which Gateway, and what they prove

The hosted Gateway does not send an icon key until this change is deployed, so a Director from this branch pointed at
it would only show the problem note. The screenshots were therefore taken against **a Gateway built from this branch**,
running on this machine:

- the desktop Gateway (`src/CcDirector.GatewayApp`, with the Cockpit and the phone app built from this branch) on
  `http://127.0.0.1:17893`, with `CC_GATEWAY_NO_TAILSCALE=1` (its log says so) and its own scratch root
  `%LOCALAPPDATA%\cc-mbicons-rig`, so nothing on the machine's real configuration or front door was touched;
- a slot-5 test Director built from this branch (`scripts\local-build-avalonia.ps1 -Slot 5 -NoRuleHook`), with the same
  scratch root, connected to that Gateway with its shared token. The `cc-director-launch` scheduled task failed with
  0xC0000142 again, so both were started through WMI `Win32_Process Create`, outside this session's process tree;
- three plain PowerShell sessions in it: one in a fixture Mission whose document was published from inside that
  session with `cc-dev-reports open docs\mission-button-web\icons-fixture-report.html --mission-document`, one in a
  fixture Mission with no document, one in no Mission.

The Gateway's verdicts for the three sessions, read from its `/sessions`:

```
with document  {"verdict":"mission-with-document","label":"Mission","colorKey":"mission","iconKey":"document","tooltip":"Mission: Mission Button Icons - fixture with a document\nGoal: Show the smaller Mission button with its icons in a test Director, and open this page from it.\nClick to open the mission document.","documentAddress":"/r/86657ddd-978b-4855-b00e-ca0e7e20faa4"}
no document    {"verdict":"mission-without-document","label":"Mission","colorKey":"mission-outline","iconKey":"none","tooltip":"Mission: Mission Button Icons - fixture with no document\nThis Mission has no document yet.","documentAddress":null}
no Mission     {"verdict":"no-mission","label":"Mission","colorKey":"warning","iconKey":"warning","tooltip":"This session has no Mission. Attach it to one with: cc-devthrottle mission attach <session> <mission>","documentAddress":null}
```

So this proves the whole desktop path - the branch's fold, the wire, the Director's presenter and both buttons -
end to end. It does not prove the hosted Gateway; that is the deploy.

In `icons-screenshots/`:

| Screenshot | What it shows |
|---|---|
| `01-desktop-mission-with-document.png`, `01b-...-closeup.png` | Indigo filled, document icon, "Mission", in the toolbar corner and the tab bar, the same size in both. |
| `02-desktop-mission-with-document-tooltip.png` | The hover: the Mission's name, its goal, and "Click to open the mission document." |
| `03-desktop-mission-without-document.png`, `03b-...-closeup.png` | Indigo outline, no icon, "Mission". |
| `04-desktop-mission-without-document-tooltip.png` | The hover: the name and "This Mission has no document yet." |
| `05-desktop-no-mission.png`, `05b-...-closeup.png` | Yellow outline, yellow icon, "Mission". Taken before the icon became a circle (see "No-Mission icon" below), so it shows the earlier triangle. |
| `06-desktop-no-mission-tooltip.png` | The hover: how to attach a Mission. |

The click on the document button, from the test Director's log:

```
2026-11-07 13:54:08.387 [MainWindow] MissionButton_Click: session=116aa908-4632-4056-a4bd-f225793b68fe, address=/r/86657ddd-978b-4855-b00e-ca0e7e20faa4
2026-11-07 13:54:08.387 [MainWindow] MissionButton_Click: opening http://127.0.0.1:17893/r/86657ddd-978b-4855-b00e-ca0e7e20faa4
```

Afterwards the test Director was stopped with its named shutdown signal (which ended its three sessions), the local
Gateway process this session started was stopped, and the `cc-director-launch` task was removed. Nothing on the hosted
Gateway was created or changed. The web screenshots are the Delivery Lead's, after the deploy.

## No-Mission icon: a circle, not a triangle

Changed on 7 November 2026 at the Delivery Lead's direction: the no-Mission icon is a yellow circle with an
exclamation mark, the recommended alternative, because the license warning (issue 4728) is a yellow triangle and the
two must not look alike. The Gateway's icon key is still `warning`; only the drawing changed, in the shared web
`MissionButton.tsx` and in the window's `MissionWarningIconGeometry` (a circle of radius 6.5, the bar from 4.5 to 8.8,
the dot centred at 11.3). The desktop screenshots above were taken before this change; no screenshot of the circle exists yet on any surface.
The Delivery Lead's web screenshots after the deploy cover the Cockpit and the phone.

Checked again after the change, on commit 0a51dcb61 rebased onto origin/main 436dc5db0:

- The new assertion (a `circle` of radius 6.5 and no closed path) fails against the triangle drawing and passes on the
  circle.
- `.\scripts	est-local.ps1 -Parked` in a separate detached worktree: GREEN, tree clean, all 18 suites Completed,
  0 failed. Core.UnitTests 3650, Avalonia.Tests 1285 (including the four icon-shape checks), Engine 85, HostedAgent 85,
  Launcher 234, Terminal.Avalonia 111, Reclaim 307, DeliveryQualification 151, RuleHook 70, setup 43, setup-engine
  1023 of 1029 (6 skipped), setup-cli 78, Gateway.Tests 426 + 482 + 439 of 442 (3 skipped) + 489, Core.Tests 4643 of
  4661 (18 skipped), Gateway.UnitTests 17487.
- Web: client-core 2508 tests, Cockpit 1504, mobile 214, all passed; type checks and eslint clean.

## Review

A separate review session reads the change; every finding is answered below.

The review ran on commit 3afeca16b. The first review seat did not survive the Director restart of 7 November, so a
new one was started; Codex refused its model, so the next seat in the order reviewed. Seven findings, none high.

1. MEDIUM - `docs/VisualStyle.md` still listed the old Mission colours. FIXED: both Mission tables now carry the
   shipped values (border `#6366F1`, outline text `#C7D2FE`, no-mission border `#CA8A04`, text `#FDE68A`, icon
   `#EAB308`) and the two new brushes `MissionBorderBrush` and `MissionWarningIcon`.
2. LOW - the hover-style comment in `MainWindow.axaml` gave a stale reason (dark text on yellow). FIXED: it now names
   what the style protects today, the light text and the document icon drawn in it.
3. LOW - `clientWidth || window.innerWidth` in `MissionButton.tsx` was a fallback. FIXED: one measure,
   `document.documentElement.clientWidth`.
4. LOW - the bubble was re-measured only when it opened, so a tooltip that changed while it was open could cross the
   edge. FIXED: the tooltip text is a dependency of the measuring effect. New test
   `MissionButton_TooltipChangesWhileBubbleOpen_IsMeasuredAgain`; it fails with the old dependency list
   (`expected '' to be 'translateX(32px)'`) and passes with the fix.
5. LOW - the two icons' path data was written twice in `MainWindow.axaml`. FIXED: the shapes are
   `MissionDocumentIconGeometry` and `MissionWarningIconGeometry` in `MainWindow.axaml`'s own resources, and both
   buttons use them. New test `MissionButtonIconShapeTests` loads the real window and checks each of the four icons
   has a shape with an area. (With the shapes first put in `App.axaml`, that test was red: a window built without
   the application's resources drew four empty icons, so the shapes moved into the window that uses them.)
6. LOW - the with-document tooltip says "Click to open the mission document.", which the phone also shows in its
   long-press bubble. NOT CHANGED: it is the approved mockup's wording. Left for the Delivery Lead.
7. LOW - `docs/mission-button-web/mission.html` still describes a yellow "No mission" button. NOT CHANGED here: the
   mission document is the Delivery Lead's to update when the record is landed.
