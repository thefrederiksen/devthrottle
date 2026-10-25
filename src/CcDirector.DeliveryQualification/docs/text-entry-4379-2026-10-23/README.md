# Text entry and paste - the proof (issue #4379, Terminal Parity phase 2)

Every run here was on devlinux, in a visible Director terminal, against the baseline in `devthrottle_internal`
(`docs/missions/terminal-parity-2026-10-23/baseline/`, product commit 7f5ffb82). The scripts that ran are in `scripts/`.
Agents: Claude Code 2.1.309, Codex 0.163.0, Pi 1.2.0 on the mindzie dev Azure Foundry `gpt-5.6-terra`.

## Before and after (`rates-compare.txt`, from the rig's ledger)

| Layer | Baseline 7f5ffb82 | After (commit 8ba3ee79, main with #4380 and the harness cells) |
|---|---|---|
| Echo agent, no model - sends written exactly | 110 of 137 (80%) | 584 of 592 (99%) |
| Real agents - sends that arrived exactly | 133 of 195 (68%) | 170 of 195 (87%) |
| Desktop Shift+Enter (desktop-newline), real agents | 0 of 12 | 12 of 12 |
| Condition cells (mid-turn, owner draft, menu open, usage limit) | not run | every counted cell passed, each twice |
| Texts sent as a file instead of the text | 18 (echo), 11 (real agents) | none |

The echo layer now runs each desktop route as a plain program AND standing in for each agent. The 8 echo sends that are
not exact are `desktop-newline as desktop` (a plain program, such as a shell): Shift+Enter is Enter there, as in a real
terminal. Standing in for Claude Code, Codex and Pi the same cells are 24 of 24 exact.

The real-agent counts above are from before the widened edge-trim rule. With it (`trim/`), all nine edge-spaces sends pass,
each naming the agent's trim and the echo cell that proves the Director wrote the spaces.

## What each piece of evidence shows

- `newline-keys.txt` - which keystroke makes a new line in each agent's composer (`--newline-keys`). Ctrl+J (one LF byte)
  is the only key all three take; Alt+Enter SUBMITS on Pi; xterm's Shift+Enter loses the break on Codex. So the desktop
  sends Ctrl+J for Shift+Enter and Ctrl+J for these three agents (`ComposerNewlineKey`).
- `paste-sizes.txt` - with the paste limit lifted, 2,001, 8,192 and 51,200 characters and a 50-line text, through the send
  and the desktop paste, idle, and through the send with all 32 cores busy: every one arrived exactly, on all three
  agents. That is the limit the file route now starts above (`TerminalSubmit.MeasuredPasteLimit`).
- `after/` - the full after-run: echo layer, text matrix (one run per cell) and the condition matrix (two runs per cell).
- `trim/` - the edge-spaces text with the echo proof (`--echo-proof`): the widened trim rule accepting the agent's own trim.

## Cases still failing, by name, with the cause

| Case | Agents | Cause |
|---|---|---|
| `tabs` on `prompt` and `desktop-paste` | Claude Code, Pi | The agent turns each pasted tab into four spaces. The Director now writes the tab inside the paste (the echo layer shows it); Pi's own editor runs `replace(/\t/g, "    ")` on every paste. A person pasting gets the same. Codex keeps tabs and passes. |
| `lead-slash` | all | The agent takes a leading `/` as a command: a person typing it gets the same (desktop routes); the prompt route is #4380's. |
| `lead-bang` | all | Claude Code and Codex run a leading `!` as a shell command, Pi drops it: the same for a person; the prompt route is #4380's. |
| `edge-spaces` (before the trim rule) | all | The agent's own trim, now accepted where the echo proves the Director wrote the spaces (`trim/`). |
| `desktop-newline as desktop` (echo) | a plain program | Not a fault: Shift+Enter is Enter for a program that is not one of the measured agents. |

## What only a Windows run can prove

The platform encoder writes non-ASCII as win32-input-mode key events on a Windows pseudo console - typed, and inside a
paste. Unit tests (`TerminalInputEncoderTests`) pin the bytes, but whether Codex and Grok then record é, dashes, curly
quotes, £ and € correctly through a desktop paste, and whether a bracketed paste of key events reaches Claude Code, Codex
and Pi as one paste, needs the one-off Windows baseline. So does the newline key: Windows Terminal may itself send
something for Shift+Enter before the Director sees it.

## Cost

Claude Code and Codex ran on subscriptions (about 450 one-word turns across all runs). Pi on terra: well under one dollar.
