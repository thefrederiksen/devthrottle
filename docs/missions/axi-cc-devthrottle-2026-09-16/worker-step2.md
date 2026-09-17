# Worker brief - step 2, shared AXI output helper

You are `AXI Tools - Worker - step 2`. Your supervisor is the Manager (session 486709d7). Report to it
only, once, when done or genuinely blocked.

Read first: `cc-devthrottle workflow instructions mission` (conduct), `gh issue view 2922 --comments`
(the Implementation brief comment is the mandate), `docs/axi-standard.md`, and
`/Users/soren/ReposFred/devthrottle-axi/docs/missions/axi-cc-devthrottle-2026-09-16/state.md` (binding
design rulings).

## Where

Worktree `/Users/soren/ReposFred/devthrottle-axi-step2`, branch `axi/output-helper`, cut from
`origin/main`. Work only there. Never touch the shared checkout. Foreground only. Push with
`git push -u origin axi/output-helper` (the branch currently tracks origin/main - do not push to main).

## The work

A new module in `tools/cc_shared` (for example `axi_output.py`) that every cc-devthrottle list command
will render through. Pure functions, no Rich, no printing side effects beyond an explicit writer.
It must provide:

1. TOON-style list rendering: `name[N]{f1,f2,...}:` header then one two-space-indented row per record.
   A value containing a comma, a double quote, a newline, or leading/trailing whitespace is quoted
   (double quotes, with embedded quotes and newlines escaped so the row stays one line and parses back
   exactly). Empty and None values have a defined, documented rendering.
2. ASCII-safe rendering: any non-ASCII character in a value is escaped reversibly (for example
   `\uXXXX`), never dropped or replaced with `?`. Output is pure ASCII.
3. The `count:` line: `count: N`, optional per-state breakdown `count: N (needs-you 3, working 4)`
   in a caller-given order, and `count: M of N total` when a filter narrowed the result.
4. Empty state: an empty result still prints `count: 0` (or `count: 0 of N total`) - never blank.
5. `help[]` lines: `help[N]:` followed by indented concrete commands with placeholders the caller supplies.
6. `--fields` validation: given the requested list and the valid names, an unknown name fails with
   exit code 2 and a message listing every valid name. Expose it so a typer command can call it.
   No fallback, no silent dropping.
7. A parse-back function (used by tests, and by step 3's recoverability test) that reads the rendered
   list back into records exactly.

Scope: the helper and its tests only. No cc-devthrottle command uses it yet. Do not change any
`--json` output. No non-ASCII in source or output. No Windows-only assumptions.

## Tests (required)

Unit tests in `tools/cc_shared/tests/` covering: quoting of commas, quotes, newlines, leading/trailing
spaces; non-ASCII input rendered ASCII-safe and parsed back exactly; round trip of arbitrary records;
`count:` with and without breakdown and `of N total`; empty state; `help[]`; `--fields` unknown name
exits 2 listing valid names. Prove at least the quoting test can fail: break the quoting on purpose,
watch it go red, restore. Say in the pull request that you did.

## CI

`.github/workflows/ci.yml`, job `tool-contracts`: the `tools/cc_shared` tests are not run there today.
Add a step that runs your new test file (only yours - the other cc_shared tests have dependencies that
job does not install; do not enlarge scope). Pull request #2932 (step 1, not yet merged) turns that job
into a Windows/macOS/Linux matrix; if it has merged by the time you push, rebase onto origin/main. If
not, still open your pull request; the Manager will ask for a rebase later.

## Proof and handoff

- Run the tests locally on the Mac. Paste the output in the pull request body.
- Open a pull request against main titled plainly (for example "Shared AXI output helper for the
  command-line tools"), body in plain English, referencing #2922. Do NOT merge.
- No attribution of any assistant anywhere: no Co-Authored-By, no "Generated with", no vendor names.
  Grep your commit messages and pull request body before pushing.
- Plain English, no abbreviations, in everything you write.
- When the pull request is open, send the Manager ONE line:
  `cc-devthrottle message send 486709d7 "step 2 worker: pull request <number> open"`
  then stop and wait.
