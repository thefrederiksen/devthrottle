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
| `menu-open` | claude | the agent shows its question menu (AskUserQuestion) when the prompt arrives |
| `owner-draft` | claude, pi | the owner has unsent text in the composer |
| `leftover-line` | claude, pi | an unsubmitted framework line (a doorbell line) sits in the composer |
| `resume` | claude | the first prompt of a session resumed on an existing conversation |
| `untrusted-folder` | claude | the first prompt in a folder Claude Code has never trusted |

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
(`ClaudeFolderTrust`), so only `untrusted-folder` meets the trust question. The rig runs in a private
Director home (`CC_DIRECTOR_ROOT` under the run folder) and removes the launching session's fleet identity,
so it never touches the owner's live Director. It takes the agent executables from the live Director's
config.

Cost: every text asks for a one-word reply and no tools. Claude Code on a subscription costs no money; an
agent on a paid provider costs a few thousand tokens per attempt.
