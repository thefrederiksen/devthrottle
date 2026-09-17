# Slice 4 evidence - the row line

What each file is, and what it does NOT show.

| File | What it is |
|---|---|
| `director-rail-row-line.png` | The Director's REAL session row template (`MainWindow.SessionList.ItemTemplate`), rendered headless with Skia and captured in-process with `RenderTargetBitmap` on the Mac mini - no screen capture. Four real `Session` objects, each stamped through `ApplyGatewayDisplayState` the way the `set-display-state` verb stamps it. |
| `director-rail-render-harness.cs.txt` | The harness that made that picture (built in a scratch folder, not committed as a project). It exits 1 if any stamped line is missing from the drawn text. |
| `cockpit-row-line.png`, `mobile-row-line.png` | The REAL Cockpit `SessionRoster` and the REAL phone `SessionRow`, bundled from source with esbuild, served with each app's own `styles.css`, rendered in headless Chrome by Playwright. |
| `web-render-harness-*.txt` | The harness that made those two pictures. |
| `web-render-result.json` | What the browser read back: the three lines, in order, verbatim, in `rgb(59, 130, 246)`, with no page errors, on both surfaces. |
| `guards-watched-failing.json` | 29 deliberate breaks, each applied, the named tests run, the file restored. 28 went red on every suite named; the one exception is explained in its `note`. |
| `web-failures-before-and-after.txt` | The 77 web test failures present at the committed head BEFORE this slice (a throwaway worktree of `f04aa97e`). The same 77, and only those, fail after it. |

## What these do not show

- The web pictures are the real components in a page of the proof's own, not the whole Cockpit or phone
  shell: there is no sign-in, no app bar, and the phone rows sit in a plain list (the bullets are that list's,
  not the app's). The Gateway is simulated only by the rows handed to the components; every other request
  answers 404, which is why the Cockpit shows "Could not read the restart requests".
- The desktop picture is the row template inside a bare window, not the running Director. No live Director,
  no live Gateway and no real message were involved in any picture. The live path from a queued message to
  the wire and down the display push is proven by `FleetMessageRouteTests.The_roster_the_session_read_and_the_desktop_push_carry_the_row_line_until_it_is_read`
  on a real Gateway host with a recording tunnel Director.
