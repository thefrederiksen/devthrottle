# The AXI standard for our command-line tools

**AXI (Agent eXperience Interface)** is a set of ten design principles for command-line tools whose
main user is an AI agent. It was created by **Kun Chen**:

- The standard: https://axi.md
- The repository, reference tools and benchmarks: https://github.com/kunchenguid/axi

The principles and their original evidence (915 benchmark runs across browser automation and GitHub)
are his. This page adopts them for DevThrottle and turns them into the checklist we build and review
against.

**Scope today: `cc-devthrottle`.** Other tools in `tools/` adopt it later, each in its own issue.
Tracking issue: #2922.

## Why we adopted it

Agents run `cc-devthrottle` far more often than people do, and every character it prints is paid for.
Before AXI, `session list` printed a table that truncated names and states to fit 80 columns
(`devthr...`, `Waitin...`) or a 113 KB JSON dump with about 100 fields per session. Agents read help,
gave up on the table, dumped the JSON and wrote scripts to search it.

Issue #2920 measured an AXI-shaped `session list` against it: six everyday fleet tasks, five runs each,
with a repeat of the old tool as the noise control and pass criteria written before any run.

| | Before | AXI-shaped | Change |
|---|---|---|---|
| Right answers | 29/30 | 30/30 | no loss |
| Tokens per task | 201K | 93K | -54% |
| Commands per task | 5 | 2 | -60% |
| Time per task | 28s | 10s | -63% |
| Sessions readable back from the output | 0/26 | 26/26 | |

## The checklist

Every command meets all ten. The wording is ours; the principles are from https://axi.md.

1. **Token-efficient output.** Compact TOON-style output by default, not wide tables and not raw JSON:

   ```
   count: 26 (needs-you 3, working 4, ready 17, snoozed 2)
   sessions[26]{id,name,state,repo}:
     d2a4069f,cc-consult,needs-you,cc-consult
     ...
   ```

   A value containing a comma or a quote is quoted.
2. **Minimal default fields.** Three or four per row. `--fields a,b,c` asks for more, and the valid
   field names are listed in the help.
3. **Truncation with a size hint.** Long free text (a body, a transcript) is cut with a hint such as
   `(truncated, 2847 chars total - use --full)`. Identifiers and names in a list are never cut.
4. **Pre-computed aggregates.** Always a total count; add the summary an agent would otherwise compute
   (counts by state, checks passed and failed).
5. **Definitive empty states.** `count: 0` or `none match`, never blank output. When a filter matched
   nothing, say `count: 0 of 26 total`.
6. **Structured errors and exit codes.** Never prompt for input. Exit 0 on success, 1 on error, 2 on a
   usage error. An unknown flag or an unknown field fails loudly with the valid values. Errors are
   written so an agent can act on them.
7. **Ambient context.** The fleet preamble a session receives at start carries a compact, current
   view, so an agent has the state before it runs anything.
8. **Content first.** `cc-devthrottle` with no arguments shows live state - who this session is, what
   needs the owner, the most useful next commands - not the help screen.
9. **Contextual disclosure.** After output, `help[]` lines with concrete next commands, with runtime
   values as placeholders (`cc-devthrottle message send <id> "<message>"`), never guessed values.
10. **Consistent help.** Every subcommand has a short `--help`.

## Rules that do not bend

- **`--json` keeps its shape.** Other code parses it. It stays a machine format with every field.
- **Every filter applies to `--json` too.** `--state working --repo X --json` returns only those rows,
  in the same shape. The first #2920 prototype ignored filters under `--json`; agents combined them
  immediately, received the whole fleet, and paid for it. A silently ignored flag is a defect.
- **One plain state per session**, from the same fold the Cockpit uses: `needs-you`, `working`,
  `ready`, `snoozed`, `crashed`. Never make the agent infer state from several raw fields.
- **ASCII only** in all output.
- **No platform assumptions** in output code: no Windows-only paths, shells or encodings.

## What "done" means for a command

- It meets the checklist above.
- A **recoverability test** proves every record can be read back exactly from its default output
  (id, full name, state). The old table fails this test, which is what proves the test can fail.
- A test proves `--json` output is unchanged for an unfiltered call, and filtered for a filtered one.
- The `cc-devthrottle` tests pass in CI on **Windows, macOS and Linux**.
- It has been run for real against a live fleet, not only in tests.

## How to measure a change

The #2920 harness runs headless agents on fixed fleet tasks against a frozen fleet snapshot, with
answers graded by exact comparison. Two lessons from building it:

- Run each agent in an **empty folder outside any repository**. Inside a repository the agent picks up
  that repository's context and wanders, and the numbers measure the wandering.
- Always include a **repeat of the old version** as a control. The gap between the two identical runs
  is the smallest difference you are allowed to call real.
