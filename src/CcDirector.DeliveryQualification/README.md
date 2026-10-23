# The delivery qualification rig

Starts REAL agents (Claude Code, Codex, Pi, ...) in real terminals through the Director's own
`SessionManager` - the same pseudo-terminal, screen parser, composer reader and send path that ships - and
judges every send by the agent's OWN conversation records, never by the screen and never by the Director's
verdict. Never shipped; run by hand.

## The delivery scenarios (Prompt Delivery mission, October 2026)

Each scenario puts the agent into a state in which prompts were lost in the field, then sends one text with
its own token. An attempt PASSES only when the records hold the text exactly once, alone, in a user turn,
and the Director did not report it failed (a false alarm makes the owner send it again).

| Scenario | Agents | What it sets up |
|---|---|---|
| `first` | all | a new session's first prompt, through the first-prompt gate, as a spawn sends it |
| `idle-short`, `idle-long-line`, `idle-multiline-crlf`, `idle-special`, `idle-unicode`, `idle-huge` | all | text shapes sent to a waiting session (`TextCatalogue`) |
| `busy` | claude, pi | the agent is running `sleep 40` when the prompt arrives |
| `on-screen-repeat` | claude | the prompt's exact words are already visible above the composer |
| `shell-bang` | claude | a `! command` whose words are on screen (the agent told the owner to type it) |
| `shell-bang-fresh` | claude | the same without the words on screen - the control |
| `menu-open` | claude | the agent shows its question menu (AskUserQuestion) when the prompt arrives; passes when the prompt is refused plainly (or arrives) and the next prompt, after the menu is closed, arrives |
| `owner-draft` | claude, pi | the owner has unsent text in the composer |
| `leftover-line` | claude, pi | an unsubmitted framework line (a doorbell line) sits in the composer |
| `resume` | claude | the first prompt of a session resumed on an existing conversation |
| `untrusted-folder` | claude | the first prompt in a folder Claude Code has never trusted |
| `claude-trusted-folder` | codex | the first prompt in a repository trusted in Claude Code and never opened in Codex |
| `trusted-worktree` | claude | the first prompt in a new git worktree of a trusted repository (how reviewer and gate seats start) |
| `auto-mode-question` | claude | a seat (opened by another session) in auto mode on a machine whose owner never answered Claude Code's "Teach auto mode about your environment?", which it shows when the first turn ends; passes when the start is refused in words naming the question (`REFUSED-AT-START` / `REFUSED-IN-WORDS`) or the next prompt arrives |
| `auto-mode-question-settled` | claude | the control: the same seat where the owner answered it "Don't show again"; passes only when the seat starts and the next prompt arrives |

Truth words: `ARRIVED`, `LOST`, `PARTIAL`, `DOUBLED` (two turns), `WELDED` (one turn holding the prompt and
other text). Director words: `OK`, `DELIVERING`, `REFUSED`, `FAILED`, `HUNG`, and `SETUP-FAILED` for a
scenario that could not create its precondition (kept in the ledger, counted in no rate).

## Running it

    dotnet build src/CcDirector.DeliveryQualification
    B=src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification

    # every Claude Code scenario, five times
    $B --scenarios all --agents claude --repeat 5 --args-claude "--model haiku --allowedTools Bash(sleep:*)"

    # a few scenarios on Pi, in its own folder so it cannot disturb a Claude run beside it
    $B --scenarios first,idle-short,owner-draft --agents pi --repo-root ~/ReposFred/_delivery-qa-pi \
       --ledger ~/ReposFred/_delivery-qa/ledger.jsonl --args-pi "--thinking off"

    # the success rate per product commit, agent and scenario, with 95 per cent intervals
    $B --rates

Every attempt is appended to the ledger (`<repo-root>/ledger.jsonl`) as it ends, tagged with the commit the
rig was built from. To prove a fix moves a rate: run the same scenarios on main and on the fix branch, then
`--rates` - two rates whose intervals do not overlap differ.

The rig's scratch repositories are marked trusted for Claude Code through the product's own writer
(`ClaudeFolderTrust`), so only `untrusted-folder` meets the trust question, and `trusted-worktree` meets it only on a
Director that does not carry a repository's trust over to its worktrees. That is the ONE write the rig makes
outside its scratch folders: one entry per scratch repository in the owner's `~/.claude.json`. Each `trusted-worktree` attempt leaves a git worktree under `<repo-root>/worktrees/`, which the Director records as
trusted in that same file. Each `untrusted-folder` attempt also leaves a new folder under `<repo-root>/untrusted/`, and, when the question is
answered, an entry in that file and a project folder under `~/.claude/projects`; the rig removes none of them.

The two `auto-mode-question` scenarios run Claude Code in a configuration folder of their own under
`<repo-root>/auto-mode-config/` (`CLAUDE_CONFIG_DIR`), built from a copy of the owner's configuration file with the
question's conditions set, an empty settings file, and a copy of the sign-in, which is deleted when the attempt ends.
They never change the owner's own files. They refuse to start (a setup failure) when the sign-in expires within half
an hour, because a copy that refreshed it would rotate the owner's own out. They run on `claude-sonnet-5-5`, whatever
`--args-claude` says: auto mode offers the question only on a model its classifier serves. Each attempt costs two
one-word turns on that model.

`--repo-root` must contain `delivery-qa` in its path: the records witness finds the rig's conversations by that
word in their folder name, and refuses any other root rather than record every attempt as lost.

Each ledger row names the commit the rig was BUILT from (stamped at build time), with `+local-changes` when the
product's send path had uncommitted changes. A run that counted no attempt exits 2. An attempt that fails after
its prompt was sent is a counted failure (`RIG-ERROR`); only a failure before the prompt is `SETUP-FAILED`. The rig runs in a private
Director home (`CC_DIRECTOR_ROOT` under the run folder) and removes the launching session's fleet identity,
so it never touches the owner's live Director. It takes the agent executables from the live Director's
config.

Cost: every text asks for a one-word reply and no tools. Claude Code on a subscription costs no money; an
agent on a paid provider costs a few thousand tokens per attempt.

## The parity harness (Terminal Parity mission, issue #4378)

Three more modes measure how close the Director comes to a person at the keyboard. They are run on demand, for a
baseline, after each fix, and whenever the work is picked up again - never on a schedule (the owner's ruling,
23 October 2026). The mission document is `docs/missions/terminal-parity-2026-10-23/MISSION.md` in
`devthrottle_internal`.

**Routes.** Each is a way text reaches an agent's composer:

| Route | What it does |
|---|---|
| `desktop-type` | Types a single-line text into the SHIPPED desktop terminal control, one character at a time (8 ms apart), then Enter. |
| `desktop-newline` | Types a multi-line text with Shift+Enter between the lines, as a person makes a new line, then Enter. |
| `desktop-paste` | Pastes the text through the code Ctrl+V runs once it has read the clipboard, then Enter. |
| `prompt` | The Director's own send, `Session.SendTextAsync` - what the Gateway's `/prompt`, the phone and dictation reach. |

The desktop routes host the real `TerminalControl` headless (Avalonia.Headless) on the real session, through its
`HarnessTextInput`, `HarnessKeyDown` and `HarnessPasteAsync` hooks, so the bytes measured are the bytes the desktop
sends.

**Texts.** `MatrixTexts`: ASCII, every punctuation mark, curly quotes and dashes, accents and currency, emoji, Chinese,
Japanese and Korean, right-to-left text, tabs, LF and CRLF line breaks, blank lines, a leading `/`, `!`, `@` or `#`,
and a size on each side of every routing limit in `TerminalSubmit` (199/200/201, 299/300/301, 1999/2000/2001), then
8 KB, 50 KB and a 50-line text. `lead-hash` reaches only the echo agent: a leading `#` can write to Claude Code's
memory.

    # 1. THE ECHO LAYER - free, no model. The rig starts ITSELF as the agent (--echo-agent), which logs every byte it
    #    reads. Every route and text; the prompt route once as each agent, since its route choice depends on the agent.
    $B --echo --routes all --texts all

    # 2. THE TEXT MATRIX on real agents, judged by their own records character for character (ALTERED when the text
    #    arrived but not exactly). Stops at --max-turns (default 240) so a run stays small on the owner's subscriptions.
    $B --text-matrix --agents claude,codex,pi

    # 3. THE WORKING-OR-WAITING MATRIX: quick answer, long answer, a 40-second tool, an approval prompt (permissions ON),
    #    a question menu, the model picker. The session's state is sampled every 250 ms against the agent's records.
    $B --state-matrix --agents claude,codex,pi

**Echo verdicts.** `EXACT`; `SPLIT` (an Enter arrived before the text ended, so the agent submits it in pieces);
`ALTERED` (characters changed or lost - the first difference is shown); `VIA-FILE` (a file reference or file
instruction was typed instead of the text, with whether the payload file is exact); `NOT-SUBMITTED`; `NOTHING`.
`framed` says whether the text came as a bracketed paste. The echo agent asks for bracketed paste and echoes typed
text and repaints at the end of a paste, as the agents' composers do, so the send path's waits behave as they do with a
real agent.

**State pass rules** (the mission's targets): Working within 2 s of the send; never shown waiting while the agent
truly works; shown waiting within 2 s of the true end (the answer's record, or the ask's record for an approval or a
question menu); an approval shown as `WaitingForPerm`. Each case is watched 30 s past the true end, so a late flip is
measured rather than reported as never. The true end comes from record timestamps, good to about a second.

Each matrix appends to the ledger with `Route`, `Exact` and `ViaFile`, and writes `echo-results.jsonl`,
`agent-results.jsonl` or `state-results.jsonl` plus a summary to the run folder. Linux and macOS only for now: the
echo agent's Windows half is built with the Windows baseline run.

Cost: Claude Code (Max) and Codex (ChatGPT sign-in) run on subscriptions - no bill, a small share of the allowance,
each answer one word. Pi runs on the mindzie dev Azure Foundry, `gpt-5.6-terra`, thinking off - cents a run.

## The additions before the phase-2 runs (the mission's 11.5)

**Every cell runs twice.** Each parity matrix (`--echo`, `--text-matrix`, `--state-matrix`, `--condition-matrix`) runs
each cell twice, the two runs side by side, and every summary counts the UNSTEADY cells - those that passed on one run and
failed on the other, or gave two different verdicts. An unsteady cell is a race, not a rule. `--repeat N` changes the
count; the delivery scenarios still run once unless `--repeat` says otherwise. Twice the cells is twice the turns: a full
text matrix now needs `--max-turns 400`.

**The screen of every refused send is saved** in `<out>/screens/`, named after the cell
(`text-claude-prompt-emoji-run1.txt`), every row numbered, taken the moment the send returned - before the judge waits -
so it is the screen the Director refused on. The results line names the file. The state matrix also saves each case's
screen (at the ask for an approval or a menu, else at the end of the watch), and the condition matrix saves the menu and
the dialog it sent into.

**The agent's own trim of edge spaces is accepted** - Claude Code drops trailing spaces, Codex and Pi leading ones, and
a person typing at the agent gets the same. Only that trim, on that agent's edge, passes (`Accepted` names it in the
results, `trim-accepted` counts it in the summary). The other edge, both edges, or any change inside still fails:
`edge-spaces` stays strict for everything else.

**The Codex approval ask is read from its records.** The ask is the agent's TOOL CALL holding the approval's file name -
Codex's `function_call`, `custom_tool_call` or `local_shell_call` (or an approval-request event), Claude Code's
`tool_use`, Pi's `toolCall` - written when the agent asks to run the tool, before the approval. The baseline's reader took
any record that was not a user turn, and Codex echoes the prompt (which names the same file) in an `item_completed`
event, so it would have taken the prompt for the ask. The `tool-40s` case now runs `python3 -c "import time; time.sleep(40)"`:
Claude Code refuses a standalone `sleep 40`, so the baseline timed a refused tool on Claude.

**Claude Code's mid-turn prompt is a user turn.** A prompt typed while Claude Code works is recorded as a
`queued_command` attachment, not a user line; the strict witness now reads it.

### The condition matrix

    $B --condition-matrix --agents claude,codex,pi [--conditions mid-turn,owner-draft,menu-open,usage-limit] [--routes ...] [--texts ...]

Each route sends its default text (`ascii`; `multiline-lf` for `desktop-newline`) unless `--texts` names others. Each
condition is set up for real and confirmed before the send; a cell whose condition could not be set up is `SETUP-FAILED`,
listed, and counted in no rate. Nothing is faked.

| Condition | Agents | Set up | What a pass proves | What it cannot prove |
|---|---|---|---|---|
| `mid-turn` | claude, codex, pi | asked through the desktop to run a 25-second tool; the text is sent once the records show the tool call, and the cell counts only if the turn's answer was not yet recorded | the text reached the agent once, alone, character for character, queued or steered into the turn | for Codex and Pi, which record a queued prompt only when the turn ends, that the prompt was taken at once |
| `owner-draft` | claude, codex, pi | the owner's draft (`My unfinished draft <token>`) typed through the desktop and seen on the screen | one turn holding the draft, then the text, both character for character (`ARRIVED-AFTER-DRAFT`) - the owner's ruling of 21 October 2026 and what a person typing more gets. Wrong: `DRAFT-LOST`, `DRAFT-LEFT`, `ALTERED`, `DOUBLED`, `LOST` | how many spaces join the two: the prompt route adds one, a person types their own |
| `menu-open` | claude | asked through the desktop to open its question menu; confirmed by the AskUserQuestion record and the screen | prompt route: refused in words or delivered, never reported delivered and lost, never arriving late; every route: the next text after Escape closes the menu arrives alone | on desktop routes, where the person's own keys typed into the menu go - recorded (`ANSWERED-MENU`, `LOST`, ...), not judged. Codex and Pi have no question menu to raise: Codex asks only in plan mode, Pi has none; their model pickers are not used, because Enter in one changes the agent's model and Codex writes it to the owner's configuration file |
| `usage-limit` | the echo agent | the echo agent draws Claude Code's usage-limit dialog (the real captured words) and the Director's own guard is checked to read it as showing | a desktop paste - a person at the terminal - reaches the agent (`EXACT`); desktop typing is the control. `DROPPED` is the verdict when only the Enter got through | what Claude Code does with the paste: its dialog appears only when the owner's allowance has run out, which no run can bring about. The prompt route is left out until the owner answers whether phone and Cockpit prompts pass the dialog (mission 11.6) |

The menu cell's tokens are letters only: a digit typed into the menu would pick an option. The summary ends with what each
condition cannot show, so the results carry it as well as this file.

### Running it the same way every time

Every run goes in a VISIBLE Director terminal, never a background job (the owner's rule; the baseline was one, and that was
wrong). Put the run in a script and open a RawCli terminal on it, then read the results files; close the terminal when done:

    cc-devthrottle session spawn <this worktree> --agent RawCli --command bash \
      --command-args "-lc '<the script>; exec bash'" --controlled-by self --name "<Mission> - Worker - harness run"

The script, one line per mode (`B` is the rig, `R` the run folder):

    export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet
    B=<worktree>/src/CcDirector.DeliveryQualification/bin/Debug/net10.0/CcDirector.DeliveryQualification
    R=$HOME/ReposFred/_delivery-qa/runs/<name>-$(date +%Y%m%d-%H%M)
    $B --echo --routes all --texts all --out $R/echo > $R/echo.log 2>&1
    $B --condition-matrix --agents claude,codex,pi --out $R/conditions > $R/conditions.log 2>&1
    $B --state-matrix --agents claude,codex,pi --out $R/state > $R/state.log 2>&1
    $B --text-matrix --agents claude,codex,pi --max-turns 400 --out $R/text > $R/text.log 2>&1

`docs/harness-cells-2026-10-23/` beside this file holds the first run of these additions: its scripts (`run.sh`,
`run-conditions.sh`, `run-menu.sh`), its results and saved screens, and a README of what it found.

Cost of the condition matrix with its defaults (two runs): 24 cells on claude, 16 on codex and 16 on pi, at one to three
turns each. Pi is the only bill: about 40 model calls of about 12,000 tokens in (mostly cached) and 100 out.

