# Inspection brief - phase 3, the Reports view in the Cockpit and on the phone

You are the independent Inspector. Your worktree `D:\ReposFred\devthrottle-dev-reports-p2-review` is detached at
`fd6fc3b36`. Read the change with `git diff origin/main...HEAD` (phase 2 is already on main, so the diff is phase 3:
`packages/client-core`, `apps/cockpit`, `apps/mobile`, the E9 fix in the note script and the Gateway, and proof files).

You did not build it. Be adversarial. Treat the proof README, reports and handoffs as unverified self-testimony;
read the code.

## Questions

1. **The frame.** Does every host follow `packages/client-core/src/devreports/CONTRACT.md` section 4 exactly -
   `sandbox="allow-scripts"` with no `allow-same-origin`, a head the host writes with a Content-Security-Policy that
   lets only its injected script run (fresh nonce per load), a fresh token per load, traffic over the private port,
   and any frame load the host did not cause ending the token? Look for a path where report content lands before
   or outside the host's head, a reused nonce or token, or a message accepted without the token.
2. **Can the report reach the app** - storage, cookies, the Gateway with the owner's credentials, the parent DOM?
3. **Client is dumb** (repository rule 7): does any client code decide what an item state MEANS instead of
   rendering the Gateway's labels verbatim?
4. **One implementation**: is anything duplicated between the Cockpit and the phone that belongs in client-core?
5. **E9**: are item ids now unguessable and unique across browsers, and does the Gateway refuse a reused id with
   different content while an identical resend stays idempotent? Could the refusal lose a note?
6. **State held by the host**: can queued notes or half-typed text be lost on reload, on a new report version, or
   shown on the wrong report?
7. Where could a constant or stub be substituted and the tests stay green?
8. **Hygiene** in the diff: private paths, machine names, session keys or tokens in evidence files, non-ASCII,
   assistant or tool attribution.

## Rules

- Do not edit code or tests. Do not run full test suites (the machine is short of memory); focused runs are fine.
- Write findings to `D:\ReposFred\devthrottle-dev-reports-p3\docs\missions\dev-reports\INSPECTION-phase-3.md`: each
  with severity (high, medium, low), file and line, the concrete scenario, and how you verified it. Say what you did
  NOT check.
- Then run: `cc-devthrottle message send 184d1571 "<counts by severity> - INSPECTION-phase-3.md written"`. If that
  fails, the file is enough.
