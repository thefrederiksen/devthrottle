# Inspection 13 — PR 3034 reply help test

**Verdict: FAIL — 2 findings.** Reviewed `gh pr diff 3034` at head `eeb2cb738abde86ea5c71b818982110cd546591e`. The width-100 failure is real and caused by table borders, but the proposed whole-output normalization can certify text that the help does not actually say, and the new comment's “any console width” claim is false.

## Findings

### 1. The normalizer erases literal help text as well as borders

`tools/cc-devthrottle/tests/test_message_queue.py:625-636` removes **every** ASCII `|` and Unicode `│` from the whole page before matching. The new test therefore accepts `Nothing | waits for it` as `Nothing waits for it`. The pipe can be legitimate help punctuation, such as a separator or alternatives marker; erasing it changes what the user reads. This is a false positive for an asserted sentence, not just a formatting tolerance. The current `message send` and `message reply` help contains no interior `|` or `│` at widths 100, 80, 60, or 40, so this is a guard weakness rather than an observed current help defect.

The flattening also removes line and cell boundaries. For example, it accepts `1 to |\n| 1440, 60 when omitted` as the target phrase. That is the desired effect when those are two wrapped lines of the same option, but the test does not establish that the fragments belong to the same option. A normal Rich row for a *different* option normally inserts its label, which would interrupt this particular phrase; the risk should not be overstated as an observed cross-option match. The current assertions also search the whole page rather than the `--reply-by` option specifically.

### 2. The new “any console width” claim is disproved by the declared floor renderer

With exactly Typer 0.16.1, Click 8.2.1, and Rich 13.0.0, `COLUMNS=40` and `TERM=dumb`, Rich renders the `--reply-wanted` option label as `--reply...` and truncates parts of its option help with `...`. The changed test still passes: the full `--reply-wanted` string appears in the command description above the option table, while the other target snippets survive wrapping. This is a concrete false positive if the test is supposed to guarantee that the option table exposes the complete flag and help at every console width. The same phenomenon could happen when the table help changes while the descriptive paragraph stays intact. At `COLUMNS=100` and `80`, there were no ellipses in either command's rendered help; the width-100 failure was solely the border between `1 to` and `1440, 60 when omitted.`

## Floor-version reproduction

Installed the exact three floor versions in an isolated virtual environment, invoked the real `src.cli.app` through `typer.testing.CliRunner`, and set `COLUMNS` on each invocation. The relevant width-100 output was:

```text
| --reply-by            INTEGER  Minutes the recipient has to reply, with --reply-wanted: 1 to     |
|                                1440, 60 when omitted.                                            |
```

The raw whitespace-folded output did not contain `1 to 1440, 60 when omitted`; the PR's normalized output did. At width 100 and 80 all five assertions matched and neither help page contained ellipses. At width 40 the send help included:

```text
 Add --reply-wanted to ask for a reply
| --reply...                 Ask the   |
| --reply-by        INTEGER  Minutes   |
|                            --repl... |
|                            1 to      |
|                            1440, 60  |
```

The focused test ran with `COLUMNS=100`: `1 passed, 50 deselected`. The inspection did not run on Windows; it verifies the declared package versions and console widths, not Windows terminal behavior itself.

## Better guard

Assert the actual option help strings through Typer/Click command metadata, including the association of `--reply-wanted` and `--reply-by` with their respective descriptions, and assert the `message reply` command's help/docstring there. This avoids layout, truncation, and global pipe deletion. Keep a small `--help` invocation with exit code and option presence if rendering itself is part of the contract. Directly reading source literals in `src/cli.py` would test the intended words, but alone would not prove that Typer registered them or that users can see them; command metadata is a better middle point. A rendering assertion should parse or inspect the relevant row rather than flattening the entire page.
