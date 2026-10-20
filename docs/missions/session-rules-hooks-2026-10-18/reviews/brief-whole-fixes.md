Read `brief-template.md` beside this file and follow it. The work to review is two commits on
`mission/session-rules-hooks-p1`, the answers to the whole-phase review (`phase-1-whole-review.md` and
`phase-1-whole-answers.md` beside this file):

1. `bdcf94e39` - the rule evaluator registered in exec form (`command` the absolute path, `args` empty, no
   shell), and the 4000-character subject limit shared between the evaluator and the Gateway as one linked
   source file. Evidence of the live check under the installed agent is in `evidence/exec-form-*`.
2. `42cbb5d00` - the Windows publish of the embedded hook: the output folder passed as `<folder>\.` so a
   trailing backslash no longer escapes the closing quote. Proven by hosted run 39419360410 (green; the run
   before it, 39418982124, failed on the defect).

Questions to answer, each with what proves it:
- Does exec form behave on every platform the Director supports (Linux, Windows with and without Git Bash,
  macOS), and what happens with an installed agent too old to know `args` - can it block a command?
- Does the linked source file keep the evaluator free of project references, and can the two limits still
  drift apart without a test going red?
- Does `<folder>\.` work for every runtime identifier the embedding runs for, and is there any other place
  in the build where a quoted path ends in a backslash?
- Is every call in "The Tech Lead's calls" of the whole-phase review answered as asked?

Write your review to `phase-1-whole-fixes-review.md` beside this file and commit it.
