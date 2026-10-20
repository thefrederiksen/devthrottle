# The Mail Desk rig

Mail Desk phase 4, contract section 5 (`docs/qa/mail-desk/phase-4/CONTRACT.md`). `run_qa.py` proves live mode and
every failure case on a throwaway Gateway and a test Director built from this tree, on devlinux, and writes
`transcript.txt` (committed). It runs in the foreground, owns everything it starts, and stops everything it started,
pass or fail - also when it is sent a termination signal.

## How to run it

From the repository root, on devlinux:

```
export PATH=$HOME/.dotnet:$HOME/.local/bin:$PATH
dotnet build src/CcDirector.Gateway/CcDirector.Gateway.csproj -p:EnableWindowsTargeting=true
bash scripts/local-build-linux.sh --slot 5 --framework-dependent
uv run --script docs/qa/mail-desk/rig/run_qa.py [--only observe,signin,...]
```

The whole run takes a long time - every case waits for real check runs, and a start runs at most once a minute - so
run it with `--only` in parts, or as one long foreground command. The sections are listed in `SECTIONS` at the end of
`run_qa.py`; `--only` runs the set-up and the sections named. The wrapper's own tests:

```
uv run --no-project --with pytest --with "msal>=1.28" python -m pytest docs/qa/mail-desk/rig/tests -v
```

## What it sets up

| Piece | What it is |
|---|---|
| Gateway | `src/CcDirector.Gateway` (Debug) on a free port of 127.0.0.1, storage root `/tmp/mail-desk-rig/gateway-root`, a minimal environment (no `CC_` variable, no service token, no key of the owner's), Tailscale off, authentication on, factory agents on in its own `config.json`, an administrator token made for the run. Mail Desk is switched on for its one account (`local`) through `POST /gateway/admin/mail-desk`. |
| The model | The Gateway's `config.json` sets **`mailDeskModelBaseUrl`** to the rig's fake model on 127.0.0.1. That setting was added for this rig (Tech Lead's decision, 19 October 2026): the Mail Desk's model address was a constant, so a throwaway Gateway could not be pointed at a fake without it. It is honoured only for a loopback host; any other value stops the Gateway from starting, and absent means the hosted endpoint, so production is unchanged. Its key vault holds a rig-only key that only the fake model accepts. **The rig never calls DeepInfra or any real model.** |
| Director | `local_builds/linux/cc-director-linux5`, built from this tree, with a scratch `CC_DIRECTOR_ROOT` and a scratch `HOME` under the rig folder, every `CC_` variable removed, auto-update off, stopped only by its own lifecycle request file. Afterwards the rig checks that `~/.local/bin` is unchanged and still points at the owner's tools. Its `PATH` starts with a rig folder whose `cc-devthrottle` is this tree's command. |
| The check | The published `mail-desk` definition is the template with four recorded changes: this machine's name, `every: 1m` (the shortest interval a start may have), `checks/screen.py` = the rig's wrapper (`checks/screen.py` here) and `checks/real_screen.py` = the template's real `screen.py`, byte for byte (the rig checks the hash), plus `files/rig.json` telling the wrapper where the rig is. |
| Fake Graph | On 127.0.0.1: an Inbox, Sent, Archive and Junk Email per mailbox, told apart by the wrapper's fake token; the receive-time filter, order and paging the check asks for; the move; a log of every call (time, mailbox, method, path with ids replaced, folder, destination - never a body or an id); a failure that can be switched on per mailbox. |
| Fake model | On 127.0.0.1: chat completions with log probabilities, scripted per subject, able to fail (HTTP 500) or answer off the labels. |
| Seats | `qa-books/clerk` and `qa-support/desk`, each a small factory whose seat is a real Claude Code session on the cheapest model, told by its brief to run `maildesk done` or `maildesk return` for the emails in its `check.json`. |

Addresses are `qa-one@example.com`, `qa-two@example.com` and example senders only. The transcript passes through
`shown()`, which replaces the rig, tree and home folders, every key and token, and any address that is not an example
address.

## The wrapper: what is real and what is faked

The check that runs is the real `screen.py`. The wrapper loads it and replaces exactly two things:

1. **The network** - `urllib.request.urlopen`, the same function the phase 2 tests replace. The real
   `graph_request()`, its request guard and its `transport()` with its error handling all run; only the connection
   goes to the fake Graph on 127.0.0.1 instead of `graph.microsoft.com`.
2. **The sign-in** - `OutlookReader.sign_in`. It looks for cc-outlook's file name in the rig's scratch sign-in folder
   (`/tmp/mail-desk-rig/sign-in`): found, the mailbox is signed in with a fake token; not found, the real check's own
   "not signed in" failure is raised. MSAL is never constructed.

Everything else - the memory, the cursor, the keys, the Sent cache, the batches, the request guard, the filing - is
the real code. **The rig can never let the check find the owner's real sign-in or reach Microsoft:** the wrapper
refuses to run (exit 1, nothing read) when its sign-in folder is not inside the rig folder or is the user's real
Director folder, when the rig folder lacks the rig's marker, when its own copy is not inside the rig folder, or when the
fake Graph is not an http address on 127.0.0.1. `tests/test_wrapper.py` proves each refusal.

## What it does NOT prove

- **Not Microsoft Graph or the real sign-in.** The fake Graph answers the way phase 2's tests and the phase 0
  measurements say Graph answers; a real Graph that differs (paging, throttling, an id format, a folder name) is not
  seen here. The sign-in is a file being present; MSAL, token renewal and the sign-in service are not exercised. Phase
  6 proves those on the real mailbox.
- **Not the real model.** The fake answers what the rig scripts. How often the real model is unsure, off the labels or
  wrong is phase 6's measure.
- **Not the hosted Gateway, PostgreSQL or another operating system.** The rig's Gateway is self-hosted on SQLite on
  Linux, with the account `local`. Hosted-only paths (the plan's gate, the account named on an AI call, the service
  token) are not run; the migrations on PostgreSQL are proved by `scripts/test-database.ps1`, not here.
- **Not a second real machine.** The backup case's second Director is the rig standing in for one (`StandInDirector`
  in `run_qa.py`): it holds a real Director stream connection and says Hello as machine `qa-backup`, then polls and
  reports through the Director routes, running the same wrapper. devlinux has one host name and this account may not
  make a namespace to change it, so a second real Director would be the same machine. The check's own sentences name
  devlinux; the Gateway's claim names `qa-backup`.
- **Not a released Director.** The rig's Director is built from this tree, so it does not prove that a Director
  already released accepts the routed sessions the Gateway starts.
- **Not the 08:00 email's delivery.** The section is read from what the Gateway composed; the rig's Gateway holds no
  account sign-in, so nothing is sent.
- **The seats' sign-in is the owner's Claude Code sign-in.** The scratch home links `~/.claude` and `~/.claude.json`
  to the owner's, so the seats run signed in; the Director writes its folder-trust entries for the rig's folders into
  that `~/.claude.json`, as the owner's own Director does for any folder.
- **Timing is approximate.** A run "after" a change is the first run whose check ended more than 20 seconds after it.
