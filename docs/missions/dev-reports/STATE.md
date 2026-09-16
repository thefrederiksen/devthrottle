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
7. Deploy: the owner approved ONE deploy through the `deploy-hosted-gateway` skill after phase 3 merges.

## Phases

| Phase | State |
|---|---|
| 1. Note-taking script and shape check | starting |
| 2. Gateway record and delivery | not started |
| 3. Cockpit and phone viewer (then deploy) | not started |
| 4. Director pane | not started |
| 5. Tool, skill, global instruction rule | not started |
