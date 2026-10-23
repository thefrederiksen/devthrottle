# Issue #4380, the guarded path: before and after (23 October 2026, devlinux)

The harness proof for #4380 (epic #4386, phase 2).

- **Before.** The product at `4b58b3a23`:
  - the condition cells come from the harness cells' first run, in `../harness-cells-2026-10-23/`, using `conditions/`
    and `menu/`, which supersedes the menu rows in `conditions/`;
  - the leading `!` and `/` texts come from the mission baseline (`devthrottle_internal`,
    `docs/missions/terminal-parity-2026-10-23/baseline/agent-results.jsonl`, product `7f5ffb82`, one run per cell).
- **After.** Main at `45e437c69`, which holds #4380 (`efa52f2ee`), plus the Codex clear-key fix in this pull request
  (below).
- **Agents.** As in the first run: Claude Code 2.1.309 on Haiku 4.5, Codex 0.163.0 on gpt-5.6-luna at low effort, and
  Pi 1.2.0 on the mindzie dev Azure Foundry's `gpt-5.6-terra`, thinking off.
- **Repeats.** Every cell ran twice.
- **Where it ran.** Every run went in a visible Director terminal, from the scripts in this folder.

| Folder | What ran | Script |
|---|---|---|
| `conditions/` | the condition matrix on claude, codex and pi: mid-turn, owner-draft, menu-open, usage-limit | `run.sh` |
| `leading-before-keyfix/` | the leading `!` and `/` texts on the prompt route, before the Codex clear-key fix; it found the fault below | `run.sh` |
| `keys-codex.log` | which keys empty a Codex composer, including one holding a command Codex refused | `run-keys.sh` |
| `leading/` | the leading texts again, after the fix | `run-leading.sh` |

## What moved

| Cell | Before | After | In plain words |
|---|---|---|---|
| usage-limit, desktop paste (echo) | **0 of 2**, `DROPPED` | **2 of 2**, `EXACT` | A paste at the desktop now reaches a session showing Claude Code's usage-limit dialog, as typing always did. Before, the guard dropped every character, because a paste carried no origin. |
| usage-limit, desktop typing (echo) | 2 of 2 | 2 of 2 | Unchanged: the control. |
| Codex, prompt, leading `/` | `DOUBLED` | 2 of 2 refused by Codex itself, **once**, and reported | Codex refuses an unknown command and keeps the line. Before, the send pressed Enter again and again, which re-ran the command. Now it presses Enter once, as a person does, and reports "not submitted", naming the mode: "its first character makes it a slash command in Codex, and Codex did not take it". |
| Claude Code, prompt, leading `/` | `FAILED`: refused, "composer not on the screen" | typed, then `LOST` both runs | No longer refused for a screen the Director could not read. Claude Code itself answers "Unknown command" and drops the line, as it does for a person. The prompt verb names the mode (`LeadingMode`). |
| Claude Code and Codex, prompt, leading `!` | `ALTERED` | `ALTERED` both runs | Unchanged: the agent runs it as a shell command, as for a person. The prompt verb now names the mode. |
| Pi, prompt, leading `/` and `!` | `ARRIVED` and `LOST` | the same, both runs | Unchanged: an unknown `/Token` is a prompt to Pi, and `!` runs as a shell command. |

`ALTERED` and `LOST` on a leading `!` or `/` are the agent's own behaviour, the same as for a person at the terminal
(parity, mission 11.2). What #4380 changed is that they are no longer silent: the prompt verb returns `LeadingMode`,
and a command the agent refused is reported as not submitted, with its mode.

## What did not move, and should not have

| Condition | claude | codex | pi | Before | Note |
|---|---|---|---|---|---|
| mid-turn | 6 of 7 | 6 of 7 | 6 of 8 | 6 of 8 each | The one uncounted run each for Claude Code and Codex was a set-up failure: the 25-second tool never started. That run did not count; its second run passed. |
| owner-draft | 6 of 8 | 6 of 8 | 6 of 8 | 6 of 8 each | |
| menu-open (Claude Code only) | 6 of 8 | - | - | 6 of 8 | On the prompt route the send is still refused in words both times, "showing a menu or a dialog". Look-before-typing now refuses only on a positive reading of a menu, and this is one. |

Every remaining failure is `desktop-newline`: Shift+Enter submits the first line alone. That is #4379's. No cell was
unsteady: both runs of every counted cell agreed.

## The fault the first after-run found, and its fix

`leading-before-keyfix/` shows it. The first Codex leading-`/` send left Codex's refused line in its composer, and the
send said so. Every later send to that Codex session was then refused: "the composer still holds text after it was
cleared". That included both leading-`!` cells, which the baseline had delivered. A person would have edited the line;
the Director could not clear it.

**Cause, measured.** `run-keys.sh` ran `--keys codex` with two new drafts. Codex keeps a refused command with the cursor
at the line's START. The clear key measured on 24 September, Ctrl+U, deletes back to the start of the line, so there it
deletes nothing.

| Draft (Codex 0.163.0) | Ctrl+U | End, then Ctrl+U |
|---|---|---|
| typed line | emptied | emptied |
| a command Codex refused | **not emptied** | **emptied** |
| empty composer | nothing happens | nothing happens |
| two-line paste | first line left | first line left |

**Fix.** Codex's clear keys are now End then Ctrl+U (`ComposerClearKeys`, test
`Each_agent_gets_the_clear_key_measured_for_it_and_never_a_lone_Escape`). `leading/` is the run after it: each refused
`/` line is cleared by the next send, and the leading-`!` cells reach Codex again.

**Not fixed here, named.** Neither key empties a two-line paste on Codex 0.163.0: the 24 September table said Ctrl+U
did on 0.155.1. A two-line text the Director left in a Codex composer would therefore still refuse later sends.
#4384 (Codex) owns that.

## What these cells cannot show

- **Usage-limit.** The cell uses the echo agent drawing the dialog's real words, so it shows what the Director lets
  through, not what Claude Code does with it. The prompt route is left out: whether phone and Cockpit prompts pass is the
  owner's open question (mission 11.6), and `UsageLimitInputGuard.PhoneAndCockpitPromptsPassTheDialog` stays false.
- **Live browser terminal keys.** These pass the dialog since the review of #4394. They are covered by unit tests, not
  by a cell, because the rig has no browser terminal route.
- **`LeadingMode`.** The rig's prompt route calls the Session directly, so the verb's `LeadingMode` field is proven by
  unit tests (`SendPromptAsync_LeadingBang_ReportsTheShellMode`), not here.

## Cost

- **Before the run:** Pi about $0.30 for the condition run, under $1.30 at the first run's assumed ceiling price, and
  four Pi sends for the leading texts. The stop line was twice that.
- **Used, from Pi's own records:** 40 model calls between 19:29 and 20:59 UTC, with 206,490 tokens read from cache,
  23,040 written to it, 120 uncached in and 1,107 out. Pricing every token read, cache included, at the $2.50 per
  million ceiling (an upper bound; cache reads cost less) and output at $15 per million gives at most $0.59.
- **Free:** the keys measurement types into Codex and never submits a prompt. Claude Code and Codex ran on their
  subscriptions, with one-word answers.
