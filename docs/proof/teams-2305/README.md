# Proof - the Mentor's weekly block (devthrottle_internal#2305), Gateway part

| File | What it shows |
|---|---|
| `contract.md` | The read route and the block's JSON shape, written before the code for the Cockpit. |
| `team-week.json` | One generated team-week for a test team of two people: Rob ran sessions and sent prompts, the Owner ran none. Written by the real writer with a FAKE model (`TeamMentorProofRig`, run with `CC_TEAMS_2305_PROOF` set). It holds every stored row: the one block (with its quote copied verbatim from Rob's stamped prompt record), the two outcomes (`written` for Rob, `no-sessions` for the Owner), the run marker, the person column on session history, and the stamped prompt records. One model call was made - for Rob, none for the Owner. |
| `test-runs.md` | The test runs, and the red proofs. |

No test, proof run or manual try called a paid model. Every model in these runs is the fake `FakeBrain`.

## What this evidence does not cover

- **Who the person is, in production.** This pull request does not yet resolve the person behind a team key: that is
  the one team resolver of devthrottle_internal#2311 (devthrottle#3530), and the three places that use it come in a
  follow-up pull request once #3530 is on main. Until then a `POST /prompts` into a team's tenant is REFUSED (403,
  nothing stored) rather than stamped with a guessed person, and no team session carries a person - so the writer,
  even if switched on, would find no sessions and write nothing. The proof's rows were produced by stamping in the test
  rig, the way the follow-up stamps from the key.
- **The model's real output.** Only the fake model ran. The instruction file is reviewed text; how a real model
  follows it has not been measured, and switching the writer on is the owner's decision (`CC_GATEWAY_TEAM_MENTOR`).
