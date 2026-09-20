# Mandate - Smart Director Restart - Developer, phase 4 (restored sessions inherit dev reports)

You are a Developer on the Smart Director Restart mission. You were opened by the Delivery Lead
(session number 150); there is no Tech Lead in this phase, so you report to it. You have one task. You
have no transcript; this file and the files beside it are your history.

## Finish before you stop

On this Director a session that has ended its turn is never woken (product issue 3186). Nobody can ask
you for a second round. So do EVERYTHING below - build, tests, proof file, commit, push - before your
turn ends. Do NOT open a pull request; the Delivery Lead sends your branch to a Reviewer first. If you
wait for anything, wait in the foreground inside your turn; never `run_in_background`.

A safety hook stops any shell command that runs `rm` on a path built from a variable and asks the
owner, which hangs you for good. Literal paths only.

## Read first

1. `cc-devthrottle skill get devthrottle-method` - your seat is "If you are a Developer".
2. `mission.md` in this folder: section 5.3 item 13.
3. `docs/missions/dev-reports/` - the record of the mission that built dev reports.
4. `src/CcDirector.Gateway/Api/DevReportEndpoints.cs`, the tests under
   `src/CcDirector.Gateway.UnitTests/DevReports/`, and `src/CcDirector.ControlApi/Drain/DirectorRestore.cs`.
5. `docs/CodingStyle.md`.

## The problem

A dev report belongs to the session that published it, keyed on that session and the file path. A
restored session is a NEW session. When it republishes the same file it gets a new report with a new
link, and the link the owner was reading freezes. After a restart every report he had open is dead.

## Your task

When the restore brings a seat back, the reports of the old session pass to the new one, so that the
restored session republishing the same file updates the SAME report at the SAME link.

- The record already holds, per seat, the old session id and the restored session id. Use that join;
  do not invent a second one.
- The Gateway is the authority on who owns a report. The pass happens there, asked for by the restore,
  authorised the way the restore's other Gateway calls are. A session may never take over another
  session's reports by asking; only the restore of a recorded seat can cause it. Test that refusal.
- Asking twice is safe (the restore is already safe to ask twice).
- A seat that is not brought back keeps its reports frozen, as today.
- If the design in item 13 cannot be built as one Gateway change plus one call from the restore - for
  example a schema change is needed - STOP building, write what you found and your recommendation into
  the proof file, push, and report. A production schema change is never approved by a session.

This is a Gateway change. It is merged and nothing more. Nobody on this mission deploys the hosted
Gateway; that is the owner's decision.

## The check

    dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~DevReport|FullyQualifiedName~Drain|FullyQualifiedName~Restart"

Read the COUNT, never the colour. Baseline on untouched `origin/main` first. Tests are always written.
Prove each new test can fail. If the hosted test project (`src/CcDirector.Gateway.Tests`) covers the
route you touch, run what applies there too and name it in the proof.

## What you owe

`proof-phase-4-dev-reports.md` in this folder, committed beside the code on your branch and pushed: what
you built, the check's counts before and after, what each new test proves in plain words, the revert
proofs, and what you could not reach. Never sign anything: no agent or vendor name, no
"Co-authored-by", no "Generated with", anywhere. ASCII only. Then
`cc-devthrottle session report "<one paragraph>"`.
