# Review - Factory Definition, phase 3: export and import (pull request #4007)

Reviewer: session "Factory Definition - Reviewer - phase 3 export and import" (26547a56), 2026-10-15.
Reviewed: the three commits of `factory-definition/p3-export-import` (`67c2f7cb3`, `97eefeaf7`, `0e1f17878`) against
`origin/main` at `21a049971`, which had not moved when this review was written. Nothing in the worktree was edited.
The one thing reproduced out of tree is finding 1 (a scratch program in this session's scratchpad folder against
YamlDotNet 16.2.1, the version `CcDirector.Core` references).

## Verdict

**Mergeable once finding 1 is fixed (one small change plus a test).** Findings 2 to 4 are for the Delivery Lead to
decide, not defects in the code as written; 5 to 7 are small and can be taken or left. The seven points the brief
asked me to look hard at are answered in the last section; none of them turned up a correctness defect in the
stored-version export, the import, the null-path refusal or the guard.

## Findings

### 1. The YAML writer leaves U+2028 and U+2029 raw, and YamlDotNet reads both as line breaks - LOW, fix before merge

`src/CcDirector.Gateway/Factory/Definition/DefinitionYamlWriter.cs:149` (a value holding one may still be written as a
literal block) and `:177-181` (a quoted value writes it raw).

The writer's contract is that `DefinitionDocument.ParseYaml` "reads back exactly the text it was given". It treats
every control character and U+FEFF as unsafe, but the two Unicode line separators are not control characters, so
they pass both tests. YamlDotNet 16.2.1, which the definition parser sits on, treats U+2028 and U+2029 as line breaks
(it follows YAML 1.1 there). Reproduced out of tree against that exact package version:

```
k: "a<U+2028>b"      -> SyntaxErrorException: While scanning a multi-line double-quoted scalar, found wrong indentation.
k: |\n  a<U+2028>b\n -> SemanticErrorException: While parsing a block mapping, did not find expected key.
(and the same for U+2029)
```

Failure scenario: a seat's published brief holds a U+2028 (text pasted from macOS, iOS Notes or Word carries them;
nothing on the brief path strips them). `factory export` of that factory with no definition writes
`seats/<seat>.yaml` with the raw character inside the `brief: |` block. The import's check then refuses the file as
"not valid YAML: ... did not find expected key" on a line where nothing is visibly wrong, and the owner has no way
to see what to fix. Worse, where the text after the separator happens to start with two spaces, the file parses and
the separator comes back as a line break: the imported brief differs from the published one with no report.

Fix: in `BlockChomping` and in `Quoted`, treat `' '` and `' '` exactly as U+FEFF is treated (never a
block; escaped as ` ` / ` ` in a quoted value). Two rows in `DefinitionYamlWriterTests` (one per form)
prove it. The stored-version export is untouched by this: a file holding such a character is exported as the bytes
stored.

### 2. `--factory` on import cannot change what is stored - DECISION for the Delivery Lead

`tools/cc-devthrottle/src/cli.py:4740`; `src/CcDirector.Gateway/Api/FactoryDefinitionEndpoints.cs:427-447`;
mission document section 5.4 (`docs/missions/factory-definition-2026-10-14/MISSION.md:205`).

The Developer's decision is sound and is stated plainly in the pull request and the QA report: import never rewrites
a file, so "import into a new id" is a folder whose `factory.yaml` names that id, and the check refuses a mismatch
on `factory.yaml` line 1 as a save does. The round-trip proof is honest about it: the folder imported and the export
after it are the same six files, and the first factory's export differs from the copy's in `factory.yaml` only.

What follows from that decision is that `--factory` has no effect on any successful import. Traced through the
route: when the two names agree the draft stored is the same with or without it; when they disagree it is refused
(422 from the check with the flag, or 403 from the caller gate for a session without it); when `factory.yaml` is
missing or names nothing, the check refuses anyway because `factory.yaml` is required. So the flag only ever picks
which name a refusal carries. The mission text `import <folder> [--factory id]` reads as a target id, which it is not.

Options, either one a mission-document edit: (a) drop `--factory` from the command, the route body and 5.4, since a
flag that never changes the outcome is the kind AXI calls a defect; or (b) keep it and write its real meaning into
5.4 ("the factory the folder is checked for; it must agree with factory.yaml"). I would take (a). Not a code defect.

### 3. An import answers about the content before it checks who is asking - LOW

`src/CcDirector.Gateway/Api/FactoryDefinitionEndpoints.cs:425` (the null-path 400) and `:427-443` (the "nothing says
which factory" 422, which carries the format check's findings) both run before `TryCaller` and `TryWriter` at
`:445-446`. The draft save checks the caller first (`:293-299`).

Failure scenario: a machine token, or a session of another factory, sends `{"content": {"factory.yaml": null}}` and
gets 400 instead of 403; sends a folder with no `factory.yaml` and gets 422 with the format findings on its own
content instead of 403. Nothing of the tenant's is read on either path, so nothing leaks; but "the same callers as a
save" is only true once the factory is known, and the test `A_SESSION_OF_ANOTHER_FACTORY_AND_A_MACHINE_TOKEN_CANNOT_IMPORT`
sends valid content, so it would not notice if that order changed further. The order is partly forced (the factory
has to be read from the content before a session can be matched to it). Accept as is, or at least refuse a machine
token before reading the content, since that needs no factory.

### 4. A schedule that becomes a start loses its name with no report - LOW, A8

`src/CcDirector.Gateway/Factory/Definition/FactoryDefinitionExport.cs:218` (`WhatAStartLeavesOut`).

Every other field of a schedule is either carried into the start (cron, time zone, prompt, preventOverlap) or
reported by sentence (machine, working folder, notification, kept-open session, work list, kind, switched off). The
schedule's `Name` is neither: a start has no name key. When phase 4 publishes the definition, the schedule the owner
named "QA Live boss morning" comes back under whatever name publish gives it, and nothing told him. Either one more
sentence in `WhatAStartLeavesOut` (it would appear on every start, which is noise), or a line in the mission that
start names are generated at publish. The Delivery Lead's call; I lean to the mission line.

For completeness, what else the registry holds that the folder does not and no sentence names: `RegisteredBy`,
`RegisteredAtUtc`, `ArchivedSchedules`, and a seat's `BriefFile` when its brief IS published. None of these decides
how the factory behaves, so I do not count them as dropped.

### 5. One reported sentence formats a date under the current culture - NIT

`src/CcDirector.Gateway/Factory/Definition/FactoryDefinitionExport.cs:148`: `{r.ArchivedAtUtc.Value:yyyy-MM-dd}` in
an interpolated string uses the thread's culture; under a culture whose default calendar is not Gregorian (th-TH,
ar-SA) the year differs. It is in a `reported` sentence, never in a file, so the folder's bytes are unaffected. Use
`CultureInfo.InvariantCulture`, as the check does for `goalApprovedOn`.

### 6. The wording of one report is wrong in one case - NIT

`FactoryDefinitionExport.cs:101`: a schedule the registry lists for a seat that exists on the Gateway but names a
different factory, or none, is reported as "no such schedule exists on the Gateway". It exists; it does not name
this factory. It is reported, so nothing is dropped; only the sentence misleads.

### 7. The command line's export write path - NITS

`tools/cc-devthrottle/src/factory_definition_ops.py:454`: a disk failure while writing (`OSError`) is not caught, so
it ends in a Python traceback rather than a sentence; `read_folder` catches the same on the read side. And the refusal
at `:421` says "nothing more was written" although every path is checked before the first write, so nothing was
written at all. Both are wording and polish.

## The seven points in the brief

1. **Determinism.** A stored version is exported as the bytes stored: the store refuses a text that cannot be UTF-8
   (a lone surrogate) and two paths that differ only in case, the route copies the map, JSON round-trips the strings
   losslessly, and the command line writes each as binary UTF-8 with no byte-order mark, paths in ordinal order,
   so the folder is the same on Windows, macOS and Linux. Nothing dated or clock-driven enters a file. The live
   export is a pure function over its parts, ordered by ordinal id, and the test proves the same parts in any order
   give the same bytes; its only culture-sensitive formatting is finding 5, in a sentence. The transcript shows
   both exports hashing equal. Confirmed, with finding 1 as the one exactness gap in the writer.
2. **Same check, never publishes.** `SaveChecked` is one private method serving `PUT .../draft` and
   `POST .../import`; the test asserts the import's findings equal the save's for the same broken folder. The only
   write it reaches is `store.SaveDraft`, which appends with `published: false`; `Publish` and `Restore` are not
   reachable from it. Confirmed; no path publishes.
3. **A8, nothing dropped.** The registry entry, published briefs, schedules and triggers are read in
   `GatewayHost.FactoryLivePartsOf`; what a definition cannot hold is reported with its file, and the unit test
   names fourteen kinds. The one unreported loss is finding 4. The real factories' boss seats all carry the role
   word `Boss` (phase 0 inventory), so the live `factory.yaml` passes the boss-seat rule. Briefs set from a file
   are read with Python's universal newlines, so stored briefs hold LF and export as readable literal blocks.
4. **`--factory`.** It exists; it does not do what the mission's phrase suggests, and cannot, given the sound
   decision that import never rewrites a file. Finding 2. The QA report is honest about what the round trip shows.
5. **Null path.** Refused with 400 and the path in both routes that take content, before the check runs, and the
   store refuses the same if reached directly. Publish and restore read stored content, which is never null.
   Proven by unit tests on both handlers, a pipeline test with the exact body review 2 sent, and the transcript.
6. **Rule 7.** The Gateway composes the export summary, every reported sentence and every finding; the command line
   prints them. Its own sentences ("Imported: version N ... is a draft", "This import created ...", "Nothing was
   published: ...") render Gateway fields (`state`, `created`, `publishedVersion`) and decide nothing. The one local
   rule, refusing a non-empty folder, is about the local disk, which the Gateway cannot see. Confirmed.
7. **Tests.** Each refusal added here has a test that fails without it: the two null-path tests would meet the
   check's exception if the route check were removed; the guard rows for the three routes and their wrong verbs run
   through the pipeline test that holds the mapped routes and the guard to one list; the command line's four
   refusals each have a test. The client-core file that failed once and passed twice: this branch changes no
   TypeScript and nothing the client reads (the DTO additions are new JSON fields the Cockpit will first read in
   phase 6), so it is not this change's.
