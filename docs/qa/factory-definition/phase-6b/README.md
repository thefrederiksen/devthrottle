# Factory Definition, phase 6b: New factory on the Factories screen

Date: 2026-10-16. Mission: `docs/missions/factory-definition-2026-10-14/MISSION.md`, sections 5.3, 5.4 and 5.7.
Brief: `BRIEF-phase6b-new-factory.md`, with the mission's private briefs.

Phase 7 found that a factory that does not exist yet could not be started from the Cockpit: it had no page, so no
Definition tab to import on, and the Factories screen had no import of its own (finding 2 of
`../phase-7/eric-factory.md`). This closes that.

## What was built

- **New factory**, on the Factories screen. The owner picks a zip of a definition folder. The Gateway
  (`POST /gateway/factories/new`) unzips it with the Definition tab's own unzip (`TryUnzip`, its caps and its
  refusals) and imports it through the same checked import (`FactoryDefinitionEndpoints.Import`), as version 1, a
  draft, of the factory its `factory.yaml` names. Then the Cockpit opens that factory's Definition tab, at the address
  the Gateway's answer names. Person only: the route takes the owner's gate, and `SessionKeyGuard` lets no session post
  to it.
- **Refusals**, each a sentence from the Gateway (`FactoryNewFold`), shown under the button:
  - the id is a live factory: "... that factory already exists: Eric. Open it and import there, on its Definition
    tab ...";
  - the id is an archived factory: "... is archived under that id. A new factory needs a new id: change factory: in
    factory.yaml. Or Restore it under Show archived and import on its page ...";
  - the id already has a draft that was never published: "... Open it under Not published yet and import there ...";
  - the check finds errors: the import's own 422, with every finding by file and line.
- **A factory that is not published yet is visible.** It is a row under the list, headed "Not published yet"
  ("Eric - draft, not published yet"), which opens its page on the Definition tab. Its page has the usual header and
  tabs. Its status reads DRAFT, and it has no Talk and no Archive. Every tab but Definition shows one sentence from the
  Gateway: "This starts when Eric is published. Until then nothing runs; its draft is on the Definition tab." The
  Definition tab's overview, what changes, Publish, History and Export work as they do for any factory. The first
  publish registers the factory and the row goes.
- After a publish, restore or import, the tab now has the page read itself again, so the header stops saying DRAFT
  (found in this proof's first run, picture 06).
- The list's empty sentence now points at New factory, not at `cc-devthrottle factory register`.

Rule 7: the Cockpit renders `notYetText` when the Gateway sends it. Every word, and which tabs carry it, is decided
in `FactoryNewFold`.

## Proof, on a throwaway Gateway, in the Cockpit

`rig/run_qa.py` beside this note, run in the foreground from the repository root after building the Gateway (Debug)
and the Cockpit:

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
(cd apps/cockpit && npm run build)
uv run --no-project --with "requests>=2.31.0" --with playwright python docs/qa/factory-definition/phase-6b/rig/run_qa.py <eric zip> docs/qa/factory-definition/phase-6b/eric
```

It reuses the phase 5b rig: a Gateway of this tree on port 7991 with its own empty storage root, Tailscale off,
authentication on, serving this tree's Cockpit. The person is an enrolled browser device, and headless Chrome is
signed in as that person. No Director was started, so no scheduled task was read or changed. A publish of Eric's
factory asks no machine anything (it has no secret and no repository), and "No Director on SOREN_NORTH is connected
now" is a notice, not a refusal. The zip is `phase7/eric-factory.zip` from the private briefs; only its size and hash
are in the transcript.

| Step | Picture | Result |
|---|---|---|
| A blank Factories screen | `eric/01-blank-factories-screen.png` | New factory, no factory, no draft row |
| New factory with the zip | `eric/02-definition-tab-draft.png` | opens `/factories/eric/definition`: "Nothing is published yet. Version 1, a draft, is the only version.", what changes ("First publish: 5 factory settings set, 1 seat added, 1 start created, 1 file added."), Publish version 1 |
| The draft row | `eric/03-factories-screen-draft-row.png` | "Eric - draft, not published yet" under "Not published yet" |
| Its other tabs | `eric/04-draft-page-overview.png` | the Overview and the Seats tab each show the sentence, with no error (the Cockpit test proves the Seats tab is then never read) |
| New factory again | `eric/05-new-factory-refused-draft-exists.png` | refused: Eric already has a draft that is not published yet |
| Publish, through its confirm | `eric/06-definition-tab-published.png` | "Version 1 is published.", "Registry: eric registered from its definition, in no folder", "Schedule ... (Eric - Mail Desk) created for seat mail-desk: Daily 08:00 America/Toronto."; the header reads RUNNING |
| The list after the publish | `eric/07-factories-screen-published.png` | Eric on the list, running; the draft row gone |
| Its Seats tab | `eric/08-seats-tab-schedule.png` | Mail Desk, Daily 08:00 |
| New factory once more | `eric/09-new-factory-refused-exists.png` | refused: that factory already exists, open it and import there |

The transcript is `eric/transcript.txt`: the list's answer before and after, every check the rig made (15, all
PASS), the Publish confirm as worded, and the factory's schedules.

## Tests

- `FactoryNewThroughThePipelineTests` (Gateway unit tests, in memory, real stores over SQLite): the owner's loop
  from a blank screen to the published factory, with the draft row appearing and then going. Plus each refusal (live,
  archived, already drafted, a spelling that folds to a registered id, a folder that names no factory, the check's findings, not a zip), a session refused by the route itself and by the guard, a registered factory with only drafts that is never a
  draft row. With the refusal check switched off, the three id refusals fail (3 red, 5
  green), so each test fails if its refusal is removed.
- `FactoryNew.test.tsx` (Cockpit): New factory sends the zip to the Gateway's address and opens the page it names; a
  refusal stays with its findings; the draft rows; a tab with a "not yet" sentence shows it and reads nothing.
- `FactoryDefinitionTab.test.tsx`: a publish has the page read itself again.

## What this does NOT prove

- The hosted Gateway and the owner's own browser: everything ran on the throwaway Gateway.
- A start firing: phase 7 proved that on the same factory. Here the rig only checks that the schedule exists.
- The phone: it has no factory page (section 5.7).
