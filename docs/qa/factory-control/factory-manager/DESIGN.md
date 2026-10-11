# The Factory Manager seat - design (Factory Control, step 5)

Written before building, as brief 7 asks. Branch `factory-control/manager`. The branch has two dependencies
merged locally:

- `origin/factory-control/runresult` (pull request 3799, as revised after review);
- brief 4's commit "Factory page: open problems with Handle the issue and Resolve", cherry-picked.

The rest of brief 4's branch is its email-reading commit, which this seat does not need, and an older run results
merge. On the revised 3799 brief 4's code needs one rename (`RecordedUtc` became `RaisedAtUtc`). That rename is its
own commit here.

## What the seat is

One standing session per account, over all of its factories. The owner talks to it. It:

- points the owner to the right factory's boss;
- deals itself with what no single boss owns: a dead machine, a clash of schedules, a rule broken across factories;
- chases each open problem with the boss concerned;
- flags any factory that brings back "CEO" or a person's name for a seat.

The email stays Gateway code. This seat reads the same facts as the email (`cc-devthrottle run problems`, the one
open-problems fold), but it sends nothing.

## How the Fleet Manager does it today, and what is taken from it

The Fleet Manager is bound by one account setting, `fleet_manager_session_id`. It is read and written through
`GET`/`PUT /gateway/fleet-manager` and the `cc-devthrottle fleet-manager` commands. Around that binding sits a large
machinery: a placement service, raising, replacement with a successor, events, and a home folder that the Director
creates (`FleetManagerHome`, behind a capability the Director announces).

The Factory Manager takes **the binding and nothing else**. It needs no events, no raising and no successor
hand-off: it is a talk that the owner opens.

## The pieces

### 1. Binding

- **What is stored.** One account setting, `factory_manager_session_id`. Beside it, the seat's placement:
  `factory_manager_machine` and `factory_manager_folder`.
- **Routes:**
  - `GET /gateway/factory-manager` returns the session, the computer and the folder.
  - `PUT /gateway/factory-manager { "sessionId": "<id>" | null }` sets the mark or clears it. A body with no
    `sessionId` is refused, exactly as the Fleet Manager's route refuses it.
- **Command line:** `cc-devthrottle factory manager show | set <id> | clear` (under `factory`, next to `factory name-check`, rather than a top-level `factory-manager`: the longer top-level name would have pushed three existing one-line helps past one row) (AXI: `--json` keeps its shape, and
  unknown flags fail).
- **Who may set the mark:** the owner's own device, or any session key of the account. This is the same rule as
  the Fleet Manager, and it has the same stated gap: a session can mark itself. The change is logged with who made
  it. Narrowing it later is one check in one route.

### 2. Starting it: a talk, like "Talk to the boss"

`POST /gateway/factory-agents/factory-manager/talk` takes an optional body `{ "computer": "...", "folder": "..." }`.

- **Who may call it:** only the owner's own browser or phone. It uses the same check as the factory talk
  (`FleetManagerOwnerDevice.Require`).
- **If the bound session is still running**, the answer is that session. The button opens it; a second Factory
  Manager is never started.
- **Otherwise it starts a new, visible, top-level session owned by the person.** This is the path a factory talk
  takes, through `StartSessionOnDirectorAsync`. It is never headless. The new session becomes the account's
  Factory Manager straight away.
- **Its folder is the one open question this design settles.** The Director can only create a home folder for the
  Fleet Manager. A Factory Manager home would need a Director change, a capability flag, and a release before
  anyone could use it.
  - So the account saves the seat's computer and folder once. The first start asks for them. The computer is
    picked from the account's running Directors. The folder is a path on that computer, typically the folder that
    holds the factories.
  - Later starts reuse what was saved.
  - Nothing is guessed. With no placement saved and none sent, the answer says what to send.
  - If no Director is running on the saved computer, the answer is 409 and says so, as the factory talk does.

### 3. Its conduct: in the first prompt, not a built-in skill

`Factory/Talk/FactoryManagerSeed.cs` composes the first prompt. It is pure and tested, like `FactoryTalkSeed`. It
holds:

- who the seat is;
- the account's factories, each with its boss;
- the commands to read: `cc-devthrottle run problems`, the factory status, and the name check;
- the rules:
  - chase each open problem with the boss concerned, through the owner's "Handle the issue" (the seat tells the
    owner which button to press; it never edits a factory's files or prompts itself);
  - resolve with a reason only what it fixed itself;
  - send the owner to the boss when the question belongs to one factory.

**Why not a built-in skill.** A built-in reaches every agent on every machine (the four built-ins are fetched by
every Director). This conduct is read by exactly one seat, which only the Gateway starts. It needs the account's
live facts (its factories and their bosses) in the same text anyway. And it changes with the Gateway release, just
as the boss talk's seed does. Putting it in the seed keeps one source with no second copy, so the rule "a built-in
skill is changed in one place" is never in play.

### 4. Resolve rights

`POST /cron/runs/{runId}/resolve` today lets three callers resolve a problem: a person, the account's machine
token, or a session of the run's factory. The Factory Manager is in no factory, so it is refused.

The route now also asks "is the calling session this account's bound Factory Manager?" When it is, the seat may
resolve any problem of its account. Its reason is kept, and the resolver is recorded as
`the Factory Manager (session <id>)`. A session that is neither of the factory nor the bound Factory Manager is
still refused, in the same words as before.

### 5. Hand-over for a factory with no boss

Brief 4's named point is `FactoryProblemHandoff`. It now answers one of three things:

- the boss;
- **the Factory Manager**;
- a refusal.

"Handle the issue" on a factory with no boss opens a Factory Manager talk briefed the same way a boss is: which
run, what went wrong, when, which session to read, and to resolve it with a reason once fixed.

- It is a new talk, exactly as Handle on a boss is a new talk, and it becomes the bound Factory Manager.
- With no placement saved, it is refused with the sentence that says how to set one.

### 6. The name check

`GET /gateway/factory/name-check`, a fold over the factory registry. It is reachable by a session key, so that the
seat itself can run it, and it is listed in `SessionKeyGuard`. On the command line it is
`cc-devthrottle factory name-check`. The Factories screen shows its findings on the Factory Manager card.

**The rule.** It uses no list of first names. A seat is flagged when either of these is true:

- **CEO:** its id, name or role contains the word "CEO". The match is on whole words and ignores case.
- **Not a role word:** its name is not a role word of its factory. The factory's role words are the registered
  roles of its seats, plus "Boss" for the boss seat.
  - So "Savings Engineer / Savings Engineer" passes, and "Boss" on the boss seat passes.
  - "Nora Hale / Boss" is flagged: "the seat is named 'Nora Hale', which is not a role word of this factory (its
    role is Boss)".

**What the rule cannot see.** A role that is itself a person's name (role "Nora", name "Nora") passes. The rule
checks that names are role words; it cannot tell a word from a name.

### 7. The Factories screen

The Factory Manager card sits on the Factories list. It is folded on the Gateway from
`GET /gateway/factory-agents/factory-manager`. It shows:

- whether the seat is running, and its session;
- the button: "Talk to the Factory Manager", or "Open the Factory Manager" when it is running;
- the computer picker and the folder field, but only when no placement is saved;
- the name-check findings.

## Not built

- **A Director-made home folder for the Factory Manager.** It needs a Director release; see section 2.
- **Events pushed to the Factory Manager** in the way the Fleet Manager has them. The seat reads
  `run problems` when the owner talks to it; it is not woken by new problems.
- **Narrowing who may set the mark.** This is the same gap the Fleet Manager has, stated in section 1.
