# Mission: License Switching

Opened 2026-10-11 by the owner. Issue: #3848. Architect: the License Manager session. This file is public; the
owner's own accounts, machines and the working prototype live in the private mission record
(devthrottle_internal `docs/missions/license-switching-2026-10-11/`), which the Delivery Lead reads
first.

## 1. The mission

DevThrottle watches every Claude Code subscription the owner has, and when the account a machine is
working on reaches 97% of its limit, it moves that machine's sessions onto the account with the most
room left - by itself, at any hour - and shows all of it on a new **Licenses** page in the Cockpit.

## 2. The why

The owner pays for several Claude Max subscriptions. Each has a 5-hour window and a weekly limit.
When the account a machine is using runs out - often in the middle of the night - every session on
that machine stops, while another paid account sits unused. Switching by hand means being awake,
knowing which account has room, and doing a browser-and-email sign-in. In the owner's words:
"sometimes the licenses expire in the middle of the night while I'm sleeping. What I really want to
happen is, at 97%, you start switching the licensing, then you switch the license to another one, and
you maintain and figure out which of the licenses are going to be available next, and when do we
switch with it."

## 3. The goal

At the end, all of this is true and shown in a QA report (HTML, screenshots, written by a seat that
did not build it):

1. The Cockpit has a **Licenses** page listing every Claude account: plan, 5-hour use, weekly use,
   when each resets, renewal date, and which machines are on it.
2. The page shows the **schedule**: which account each machine is on now, which account is next, and
   roughly when the next switch will happen.
3. The page shows a **log** of every switch: when, which machines, from which account to which, why
   (threshold reached, or by hand), and whether it worked.
4. When an account in use reaches **97%** of either window, every machine on it is switched to the
   best other account **without anyone touching anything**, and the sessions already running carry on
   on the new account at their next call. Proven on a Windows machine and on the Mac.
5. A **Switch now** control does the same on demand.
6. If no account has room, the owner gets one email saying so, and nothing is switched.
7. The Gateway never holds a Claude sign-in key.

## 4. Decisions (the owner's words)

- "you need to make this part of the gateway and you need to make it a whole new page on the cockpit
  that displays the licenses available and the schedule for switching and then you need to set it up
  so we switch the license as soon as we are using as soon as we use 97% we start switching the
  licenses over to the new account" (2026-10-11).
- Sign-in keys stay on the machines, never in the Gateway: Architect asked "Should the sign-in keys
  stay on your computers only?", owner: "i agee, go ahead" (2026-10-11).
- **Production deploy is granted for this mission** by the owner's words above ("make this part of
  the gateway ... set it up so we switch"). The Gateway is deployed ONLY with the
  `deploy-hosted-gateway` skill. This grant dies with the mission.
- **Outward email**: the only email this mission sends is to the owner himself, when no account has
  room. Nothing else leaves the fleet.
- Architect's calls, overridable by the owner, recorded here: (a) a switch moves EVERY machine on the
  tired account, because usage is counted per account, not per machine - every machine that is READY,
  at once, with any machine left behind following the moment it is ready (the owner's ruling of
  16 October 2026, section 5); (b) the threshold is 97% of
  either the 5-hour window or the week, whichever comes first; (c) an account that triggered a switch
  is not chosen again until the window that triggered it has reset.

## 5. Design

### Words

- **Account**: one Claude subscription, identified by its email.
- **Sign-in key**: what Claude Code keeps after signing in to an account (an OAuth access + refresh
  token). Windows and Linux: `<config dir>/.credentials.json`. macOS: a Keychain generic password,
  service `Claude Code-credentials` for the default config dir, `Claude Code-credentials-<first 8 hex
  of sha256(config dir path)>` for any other `CLAUDE_CONFIG_DIR`, account = the macOS user.
- **Active key**: the one in the machine's default Claude config, which every session uses.
- **Spare key**: a key for another account, kept by the Director in its own folder per account.

### What was proven before this mission (2026-10-10, prototype in the private record)

- The full sign-in runs with no human: `claude auth login --claudeai --email X` with
  `CLAUDE_CONFIG_DIR` set and `BROWSER=none` prints an authorize URL and waits for a pasted code. The
  claude.ai email-link sign-in is done in a browser profile signed in as X; the authorize page says
  "Logged in as X"; clicking Authorize shows `code#state`; pasting it gives "Login successful".
  Works across machines: the login ran on the Mac over SSH, the browser and mailbox on Windows.
- Swapping only the sign-in key moves the billed account. A conversation continued across accounts
  with `--resume`.
- **A RUNNING process picks up a swapped key on its next API call** (Mac, keychain): started on
  account A, key swapped to B mid-task, the next call was refused with B's own reset time.
- Usage is readable with the key alone: `GET https://api.anthropic.com/api/oauth/usage`, Bearer the
  access token, header `anthropic-beta: oauth-2025-04-20` -> `five_hour` and `seven_day`
  `{utilization, resets_at}` and `extra_usage`.

### Traps found, which the design must handle

1. `claude auth status` reports the account from the cached `oauthAccount` block in `.claude.json`,
   not from the key. A switch rewrites both, or the screen lies.
2. Two places holding the same key share one refresh token; a refresh in one may cancel the other.
   **A key is never copied. A switch EXCHANGES the active key with the spare.** Each sign-in exists in
   exactly one place. (A separate `claude auth login` makes an independent key; that is fine.)
3. Over SSH on macOS the Keychain is locked; the Director runs in the user's GUI session and is not
   affected, but tests over SSH must unlock it.
4. **Never send Enter (or any key) to a session showing Claude's usage-limit dialog** - one of its
   options turns on paid extra usage. Switching at 97% exists so no session reaches that dialog. If one
   does, the Director only ever sends Escape, and logs it.
5. A spare key that sits unused must stay valid (access tokens expire in hours). The Director keeps
   spares fresh and proves a spare left for 24 hours still works.

### Pieces

**Director (each machine) - holds keys, acts, reports numbers**
- A key store per machine: lists accounts it holds (active + spares), adds a spare (from a sign-in),
  removes one, and EXCHANGES active <-> spare, rewriting `oauthAccount` too. Windows/Linux file,
  macOS Keychain. Atomic, logged, and verified after the exchange by reading the new active account.
- Reads usage for every key it holds every few minutes and sends ONLY the numbers (account email,
  5-hour %, week %, reset times, extra-usage state) to the Gateway.
- Obeys a `switch to <account>` command from the Gateway and reports the result.
- Command line: `cc-devthrottle license list | usage | switch <account> | add <account>`.

**Gateway - decides and remembers, never holds a key**
- Stores accounts, the latest readings, which machine is on which account and which spares each
  machine holds, the switch log, and the forecast.
- The decision, every minute: for each account in use, if either window is at or above 97%, switch
  every machine on it that is ready, at once (the owner's ruling of 16 October 2026: "Switch every ready
  machine at once; the one left behind keeps trying and switches the moment it is ready"). In plain words:
  - **An account has room** when it is not the account in use, its latest reading is fresh, both its
    windows are under 97%, and it is not barred by decision (c). That is judged on the account alone.
    Which sign-ins the machines hold never decides whether an account has room. The best account with
    room is the one with the lowest weekly use, then the lowest 5-hour use, then its email in
    alphabetical order. That is "the account it would switch to next".
  - **A machine is ready** for an account when it holds a sign-in for it that was not refused and that
    does not expire before the switch could finish (contract 5.7). There is one definition of ready, in
    the Gateway's decision code, and everything that asks - the automatic switch, Switch now, the
    Licenses page - uses it.
  - **Each machine goes to the best account with room that it is ready for.** So when the best account
    is held by only some machines and the second best by all, the ones that hold the best go there and
    the rest go to the second best, all at once. Nobody waits for anybody.
  - **A machine ready for no account with room is left behind.** It is not switched and it holds back no
    other machine. It is switched on a later minute, by this same rule, the moment it becomes ready
    while the account is still at its limit. The switch log row of the machines that did move names it
    and the reason ("machine-d holds no sign-in for it and switches as soon as it does").
  - **Only when no account has room** is it the no-room event: nothing is switched and the owner gets
    one email for the event. When an account has room and some machine is not ready for it, the owner
    is never told "no account has room"; the license alarm (phase 6, track A) covers that case.
- Forecast: for each account, its reset times and, from recent readings, the expected time to 97% at
  the current rate; from that, the expected next switch and its target. The target is named whenever an
  account has room, and the sentence names any machine that would not move and why.
- Switch now with a target chosen moves the machines that are ready for it, and says which machines it
  leaves and why; with no target named it does exactly what the automatic switch would do.

**Cockpit - the Licenses page**
- Accounts table, machines, schedule/forecast, switch log, Switch now. Follows
  `docs/CockpitVisualStyle.md` and `docs/VisualStyle.md`.

**Adding a spare to a machine** - the email-link sign-in, driven from the machine that has the
owner's browser profiles and mailbox tools; the target machine runs `claude auth login` into a spare
folder, the authorize code travels back (it is useless without the target's PKCE verifier). Spares are
added ahead of time, so a 3 AM switch needs no browser.

### In / out

In: the above. Out: buying or cancelling subscriptions (owner only, by hand); API-key (Console)
accounts; per-session account choice (all sessions on a machine share the active key).

## 6. Phases

| Phase | What | Needs | Runs in parallel with |
|---|---|---|---|
| 1 | Director key store, usage reading, exchange, CLI; Windows live-swap proof (a running process moves on its next call) and macOS proof | Tech Lead review | 2 |
| 2 | Gateway: storage, ingest from Directors, decision engine, switch command, owner email, forecast | Tech Lead review | 1 (agree the Director<->Gateway messages first, in this folder) |
| 3 | Cockpit Licenses page | Reviewer | after 2's API exists |
| 4 | Adding a spare to a machine from the Cockpit/CLI, using the proven email-link flow | Reviewer | after 1 |
| 5 | Deploy (Gateway via `deploy-hosted-gateway`), install the Director build on the owner's machines, set up spares, QA report | QA seat that did not build | last |

## 7. The check

- Unit tests for the decision engine: under 97% does nothing; 97% on the 5-hour window switches;
  97% on the week switches; target choice order; no target -> one email, no switch; an account that
  triggered is not chosen again before its reset; Switch now.
- Unit tests for the key store: exchange on Windows file and macOS Keychain naming; a key never exists
  in two places after any operation; `oauthAccount` follows the key.
- Live proof script (committed in this folder): in isolated config folders only, never the machine's
  real default config - start a `claude -p` task that pauses inside a tool call, exchange the key
  mid-task, show the next call is on the other account. Run on Windows and on the Mac.
- End to end in production: lower the threshold for one test account or use Switch now, and show the
  Cockpit page, the log entry, and the machine's new active account.

## 8. Merge plan

Phase-sized pull requests, each merged on green to main the day it is ready (trunk rules: a branch
lives under a day). CI is on demand - run the suite the changed projects need before merging. The
Director parts are inert until the Gateway sends a command; the Gateway parts are inert until a
Director reports; so each phase is safe on main alone.

## 9. Where it ends

In production: the Gateway deployed with the feature, the Directors on the owner's machines running a
build that has it, spares set up, the 97% switch live, and the QA report published to the owner.

## 10. Questions

1. Where do sign-in keys live? Recommendation: on the machines only. **Answered: machines only.**
2. Does a switch move every machine on the account? Recommendation: yes. Decided by the Architect,
   overridable.
3. May the mission deploy the Gateway? Granted by the owner's opening words.
