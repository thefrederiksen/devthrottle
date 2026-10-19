# Mail Desk phase 2 - review of the template factory and its draft offer

Reviewed: branch `origin/mail-desk-p2-template` at commit 7814e6917, as the diff against `origin/mail-desk-phase-2`
(which is at the same commit as `origin/main`, 7a365544b). Reviewer seat opened by the phase 2 Tech Lead
(session 113). Date: 18 October 2026.

**Verdict: accepted. No finding that must change before merge.** Three notes below are for the Developer and Tech
Lead to weigh; none shows a harm that blocks the merge. The Developer's claim about the four failing tests is true, and I
proved it by running the tests with the screening check in place.

## 1. Scope

### What I read

- The whole diff (11 files, 961 lines added, 5 removed): `.gitattributes`, `CcDirector.Gateway.csproj`,
  `MailDesk/MailDeskTemplate.cs`, `MailDesk/MailDeskTemplateOffer.cs`, `MailDesk/Template/factory.yaml`,
  `MailDesk/Template/seats/mail-desk.yaml`, `Api/FactoryDefinitionEndpoints.cs`, `Factory/Definition/FactoryDefinitionStore.cs`,
  `Factory/Briefs/FactoryBriefPanelFold.cs`, and the two new test files.
- The unchanged code the change leans on: the store's `SaveDraft`, `Publish`, `Restore`, `PrepareAppend` (the stale
  rule and the session draft limit), `TooManyWaitingDrafts`; the endpoints' `Import`, `Create`, `SaveDraft`,
  `SaveChecked`, `TryCaller`, `KindOf`; `FactoryDefinitionCheck` (the check path reference and the script header
  check); `CheckScriptHeader`; `DefinitionPaths` limits; `FactoryDefinitionBriefs` (a seat brief derived from a
  definition version); `FactoryDefinitionTabFold`, `FactoryRecordsTabFold`, `FactoryPublisher`,
  `FactoryMemoryEndpoints`; every reader of `AuthorKind` in the Gateway, the web clients (`apps/`, `packages/`) and the
  command line tool (`tools/cc-devthrottle/src/factory_definition_ops.py`).
- The mission document sections 5.3, 5.5, 5.7, 5.8, 5.10, decisions D16, D17, D19, D20 and question 2; the Developer's
  brief; the phase 1 contract (sections 1, 6, 7); the repository `CLAUDE.md`.
- The screening check on its own branch, `origin/mail-desk-p2-screen` (commit 44a9af182): its path, size, header and
  line endings.

### What I ran (in my own detached worktree `../devthrottle-md2-review-b`, .NET 10 at `~/.dotnet/dotnet`,
`DOTNET_USE_POLLING_FILE_WATCHER=1`)

| Run | Command | Result |
|---|---|---|
| Mail Desk tests, the branch as pushed | `dotnet test --filter Category=MailDesk` | 19 passed, 4 failed |
| Mail Desk tests, with `checks/screen.py` copied in from `origin/mail-desk-p2-screen` | the same | 23 passed, 0 failed |
| Factory regression | `dotnet test --filter FullyQualifiedName~Tests.Factory` | 2540 passed, 1 failed |

The four failures on the branch as pushed are `Files_Embedded_HoldExactlyTheExpectedPaths`,
`Import_TemplateFolder_IsStoredAsVersionOneDraftWithNoErrors`, `Create_TemplateFolder_IsStoredAsANewFactorysFirstDraft`
and `Check_TemplateOnAnAccountWithoutDevlinux_GivesANoticeNotAnError`. Every one fails with the check's finding
"The start names the check 'checks/screen.py', which is not in this definition" (or, for the first, the missing path).
With the real `screen.py` from the screening branch placed in the template folder and the binary rebuilt, all 23 pass.
**The Developer's claim is true.** I removed the copied file afterwards; the worktree is clean.

The one factory regression failure is `DefinitionFormatDocumentTests.Document_NamesEveryKeyTheSchemasTake`, which says
`docs/factories/definition-format.md` does not name `branch, continueWhen, decision, detail, ends, repeats, runs,
steps, when`. That test reads only the format document and the definition schemas. Both are byte-for-byte identical
between `origin/main` and this branch (`git diff origin/main origin/mail-desk-p2-template -- docs/factories/definition-format.md
src/CcDirector.Gateway/Factory/Definition/Schemas` is empty), and none of the nine missing keys appears anywhere in this
branch's diff. So the failure is main's, not this change's. I did not build main to watch it fail there; the identical
inputs are the proof.

### What I could not reach

- The HTTP pipeline. The tests call the route handlers (`Import`, `Create`) directly with a `DefaultHttpContext`; the
  authentication middleware and the route mapping were not exercised. That is the same way the existing definition
  tests work.
- The wiring of the offer to the switch. There is none: `MailDeskTemplateOffer` has no production caller, and phase 1's
  `MailDeskSwitch` exists on neither `origin/main` nor `origin/mail-desk-phase-1` (that branch holds only the contract).
  The Developer's brief allowed stopping at the service in that case. The offer is dead code until phase 1 lands and one
  line calls it; the Tech Lead should hold that line as open work.
- A Windows build. The embedded resource names carry the building machine's directory separator; the `Replace('\\', '/')`
  in `MailDeskTemplate.Load` handles it, and `.gitattributes` pins line feeds, but I built on Linux only.
- The Cockpit rendering "Written by the Gateway (Mail Desk template)" in a browser. The fold is tested; the page is not.
- The screening check's own behaviour. Out of this review's scope; it has its own reviewer.

## 2. The questions the brief asked, answered

1. **Can the offer publish, overwrite an edited draft, or offer the same version twice? Two at once?**
   It cannot publish: it only calls `CheckAndSave`, which only calls `SaveDraft` (`published: false`), and the store's
   `Publish` and `Restore` keep `RequireAWriter`, which throws on the `gateway` kind (`Publish_GatewayAsPublisher_Throws`
   proves it). It cannot overwrite: versions are only ever appended, and the offer saves after the newest version it read,
   so an owner's edit that lands in between is a stale refusal, never a lost edit
   (`Offer_OwnerEditedTheOfferedDraft_IsNotOverwrittenOrOfferedAgain`, `Offer_NewerTemplateAfterTheOwnersEdit_GoesAboveItAndKeepsIt`).
   Two at once: both read the same newest version, the store's lock serialises the appends, the second is refused as
   stale and stores nothing; the next offer finds the first. The same template version is not offered twice by the
   Gateway (tests for the same version, and for an older version coming back after a rollback). See note A for the one
   edge I found.
2. **Does the `gateway` author kind leak?** I checked every reader. The store's `Publish` and `Restore` refuse it. The
   publisher record (`PublishedByKind`) is only ever set on a publish or a restore, so it can never hold `gateway`.
   `FactoryBriefPanelFold.Who` is the one fold that switches on a definition version's author kind, and it is the fold
   both the Definition tab (`FactoryDefinitionTabFold`, two places) and the seat brief panel use; it now says "the
   Gateway (Mail Desk template)". `FactoryRecordsTabFold.Who` switches on record kinds (journal, documents,
   attachments), which the offer never writes. The Cockpit reads the folded `whoText`, not the kind; the mobile app has no
   definition history screen; the command line tool prints the kind and id as words ("gateway Mail Desk template") with
   no branch on the value. The `AuthorKind` column allows 20 characters; `gateway` is 7. A seat brief derived from a
   gateway-authored definition version (`DefinedSeatBrief.AsBriefVersion`) carries the kind to the same fold. The session
   draft limit (`TooManyWaitingDrafts`) applies to sessions only, so the Gateway is not bounded by it; it is bounded
   instead by "one draft per template version", which is enough.
3. **Did extracting `CheckAndSave` change behaviour?** No. `SaveChecked` before: check; if errors, `CheckRefused`; else
   `SaveDraft(..., caller.Kind, caller.Id, note, nowUtc())`; then `Answer(write, ..., created: write.Ok ? created : null)`.
   After: `CheckAndSave` does the same check and the same save with the same arguments, and `SaveChecked` returns the
   same `CheckRefused` when `write` is null and the same `Answer` otherwise. Status codes and answers are unchanged, and
   the 2540 passing factory tests include every existing draft, import and create test.
4. **Is the template version stable?** The version is SHA-256 over the ordinal-sorted list of (path, SHA-256 of the
   UTF-8 text). Paths are normalised to forward slashes before hashing, so the building machine's separator does not
   enter it. The committed blobs hold no carriage return (I counted on both YAML files and on `screen.py` on its branch),
   `.gitattributes` now pins `text eol=lf` on the whole template folder so a Windows checkout cannot rewrite them, and a
   test fails on any carriage return or byte order mark. Embedded resource names are set with an explicit `LogicalName`,
   so they do not depend on the project's root namespace.
5. **Does the template pass the real check through the real path?** Yes, once the check file is present:
   `Import_TemplateFolder_...` and `Create_TemplateFolder_...` call the real `Import` and `Create` handlers, and the offer
   calls the same `CheckAndSave` the handlers call. I confirmed with the real `screen.py` (30,694 bytes, under the
   262,144-byte file limit; a PEP 723 header that opens with `# /// script` and closes with `# ///`, so the header check
   passes; no carriage returns).
6. **Does anything assume Outlook or one mailbox?** No. The two YAML files name no address, provider or mailbox (a test
   checks the address pattern on both). The seat brief says "email today; other channels later", tells the agent to read
   the channel list, and says outright never to assume Outlook or one mailbox. The default machine `devlinux` is a
   literal because the format requires one; mission question 2 chose it, and another account gets a notice, not an error
   (`Check_TemplateOnAnAccountWithoutDevlinux_GivesANoticeNotAnError`).
7. **Public repository: addresses.** No email address anywhere in the diff. Patterns are written `*@domain`.

**Contract check.** Factory `mail-desk`, check path `checks/screen.py`, seat `mail-desk` match the phase 1 contract's
`MailDeskStart` constants. The command lines in the seat brief match the contract's section 6 flag for flag: `allow add
<pattern> --what --do archive|keep-for-owner|route:<factory>/<seat> --why`, `block add <pattern> --why`,
`rule add <name> --subject-contains --from --route <factory>/<seat> --why`, `route add <factory>/<seat> --handles --why`,
`rules show [--version]`, `rules history`, `status`, `channel list`, `decisions [--since] [--layer] [--decision] [-n]`,
and `done <routed id> --note` and `return <routed id> --note` as the mission's 5.8 table has them. The brief omits
`decisions --channel <address>`; nothing breaks for that.

**Repository rules.** Entry, result and failure are logged on `Offer` and on the template load. There is no fallback: a
template that is not UTF-8, is missing, or cannot be read throws; a check refusal stores nothing and says why. Try-catch
sits at the two entry points only and rethrows. Tests are named `Method_Scenario_Result`, use a fixed clock, a SQLite
file database through the shared harness, no Docker, no port and no sleep.

## 3. Notes (no change required; the Developer decides)

**A. A template the owner imported himself is offered once more.** `VersionHoldingThisTemplate` compares the newest
version and then only versions the Gateway wrote. If a person imports the exact template files by hand (author kind
`person`), then saves an edit on top, the next offer finds the newest version is not the template and no gateway-authored
version holds it, and stores the same template again as a new draft. Reproduction: `_store.SaveDraft(A, "mail-desk",
Template(), 0, Person, "owner", null, Now)`, then a second person save with one byte changed, then `Offer().Offer(A)`:
the outcome is `Offered`, version 3, with files equal to version 1. The harm is one draft the owner did not ask for, and
only when he reproduces the template byte for byte without the offer having run; the fix, if wanted, is to compare every
version in the history rather than gateway-authored ones only. Below the bar for a finding.

**B. A lost race is reported as a defect.** Two offers at once end with the second refused as stale, and the code's own
summary calls `Refused` "a defect to look at". The log line is `Offer REFUSED by the store: ... outcome=Stale: the
definition of mail-desk is at version 1, not 0; read it again, merge, and save once more`, a sentence written for a
person editing. Harmless today, since nothing calls the offer; when phase 1 wires it to the switch, whoever reads the
log should know a `Stale` refusal from an offer is a race already resolved, not a defect. A distinct outcome for it would
make that plain. Below the bar for a finding.

**C. The offer is unwired, by the brief's own allowance.** Recorded under "could not reach" above so the Tech Lead's
record shows it as open work rather than as done.

## 4. Verdict

Accepted. The template matches the mission and the phase 1 contract, the offer does what design 5.3 says and provably
cannot publish or overwrite, the shared code changes keep every existing behaviour, and the four failing tests fail for
exactly the stated reason and pass with the screening check in place. Merge order matters: the screening branch's
`checks/screen.py` must be in the phase branch before this branch's tests go green there.
