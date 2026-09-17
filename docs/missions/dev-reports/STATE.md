# Dev Reports mission - running state

Tracking issue: #2936. Design: `docs/design/dev-reports/cc-dev-reports.html`.
Conduct: `cc-devthrottle workflow instructions mission`.
Mission branch: `mission/dev-reports`, worktree `../devthrottle-dev-reports` (cut from origin/main 66609c3cd).
Each phase lands on main as its own pull request from a branch cut off the mission branch or origin/main.

## Rulings (Architect, 2026-09-16)

Owner rulings are in the issue. These are the Architect's, settling the inferred points:

1. Agent-written HTML is shown in `<iframe sandbox="allow-scripts">` with NO `allow-same-origin`.
   Both apps already do this for HTML files (`FileViewerModal.tsx`, `FileView.tsx`); the Reports view
   reuses that posture. The page talks to the host ONLY by `postMessage`. Everything the page must
   remember across a reload (queued notes, half-typed text, scroll position) is held by the HOST,
   because an opaque-origin frame has no storage.
2. The Gateway stores the report (HTML bytes, version, notes, replies). The Cockpit and phone never
   read a Director's disk.
3. A report stays readable after its session ends. Sending to an ended session is refused with
   "this session has ended".
4. The Gateway owns every ruling: the shape check verdict, the delivery state of a note (queued / held
   until the turn ends / delivered), and the text of the prompt the session receives. Clients render.
   The shape check is therefore C# in the Gateway, not in the tool or the script.
5. The note-taking script is ONE self-contained plain JavaScript file with no dependencies, built from
   one source, injected into the report by the host. The same file serves Cockpit, phone and Director.
6. Size limit on publish: 10 MB of HTML, refused with a clear error above it.
7. Deploy (owner, 2026-09-16, widened the same day): deploy through the `deploy-hosted-gateway` skill
   and nothing else, as soon as there is something to test and at the latest when phase 3 merges; deploy
   again after every later phase that changes the Gateway, the Cockpit or the phone. After each deploy,
   tell the owner what he can test and how, on the phone and in the Cockpit.
8. Report scripts are blocked (upheld 2026-09-16, from the phase 1 review). A script in an agent-written
   report shares the frame with the note-taking script, so it could forge the owner's notes and read restored
   state. Every host MUST follow `packages/client-core/src/devreports/CONTRACT.md` section 4: a
   Content-Security-Policy letting only the host's injected script run (fresh nonce per load), a fresh token
   on that script carried by every message, and any frame load the host did not cause ends the token. The
   shape check refuses `<script>` and inline event handlers. Cost accepted: no script-drawn charts; images
   are `data:` URLs.
9. The shape check parses with a real HTML5 parser (AngleSharp, MIT), not a hand-written scanner
   (from the phase 1 inspection: four review rounds kept finding parser gaps - comments inside templates,
   implied end tags, hidden sections). It checks the DOM a browser would build. The shape check is guidance
   for agents at publish time; it is NOT the security boundary. The host policy of ruling 8 is.

## Phases

| Phase | State |
|---|---|
| 1. Note-taking script and shape check | MERGED to main (pull request #2948, ce017b08c) after three inspection rounds |
| 2. Gateway record and delivery | MERGED (#3006) and DEPLOYED 2026-09-17 |
| 3. Cockpit and phone viewer (then deploy) | MERGED (#3015) and DEPLOYED 2026-09-17 14:01 (external outage 24.2 s, over the 10 s budget) |
| 4. Director pane | starting on mission/dev-reports-p4 (HANDOFF-phase-4.md) |
| 5. Tool, skill, global instruction rule | not started |

## Notes

- `cc-dev-reports` is installed for the owner's machine from the `../devthrottle-dev-reports-p3` worktree (detached at
  main). Do not remove that worktree before phase 5 puts the tool in the installer.
- Fleet `message send` to child sessions fails with "unknown error" (issue #3009); read terminals, and re-seat a
  session to deliver an instruction.
