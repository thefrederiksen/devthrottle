# Brief - Developer, the ten factory manifests (phase E data)

You are a Developer on the Factories screen mission. Read `MISSION.md`, `PLAN.md` (decisions 1-3) and the design
report in this folder first. The Implementation Lead (session b496c54d) opened you and is who you report to.
This is a data task: no code, no build, no pull request.

## Your task

Write one registry manifest per factory into `manifests/<factory-id>.json` in THIS folder
(`D:/ReposFred/_wt/factories-screen/docs/missions/factories-screen-2026-10-06/manifests/`). The format is
`docs/cli-reference.md`, section "Factory registry" (read it from origin/main: `git show origin/main:docs/cli-reference.md`),
and every key there is exact - an unknown key is refused.

The ten factories (from the owner's morning review): mindzie Web, Website Business, WarmForward, Tallyhand,
DevThrottle, ClickFunnels, Business Research, Center Consulting, mindzie AI Reports, Machine Care.

For each one, from evidence only:
- **factory id**: it MUST be the id the factory already uses in its activity rows and memory, so the screen joins
  them (`cc-devthrottle factory activity -n 1000 --json` and look at the `factory` values, e.g. `website-business`,
  `clickfunnels`). A factory that has never written a row: take the id its folder or briefs use, and say so.
- **title, folder, computer**: from its Gateway schedules (`cc-devthrottle schedule list --json`: `action.repoPath`,
  `target.machine`) and the folder on disk.
- **seats**: every agent the factory's own definition names (agents/*.yaml, FACTORY.md, briefs) - id (lower-case,
  hyphens), display name, role, brief file relative to the folder, and the schedule ids that run it (enabled or
  disabled; a seat with no schedule gets `[]`). Never list an activity-row name that is not a seat (e.g.
  "Owner Session", "Certifier 19", trigger names).
- **ceoSeat**: the seat whose role is CEO (or the factory's own name for its head, e.g. CFO for Center Consulting
  only if the factory calls it its head - otherwise leave it out and say so). Two factories may have CEOs with the
  same display name; record them as they are.
- **goalFile / goalApprovedOn**: only when a GOAL.md exists in the folder (and its approved date when it states one).

Then write `manifests/README.md`: one line per factory with what you were sure of and what you guessed, and
validate each manifest's JSON (`python -m json.tool`). Do NOT run `cc-devthrottle factory register` - the Lead
registers them after the deploy. Do not change anything in any factory's folder or any schedule. When done:
`cc-devthrottle message send b496c54d "<one line>"`.
