# A Boss delegates: the live proof

Run on 1 November 2026 on SOREN_NORTH, on an isolated local Gateway, launcher and Director built from this branch
(`scripts/restart-qa-rig.ps1`, its own root and port 7911). The factory `delegation-proof` had a Boss on Sonnet and a
Scribe on Haiku. Real agent sessions did the work. `transcript.txt` holds the calls and answers.

| What the owner asked for | What happened |
|---|---|
| A Boss hands work to the right seat | A Boss started by its real schedule ran `factory seat start`. Session `0d07efa8` started as the seat `scribe`, on the seat's own model |
| That session has what it needs | Its kit held `brief.md`, `grants.md`, `kit.json`, `rules.json` and `records/` (`scribe-kit-listing.txt`, `scribe-kit-grants.txt`). Its note began with the mark from its brief |
| It belongs to the Boss | Owner and parent were the Boss session, kit `seat`, origin `agent` |
| It comes back | The Scribe ran `session handback`. The Boss read it, said what the note was, and the run was recorded `ok` |
| It is recorded | The activity record holds "The boss session dc287b5a started the seat scribe in session 0d07efa8" |
| The Boss does not do the work | In the first run the command was missing from the rig. The Boss did not write the note: it reported a problem |
| Nobody else can do this | The Scribe ran the same command and got "only the Boss of a factory may start another seat of it", logged as a refusal |
| The Boss stays free | An open Boss with a 120-second job still Working under it answered the owner's question at once (`transcript.txt`: "the Boss answered: ANSWER 42", and the final list shows that session Working). That job came back afterwards: the Boss's screen then said it "reported back with the word FINISHED" |

## What this does not prove

- **The 120-second job that came back with FINISHED was an ordinary child, not a seat.** The rig's Boss for that round
  was started by hand with a bare prompt and used `session spawn`. The seat started after it (`slow-count`) could not
  run `sleep`, because the agent tool blocks it, so it handed back a not-finished status
  (`slow-count-seat-screen.txt`). The rig was stopped before the Boss's screen showed that handback arrived. The seat's
  way back is proved by the Scribe.
- **The open Boss was not a real Talk.** The rig has no owner's device, so it was started with the rig's token, with
  the seat fields a talk sends.
- **One unexplained failure.** The Boss's first `factory seat start` of the successful run answered "The system cannot
  find the file specified", and a shorter prompt worked. Only one Scribe started. The cause was not found. The rig ran
  the command through a stand-in `.cmd` file, which is the first place to look, but that was not tested.
- **The Director carried no Session Rules hook**, because this machine has no C++ linker (`-NoRuleHook`). A seat's
  rules reach it through its own `--settings` file, so a seat start does not depend on the hook.
- **Close after handback was added after the run**, on what the run showed. A unit test pins the wording. No live
  session has run it.

## How the rig differed from the script

A temporary copy of `restart-qa-rig.ps1` turned factory agents on, built with `-NoRuleHook`, waited longer, and started
the Gateway and launcher through WMI, because Task Scheduler failed with 0xC0000142 on this machine. This tree's
command line replaced the one in the rig's `bin` folder. The copy was deleted. `rig/` holds the drivers.
