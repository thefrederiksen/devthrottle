# Answers to the review of pull request 4007 (Factory Definition, phase 3)

The review is `review-export-import.md` beside this file. The Delivery Lead ruled on each finding; every one was
taken.

1. **U+2028 and U+2029 in the YAML writer - FIXED.** `DefinitionYamlWriter.IsUnsafeRaw` now treats both line
   separators as it treats a control character and the byte-order mark: never written in a literal block, escaped as
   ` ` and ` ` in a quoted value. Two rows in `DefinitionYamlWriterTests.Values` (one inline, one in a
   value of several lines) run through all three positions - after a key, as a list item, as the first key of a list
   item - and are read back exactly by the definition's own parser.
2. **`--factory` on import - DROPPED (option a).** Gone from `cc-devthrottle factory import`, its entry in
   `cc-devthrottle actions`, the route body (`ImportFactoryDefinitionRequest.Factory`), `docs/cli-reference.md` and
   mission section 5.4. The factory is the one `factory.yaml` names. The handler test for a mismatching name went
   with it; `test_import_Factory_IsNotAnOption_BecauseFactoryYamlNamesTheFactory` proves the flag is refused and
   nothing is called. The rig's mismatch case is now a session of `qa-outreach` importing a folder whose
   `factory.yaml` names `qa-outreach-copy`, refused by the caller gate.
3. **A machine token answered about its content - FIXED as ruled.** The import refuses a caller that can never write
   (the machine token, a Director) before it reads the content; that needs no factory. The rest of the order stands:
   a session is matched to the factory once `factory.yaml` has named it. `A_MACHINE_TOKEN_IS_REFUSED_BEFORE_ITS_CONTENT_IS_READ`
   sends a null path and a folder with no `factory.yaml` as a machine token and gets 403 for both.
4. **A schedule's name - WRITTEN INTO THE MISSION.** Mission section 5.4 now says a start has no name: a schedule
   that becomes a start does not carry its old name, and the name is generated at publish. No sentence on every start.
5. **The archive date's culture - FIXED.** Formatted with `CultureInfo.InvariantCulture`.
6. **"No such schedule exists" - FIXED.** The sentence now reads "no schedule of this factory has that id on the
   Gateway", which is true whether the id does not exist or belongs elsewhere.
7. **The command line's write path - FIXED.** A disk failure while writing is a sentence naming the file, how many
   were written before it and that the folder is incomplete (`test_export_ADiskFailure_IsASentenceNotATraceback`);
   the out-of-folder refusal now says "nothing was written", which is true because every path is checked first.
