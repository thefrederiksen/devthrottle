# Inspection brief - phase 2, pull request #3006

You are the independent Inspector for pull request 3006 in thefrederiksen/devthrottle: phase 2 of dev reports - the
Gateway report record, owner-only access, holding the owner's notes until the session's turn ends and delivering
them as one prompt, and the first `cc-dev-reports` tool. Your worktree `D:\ReposFred\devthrottle-dev-reports-p2-review`
is at the head `da1819e1f`. Read the change with `git diff origin/main...HEAD`.

You did not build it. Be adversarial. Treat `PHASE-2-REPORT.md` and the WORKER and REVIEW files in
`docs/missions/dev-reports/` as unverified self-testimony; read the code.

## Questions

1. Can anyone other than the session's owner read, list, send to, or learn the existence of a report? Check every
   new route, its registration with the session key guard, tenant scoping, and the reverse (a session key calling an
   owner route).
2. Can a note be delivered twice, lost, or left stuck? Read the database claim and settle code; look for a path
   where the conditional update is not actually conditional.
3. Can owner or report text forge instructions in the prompt the session receives? Is the owner's text passed
   through verbatim?
4. Can anything interrupt a working session?
5. Where could a constant or stub be substituted and the tests stay green? Name tests that build their own input
   instead of exercising the real caller.
6. Is the database migration present for every provider, and does it match the model?
7. `cc-dev-reports`: is an unknown flag an error, and does nothing wait forever?
8. Public-repo hygiene in the diff: private paths, machine names, non-ASCII, assistant or tool attribution.

## Rules

- Do not edit code or tests. Do not run full test suites - the machine is short of memory. A focused `dotnet test
  --filter` is fine.
- Write findings to `D:\ReposFred\devthrottle-dev-reports\docs\missions\dev-reports\INSPECTION-phase-2.md`. Each
  finding: severity (high, medium, low), file and line, the concrete scenario, and how you verified it. Say what
  you did NOT check.
- When the file is written, run exactly:
  `cc-devthrottle message send 184d1571 "<counts by severity> - INSPECTION-phase-2.md written"`
  Sending that message is REQUIRED. The Architect waits for it and nothing moves until it arrives.
