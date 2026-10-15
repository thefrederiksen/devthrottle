# Factory Definition, phase 6 - the seat page on a defined factory: QA report part

Date: 2026-10-15. Mission document: `docs/missions/factory-definition-2026-10-14/MISSION.md` (section 5.7 the Cockpit;
section 7, phase 6). This part covers the two seat page items the phase 4 QA report handed to phase 6
(`docs/qa/factory-definition/phase-4/README.md`, findings 3 and 5). Desktop only. It joins the phase 6 `README.md` once
the other phase 6 pull requests are on main too.

## The two items

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

## How the pictures were made

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
checks the published version is still 1 afterwards. `seat-answers.json` holds the Gateway's answers.

Playwright is used as a library, not browser-harness, because this is a repeatable script a reviewer re-runs to get the
same pictures; the browser is a headless one the script starts and stops.

## State by state

| # | Picture | State |
|---|---|---|
| 30 | `30-defined-seat-no-edit-no-take-off.png` | The defined factory's boss seat. How it starts: the Gateway's sentence ("QA Outreach has a definition, and this seat's schedules come from it. To change or remove one, change the seat's start in the definition's folder, import it on the Definition tab, and publish it."), **Open the Definition tab**, and the schedule with Pause only - no Edit schedule, no Take off this seat. |
| 31 | `31-brief-publish-confirm-names-the-rename.png` | An edit of the boss's brief, Publish pressed: the confirm's last line is "It also puts live the other change saved in the definition since version 1 was published, not only this brief: Seat Author (writer) changed: name Writer -> Author." |
| 32 | `32-no-definition-seat-unchanged.png` | The scout seat of the factory with **no definition**: Edit schedule, Pause and Take off this seat, as before, and no definition sentence. |

## What was built

- **Gateway**: `FactorySeatPageFold` takes Edit and Take off away on a defined factory and adds `DefinedText`,
  `DefinitionLabel` and `DefinitionHref` to the seat's starts. `FactoryBriefPanelFold.AlsoGoesLive` composes the
  publish confirm's line from `FactoryDefinitionChanges.Compare` of the published version and the version going live;
  the seat page's editor and history routes hand it to the editor and to each history row that carries Publish.
- **Cockpit**: `FactorySeatView` draws the sentence and the link; the confirm lines were already drawn verbatim.

## Tests

`FactorySeatPageFoldTests` (2 new: a defined factory offers no edit and no take-off and says where they change; a factory
with no definition is unchanged), `BriefPublishAlsoGoesLiveTests` (4: only this brief changed; another seat renamed and a
start moved, both named and the brief not; this seat's own other change named without its brief; eight changes, six
named and "2 more"), `FactoryBriefsThroughTheDefinitionTests` (2 new, through the mapped routes: the editor's and the
history row's confirm name the waiting rename word for word; with nothing else saved the editor says the brief is the
only change), and `FactorySeatView.test.tsx` (1 new).

## What this does NOT prove

- **The Definition tab link's target.** The Definition tab is in its own pull request (#4028). Until that is on main,
  `/factories/<id>/definition` opens the factory's Overview.
- **The history row's confirm in a picture.** It is proven word for word through the routes, not photographed.
- **The phone.** Desktop only, as the mission says.
