# Mission Button on the Web - review of pull request 4734

Reviewed by a separate review session on 7 November 2026, in the detached worktree
D:\ReposFred\devthrottle-mbw-review at a26be4524 (branch mission-button-web, base 1396d7a00 = origin/main at the
time of review). Nothing in the worktree was edited, committed or pushed. The only files created were the ignored
node_modules folders needed to run the web tests.

## Verdict

No blocking findings. The change does what the mission document asks and keeps repository rules 7 and 8. Findings
1 to 8 below are not blocking; each needs an answer in docs/mission-button-web/developer-proof.md, and findings 1
and 2 deserve a small follow-up.

## What was checked and holds

- Rule 7 (the client is dumb): MissionButton.tsx reads label, colorKey, tooltip and documentAddress and renders
  them. It never reads verdict (pinned by the test that changes the verdict word and expects the same look). The
  only mapping is colour key to a stylesheet class, which is drawing, not meaning. No branch in SessionDetail.tsx or
  SessionAppBar.tsx decides anything; both pass the row's missionButton through.
- Rule 8 (one shared component): the component and its stylesheet live in packages/client-core and are exported as
  "./sessions/MissionButton". The Cockpit and the phone each add layout only (.session-mission in the Cockpit
  stylesheet; a width cap and margin under .session-app-bar in the phone stylesheet).
- Wire shape: the TypeScript MissionButtonDisplay (verdict, label, colorKey, tooltip, documentAddress nullable)
  matches src/CcDirector.Gateway.Contracts/MissionButtonDisplay.cs, and SessionDto.MissionButton exists on
  origin/main. MissionButtonFold.Stamp is called from the roster read and the pushed display-state path, and it
  builds the address as DevReportLinkRoute.Prefix ("/r/") plus the report id, so the field reaches both web clients.
- Unknown colour key: a visible problem note (role alert, class mb-problem) naming the key replaces the button, and
  reportShownError is called once per sentence in the background. A null or absent verdict renders nothing. Both
  pinned by tests.
- The link: href is the Gateway's path verbatim, target _blank, rel "noopener noreferrer". Without an address it
  is a button, not a link. Pinned by tests in all three packages.
- The mounts are exercised: the Cockpit test renders the real SessionDetail inside a router with the panes mocked
  and asserts the link sits in .session-tabs before the session menu and that another row's verdict is not drawn;
  the phone test renders the real SessionAppBar and asserts the link sits in the header.
- Hygiene: zero non-ASCII characters in the added lines; no attribution to any tool in the commit message, the
  pull request body or the diff; the prose is plain English apart from finding 8.
- Run here on Node 22.17.0 after npm ci:

  | Package | tsc --noEmit | vitest run |
  |---|---|---|
  | packages/client-core | clean | 193 files, 2495 passed, 0 failed, 2 unhandled errors (finding 2), exit code 1 |
  | apps/cockpit | clean | 138 files, 1504 passed, 0 failed, exit code 0 |
  | apps/mobile | clean | 41 files, 214 passed, 0 failed, exit code 0 |

  eslint on the seven changed source and test files: clean.

## Findings

1. Not blocking. The address guard lets a backslash address leave the Gateway. missionButtonProblem accepts any
   string that starts with one slash and not two. A browser resolves "/\evil.example/r/1" against the Gateway
   origin as "https://evil.example/r/1" (checked with Node's WHATWG URL parser: the string passes the guard and
   resolves off-origin). The guard's own sentence says the address "must be a path on the Gateway", which this
   case is not. Today the only writer is MissionButtonFold, which emits "/r/<guid>", so nothing reaches this in
   practice; it is the guard's claim that is wider than its code. Fix: resolve with
   `new URL(address, window.location.origin)` and require `.origin === window.location.origin`, or also reject a
   backslash anywhere in the address, and add the backslash case to the test next to the protocol-relative one.
   The desktop's BuildOpenUrl has the same gap but concatenates onto the Gateway base, so it is not open in the
   same way there.

2. Not blocking. The client-core test run exits red. vitest reports 2495 passed and 0 failed but also "2 errors"
   (an uncaught ReferenceError: WebSocket is not defined, thrown by a reconnect timer in src/terminal/stream.ts
   after its test's environment was torn down) and exits with code 1. This change touches neither that file nor its
   tests, so it is not caused here, but the proof file quotes "2,495 passed, 0 failed" and does not say the run
   exited red. Say so in the proof, and if it reproduces on origin/main it is a defect to log or a test to remove
   under the "a test works or it is removed" rule.

3. Not blocking. The comment on the hand-written type gives the wrong reason. client.ts says missionButton is
   hand-written "for the reason modelDisplay is: the committed schema.ts predates the field". schema.ts on
   origin/main does contain modelDisplay (around line 12409); it is only missionButton that is missing. The
   sentence should state its own reason: schema.ts has not been regenerated (npm run gen:api against a running
   Gateway) since the Gateway gained the field. Regenerating it would remove the hand-written interface.

4. Not blocking. The touch tooltip is unproven on a real phone. The long press arms a 500 millisecond timer and
   cancels it on pointerup, pointerleave and pointercancel. A phone browser may fire pointercancel when it takes
   over a long press for its own link menu; if it does so before the timer fires, the bubble never shows on the
   link. The tests describe a finger by hand in jsdom and cannot show this, and the mission's planned proof (a
   phone-sized window on the computer) drives it with a mouse, which skips the touch path entirely. The quality
   assurance step should try the long press on a real phone or with touch emulation and report what happened.

5. Not blocking. The development proxies do not front "/r". Both vite.config.ts files front every root-relative
   prefix the pages call so that `npm run dev` works against a real Gateway; "/r" is not in either list, so under
   the development server the button's new tab lands on the development server's own index page. Production is
   unaffected (same origin). Add "/r" to both lists.

6. Not blocking. On a mouse surface a click on a button without a document toggles the bubble as well. The
   "No mission" and outline buttons open the mb-tip bubble on any click, including a mouse click on the Cockpit,
   where the title attribute already shows the same words on hover. Harmless, and arguably useful, but the
   comments describe the bubble as the touch answer; either gate the click-to-show on a touch pointer or say in the
   comment that it is for every pointer.

7. Not blocking. The three looks are literal hex values in a client-core stylesheet. docs/VisualStyle.md's note on
   the web shells says to use each shell's tokens and not to port hex values into the stylesheets. Neither shell
   defines an indigo or a warning-yellow token, the Developer brief specified these exact values, and other
   client-core stylesheets already carry literals, so this is consistent with what exists. Recording it so the
   "extend the guide to the web" work knows these colours exist.

8. Not blocking. One abbreviation in the proof file: "the mission's QA step" in developer-proof.md. Rule 0a asks for
   the full words ("quality assurance").

## Not findings, noted for the record

- Placement on the Cockpit: the mission document says "the session detail header beside its actions" and the
  Developer brief said "near SessionActionBar". The Developer mounted it in the tab strip beside the model chip and
  the session menu and explained why in the proof (the action bar is at the bottom and hidden on the Wingman tab).
  That reading honours the mission document, which wins, and the mount test pins it.
- The mission document asks for screenshots of both live surfaces after the deploy. The proof's screenshot is of
  the component alone, built from the real source and stylesheet, and the proof says plainly that the two surfaces
  were not screenshotted locally. That is the mission's quality assurance step, not a gap in this pull request.

## Re-check

Re-checked on 7 November 2026 against 60c3adce9 (the Developer's answer commit, now the head of pull request 4734),
checked out detached in the same worktree. Checked: the diff from a26be4524 to 60c3adce9 and
docs/mission-button-web/developer-proof.md. Run here: client-core tsc clean; client-core vitest 193 files,
2495 passed, 0 failed (exit 1 again, same two unhandled WebSocket errors from the terminal stream test, see
finding 2); the Cockpit mount test file 3 passed; the phone mount test file 4 passed. The new commit adds no
non-ASCII character, no attribution and no abbreviation.

1. Closed. isGatewayPath resolves the address against window.location.origin and requires the same origin after the
   one-slash check; the backslash case is asserted in the test and the test passes.
2. Closed. Not caused by this change; reproduced by the Developer on origin/main; logged as issue 4738, which is
   open. It still exits 1 here, as expected until that issue is fixed.
3. Closed. The comment on MissionButtonDisplay now gives its own reason (schema.ts not regenerated since the Gateway
   gained the field) and says regenerating removes the interface.
4. Open, by design. Cannot be proved in jsdom; the proof says so, names the risk and the fix if it fails, and hands
   the long press to the live check under touch emulation. Closes when that check reports.
5. Closed. Both development servers proxy "/r/" with the trailing slash, so the app's own pages beginning with r
   are not caught.
6. Closed. Kept as a click by any pointer, and the component comment and the proof now say so.
7. Closed. Answered in the proof with the three looks recorded for the visual style guide's web extension.
8. Closed. "QA" is now "quality assurance".

New, not blocking: the proof's "Review findings" section writes the review file's path with a literal carriage
return byte where "\review.md" should be (the file reads "mission-button-web" + CR + "eview.md"). A control
character in a committed document; fix the path to D:\ReposFred\_briefs\mission-button-web\review.md.

Verdict unchanged: nothing blocking. Ready to merge; finding 4 is answered by the live check after the deploy.
