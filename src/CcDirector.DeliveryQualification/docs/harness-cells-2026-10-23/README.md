# The harness additions, first run (23 October 2026, devlinux)

The proof run of the mission's 11.5 additions (issue #4378, epic #4386). The PRODUCT code measured is origin/main at
`4b58b3a23`: every commit on this branch changes only the rig, so the ledger's commit stamp (which names the rig's build)
differs between folders while the product does not. Agents: Claude Code 2.1.309 on Haiku 4.5, Codex 0.163.0 on
gpt-5.6-luna at low effort, Pi 1.2.0 on the mindzie dev Azure Foundry's `gpt-5.6-terra`, thinking off.

Every run went in a visible Director terminal ("Terminal Parity - Worker - harness run"), from the scripts in this folder.
Fixing the failures below is phase 2's work, not this pull request's.

| Folder | What ran | Script |
|---|---|---|
| `echo/` | the echo layer in full, every cell twice | `run.sh` |
| `conditions/` | the condition matrix on claude, codex and pi | `run-conditions.sh` |
| `menu/` | the menu cells again, after their setup was fixed (supersedes the menu rows in `conditions/`) | `run-menu.sh` |
| `mid-turn/` | the mid-turn cells again, after the review of pull request 4395: the tool now proves it was running (supersedes the mid-turn rows in `conditions/`) | `run-mid-turn.sh` |
| `state/` | the approval case on claude and codex, whose Codex ask is now read from its records | `run.sh` |

## What each new cell proves, and what it cannot

**Mid-turn** (all three agents, every route). The agent is asked, through the desktop as the owner would, to run a
25-second tool that writes a start mark before it sleeps and an end mark after. Once the start mark exists, the text is
sent by the route. A pass means the text reached the agent once, alone, character for character, while the tool was
running - its own marks say it started before the send and finished after it. (The first run used the tool-call record,
which shows only that the agent asked to run it; the review of pull request 4395 found that, and `mid-turn/` is the run
with the marks: in all 24 cells the tool started 2.0 to 2.5 seconds before the send and finished at least 22.5 seconds
after it, and no verdict moved.) It cannot show that Codex or Pi took the prompt at once, because they
record a queued prompt only when the turn ends; it shows only that the prompt arrived.

**Owner-draft** (all three agents, every route). The owner types a draft into the composer through the desktop and leaves
it unsent; then the text comes by the route. A pass means one turn holding the draft, then the text, both character for
character (the owner's ruling of 21 October 2026, and what a person typing more gets). It does not judge how many spaces
join the two: the prompt route adds one, and a person types their own.

**Menu-open** (Claude Code only). Claude Code opens its question menu; the text is sent by the route while the menu
shows; then Escape closes it and a next text is sent by the same route. On the prompt route a pass means the Director
refused in words (or delivered), never reported a lost text as delivered, and the next text arrived. On a desktop route the
keys are the person's own, typed into the menu as a person's would be: where they went is recorded (`ANSWERED-MENU`,
`LOST`), not judged, and a pass means the next text arrived alone. Codex and Pi have no question menu the rig can raise.
Codex asks questions only in plan mode, and Pi has none. Their model pickers are not used, because Enter in one changes
the agent's model, and Codex also writes the choice to the owner's own configuration file.

**Usage-limit** (the echo agent). Claude Code shows its usage-limit dialog only when the owner's allowance has run out,
which no run can bring about on demand. So the byte-echo agent draws the dialog's real words, taken from the captured
screens in the product's own tests, and each cell first checks that the Director's own guard reads the dialog as showing.
A pass means a desktop paste reached the agent; desktop typing is the control. It shows what the Director lets through,
not what Claude Code would do with it. The prompt route is left out until the owner answers whether phone and Cockpit
prompts should pass the dialog (mission 11.6).

## Results

**Echo layer:** 274 sends, every cell run twice, **0 unsteady cells**. The failures are the baseline's, each twice:
Shift+Enter splits a multi-line text, the desktop paste drops tabs, and the prompt route sends long or multi-line text as
a file.

**Condition matrix:** 60 cells, of which 56 counted. The four that did not count are the first menu run's setup
failures, superseded by `menu/`. In the cells that counted, **0 cells were unsteady**: both runs of every cell agreed.

| Condition | claude | codex | pi | Every failure |
|---|---|---|---|---|
| mid-turn | 6 of 8 | 6 of 8 | 6 of 8 | `desktop-newline`: Shift+Enter submits the first line alone (#4379) |
| owner-draft | 6 of 8 | 6 of 8 | 6 of 8 | `desktop-newline`: the same split, and the draft is welded to the text's first line with no space |
| menu-open (`menu/`) | 6 of 8 | - | - | `desktop-newline`: the same split, before and after the menu |
| usage-limit (echo) | desktop-type 2 of 2; desktop-paste **0 of 2** | | | **`DROPPED`**: the paste never passes the dialog, only its Enter does - the 11.3 bug, now measured |

Outside the Shift+Enter fault, the mid-turn and owner-draft cells pass on every route and every agent. On the menu cells,
the prompt route refused in words both times (`ComposerNotAcceptingInputException ... showing a menu or a dialog`) and
the next text arrived. Typed text went into the menu's answer (`ANSWERED-MENU`), as a person's keys would. A pasted text
reached no record (`LOST`).

**Approval case:** Codex's approval is now JUDGED on both runs, where the baseline could not judge it at all. Its ask
was read from its `custom_tool_call` record, and the saved screen shows Codex's real "Would you like to run the following
command?" prompt. Both runs fail as the baseline predicted for every agent: the Director never shows `WaitingForPerm`, and
never shows waiting. Claude Code's two runs fail the same way.

## Faults the run found in the rig itself, all fixed on this branch

Each fault produced a plausible verdict that was wrong, so each was chased to the records before it was believed:

1. **Codex 0.163 refuses `-a untrusted`**: it accepts only `on-request` or `never`, so the baseline's Codex approval
   session exited before the prompt. That is why the baseline could not judge it (`state/screens` holds the screen). It
   now runs `-a on-request -s read-only`.
2. **Claude Code 2.1.309 writes an `api-request-blob`** (a copy of the request it sent the model) BEFORE a queued
   prompt's own record. The folding witness read the reminder around the prompt as text welded on, so every mid-turn
   cell showed `DOUBLED`. Copies of a prompt are now skipped.
3. **Codex echoes the prompt** in an `item_completed` event. The prompt asks for the very `ACK` words, so the answer
   reader saw the turn end before it began, and every Codex mid-turn cell was set aside as not mid-turn. The same echo
   would have been taken for the approval ask by the baseline's reader.
4. **The menu cell's setup** first used the composer reader, which on a fresh session reads the prompt row above the
   menu as a composer holding text. It then used the records, but on a session's first turn Claude Code had not yet
   WRITTEN the AskUserQuestion call while the menu showed. The menu is now confirmed by its own hint row under the setup's
   token. And after a few menu-time texts had landed as answers, Haiku refused the next request to open a menu as "a
   prompt injection test", so each menu cell now starts a fresh session. The earlier tries are kept outside the
   repository, under `_delivery-qa/runs/harness-cells-20261023-1309/`.

**For #4381, from fault 4:** an agent's record carries the time of the event, but it may be written well after that
time. The state matrix reads its true end from these records after the watch, so its timings stand. But a live state
source that waits for a record to appear would be late.

## Cost

The estimate made before the run: Pi, the only per-token bill, about 40 model calls per condition run at about 12,000
tokens in (mostly cached) and 100 out. That is 480,000 tokens, under $1.30 a run even at an assumed ceiling of $2.50 per
million uncached tokens in and $15 per million out; the stop line was twice that. What both condition runs used, from
Pi's own records: 57 model calls, 376,526 tokens read from cache, 70,510 written to it, 171 uncached in and 990 out. At
that ceiling price that is about $0.30. Claude Code and Codex ran on their subscriptions, one-word answers, Haiku and
luna at low effort.
