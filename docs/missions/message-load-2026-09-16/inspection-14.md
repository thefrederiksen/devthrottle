# Inspection 14 — PR 3034 reply help test

**Verdict: FAIL — 2 findings.** Reviewed `gh pr diff 3034` at current head `7f8fd87b55a20f0f6de7f729a31d43930d49c6de` and read inspection 13. The previous pipe-removal false positive is closed: the changed test no longer deletes punctuation from rendered help. The option association concern is also closed at the metadata level: each sentence is read from its required Click option, and removing or renaming that option raises.

## Findings

### 1. A passing test still does not establish what the reader sees in the option table

`tools/cc-devthrottle/tests/test_message_queue.py:650-668` proves that Typer registered the sentences and that the rendered send page contains two flag strings *somewhere*. It does not prove that the renderer exposes those sentences or even the full `--reply-wanted` label in the option table. At the exact declared floor (Typer 0.16.1, Click 8.2.1, Rich 13.0.0), `COLUMNS=40 TERM=dumb` gives `1 passed`, while the actual send help labels the option `--reply...`. The full `--reply-wanted` string survives only in the descriptive paragraph (`Add --reply-wanted to ask for a reply`). Rich also shortens words inside option help, including `correl...` and `--repl...`. Thus the rendering assertion can pass while an option row says something different from the registered metadata. A renderer that suppressed the target option text altogether would still satisfy the metadata checks and could satisfy both page substring checks from other text. This is the reader-surface gap behind inspection 13's second finding, despite its narrower wording here.

The two successful `--help` invocations establish that both pages open. The last send invocation's exit code is not checked, and its whole-page substring matches do not establish that the flags are rendered as option labels. That makes the flag part of the rendering assertion weak, though page startup remains a meaningful check.

### 2. The retained rendering check can still fail solely because of terminal width

At the same exact dependency floor, the focused test passes at `COLUMNS=100`, `80`, `60`, and `40`, but fails at `COLUMNS=30` and `20`. At 30, Rich abbreviates the `--reply-by` option label, so the final `assert "--reply-by" in page` fails even though Click still registers the option and its complete help. This retains a width-dependent false failure in the very renderer leg that the change is meant to stabilize. The Windows floor leg does not run on pull requests; I could not execute Windows here, and the local result does not claim that its current default width is 30. It demonstrates a concrete condition under which that leg can go red again without a command/help regression.

## Other checks

- At the floor, the unmodified `test_message_queue.py` ran **51 passed**. The focused test also passed at the latest versions available during this inspection: Typer 0.27.2, Click 8.5.0, Rich 15.0.0. These endpoints do not certify every intervening release, but `_registered()` follows the actual `get_command(app).commands["message"].commands[...]` tree at both endpoints.
- In an isolated worktree pinned to the PR head, renaming the registered `--reply-wanted` option to `--ask-reply` made the focused test fail with `AssertionError: --reply-wanted is not an option of send` at the floor versions. Renaming the `reply` command to `answer` made `_registered()` fail with `KeyError: 'reply'`. The command tree lookup therefore fails loudly for a missing path; it does not silently fall through to another command.
- The isolated mutations were restored. No production or test code was changed for this review.
