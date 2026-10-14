# Factory Seats, phase 2b - the brief panel on the seat page: the proof

Date: 2026-10-13. Mission document: `docs/missions/factory-seats-2026-10-13/MISSION.md` (issue #3950).
Branch: `factory-seats/phase-2b-brief-panel`, cut from `origin/main` at `4ebd3e9c2` (phase 2a, pull request 3964).
`origin/main` had not moved when the check below was run; the merge of it said "Already up to date".

## What was built

1. **"What this seat does" reads the brief on the Gateway.**
   - A seat with a published brief shows the text, its version, who published it and when, with Edit and History.
   - A seat with drafts and nothing published says so, still shows where its brief file is, and offers "Open the
     newest draft".
   - A seat with no brief on the Gateway shows what phase 1 showed - the file's path and computer - and adds "Write
     the brief here".
2. **Edit.** The editor starts from the seat's newest version and says which that is: the published brief, or the
   newest draft "written by ... It is not the published brief". It has Save draft and Publish.
   - A save states the version the text was started from. When that is no longer the newest, the Gateway refuses
     with the newer version; the panel shows that version beside the owner's own text, which stays in the box.
     Nothing more is sent until the owner chooses "Save my text after the newer version" or "Take the newer version
     and drop my text".
   - Publish asks first. Its confirm says "Every later talk and every later run of <seat> will be given this text."
     A changed text is saved as a new version first and that version is published.
3. **History.** One page of 20 versions at a time, newest first: each version's state, who wrote it, when and its
   size. Open reads one version's text. Publish is on a draft above the published version. Bring back is on every
   version below the published one, behind a confirm, and sends the newest version the owner was looking at.
4. **Twenty waiting drafts** are said in one Gateway sentence on the panel, in the editor and in the history.
5. **The development proxy** in `apps/cockpit/vite.config.ts` fronts `/factory-briefs`.

**The Gateway composes the words; the Cockpit draws them.** Every sentence, label and confirm is folded in
`FactoryBriefPanelFold` and reaches the Cockpit through the seat page and three new owner-only reads:

| Route | Answers |
|---|---|
| `GET /gateway/factories/{factory}/seats/{seat}` | the seat page; its `brief` now says how the brief stands |
| `GET /gateway/factories/{factory}/seats/{seat}/brief/editor` | what the editor starts from, and every label of the editor |
| `GET /gateway/factories/{factory}/seats/{seat}/brief/history?before=N` | one page of the history, with each row's buttons and confirms |
| `GET /gateway/factories/{factory}/seats/{seat}/brief/versions/{version}` | one version, with its text |

Saving, publishing and bringing back go to phase 2a's own routes under `/factory-briefs/{factory}/{seat}`, unchanged.
No phase 2a file was changed.

## The check, as run (mission document, section 7)

Run on devlinux, in the foreground, one after another, on the branch as pushed.

| Command | Result |
|---|---|
| `dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~Factory\|FullyQualifiedName~Cron\|FullyQualifiedName~Schedule"` | First run: passed 1404, failed 1. Second run: **passed 1405, failed 0**. See below |
| `dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~Factory"` | Passed: 28, failed: 0, skipped: 0 |
| `npm run typecheck` | Exit 0, all four workspaces |
| `npm test --workspace @devthrottle/client-core` | 167 files, 2068 tests passed |
| `npm test --workspace @devthrottle/cockpit` | 111 files, 1185 tests passed |
| `python -m pytest tools/cc-devthrottle/tests -q -k "factory or schedule"` | 933 passed, 1 skipped |

**The one failure of the first run is not this phase's, and it is a flaky test.**
`DirectorTriggerRunnerTests.RunCheckAsync_ACommandThatPrintsACount_ReportsExitZeroAndTheOutput` starts a real
process. It failed once in the whole run, passed twice when its class was run alone (9 of 9 both times), and passed
in the second whole run. This branch changes no file under `Factory/Triggers`. By the repository's test standard it
should be fixed or deleted; that is not done here, because it is outside this task.

The machine needed what phase 2a's proof lists: the version 10 .NET kit first on the path, and
`DOTNET_hostBuilder__reloadConfigOnChange=false` with `DOTNET_USE_POLLING_FILE_WATCHER=1` for `dotnet test`.

### This phase's own tests

| Where | Count | What it holds down |
|---|---|---|
| `Gateway.UnitTests/Factory/Briefs/FactoryBriefPanelFoldTests.cs` | 29 | the panel for a seat with no brief, with drafts only, with a published brief, with a newer draft; the waiting sentence at 19, 20 and 21, and that it is said exactly when the real store refuses a session's draft; the editor's four starting sentences; each history row's words; that every button the history offers is one the real store accepts, and that it offers no Publish where the store refuses one; the publish and bring back confirms, sentence by sentence; an older page; an empty history; one version; sizes; times in the account's zone |
| `Gateway.UnitTests/Factory/FactorySeatRoutesTests.cs` | 21 new | over real HTTP with the real brief store and the brief's own routes mapped: **no brief**; **written, saved, published, a second version, the first brought back, by a person**; **the stale save** - 409, the newer version in the answer, nothing overwritten; **a refused publish**; a session's publish refused; **a 45 version history read in three pages**; a bad starting version refused in words; a missing version; **twenty waiting drafts** said in all three places and a person's publish making room; the views refused to a session key; an unknown seat |
| `apps/cockpit/src/factory/FactorySeatBrief.test.tsx` | 30 | the three states of the panel; a first brief saved expecting version 0; **a stale save**: both texts on screen, the owner's kept, nothing sent again, then saved after the newer version, or the newer one taken, or a third writer shown again, or met while publishing; Publish asks first, saves a changed text first and publishes that version; **a refused publish** stays in the confirm; a refusal after the save does not save twice; the history's words and buttons; **paging** asks for exactly the page the Gateway named; Open; Bring back sends the version and the newest version seen; a refused bring back; **the twenty waiting drafts** |
| `packages/client-core/src/factory/factoryBriefClient.test.ts` | 10 | the addresses, escaped; a first draft states version 0; a stale save thrown with the Gateway's sentence, outcome and newer version; a web page instead of data is refused |

## The pictures

Taken by `capture.py` in this folder against a throwaway Gateway built from this branch, at 1280 and at 375 pixels
wide. The flow writes versions, so each width works on a seat of its own: the Scout at 1280 (`-desktop`), the Writer
at 375 (`-phone`). Every Gateway answer quoted below is in `answers.json`, as the Gateway sent it.

### How a person's request was authenticated - exactly

A person's publish had never been exercised end to end, so this is stated in full.

- To this Gateway, **a person is a request made with a device key whose device type is a browser or a phone**
  (`FactoryBriefEndpoints.TryCaller`). The Gateway's own machine token is not a person.
- The scratch Gateway ran with authentication on. The script enrolled one device the way the Gateway stores a device
  it enrolled itself: one row in the scratch Gateway's own `device_credentials` table, device id
  `qa-cockpit-browser`, device type `browser`, holding the SHA-256 of a key the script made up for that run.
  **That row was written into the database directly, not through a sign-in.** The sign-in and enrollment routes were
  not exercised.
- The browser held that key and nothing else: as its `cc-gateway-token` cookie and as the key the Cockpit sends.
- Every save, publish and bring back in the pictures is the Cockpit's own request to
  `/factory-briefs/brief-demo/<seat>/draft`, `/publish` or `/restore` with `Authorization: Bearer <that device
  key>`. The script watched the browser's requests and stops the run if one write carried anything else:
  `00-writes-the-app-made-desktop` and `-phone` in `answers.json` list all nine writes of each pass.
- The Gateway recorded the author it derived from that key: `"authorKind": "person"`,
  `"publishedBy": "device browser qa-cockpit-browser"`.
- The same routes refuse the machine token (below), so the person's key is what was let through.

### The flow

| Step | Picture | What the Gateway answered |
|---|---|---|
| A seat with no brief on the Gateway | `01-no-brief-on-the-gateway` | `GET /factory-briefs/brief-demo/scout` is 404: `'scout' of brief-demo has no brief on the Gateway` |
| Writing the first brief | `02-writing-the-first-brief` | |
| Saved as a draft: drafts and nothing published | `03-a-draft-and-nothing-published` | the published brief is still 404: `'scout' of brief-demo has no published brief yet: its newest version, 1, is a draft` |
| Publish asks first | `04-publish-confirm` | |
| Published | `05-published-version-1` | 200, `version` 1, `state` `published`, `publishedBy` `device browser qa-cockpit-browser` |
| A second version published | `06-second-version-published` | 200, `version` 2, with the second text |
| History, version 1 opened | `07-history-with-version-1-open` | |
| Bring back asks first | `08-bring-back-confirm` | |
| The first brought back | `09-first-version-brought-back`, `10-the-brief-after-bring-back` | 200, `version` 3, the first text; the history holds versions 3, 2 and 1, all kept |

### The failure cases

A refusal is proven by the answer that carries it.

| Case | Picture | Status | The answer's `error` |
|---|---|---|---|
| A save with the Gateway's machine token | none: no screen can send it | 403 | `a seat's brief is written by a person, or by a session of its factory` |
| A publish with the machine token | none | 403 | the same sentence |
| A publish of the version already published | `11-refused-publish-already-published` | 409 | `version 3 is already the published brief of 'scout'`, `outcome` `AlreadyPublished` |
| A publish of a version that was replaced | none | 409 | `version 1 was published before, and version 3 has replaced it. To use its text again, bring version 1 back: that publishes a copy of it as the newest version` |
| A stale save | `12-stale-save-newer-version-beside-yours` | 409 | `the brief of 'scout' is at version 4, not 3; read it again, merge, and save once more`, `outcome` `Stale`, the whole of version 4 under `current` |
| Then saved after the newer version | `13-saved-after-the-newer-version` | 200 | version 5 holds the owner's text; version 4 is unchanged |
| Twenty drafts waiting | `14-twenty-drafts-waiting`, `15-history-newest-page-twenty-waiting` | 200 | `waitingText`: `20 drafts are waiting that nobody has published (versions 4 to 23). A session of this factory is refused another draft while 20 or more are waiting. Publish one to make room. Every version is kept.` |
| A history longer than one page | `15-...`, `16-history-older-page` | 200 | `Versions 23 to 4 of 23.` with `nextBefore` 4, then `Versions 3 to 1 of 23.` with no older page |
| A page starting below version 0 | none | 400 | `before is the version a page of the history starts below: a whole number from 1, not '0'` |
| A person's publish makes room | `17-published-from-the-history-nothing-waits` | 200 | `waitingText` is null, version 23 is published |

In the stale save the script checks, and stops if not, that the owner's text is still in the box and that what the
Gateway stores is still the other writer's version 4.

**The development proxy.** The script starts the Cockpit's development server pointed at the scratch Gateway and
reads `GET /factory-briefs/brief-demo/scout` through it: 200, and the same body the Gateway answers directly
(`18-development-proxy`).

## Decisions made inside the task, for the Implementation Lead to keep or replace

1. **Three new read routes for the owner's screen**, beside the seat page, so the Gateway composes the editor's and
   the history's words. The alternative was to have the Cockpit word phase 2a's raw answers, which rule 7 forbids.
2. **The editor starts from the newest version, not always the published one.** When a newer draft exists it starts
   from that draft and says so. A save must state the newest version anyway, and starting from the published text
   would quietly drop what a session proposed.
3. **A published brief hides the file's path.** With drafts only, the path stays, because that seat is still told
   its file.
4. **The phase 1 sentence is kept word for word** for a seat with no brief ("...so it cannot be read or changed
   here"), beside the new button "Write the brief here". The two read slightly against each other.
5. **Bring back is offered on every version below the published one, drafts included.** The task says "an earlier
   published version". The store refuses to publish an older draft and its refusal says to bring it back, so
   without the button such a draft could not be used at all. It is not offered on a waiting draft; Publish is.
6. **After Save draft the editor closes** and the page says what was saved. To go on, the owner opens the draft.
7. **Publish with an unchanged text publishes the version the editor started from.** If that is already the
   published one the Gateway refuses and the confirm shows its sentence; the button is not hidden beforehand.
8. **The refused save shows the newer version's text, and not who wrote it or when.** The Gateway's refusal names
   the version; the history says who and when.
9. **The waiting count is the newest version minus the published version**, not a second query. It is the same
   number because no version is ever removed; a test runs it against the real store's refusal.
10. **A history page on the screen holds 20 versions.** Who wrote a version is shown as the Gateway recorded it
    (`session <id>`, `a person (device browser <id>)`), never shortened and never resolved to a name.
11. **`FactorySeatBriefDto`'s file fields became nullable** and `FactorySeatPageFold.Page` takes the brief's
    standing as a required argument.

## What this does NOT prove

- **No session took part in the pictures.** The scratch Gateway has no Director, so no session and no session key.
  The "other writer" of the stale save and the twenty waiting drafts were written with the person's own device key.
  That a session is refused a publish and a twenty-first draft is proven by tests (`FactorySeatRoutesTests`, and
  phase 2a's), not shown on a running Gateway.
- **The person's device was enrolled by a row written into the database**, as said above. A real sign-in was not
  used, and no request went through the hosted Gateway.
- **The confirm's promise is not true until phase 2c.** "Every later talk and every later run will be given this
  text" describes the mission's design. Nothing in this phase, or on main, gives a talk or a run the brief yet.
- **Two people editing at once were not run.** The stale save is one browser and one direct request.
- **Nothing ran against PostgreSQL.** This phase adds no table and no query of its own, but the history page it
  reads was run only on SQLite, as phase 2a's proof already says.
- **The whole Gateway unit suite and the parked suites were not run**, only the check's filter.
- **The flaky trigger test above was not fixed.**
- **The mobile app has no brief panel.** The seat page is a Cockpit page; the phone pictures are the Cockpit at
  375 pixels.
- **No picture shows a brief near the 65,536 byte limit**, or a very long line in the two side by side texts.
