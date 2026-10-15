# Factory Definition, phase 6 - the Records tab: QA report part

Date: 2026-10-15. Mission document: `docs/missions/factory-definition-2026-10-14/MISSION.md` (section 5.6 records,
5.7 the Cockpit; section 7, phase 6). This part covers **the Records tab**, beside Memory. Desktop only. It joins the
phase 6 `README.md` once the Definition tab's pull request and this one are both on main.

## How the pictures were made

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

## The Records tab, state by state

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

## What was built

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

## The check, as run

From this worktree on origin/main plus this change; the commands and their results are in the pull request.

New tests: `FactoryRecordsTabTests` (17: the empty tab's sentences, the journals' order, who wrote a record and that
an unknown author kind is a broken row, the whole tab through the mapped routes over real stores in memory, a missing
document or version answered in words, an HTML attachment answered as a download with nosniff, a calling session
refused 403 by each of the five routes, another account finding no such factory on four of them, and the session key
guard), `FactoryRecordsTab.test.tsx` (6) and `factoryRecordsClient.test.ts` (5).

## What this does NOT prove

- **No session wrote a record here**: no Director is connected to the scratch Gateway, so no session key exists. How a
  session's records read ("session" and its id, whole) is proven in `FactoryRecordsTabTests`, not in a picture.
- **Paging**: the pictures hold fewer rows than a page (20 journal entries, 100 attachments, 20 document versions). The
  page sizes and the "older versions" step are in the fold and its tests, not in a picture.
- **The phone.** Desktop only, as the mission says.
