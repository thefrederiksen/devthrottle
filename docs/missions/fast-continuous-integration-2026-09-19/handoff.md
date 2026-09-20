# Handoff note - fast continuous integration

The only thing a fresh Manager needs. The Architect keeps it current.

- **Brief:** `docs/MISSION-fast-continuous-integration-2026-09-19.md`
- **Conduct:** `.claude/skills/mission/SKILL.md`
- **Branch:** `mission/fast-continuous-integration`, cut from origin/main on 19 September 2026. Rebase
  onto origin/main before each package; main moves about thirty times a day.
- **Phase:** 1 of 3 - packages 1, 3 and 4 of the brief, which do not depend on each other.
- **Done and pushed:** the brief, this note. No product or workflow change yet.
- **Next Worker tasks:**
  1. Package 1 - move `HostedImagePublishedArtifactTests` to the hosted deploy workflow; establish how
     that workflow's green check treats a commit whose run was cancelled.
  2. Package 3 - PostgreSQL proofs into their own project with a path-started Linux job.
  3. Package 4 - the continuous integration keeper as a daily scheduled job.
- **Before package 2 (phase 2):** main seen green on a finished run; see the brief.
- **Held:** package 5 (release workflow) until v2.8.0 is out. Package 6 after package 5's real release.
- **Each package is one pull request, opened by the Manager against main and NOT merged by it.** The
  Architect calls a different-family inspection and lands it.
- **Proof:** each package's "proof owed" line in the brief. A fix and its guard are one unit. Say what
  is not proven.
- **.NET on the Mac:** the SDK is under the home directory's `.dotnet` folder and is not on the
  default path. Do not run the parked Gateway suite locally; GitHub's machines are the measuring
  instrument for this mission.
