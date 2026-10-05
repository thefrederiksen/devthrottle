# Proof - the Mentor's weekly block (devthrottle_internal#2305), Gateway part

| File | What it shows |
|---|---|
| `contract.md` | The read route and the block's JSON shape, written before the code for the Cockpit. |
| `team-week.json` | One generated team-week for a test team of two people: Rob ran sessions and sent prompts, the Owner ran none. Written by the real writer with a FAKE model (`TeamMentorProofRig`, run with `CC_TEAMS_2305_PROOF` set). It holds every stored row: the one block (with its quote copied verbatim from Rob's stamped prompt record), the two outcomes (`written` for Rob, `no-sessions` for the Owner), the run marker, the person column on session history, and the stamped prompt records. One model call was made - for Rob, none for the Owner. The log also holds a message another session put into Rob's session (no typed or spoken modality): it is stored, and the model was not shown it. |
| `test-runs.md` | The test runs, and the red proofs. |

No test, proof run or manual try called a paid model. Every model in these runs is the fake `FakeBrain`.

## What this evidence does not cover

- **Who the person is, in production.** This pull request does not yet resolve the person behind a team key: that is
  the one team resolver of devthrottle_internal#2311 (devthrottle#3530), and the three places that use it come in a
  follow-up pull request once #3530 is on main. Until then a `POST /prompts` into a team's tenant is REFUSED (403,
  nothing stored) rather than stamped with a guessed person, and no team session carries a person - so the writer,
  even if switched on, would find no sessions and write nothing. The proof's rows were produced by stamping in the test
  rig, the way the follow-up stamps from the key.
- **The team's own time zone.** A team cannot set a time zone yet: the settings routes are refused inside a team's
  tenant, so the team tenant has none stored and the week is cut in the Gateway machine's own zone. The week
  arithmetic is correct for any zone, across a change of clocks (`MentorWeekTests`); what does not exist is a way to
  give a team its zone. Until one exists, every team's week is the Gateway's week.
- **Whether a prompt was typed or spoken by the person.** The Mentor reads only prompts marked typed or voice, and
  that mark is the Director's: a user message takes the mark of the nearest submission within thirty seconds, and a
  submission is not used up by a match. Read from that code, not observed: a message some other sender puts into the
  session within thirty seconds of something the person typed would be marked typed and read as theirs; and a prompt
  the Director cannot tie to a submission (an agent whose history carries no timestamps never can be) carries no
  mark, so a person working only that way gets no block. Neither is changed here.
- **The model's real output.** Only the fake model ran. The instruction file is reviewed text; how a real model
  follows it has not been measured, and switching the writer on is the owner's decision (`CC_GATEWAY_TEAM_MENTOR`). Before it goes on, measure how often a real model's answer is refused - for a quotation mark or for eight words of a prompt in a row - because each refusal is final for that person and week.
