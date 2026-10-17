# Evidence: "Teach auto mode about your environment?"

Recorded on devlinux, 17 October 2026, Claude Code 2.1.302 on `claude-sonnet-5-5`.

## What Claude Code does

Read from Claude Code 2.1.302 itself, and seen by hand. When a session starts in auto mode, Claude Code decides once
whether to offer the question. It offers it when:

- the environment is not described;
- it has been started at least five times;
- auto mode has turned down at least five actions;
- the owner has not answered it.

It shows the question when the first turn ends, under the composer:

    ❯ Do not run any tools. Reply with exactly: ONE
    ● ONE
    ✻ Brewed for 1s · done 7:16 PM
    ────────────────────────────────────────────────────
    ❯
    ────────────────────────────────────────────────────
      ⏵⏵ auto mode on (shift+tab to cycle)
    ────────────────────────────────────────────────────
      Teach auto mode about your environment?
      Auto mode works better when it knows your environment. Takes about a minute.
      ❯ 1. Yes
        2. Not now
        3. Don't show again
      Enter to confirm · Esc to cancel

By hand, in a scratch configuration folder:

- The words of a second prompt typed into that screen vanished, and the screen did not change. The Enter that would
  follow them selects "Yes", which starts a setup that reads the owner's shell history and other repositories.
- Escape answered "Not now". It recorded `autoModeEnvSetup.dismissedAt` in that scratch folder, which quiets the
  question for seven days.

The owner's own `~/.claude.json` was not changed: `autoModeEnvSetup` was `{"denials": 5}` before and after.

## The delivery rig

`ledger-rows.jsonl` holds every attempt as the rig wrote it. `rates-main.txt` and `rates-fix.txt` are the rig's
summaries.

| Scenario | main | this change |
|---|---|---|
| `auto-mode-question` | 0 of 2: the next prompt was refused by look-before-typing, and the seat was stuck under the question | 2 of 2: refused at start, in words naming the question |
| `auto-mode-question-settled` (the control, answered "Don't show again") | 2 of 2 | 2 of 2 |
| Claude Code `first`, `idle-short` | - | 2 of 2 each |

**Cost:** 12 attempts. Each attempt that started made two one-word Sonnet turns; the two refused attempts made none.
