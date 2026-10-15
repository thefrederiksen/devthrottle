# Factory Definition, phase 6 - the Cockpit: QA report

Date: 2026-10-15. Mission document: `docs/missions/factory-definition-2026-10-14/MISSION.md` (section 5.7 the Cockpit,
5.3 the store and publish, 5.4 export and import, 5.6 records; section 7, phase 6: desktop screenshots of every state).
Desktop only: the phone has no factory page.

Part 1 covers **the Definition tab**, which replaces the Documents tab; part 2 covers **the Records tab**, beside
Memory; part 3 covers **the seat page on a defined factory**, the two items the phase 4 QA report handed to phase 6.

## Part 1: the Definition tab

### How the pictures were made

`capture.py` beside this file, run in the foreground from the repository root after building the Gateway (Debug) and
the Cockpit:

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
(cd apps/cockpit && npm run build)
uv run --no-project --with playwright python docs/qa/factory-definition/phase-6/capture.py
```

It starts a Gateway built from this tree on port 7989 with its own empty data folder (factory area on, Tailscale off,
authentication on), registers two factories with the Gateway's own machine token (`qa-outreach`, which is given a
definition, and `qa-undefined`, which never is), enrolls one browser device, and drives the Cockpit that Gateway serves
in headless Chrome signed in with that device key. Every import, publish and restore in the pictures is the app's own
request; `answers.json` lists each write the app made and that every one carried the enrolled device key. The folders it
imports are the phase 1 example (`../phase-1/example/`) and two edits of it, zipped the way the operating system zips a
folder (every file under the folder's own name).

Playwright is used as a library, not browser-harness, because this is a repeatable script a reviewer re-runs to get the
same pictures; the browser is a headless one the script starts and stops.

### The Definition tab, state by state

| # | Picture | State |
|---|---|---|
| 1 | `01-no-definition.png` | A factory with **no definition**: the Gateway's sentence that it keeps working exactly as today, Export and Import, no Publish and no History. The export note lists the five things today's factory has that a folder cannot hold, each with its file. |
| 2 | `02-no-definition-overview-unchanged.png` | The same factory's Overview: unchanged by this phase. |
| 3 | `03-imported-first-draft.png` | **Import**: a zip of the phase 1 example imported as version 1, a draft. The Gateway's sentence ("Imported 6 files as version 1, a draft ... Nothing is published") and the check's one notice. **What changes** is a first publish: everything listed as added, each brief and file line by line, and what publishing would do to the live registry and schedules. |
| 4 | `04-publish-confirm-first.png` | The **publish confirm** of the first version: it becomes the definition; registry, schedules and triggers change in one step or nothing changes; what changes; and that from then on a direct edit is refused. |
| 5 | `05-published-version-1.png` | **Published**: the Gateway's sentence and the steps the publish transaction applied. |
| 6 | `06-draft-with-what-changes.png` | A **second draft** beside the published version: the writer renamed Author, its brief changed (one line removed, one added, in red and green) and the boss's start moved an hour, plus the two live steps a publish would take. |
| 7 | `07-publish-confirm-second.png` | Its publish confirm. |
| 8 | `08-published-version-2.png` | Version 2 published. |
| 9 | `09-publish-refused-with-findings.png` | **Publish refused with its findings**: version 3 starts the writer by a check, which saves but cannot be published before phase 5b. The confirm closes and the tab keeps the Gateway's sentence with each finding by file and line (`seats/writer.yaml:8`, error), and the notice. What changes already showed the same refusal in the live plan before Publish was pressed. |
| 10 | `10-history.png` | **History**: three versions, the published one marked, the draft carrying Publish, the older version carrying Restore, and Open and Export on every row. |
| 11 | `11-version-1-open.png` | One version **opened**: every file, folded, the first one open. |
| 12 | `12-restore-confirm.png` | The **restore confirm** of version 1: a copy becomes version 4 and is published, nothing is removed, and the draft above the published version is then older than it. |
| 13 | `13-restored-version-1-as-version-4.png` | **Restored**: version 4 published, "Restored from version 1.", and the steps applied (the rename and the moved start undone). |
| 14 | `14-exported.png` | **Export** of the published version, downloaded twice. |
| 15 | `15-import-refused-with-findings.png` | **An import the check refuses**: the writer's seat file has a key no seat file can have (`colour: red`). The tab keeps the Gateway's sentence and the finding by file and line; nothing is stored. |
| 16 | `16-import-for-another-factory-refused.png` | **An import of a folder for another factory**: its `factory.yaml` names `qa-elsewhere`, and the tab says to import it on that factory's page. Nothing is stored: the newest version is 4 before and after both refused imports (`answers.json`). |

The answers behind the pictures, from `answers.json`:

- The refused publish, as the Gateway put it on the wire: HTTP 422, outcome `CheckFailed`, error `Version 3 was not
  published: the check found 1 error in the definition of qa-outreach. Fix each one where it is and try again.`, and the
  finding `seats/writer.yaml` line 8, `This start has a check. Checks from a definition run from phase 5b of the factory
  definition work, so this version cannot be published yet; publish it without the start, or wait.`
- **Two exports of one version are the same bytes**: both zips of version 4 hash to
  `831f52b271a850cbf5243329771a7ed6da6954864299f3073f2a0751980bdf44` (six files: `factory.yaml`,
  `files/post-template.md`, `grants.yaml`, `rules/base.json`, `seats/boss.yaml`, `seats/writer.yaml`).
- The export of the factory with no definition holds `factory.yaml`, `seats/boss.yaml` and `seats/scout.yaml`.
- The Cockpit's development server, pointed at the scratch Gateway, passes `GET /factory-definitions/qa-outreach`
  through and answers exactly what the Gateway answers directly (the proxy line this phase adds).

### What was built

- **Gateway** (`FactoryDefinitionTabFold`, `FactoryDefinitionTabEndpoints`): the tab, a page of the history and one
  version, each composed on the Gateway with every sentence, state word, tone and confirm (rule 7); the export as a zip
  (files in path order, one fixed time on every entry, so the same files make the same bytes); the import of a zip, which
  goes through the SAME checked import the command line uses and answers with the Gateway's sentence. The tab's routes
  are the owner's: a session key is refused by `SessionKeyGuard`. The page's tabs now say Definition where they said
  Documents, and the old `DocumentsText` field is gone.
- **Publish and restore** go to the definition's own routes, `/factory-definitions/{factory}/publish` and `/restore`,
  with exactly the versions the Gateway's confirm named (`version`, `replaces`, and for a restore `expectedVersion`).
  Only a person may do either; that rule is the Gateway's, unchanged.
- **The publish confirm says the version goes live exactly as it is.** When drafts were saved between the published
  version and the one being published, it says so and that they are passed over and kept, for example "Version 5 goes
  live exactly as it is. Version 4 is passed over: it stays in the history, and History can restore it. What changes,
  above, is the whole difference between version 3 and version 5." It never says a version holds an earlier draft's
  change: an import replaces the whole definition, so it may not (review finding 1; proven in
  `FactoryDefinitionTabFoldTests`).
- **An import of a folder for another factory is refused on this factory's page**, naming both, and nothing is stored.
- **Cockpit**: `FactoryDefinitionTab.tsx`, `factoryDefinitionClient.ts`, the `/factory-definitions` line of the
  development proxy, and `/factories/<id>/documents` opening the Definition tab.

### The check, as run

From this worktree rebased onto origin/main `5c068319f`, with the review's rulings applied:

| Command | Result |
|---|---|
| `.\scripts\test-local.ps1` | GREEN, ten suites, every TRX `outcome=Completed` |
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Trigger"` | Passed 2,215, failed 0 |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed 28, failed 0 |
| `npm run typecheck` | Exit 0 |
| `npm test --workspace @devthrottle/client-core` | 169 files, 2,107 tests passed |
| `npm test --workspace @devthrottle/cockpit` | 113 files, 1,223 tests passed |

New tests: `FactoryDefinitionTabFoldTests` (13: every tab state, the history's buttons, one version, the import
sentence, the zip round trip and its refusals, and the publish confirm's passed-over drafts) and
`FactoryDefinitionTabRoutesThroughThePipelineTests` (12: the Cockpit's whole loop through the mapped routes, an import
for another factory refused, an import the check refuses with 422 and its findings, an import over the size cap, an
import whose files declare more than a version holds, five pages or versions that are not a whole number from 1, a
version that does not exist, and the session key guard on every route of the tab), `FactoryDefinitionTab.test.tsx` (8,
including a refused import kept on the tab and the Gateway's busy word on Export) and `factoryDefinitionClient.test.ts`
(5).

### What this does NOT prove

- **No real Director.** No machine is connected to the scratch Gateway, so every write carries the "No Director on
  SOREN_NORTH is connected now" notice, and the publish steps touch only the registry and the schedules.
- **No session.** The tab is the owner's; a session's refusal is proven by the guard test, not in a picture.
- **The phone.** Desktop only, as the mission says.
- **A zip made by hand with a different layout** than the two the script makes (the export's own, and a folder zipped
  under its name). Both are covered by tests; others are not.

## Part 2: the Records tab

### How the pictures were made

`capture_records.py` beside this file, run in the foreground from the repository root after building the Gateway (Debug)
and the Cockpit:

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
(cd apps/cockpit && npm run build)
uv run --no-project --with playwright python docs/qa/factory-definition/phase-6/capture_records.py
```

It starts a Gateway built from this tree on port 7990 with its own empty data folder (factory area on, Tailscale off,
authentication on), registers `qa-outreach` and `qa-empty` with the Gateway's own machine token, enrolls one browser
device, and writes qa-outreach's records through the Gateway's own record routes (`/factory-records/...`), the way they
are written for real: some as a person (the browser key) and some as the one-time upload (the machine token, naming its
machine `RIG_MACHINE` and an import key on every row). It then drives the Cockpit that Gateway serves in headless
Chrome, signed in with the browser key. `records-answers.json` holds the Gateway's answers the report quotes.

Playwright is used as a library, not browser-harness, because this is a repeatable script a reviewer re-runs to get the
same pictures; the browser is a headless one the script starts and stops.

### The Records tab, state by state

| # | Picture | State |
|---|---|---|
| 20 | `20-records-empty.png` | A factory with **nothing recorded**: each section says so in the Gateway's words and names the command a seat writes it with. |
| 21 | `21-records.png` | **Every kind of record**: three journals (the factory as a whole, then each seat in the registry's order) with how many entries and the newest day; two working documents as they stand, with History only on the one with more than one version; three attachments (text, a spreadsheet kept by the upload, an image), each with its seat, type, size and who kept it. |
| 22 | `22-journal-of-a-seat.png` | **One seat's journal**, newest day first: each entry with who wrote it, its text, and the attachment it names, downloadable from the entry. |
| 23 | `23-journal-written-by-the-upload.png` | The boss's journal: an entry **written by the one-time upload from RIG_MACHINE** beside one written by a person, with its attachment. |
| 24 | `24-journal-of-the-factory.png` | **The factory's own journal** (an entry no seat wrote). |
| 25 | `25-document-newest-version.png` | A **working document** opened at its newest version. |
| 26 | `26-document-history.png` | **Every version** of it: Newest and Earlier, who wrote each (the first by the upload), when, and its size. |
| 27 | `27-document-earlier-version.png` | **An earlier version** opened: "An earlier version; the newest is version 3." and its text. |
| 28 | `28-attachment-downloaded.png` | The **image downloaded** through the tab, saved as `shots-home-page.png` (its folder kept, a hyphen for the slash). The saved file is byte for byte the image that was kept (`records-answers.json`, `same_bytes_as_kept`). |

### What was built

- **Gateway** (`FactoryRecordsTabFold`, `FactoryRecordsTabEndpoints`, `FactoryJournalStore.Journals`): the tab, one
  journal, one document version and one page of a document's versions, each composed on the Gateway with every heading,
  count, empty sentence, author line and label (rule 7), and an attachment's bytes to save. The routes only read and are
  the owner's: a session key is refused by `SessionKeyGuard` (a session reads its factory's records through
  `/factory-records` with the command line). The page's tabs gain Records after Memory.
- **An attachment is always a download.** A session may keep any content type, `text/html` among them, and the
  Cockpit's origin carries the owner's cookie; the answer is `Content-Disposition: attachment` with
  `X-Content-Type-Options: nosniff`, so a kept page can never render in that origin. Both are asserted.
- A journal page reads every attachment its entries name in one query.
- **Cockpit**: `FactoryRecordsTab.tsx` and `factoryRecordsClient.ts`.

### The check, as run

From this worktree on origin/main plus this change; the commands and their results are in the pull request.

New tests: `FactoryRecordsTabTests` (17: the empty tab's sentences, the journals' order, who wrote a record and that
an unknown author kind is a broken row, the whole tab through the mapped routes over real stores in memory, a missing
document or version answered in words, an HTML attachment answered as a download with nosniff, a calling session
refused 403 by each of the five routes, another account finding no such factory on four of them, and the session key
guard), `FactoryRecordsTab.test.tsx` (6) and `factoryRecordsClient.test.ts` (5).

### What this does NOT prove

- **No session wrote a record here**: no Director is connected to the scratch Gateway, so no session key exists. How a
  session's records read ("session" and its id, whole) is proven in `FactoryRecordsTabTests`, not in a picture.
- **Paging**: the pictures hold fewer rows than a page (20 journal entries, 100 attachments, 20 document versions). The
  page sizes and the "older versions" step are in the fold and its tests, not in a picture.
- **The phone.** Desktop only, as the mission says.

## Part 3: the seat page on a defined factory

This part covers the two seat page items the phase 4 QA report handed to phase 6
(`docs/qa/factory-definition/phase-4/README.md`, findings 3 and 5).

### The two items

- **Finding 3: the seat page offered actions the Gateway refuses.** On a factory with a definition, every schedule
  offered Edit schedule and Take off this seat, and the Gateway refused both. Now the seat page fold offers neither on a
  defined factory, says why in the Gateway's words, and links to the Definition tab, where a start is changed. Pause
  stays: switching a schedule off is not defining it. A factory with no definition is unchanged.
- **Finding 5: a brief publish also publishes every draft change beneath it.** A brief is saved on the definition's
  newest version, so a person publishing a one-line brief edit also published a waiting rename. The publish confirm
  (in the brief's editor, and on each history row that carries Publish) now names every other change that goes live
  with it, composed on the Gateway from the definition's own comparison; the seat's own brief change is left out, it is
  what the person is looking at. When nothing else changed it says so. It names up to six and then says how many more,
  pointing at the Definition tab.

The review of #4036 found the same two defects one step away, and this part covers them too:

- **The Seats tab** (one crumb from the seat page) still offered Edit schedule on a defined factory. It now offers
  Pause or Resume only, and says why above the table, with the same link.
- **Bring back** on a defined factory's brief history publishes a copy of the WHOLE older definition version, and its
  confirm named nothing else and said "Nothing is removed". It now says "No version is removed" and names, in the
  restore's direction, every other change bringing that version back makes to the published one.
- A seat removed in the version going live is named, never counted as "only the brief changed"; a start is named with
  what changed in it (its prompt, check, secrets, overlap), as a seat or a grant is; and a history page reads the
  published version once, not once per row.

### How the pictures were made

`capture_seat.py` beside this file, run in the foreground from the repository root after building the Gateway (Debug)
and the Cockpit:

```
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj
(cd apps/cockpit && npm run build)
uv run --no-project --with playwright --with typer --with click --with rich --with requests python docs/qa/factory-definition/phase-6/capture_seat.py
```

It starts a Gateway built from this tree on port 7991 with its own empty data folder (factory area on, Tailscale off,
authentication on), registers `qa-outreach` and `qa-undefined` with the Gateway's machine token, and enrolls one browser
device. As that person it gives qa-undefined's scout a schedule with this tree's own command line, saves the phase 1
example as qa-outreach's definition version 1 and publishes it (which makes the boss's schedule from its start), and
saves version 2, in which the writer is renamed Author, as a draft. It then drives the Cockpit in headless Chrome,
signed in with the person's key. The brief publish confirm is cancelled: nothing is published from it, and the script
checks the published version is still 1 afterwards. For Bring back it then saves a boss brief edit over the waiting
rename (version 3) and publishes it, so version 1 lies below the published one; that confirm is cancelled too, and the
script checks version 3 is still the published one. `seat-answers.json` holds the Gateway's answers.

Playwright is used as a library, not browser-harness, because this is a repeatable script a reviewer re-runs to get the
same pictures; the browser is a headless one the script starts and stops.

### State by state

| # | Picture | State |
|---|---|---|
| 30 | `30-defined-seat-no-edit-no-take-off.png` | The defined factory's boss seat. How it starts: the Gateway's sentence ("QA Outreach has a definition, and this seat's schedules come from it. To change or remove one, change the seat's start in the definition's folder, import it on the Definition tab, and publish it."), **Open the Definition tab**, and the schedule with Pause only - no Edit schedule, no Take off this seat. |
| 37 | `37-seat-link-opens-the-definition-tab.png` | **Open the Definition tab** pressed on that seat page: the factory's Definition tab, selected, with version 1 published and the rename waiting as version 2. |
| 31 | `31-brief-publish-confirm-names-the-rename.png` | An edit of the boss's brief, Publish pressed: the confirm's last line is "It also puts live the other change saved in the definition since version 1 was published, not only this brief: Seat Author (writer) changed: name Writer -> Author." |
| 32 | `32-no-definition-seat-unchanged.png` | The scout seat of the factory with **no definition**: Edit schedule, Pause and Take off this seat, as before, and no definition sentence. |
| 38 | `38-defined-seats-tab-no-edit.png` | The defined factory's **Seats tab**: the boss's schedule with Pause only, and above the table "QA Outreach has a definition, and its seats' schedules come from it, so none is edited here. ..." with **Open the Definition tab**. |
| 39 | `39-bring-back-confirm-names-what-it-undoes.png` | Version 3 published (the boss's brief edit and the rename); **Bring back** on version 1 of the boss's brief: "No version is removed: version 3, which is published now, and every other version are kept." and the last line "It also brings back the rest of definition version 1, not only this brief, and so makes this other change to version 3, the published one: Seat Writer (writer) changed: name Author -> Writer." |

### What was built

- **Gateway**: `FactorySeatPageFold` takes Edit and Take off away on a defined factory and adds `DefinedText`,
  `DefinitionLabel` and `DefinitionHref` to the seat's starts. `FactoryBriefPanelFold.AlsoGoesLive` composes the
  publish confirm's line from `FactoryDefinitionChanges.Compare` of the published version and the version going live;
  the seat page's editor and history routes hand it to the editor and to each history row that carries Publish, and,
  in the restore's direction, to each row that carries Bring back. The published version is read once per request.
- **The Seats tab**: `FactoriesScreenFold.Seats` takes whether the factory is defined (the route asks the definition
  store); on a defined factory no schedule has an Edit, and the view carries `DefinedText`, `DefinitionLabel` and
  `DefinitionHref`.
- **Cockpit**: `FactorySeatView` and the Seats tab draw the sentence and the link; the confirm lines were already drawn
  verbatim.

### Tests

`FactorySeatPageFoldTests` (2 new: a defined factory offers no edit and no take-off and says where they change; a factory
with no definition is unchanged), `BriefPublishAlsoGoesLiveTests` (4: only this brief changed; another seat renamed and a
start moved, both named and the brief not; this seat's own other change named without its brief; eight changes, six
named and "2 more"), `FactoryBriefsThroughTheDefinitionTests` (2 new, through the mapped routes: the editor's and the
history row's confirm name the waiting rename word for word; with nothing else saved the editor says the brief is the
only change), and `FactorySeatView.test.tsx` (1 new).

For the review's findings: `BriefPublishAlsoGoesLiveTests` (4 more: this seat removed is named; a start whose time
stayed is named with its prompt change; a Bring back names the rename and the file it undoes; a Bring back that differs
only in the brief says so, and the moved-start test now asserts the whole start sentence), `FactoryBriefsThroughTheDefinitionTests` (2
more, through the mapped routes: the Seats tab of a defined factory has no Edit, keeps Pause and carries the sentence,
while before the definition the same schedule offered Edit; the Bring back confirm names what version 1 undoes and no
longer says "Nothing is removed"), and `FactoriesScreen.test.tsx` (1 new, and the plain Seats tab draws no sentence).

### What this does NOT prove

- **The history row's Publish confirm in a picture.** It is proven word for word through the routes, not photographed;
  its Bring back confirm is picture 39.
- **The Definition tab's own Restore confirm** still says "Nothing is removed" and does not list what a restore changes
  in the live definition. It is outside this pull request and is reported to the Delivery Lead as open.
- **The phone.** Desktop only, as the mission says.
