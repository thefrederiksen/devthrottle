# Handoff - phase 1 fix: the inspection findings on pull request #2948

Read `STATE.md` (rulings 1-9 bind you; ruling 9 is new) and `INSPECTION-phase-1.md`. Fix all six findings on
`mission/dev-reports`, which is pull request #2948. Do not start phase 2.

1. **Findings 1 and 3 - replace the hand-written scanner (ruling 9).** Parse with AngleSharp and check the
   document a browser builds: markers inside `template` content do not count; a `script` anywhere in the live
   document or an inline event handler is refused; the summary and questions sections must not be `hidden`
   (attribute or inline `display:none`), and the summary must have words. Keep every existing test's intent
   and add the inspection's payloads as tests. Delete the scanner code the parser makes unnecessary. Say in
   the class comment that this check is guidance, not the security boundary.
2. **Finding 2 - radio groups.** The contract requires every option in a question to share one `name`, unique
   to that question; the shape check refuses anything else; the script reads the option the owner actually
   checked. Test with the inspection's two-name payload.
3. **Finding 4 - question text length.** Check the full question text before any truncation; over the limit,
   explain and do not queue, as the contract says. Test it.
4. **Finding 5 - private path.** Remove the absolute fallback; require `PLAYWRIGHT_PATH` or a normal module
   resolution, and say so in the README with a clear error when missing.
5. **Finding 6 - attribution.** Remove the tool family name from `PHASE-1-REPORT.md`; describe the reviewer as
   an independent reviewer from a different agent family.

Proof: revert each fix and watch its test fail, then restore. Web tests and the Gateway unit tests you touched
pass; `.\scripts\test-local.ps1` green. No separate review round - the Architect calls the re-inspection.
Update `PHASE-1-REPORT.md`, commit and push, then ONE line to the Architect (session 184d1571).
